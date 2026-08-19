using System;
using System.Collections.Generic;
using System.Threading;
using System.Windows;
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
    readonly RgbHub _hub;
    readonly FrameSubscriber _bus = new();
    readonly ColorPipeline _pipeline = new();

    Thread? _thread;
    volatile bool _running;
    volatile bool _rebuild = true;

    Scene _scene;

    // одна запись на диод, в том же порядке, что и зоны выборки
    LedZone[] _zones = Array.Empty<LedZone>();
    (Binding binding, int led)[] _targets = Array.Empty<(Binding, int)>();

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
        var clock = System.Diagnostics.Stopwatch.StartNew();
        double lastMs = 0;
        int framesThisSecond = 0;
        long fpsWindow = Environment.TickCount64;

        while (_running)
        {
            int periodMs = (int)Math.Round(1000.0 / Math.Clamp(_scene.MaxFps, 1, 120));

            if (_rebuild) { _rebuild = false; Rebuild(); }

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
                Status = "не к чему привязываться: нет фигур с диодами";
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

            _hub.BeginFrame();
            for (int i = 0; i < _targets.Length; i++)
            {
                int o = i * 3;
                _hub.Contribute(_targets[i].binding, _targets[i].led, _output[o], _output[o + 1], _output[o + 2]);
            }

            if (!_hub.EndFrame())
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
                Status = $"идёт раскраска, {Fps:F0} к/с, кадр {info.Width}x{info.Height} от «{info.MonitorDeviceName}»";
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
    /// Works out which patch of screen each LED watches.
    ///
    /// An LED standing beside the monitor is outside the picture entirely, so its patch is
    /// clamped to the nearest edge - which is exactly what "a continuation of the screen"
    /// means: a fan to the right of the panel echoes the right edge, at its own height.
    /// </summary>
    void Rebuild()
    {
        var zones = new List<LedZone>();
        var targets = new List<(Binding, int)>();

        var m = _scene.Monitor;
        double left = m.CenterX - m.Width / 2;
        double top = m.CenterY - m.Height / 2;
        double w = Math.Max(1, m.Width), h = Math.Max(1, m.Height);

        double ru = _scene.SampleRadiusMm / w;
        double rv = _scene.SampleRadiusMm / h;

        foreach (var f in _scene.Fixtures)
        {
            if (f.Binding.LedCount <= 0) continue;

            var world = LedGeometry.World(f);
            for (int i = 0; i < world.Length; i++)
            {
                double u = (world[i].X - left) / w;
                double v = (world[i].Y - top) / h;

                // outside the panel the nearest edge is what this LED can honestly show
                u = Math.Clamp(u, 0, 1);
                v = Math.Clamp(v, 0, 1);

                zones.Add(new LedZone(Math.Clamp(u - ru, 0, 1), Math.Clamp(v - rv, 0, 1),
                                      Math.Clamp(u + ru, 0, 1), Math.Clamp(v + rv, 0, 1),
                                      Side.Bottom));
                targets.Add((f.Binding, i));
            }
        }

        _zones = zones.ToArray();
        _targets = targets.ToArray();
        _sampled = new byte[_zones.Length * 3];
        _output = new byte[_zones.Length * 3];
        _pipeline.Reset(_zones.Length);
    }

    public void Dispose()
    {
        Stop();
        _bus.Dispose();
    }
}
