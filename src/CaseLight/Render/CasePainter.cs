using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Windows;
using CaseLight.Core.Capture;
using CaseLight.Core.Capture.Backends;
using CaseLight.Core.Frames;
using CaseLight.Core.Leds;
using CaseLight.Model;
using CaseLight.Rgb;

namespace CaseLight.Render;

/// <summary>A stand-in for the screen: one patch of colour at a place on the scene.</summary>
public sealed class TestPatch
{
    public double CenterX, CenterY, SizeMm;
    public bool Circle = true;
    public byte R = 255, G = 64, B = 32;
}

/// <summary>
/// Drives the case from the screen.
///
/// The chain is deliberately the same one Rimlight already uses for the strip behind the
/// monitor - the frame arrives over the shared bus, <see cref="ZoneSampler"/> averages a
/// patch per LED and <see cref="ColorPipeline"/> does the colour work in linear light. The
/// only part specific to the case is deciding which patch of screen each LED looks at, and
/// that falls straight out of where the LED physically stands relative to the monitor.
/// </summary>
public sealed class CasePainter : IDisposable
{
    /// <summary>One LED, already resolved to hardware so the loop does no searching.</summary>
    readonly record struct Target(int DeviceIndex, int GlobalLed);

    /// <summary>Per-LED sampling needs enough pixels that each zone covers several.</summary>
    const int ReduceWidth = 256;

    readonly RgbHub _hub;
    readonly FrameSubscriber _bus = new();
    readonly ColorPipeline _pipeline = new();

    /// <summary>Our own capture, used when the frames do not come from Rimlight.</summary>
    HybridBackend? _capture;
    long _captureVersion;
    CaptureSource _captureMode = CaptureSource.FromRimlight;
    string _captureMonitor = "";

    Thread? _thread;
    volatile bool _running;
    volatile bool _rebuild = true;

    Scene _scene;

    // одна запись на диод, в том же порядке, что и зоны выборки
    LedZone[] _zones = Array.Empty<LedZone>();
    Target[] _targets = Array.Empty<Target>();

    /// <summary>Where each LED physically is - the test patch works in scene space, not screen space.</summary>
    Point[] _world = Array.Empty<Point>();

    /// <summary>How often each device is written, in frames. Slow buses get a larger number.</summary>
    readonly Dictionary<int, int> _deviceDivider = new();
    readonly HashSet<int> _dueNow = new();

    int _resolvedGeneration = -1;
    long _frameNo;

    /// <summary>Set by a rebuild: the devices outside the layout need their default wiped.</summary>
    bool _blankUnused;

    volatile bool _paused;
    string _pauseReason = "";

    /// <summary>Nothing is written to the hardware until this moment passes.</summary>
    long _holdUntilTicks;

    /// <summary>Reference assignment is atomic, so the UI can swap this in at any moment.</summary>
    volatile TestPatch? _test;

    byte[] _image = Array.Empty<byte>();
    byte[] _sampled = Array.Empty<byte>();
    byte[] _output = Array.Empty<byte>();

    public string Status { get; private set; } = "остановлено";
    public bool IsRunning => _running;
    public bool IsPaused => _paused;
    public string PauseReason => _pauseReason;
    public long FramesPainted { get; private set; }
    public double Fps { get; private set; }

    /// <summary>Frames taken off the bus, and how stale the last one was.</summary>
    public long FramesReceived { get; private set; }
    public long LastFrameAgeMs { get; private set; }
    public string SourceInfo { get; private set; } = "—";

    /// <summary>
    /// Which screen the frames on the bus belong to.
    ///
    /// The publisher chooses the screen, and the case has to know which one it is: the
    /// monitor rectangle on the scene is that screen, and a layout built against another
    /// one samples the wrong part of the picture.
    /// </summary>
    public string BusMonitorDeviceName { get; private set; } = "";
    public int LedCount => _targets.Length;

    public CasePainter(RgbHub hub, Scene scene)
    {
        _hub = hub;
        _scene = scene;
    }

    /// <summary>Call after anything that moves a fixture or changes its LED count.</summary>
    public void Invalidate() => _rebuild = true;

    public void UseScene(Scene scene)
    {
        _scene = scene;
        _rebuild = true;
    }

    /// <summary>Null returns to painting from the screen.</summary>
    public void SetTest(TestPatch? patch) => _test = patch;

    public bool TestActive => _test != null;

    public void Start()
    {
        if (_running) return;

        _running = true;
        _rebuild = true;

        _thread = new Thread(Loop)
        {
            IsBackground = true,
            Name = "caselight-paint",
            Priority = ThreadPriority.AboveNormal
        };
        _thread.Start();
    }

    public void Stop()
    {
        if (!_running) return;

        _running = false;
        _thread?.Join(1500);
        _thread = null;

        StopCapture();
        _hub.Blackout();
        Status = "остановлено";
    }

    /// <summary>Darkens the case and stops writing - for lock, sleep and display off.</summary>
    public void Pause(string reason)
    {
        if (_paused) return;

        _pauseReason = reason;
        _paused = true;
        _hub.Blackout();
    }

    /// <summary>
    /// Resumes, but not immediately after a wake.
    ///
    /// Devices are re-enumerated while the machine sleeps, and OpenRGB keeps its old handles
    /// for a while afterwards - it was seen dying 41 seconds after a resume. Giving the bus
    /// a few seconds costs nothing and keeps us from being the one that pokes it.
    /// </summary>
    public void Resume(int delayMs = 0)
    {
        if (!_paused && delayMs <= 0) return;

        _paused = false;
        _pauseReason = "";
        _holdUntilTicks = Environment.TickCount64 + Math.Max(0, delayMs);
        _pipeline.Reset(_zones.Length);   // не разгораться из устаревших цветов
    }

    void Loop()
    {
        try { PaintLoop(); }
        catch (Exception ex)
        {
            // A background thread that throws takes the whole process with it. Losing the
            // painting is bad; losing an unsaved layout with it is worse.
            _running = false;
            Status = "раскраска аварийно остановлена: " + ex.Message;
        }
    }

    void PaintLoop()
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        double lastMs = 0;
        int framesThisSecond = 0;
        long fpsWindow = Environment.TickCount64;

        while (_running)
        {
            int periodMs = (int)Math.Round(1000.0 / Math.Clamp(_scene.MaxFps, 1, 120));

            if (_paused)
            {
                Status = "пауза: " + _pauseReason;
                Thread.Sleep(200);
                continue;
            }

            long hold = _holdUntilTicks - Environment.TickCount64;
            if (hold > 0)
            {
                Status = $"ожидание готовности устройств после сна: {hold / 1000.0:F0} с";
                Thread.Sleep(Math.Min(500, (int)hold));
                continue;
            }

            // Между кадрами, а не внутри: перечитывание меняет длины буферов.
            _hub.RefreshIfStale();

            // A reconnect renumbers the controllers, so resolved indices have to be redone
            // before they address the wrong hardware.
            if (_rebuild || _hub.Generation != _resolvedGeneration)
            {
                _rebuild = false;
                Rebuild();
            }

            if (_targets.Length == 0)
            {
                Status = "нет включённых фигур с диодами";
                Thread.Sleep(300);
                continue;
            }

            if (!_hub.Connect())
            {
                Status = _hub.Status;
                Thread.Sleep(500);
                continue;
            }

            if (_blankUnused)
            {
                _blankUnused = false;
                _hub.BlackoutOthers(_deviceDivider.Keys);
            }

            var test = _test;
            if (test != null)
            {
                FillFromTest(test);
                SourceInfo = "тестовое пятно";
            }
            else if (_scene.CaptureSource == CaptureSource.FromRimlight)
            {
                StopCapture();
                if (!TakeSharedFrame(periodMs)) continue;
            }
            else if (!TakeOwnFrame(periodMs))
            {
                continue;
            }

            double now = clock.Elapsed.TotalMilliseconds;
            double dt = now - lastMs;
            lastMs = now;

            _pipeline.Process(_sampled, _output, ColourSettings(), _zones.Length, dt <= 0 ? periodMs : dt);

            _frameNo++;

            _dueNow.Clear();
            foreach (var (device, divider) in _deviceDivider)
                if (divider <= 1 || _frameNo % divider == 0)
                    _dueNow.Add(device);

            if (_dueNow.Count == 0) { Thread.Sleep(periodMs); continue; }

            _hub.BeginFrame();
            for (int i = 0; i < _targets.Length; i++)
            {
                var t = _targets[i];
                if (!_dueNow.Contains(t.DeviceIndex)) continue;

                int o = i * 3;
                _hub.ContributeAt(t.DeviceIndex, t.GlobalLed, _output[o], _output[o + 1], _output[o + 2]);
            }

            if (!_hub.EndFrame(_dueNow))
            {
                // The OpenRGB server dies on its own often enough that this is an expected
                // state; reconnecting re-resolves every binding and restores direct mode.
                Status = "связь с OpenRGB потеряна, переподключение";
                Thread.Sleep(500);
                continue;
            }

            FramesPainted++;
            framesThisSecond++;

            long tick = Environment.TickCount64;
            if (tick - fpsWindow >= 1000)
            {
                Fps = framesThisSecond * 1000.0 / (tick - fpsWindow);
                framesThisSecond = 0;
                fpsWindow = tick;
                Status = (test != null ? "тест размещения, " : "идёт раскраска, ") + Rate(Fps);
            }

            Thread.Sleep(periodMs);
        }
    }

    /// <summary>
    /// The frame rate with its noun in the right case.
    ///
    /// Russian declines the noun after a number - one кадр, two кадра, five кадров - and a
    /// status line that reads "3 кадров" looks like a bug in everything around it.
    /// </summary>
    static string Rate(double fps)
    {
        int n = (int)Math.Round(fps);
        int tail = n % 100, last = n % 10;

        string word = tail is >= 11 and <= 14 ? "кадров"
                    : last == 1 ? "кадр"
                    : last is >= 2 and <= 4 ? "кадра"
                    : "кадров";

        return $"{n} {word} в секунду";
    }

    /// <summary>
    /// Captures the screen ourselves, through the same backends Rimlight uses.
    ///
    /// Worth having even though the shared bus exists: it makes the program stand on its
    /// own, and it is the only option when Rimlight is not wanted at all - the case
    /// lighting has no reason to depend on the strip behind the monitor.
    /// </summary>
    bool TakeOwnFrame(int periodMs)
    {
        if (!EnsureCapture())
        {
            Status = "экран для захвата не найден";
            SourceInfo = "нет источника";
            Thread.Sleep(500);
            return false;
        }

        _capture!.MinReduceIntervalMs = periodMs;

        if (!_capture.TryGetImage(ref _image, ref _captureVersion, out int w, out int h, out int stride) || w <= 0 || h <= 0)
        {
            // A still screen produces no frames at all; keep what the LEDs already show.
            Thread.Sleep(periodMs);
            return false;
        }

        FramesReceived++;
        LastFrameAgeMs = 0;
        SourceInfo = $"свой захват ({_scene.CaptureSource}), {w}×{h}, экран {_captureMonitor}";

        ZoneSampler.Sample(_image, w, h, stride, _zones, _sampled);
        return true;
    }

    /// <summary>Creates or re-creates the backend when the method or the screen changes.</summary>
    bool EnsureCapture()
    {
        bool same = _capture != null
                 && _captureMode == _scene.CaptureSource
                 && _captureMonitor == _scene.MonitorDeviceName;

        if (same) return true;

        StopCapture();

        var monitors = Native.EnumerateMonitors();
        var monitor = monitors.FirstOrDefault(m => m.DeviceName == _scene.MonitorDeviceName)
                   ?? monitors.FirstOrDefault(m => m.IsPrimary)
                   ?? monitors.FirstOrDefault();

        if (monitor == null) return false;

        var mode = _scene.CaptureSource;
        _capture = new HybridBackend
        {
            ReduceWidth = ReduceWidth,
            MinReduceIntervalMs = 1000.0 / Math.Clamp(_scene.MaxFps, 1, 120),
            UseDda = mode is CaptureSource.Auto or CaptureSource.DdaOnly,
            UseWgc = mode is CaptureSource.Auto or CaptureSource.WgcOnly,
            UseGdi = mode is CaptureSource.Auto or CaptureSource.GdiOnly
        };

        _capture.Start(monitor);
        _captureVersion = 0;

        _captureMode = mode;
        _captureMonitor = _scene.MonitorDeviceName;

        ProbeLog.Log("захват", $"свой захват {mode}, экран {monitor.DeviceName} {monitor.Width}x{monitor.Height}");
        return true;
    }

    void StopCapture()
    {
        if (_capture == null) return;

        _capture.Stop();
        _capture.Dispose();
        _capture = null;
        _captureMode = CaptureSource.FromRimlight;
    }

    /// <summary>Pulls one frame off the bus; false means there is nothing to paint this tick.</summary>
    bool TakeSharedFrame(int periodMs)
    {
        if (!_bus.TryAttach())
        {
            Status = "ожидание кадров: " + _bus.Status;
            SourceInfo = "нет источника";
            Thread.Sleep(200);
            return false;
        }

        if (!_bus.TryRead(ref _image, out var info))
        {
            // No new frame is normal - a still screen produces none at all. The LEDs keep
            // whatever they had rather than blinking off.
            Thread.Sleep(periodMs);
            return false;
        }

        FramesReceived++;
        LastFrameAgeMs = info.AgeMs;
        BusMonitorDeviceName = info.MonitorDeviceName;
        SourceInfo = $"Rimlight, {info.Width}×{info.Height}, экран {info.MonitorDeviceName}";

        ZoneSampler.Sample(_image, info.Width, info.Height, info.Stride, _zones, _sampled);
        return true;
    }

    /// <summary>
    /// Paints straight from the movable patch instead of the screen.
    ///
    /// Deliberately bypasses the zone sampling: the patch is a shape on the scene, so the
    /// only question is whether an LED stands inside it. Everything after this - colour,
    /// gamma, smoothing - is the path the real picture takes, so what the test shows is
    /// what real content will do.
    /// </summary>
    void FillFromTest(TestPatch patch)
    {
        double half = patch.SizeMm / 2;

        for (int i = 0; i < _world.Length && i * 3 + 2 < _sampled.Length; i++)
        {
            double dx = _world[i].X - patch.CenterX;
            double dy = _world[i].Y - patch.CenterY;

            bool inside = patch.Circle
                ? dx * dx + dy * dy <= half * half
                : Math.Abs(dx) <= half && Math.Abs(dy) <= half;

            int o = i * 3;
            _sampled[o] = inside ? patch.R : (byte)0;
            _sampled[o + 1] = inside ? patch.G : (byte)0;
            _sampled[o + 2] = inside ? patch.B : (byte)0;
        }
    }

    ColorSettings ColourSettings() => new()
    {
        MaxBrightness = _scene.Brightness,
        MinLuma = _scene.MinLuma,
        Saturation = _scene.Saturation,
        Gamma = _scene.Gamma,
        TemperatureK = _scene.TemperatureK,
        GainR = _scene.GainR,
        GainG = _scene.GainG,
        GainB = _scene.GainB,
        SmoothingRise = _scene.SmoothingRise,
        SmoothingFall = _scene.SmoothingFall,
        Dithering = false        // дизеринг разносит ошибку вдоль ленты; здесь диоды не в ряд
    };

    /// <summary>
    /// Works out which patch of screen each LED watches, and resolves it to hardware.
    ///
    /// An LED standing beside the monitor is outside the picture entirely, so its patch is
    /// clamped to the nearest edge - which is exactly what "a continuation of the screen"
    /// means: a fan to the right of the panel echoes the right edge, at its own height.
    /// </summary>
    void Rebuild()
    {
        var zones = new List<LedZone>();
        var targets = new List<Target>();
        var world = new List<Point>();

        _deviceDivider.Clear();

        var m = _scene.Monitor;
        double left = m.CenterX - m.Width / 2;
        double top = m.CenterY - m.Height / 2;
        double w = Math.Max(1, m.Width), h = Math.Max(1, m.Height);

        double ru = _scene.SampleRadiusMm / w;
        double rv = _scene.SampleRadiusMm / h;

        // Snapshot under the same lock the UI takes: adding or removing a fixture while
        // this loop walks the list would throw right out of the paint thread.
        Fixture[] fixtures;
        lock (_scene.Fixtures) fixtures = _scene.Fixtures.ToArray();

        foreach (var f in fixtures)
        {
            if (!f.Enabled || f.Binding.LedCount <= 0) continue;
            if (!_hub.TryResolve(f.Binding, out int device, out int firstGlobal, out int available)) continue;

            // A device shared by several fixtures runs at the fastest rate any of them asks
            // for; painting only part of a device would leave the rest of it black.
            int divider = Math.Max(1, f.UpdateEvery);
            _deviceDivider[device] = _deviceDivider.TryGetValue(device, out int existing)
                ? Math.Min(existing, divider)
                : divider;

            var positions = LedGeometry.World(f);
            int count = Math.Min(available, positions.Length);

            for (int i = 0; i < count; i++)
            {
                double u = (positions[i].X - left) / w;
                double v = (positions[i].Y - top) / h;

                // outside the panel the nearest edge is what this LED can honestly show
                u = Math.Clamp(u, 0, 1);
                v = Math.Clamp(v, 0, 1);

                zones.Add(new LedZone(Math.Clamp(u - ru, 0, 1), Math.Clamp(v - rv, 0, 1),
                                      Math.Clamp(u + ru, 0, 1), Math.Clamp(v + rv, 0, 1),
                                      Side.Bottom));
                targets.Add(new Target(device, firstGlobal + i));
                world.Add(positions[i]);
            }
        }

        _zones = zones.ToArray();
        _targets = targets.ToArray();
        _world = world.ToArray();
        _sampled = new byte[_zones.Length * 3];
        _output = new byte[_zones.Length * 3];
        _pipeline.Reset(_zones.Length);
        _resolvedGeneration = _hub.Generation;
        _blankUnused = true;
    }

    public void Dispose()
    {
        Stop();
        StopCapture();
        _bus.Dispose();
    }
}
