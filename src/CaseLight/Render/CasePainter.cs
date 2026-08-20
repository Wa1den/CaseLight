using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Ambilight.Frames;
using Ambilight.Leds;
using CaseLight.Model;
using CaseLight.Rgb;

namespace CaseLight.Render;

/// <summary>
/// Drives the case from the screen.
///
/// The chain is deliberately the same one Ambilight already uses for the strip behind the
/// monitor - the frame arrives over the shared bus, <see cref="ZoneSampler"/> averages a
/// patch per LED and <see cref="ColorPipeline"/> does the colour work in linear light. The
/// only part specific to the case is deciding which patch of screen each LED looks at, and
/// that falls straight out of where the LED physically stands relative to the monitor.
/// </summary>
public sealed class CasePainter : IDisposable
{
    /// <summary>One LED, already resolved to hardware so the loop does no searching.</summary>
    readonly record struct Target(int DeviceIndex, int GlobalLed);

    readonly RgbHub _hub;
    readonly FrameSubscriber _bus = new();
    readonly ColorPipeline _pipeline = new();

    Thread? _thread;
    volatile bool _running;
    volatile bool _rebuild = true;

    Scene _scene;

    // одна запись на диод, в том же порядке, что и зоны выборки
    LedZone[] _zones = Array.Empty<LedZone>();
    Target[] _targets = Array.Empty<Target>();

    /// <summary>How often each device is written, in frames. Slow buses get a larger number.</summary>
    readonly Dictionary<int, int> _deviceDivider = new();
    readonly HashSet<int> _dueNow = new();

    int _resolvedGeneration = -1;
    long _frameNo;

    byte[] _image = Array.Empty<byte>();
    byte[] _sampled = Array.Empty<byte>();
    byte[] _output = Array.Empty<byte>();

    public string Status { get; private set; } = "остановлено";
    public bool IsRunning => _running;
    public long FramesPainted { get; private set; }
    public double Fps { get; private set; }

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

        _hub.Blackout();
        Status = "остановлено";
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

            // A reconnect renumbers the controllers, so resolved indices have to be redone
            // before they address the wrong hardware.
            if (_rebuild || _hub.Generation != _resolvedGeneration)
            {
                _rebuild = false;
                Rebuild();
            }

            if (!_bus.TryAttach())
            {
                Status = "жду кадры: " + _bus.Status;
                Thread.Sleep(200);
                continue;
            }

            if (!_bus.TryRead(ref _image, out var info))
            {
                // No new frame is normal - a still screen produces none at all. The LEDs
                // keep whatever they had rather than blinking off.
                Thread.Sleep(periodMs);
                continue;
            }

            if (_targets.Length == 0)
            {
                Status = "не к чему привязываться: нет включённых фигур с диодами";
                Thread.Sleep(300);
                continue;
            }

            if (!_hub.Connect())
            {
                Status = _hub.Status;
                Thread.Sleep(500);
                continue;
            }

            ZoneSampler.Sample(_image, info.Width, info.Height, info.Stride, _zones, _sampled);

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
                Status = "OpenRGB отвалился, переподключаюсь";
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
                Status = $"идёт раскраска, {Fps:F0} к/с, кадр {info.Width}×{info.Height}";
            }

            Thread.Sleep(periodMs);
        }
    }

    ColorSettings ColourSettings() => new()
    {
        MaxBrightness = _scene.Brightness,
        MinLuma = _scene.MinLuma,
        Saturation = _scene.Saturation,
        Gamma = _scene.Gamma,
        TemperatureK = _scene.TemperatureK,
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

            var world = LedGeometry.World(f);
            int count = Math.Min(available, world.Length);

            for (int i = 0; i < count; i++)
            {
                double u = (world[i].X - left) / w;
                double v = (world[i].Y - top) / h;

                // outside the panel the nearest edge is what this LED can honestly show
                u = Math.Clamp(u, 0, 1);
                v = Math.Clamp(v, 0, 1);

                zones.Add(new LedZone(Math.Clamp(u - ru, 0, 1), Math.Clamp(v - rv, 0, 1),
                                      Math.Clamp(u + ru, 0, 1), Math.Clamp(v + rv, 0, 1),
                                      Side.Bottom));
                targets.Add(new Target(device, firstGlobal + i));
            }
        }

        _zones = zones.ToArray();
        _targets = targets.ToArray();
        _sampled = new byte[_zones.Length * 3];
        _output = new byte[_zones.Length * 3];
        _pipeline.Reset(_zones.Length);
        _resolvedGeneration = _hub.Generation;
    }

    public void Dispose()
    {
        Stop();
        _bus.Dispose();
    }
}
