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

using CaseLight.Core.Text;

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
    string _captureLabel = "";

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

    /// <summary>
    /// Asks the paint thread to forget the colours it was smoothing towards.
    ///
    /// A flag rather than the call itself: resuming happens on the interface thread, and
    /// resetting the pipeline from there rewrites the smoothing buffers underneath the
    /// paint loop that is reading them. Sizes change during a rebuild, so the loop could
    /// walk off the end of an array - and an exception there stops the painting for good,
    /// which looks exactly like "it reconnected but never started again".
    /// </summary>
    volatile bool _resetPipeline;

    /// <summary>Reference assignment is atomic, so the UI can swap this in at any moment.</summary>
    volatile TestPatch? _test;

    byte[] _image = Array.Empty<byte>();
    byte[] _sampled = Array.Empty<byte>();
    byte[] _output = Array.Empty<byte>();

    /// <summary>
    /// A copy of the last frame, kept for the canvas.
    ///
    /// A copy rather than the frame itself: the paint thread writes <see cref="_image"/> in
    /// place, and the interface reading it while a new frame lands would show one picture
    /// torn across another. Taken only when someone is looking - see
    /// <see cref="PreviewWanted"/> - so the usual case pays nothing at all.
    /// </summary>
    readonly object _previewLock = new();
    byte[] _preview = Array.Empty<byte>();
    int _previewWidth, _previewHeight, _previewStride;
    long _previewVersion;

    /// <summary>Set by the window while the canvas is showing the screen.</summary>
    public volatile bool PreviewWanted;

    /// <summary>
    /// The loop rewrites this every tick, so a language change catches up on its own while
    /// the painting runs. Stopped, there is nothing to rewrite it, so the idle line is
    /// composed on read instead of being stored.
    /// </summary>
    public string Status
    {
        get => _idle ? Loc.P("остановлено", "stopped") : _status;
        private set { _status = value; _idle = false; }
    }

    string _status = "";
    volatile bool _idle = true;
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

    /// <summary>
    /// Hands out the last frame, if there is a newer one than the caller has seen.
    /// </summary>
    /// <returns>False when nothing has changed, so the canvas keeps what it already drew.</returns>
    public bool TryTakePreview(ref byte[] destination, ref long version,
                               out int width, out int height, out int stride)
    {
        lock (_previewLock)
        {
            width = _previewWidth;
            height = _previewHeight;
            stride = _previewStride;

            if (_previewVersion == version || _preview.Length == 0) return false;

            if (destination.Length < _preview.Length) destination = new byte[_preview.Length];
            Array.Copy(_preview, destination, _preview.Length);

            version = _previewVersion;
            return true;
        }
    }

    /// <summary>Keeps the canvas copy in step with the frame just sampled.</summary>
    void KeepPreview(int width, int height, int stride)
    {
        if (!PreviewWanted) return;

        int size = height * stride;
        if (size <= 0 || size > _image.Length) return;

        lock (_previewLock)
        {
            if (_preview.Length != size) _preview = new byte[size];
            Array.Copy(_image, _preview, size);

            _previewWidth = width;
            _previewHeight = height;
            _previewStride = stride;
            _previewVersion++;
        }
    }

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
        _idle = true;
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
        _resetPipeline = true;            // не разгораться из устаревших цветов
    }

    void Loop()
    {
        try { PaintLoop(); }
        catch (Exception ex)
        {
            // A background thread that throws takes the whole process with it. Losing the
            // painting is bad; losing an unsaved layout with it is worse.
            _running = false;
            Status = Loc.P("раскраска аварийно остановлена: ", "the painting stopped with an error: ") + ex.Message;

            // Written down as well: the window shows the last line only until something
            // else is said, and a painting that died quietly is the hardest kind to explain.
            ProbeLog.Log(Loc.P("раскраска", "painting"), Loc.P("аварийно остановлена: ", "stopped with an error: ") + ex);
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
                Status = Loc.P("пауза: ", "pause: ") + _pauseReason;
                Thread.Sleep(200);
                continue;
            }

            long hold = _holdUntilTicks - Environment.TickCount64;
            if (hold > 0)
            {
                Status = string.Format(Loc.P("ожидание готовности устройств после сна: {0} с",
                                             "waiting for the devices to settle after sleep: {0} s"),
                                       (hold / 1000.0).ToString("F0"));
                Thread.Sleep(Math.Min(500, (int)hold));
                continue;
            }

            if (_resetPipeline)
            {
                _resetPipeline = false;
                _pipeline.Reset(_zones.Length);
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
                Status = Loc.P("нет включённых фигур с диодами", "no enabled fixtures with LEDs");
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
                SourceInfo = Loc.P("тестовое пятно", "test patch");
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
            NeutraliseShadows(_scene.ShadowNeutral);

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
                Status = Loc.P("связь с OpenRGB потеряна, переподключение", "the connection to OpenRGB is lost, reconnecting");
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
                Status = (test != null ? Loc.P("тест размещения, ", "placement test, ") : Loc.P("идёт раскраска, ", "painting, ")) + Rate(Fps);
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

        string word = tail is >= 11 and <= 14 ? Loc.P("кадров", "frames")
                    : last == 1 ? Loc.P("кадр", "frame")
                    : last is >= 2 and <= 4 ? Loc.P("кадра", "frames")
                    : Loc.P("кадров", "frames");

        return string.Format(Loc.P("{0} {1} в секунду", "{0} {1} per second"), n, word);
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
            Status = Loc.P("экран для захвата не найден", "the screen to capture was not found");
            SourceInfo = Loc.P("нет источника", "no source");
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
        SourceInfo = string.Format(Loc.P("свой захват ({0}), {1}×{2}, {3}", "own capture ({0}), {1}×{2}, {3}"),
                                   _scene.CaptureSource, w, h, _captureLabel);

        ZoneSampler.Sample(_image, w, h, stride, _zones, _sampled);
        KeepPreview(w, h, stride);
        return true;
    }

    /// <summary>Creates or re-creates the backend when the method or the screen changes.</summary>
    bool EnsureCapture()
    {
        var wanted = ScreenChoice.Find(_scene.MonitorDeviceName, _scene.MonitorModel);
        if (wanted == null) return false;

        bool same = _capture != null
                 && _captureMode == _scene.CaptureSource
                 && _captureMonitor == wanted.DeviceName;

        if (same) return true;

        StopCapture();

        var monitor = wanted;

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
        _captureMonitor = monitor.DeviceName;
        _captureLabel = monitor.DisplayName;

        ProbeLog.Log(Loc.P("захват", "capture"),
                     string.Format(Loc.P("свой захват {0}, экран {1} {2}x{3}", "own capture {0}, screen {1} {2}x{3}"),
                                   mode, monitor.DeviceName, monitor.Width, monitor.Height));
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
            Status = Loc.P("ожидание кадров: ", "waiting for frames: ") + _bus.Status;
            SourceInfo = Loc.P("нет источника", "no source");
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
        SourceInfo = $"Rimlight, {info.Width}×{info.Height}, {ScreenChoice.Label(info.MonitorDeviceName)}";

        ZoneSampler.Sample(_image, info.Width, info.Height, info.Stride, _zones, _sampled);
        KeepPreview(info.Width, info.Height, info.Stride);
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

    /// <summary>
    /// Takes the colour out of what is nearly black, after the pipeline has had its say.
    ///
    /// Done here rather than inside the pipeline because that one is the shared copy of the
    /// Rimlight code, kept identical on both sides. Working on the finished bytes is enough:
    /// the tint is a proportion between the channels, and pulling them back towards their
    /// own luminance removes it without touching how bright the LED ends up.
    /// </summary>
    void NeutraliseShadows(double knee)
    {
        if (knee <= 0) return;

        double limit = knee * 255.0;

        for (int i = 0; i + 2 < _output.Length; i += 3)
        {
            double r = _output[i], g = _output[i + 1], b = _output[i + 2];

            double y = 0.2126 * r + 0.7152 * g + 0.0722 * b;
            if (y >= limit) continue;

            // 1 at the knee, 0 at black: the darker it is, the greyer it comes out
            double keep = y / limit;

            _output[i] = Fade(y, r, keep);
            _output[i + 1] = Fade(y, g, keep);
            _output[i + 2] = Fade(y, b, keep);
        }
    }

    static byte Fade(double luma, double channel, double keep) =>
        (byte)Math.Clamp(Math.Round(luma + (channel - luma) * keep), 0, 255);

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
