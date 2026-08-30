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
    /// <summary>
    /// The capture throttle is set a little under the paint period.
    ///
    /// Exactly one period looks right and is not: capture and painting run on unrelated
    /// clocks, so a frame arriving a hair early is thrown away and the picture waits a
    /// whole extra period for the next one. Rimlight measured that beat and carries the
    /// same slack; here it did not show up in a measurement with one capturer running, so
    /// this is insurance rather than a fix for anything seen. A few percent more
    /// reductions is what it costs.
    /// </summary>
    const double ReduceSlack = 0.8;

    const int ReduceWidth = 256;

    /// <summary>
    /// The shortest period the paint loop keeps, in milliseconds. This is what removing the
    /// frame ceiling comes to: a period of zero would leave the thread spinning on a source
    /// that hands over frames at its own pace, and nothing here is written 250 times a
    /// second anyway - the slow buses have a divider of their own besides.
    /// </summary>
    const int MinPeriodMs = 4;

    readonly RgbHub _hub;
    readonly FrameSubscriber _bus = new();
    readonly ColorPipeline _pipeline = new();
    readonly CropDetector _crop = new();

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

    /// <summary>
    /// The zones actually read from the frame: the same ones moved inside the picture while
    /// the crop is in use, a straight copy otherwise. Kept apart from <see cref="_zones"/>
    /// so the layout itself never moves - the canvas has to keep showing where the LEDs
    /// are, whatever the black bars do.
    /// </summary>
    LedZone[] _sampleZones = Array.Empty<LedZone>();
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

    /// <summary>
    /// Held around the colour pass and the write to the devices, so a blackout taken on
    /// another thread cannot land in the middle of one.
    ///
    /// The paint thread checks the pause flag at the top of its cycle and reaches the
    /// devices some milliseconds later; it was in that gap that a colour frame went out
    /// after the blackout and left the case lit through a lock or a sleep.
    /// </summary>
    readonly object _sendGate = new();

    /// <summary>
    /// How often the same picture is written again while nothing new is being painted.
    ///
    /// Silence is not enough to keep the case as it is: the OpenRGB server dies and is
    /// restarted often enough, and its devices come back in the mode they ship with.
    /// </summary>
    const int KeepAliveMs = 2000;

    /// <summary>When the devices were last written, for the repeat above.</summary>
    long _lastWriteTicks;

    /// <summary>Holds the last picture instead of following the screen - see <see cref="Freeze"/>.</summary>
    volatile bool _frozen;

    /// <summary>Set when the frame source went away: the case stays dark until frames return.</summary>
    volatile bool _sourceLost;

    /// <summary>When the bus last handed over a frame, and when its publisher was last asked about.</summary>
    long _lastBusFrameTicks;
    long _lastPublisherCheck;

    /// <summary>How long the bus may stay quiet before the publisher is asked whether it is there.</summary>
    const int BusSilenceMs = 2000;

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

    /// <summary>What the crop detector sees right now, for the line under its settings.</summary>
    public CropRect Crop => _crop.Rect;

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

    /// <param name="blackout">
    /// False leaves the case as it is. The stop button darkens it; quitting with
    /// «гасить при выходе» unticked must not.
    /// </param>
    public void Stop(bool blackout = true)
    {
        if (!_running) return;

        _running = false;
        _thread?.Join(1500);
        _thread = null;

        StopCapture();

        // The thread is gone, so nothing else is writing; the gate is taken anyway because
        // Pause can still arrive from a power event at this very moment.
        if (blackout) lock (_sendGate) SendBlackLocked();

        _idle = true;
    }

    /// <summary>Darkens the case and stops writing - for lock, sleep and display off.</summary>
    public void Pause(string reason)
    {
        lock (_sendGate)
        {
            bool already = _paused;

            // The reason is written down even when the pause is already on: the display
            // goes off first and the session locks after it, and the status line used to
            // keep naming the display.
            _pauseReason = reason;
            _paused = true;

            if (!already) SendBlackLocked();
        }

        // Записывается и при смене причины: экран гаснет первым, сессия блокируется следом,
        // и по журналу видно, что именно держит подсветку тёмной.
        ProbeLog.Log(Loc.P("раскраска", "painting"), Loc.P("пауза: ", "paused: ") + reason);
    }

    /// <summary>Writes black and remembers when, so the pause can keep it up.</summary>
    void SendBlackLocked()
    {
        _hub.Blackout();
        _lastWriteTicks = Environment.TickCount64;
    }

    /// <summary>
    /// Holds the last picture instead of following the screen.
    ///
    /// For a display that is off while the user has asked for the light to stay on: a
    /// blanked screen keeps handing over frames, and what is in them is black.
    /// </summary>
    public void Freeze(bool on)
    {
        if (_frozen == on) return;

        lock (_sendGate)
        {
            _frozen = on;
            if (!on) _resetPipeline = true;    // не разгораться из устаревших цветов
        }

        ProbeLog.Log(Loc.P("раскраска", "painting"),
                     on ? Loc.P("кадр удержан", "picture held") : Loc.P("кадр отпущен", "picture released"));
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
        lock (_sendGate)
        {
            if (!_paused && delayMs <= 0) return;

            _paused = false;
            _pauseReason = "";
            _holdUntilTicks = Environment.TickCount64 + Math.Max(0, delayMs);
            _resetPipeline = true;            // не разгораться из устаревших цветов
        }

        ProbeLog.Log(Loc.P("раскраска", "painting"), Loc.P("продолжение", "resumed"));
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

    /// <summary>
    /// Ритм всего цикла, включая паузы внутри чтения кадра: у Thread.Sleep шаг 15,6 мс,
    /// и пауза на 16 мс превращается в 31.
    /// </summary>
    PrecisionTimer? _pacer;

    void PaintLoop()
    {
        using var pacer = new PrecisionTimer();
        _pacer = pacer;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        double lastMs = 0;
        int framesThisSecond = 0;
        long fpsWindow = Environment.TickCount64;

        while (_running)
        {
            int periodMs = PeriodMs(_scene.MaxFps);

            // Switching the detector off has to reach the sampling even on a still screen,
            // where no further frame is going to arrive to carry the change.
            if (!_scene.AdaptiveCrop && !_crop.Rect.IsFull)
            {
                _crop.Reset();
                RemapZones();
            }

            if (_paused)
            {
                Status = Loc.P("пауза: ", "pause: ") + _pauseReason;
                RepeatBlack();
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

            // Экран погашен, а гасить подсветку не просили. Новые кадры не разбираются,
            // потому что в них чернота, но последний повторяется: иначе перезапуск сервера
            // вернул бы устройствам заводской режим.
            if (_frozen)
            {
                Status = Loc.P("кадр удержан: экран выключен", "picture held: display off");
                RepeatLast();
                Thread.Sleep(200);
                continue;
            }

            // Read once at the top of the tick and used for both filters that count real
            // time: the smoothing and the hold behind the crop.
            double now = clock.Elapsed.TotalMilliseconds;

            var test = _test;
            if (test != null)
            {
                FillFromTest(test);
                SourceInfo = Loc.P("тестовое пятно", "test patch");
            }
            else if (_scene.CaptureSource == CaptureSource.FromRimlight)
            {
                StopCapture();
                if (!TakeSharedFrame(periodMs, now)) continue;
            }
            else if (!TakeOwnFrame(periodMs, now))
            {
                continue;
            }

            double dt = now - lastMs;
            lastMs = now;

            _frameNo++;

            _dueNow.Clear();
            foreach (var (device, divider) in _deviceDivider)
                if (divider <= 1 || _frameNo % divider == 0)
                    _dueNow.Add(device);

            bool linkLost = false, nothingToWrite;

            // Один замок на цвет и на запись: пауза приходит из чужого потока, и без него
            // её чёрный кадр ложился в середину этого, а следом за ним уходил цветной.
            // Такт цикла выжидается уже без замка, чтобы пауза не ждала целый период.
            lock (_sendGate)
            {
                _pipeline.Process(_sampled, _output, ColourSettings(), _zones.Length, dt <= 0 ? periodMs : dt);
                NeutraliseShadows(_scene.ShadowNeutral);

                nothingToWrite = _paused || _frozen || _dueNow.Count == 0;
                if (!nothingToWrite) linkLost = !WriteFrameLocked(_dueNow);
            }

            if (nothingToWrite) { pacer.Wait(periodMs); continue; }

            if (linkLost)
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

            pacer.Wait(periodMs);
        }
    }

    /// <summary>
    /// Puts <see cref="_output"/> on the devices named. The caller holds
    /// <see cref="_sendGate"/>.
    /// </summary>
    bool WriteFrameLocked(IReadOnlyCollection<int> devices)
    {
        _hub.BeginFrame();

        for (int i = 0; i < _targets.Length; i++)
        {
            var t = _targets[i];
            if (!devices.Contains(t.DeviceIndex)) continue;

            int o = i * 3;
            _hub.ContributeAt(t.DeviceIndex, t.GlobalLed, _output[o], _output[o + 1], _output[o + 2]);
        }

        bool sent = _hub.EndFrame(devices);
        if (sent) _lastWriteTicks = Environment.TickCount64;
        return sent;
    }

    /// <summary>Keeps the case dark while the painting is paused or its source is gone.</summary>
    void RepeatBlack()
    {
        lock (_sendGate)
            if (Environment.TickCount64 - _lastWriteTicks >= KeepAliveMs)
                SendBlackLocked();
    }

    /// <summary>Writes the held picture again, so a restarted server cannot show its own.</summary>
    void RepeatLast()
    {
        lock (_sendGate)
        {
            if (_output.Length == 0 || Environment.TickCount64 - _lastWriteTicks < KeepAliveMs) return;

            // Все устройства разом: делители расставляют очередь между кадрами, а здесь
            // кадр один и тот же.
            WriteFrameLocked(_deviceDivider.Keys);
        }
    }

    /// <summary>
    /// Darkens the case because there is nothing to paint from, and keeps it dark.
    ///
    /// The lighting used to freeze on the last picture instead: closing Rimlight leaves the
    /// shared mapping behind, and the case went on showing the colours of whatever had been
    /// on screen at that moment.
    /// </summary>
    void SourceGone(string status)
    {
        Status = status;

        lock (_sendGate)
        {
            if (!_sourceLost)
            {
                _sourceLost = true;
                SendBlackLocked();
                ProbeLog.Log(Loc.P("раскраска", "painting"),
                             Loc.P("источник кадров пропал, подсветка погашена",
                                   "the frame source is gone, the lighting is off"));
                return;
            }
        }

        RepeatBlack();
    }

    /// <summary>Called on every frame that arrives, to let the painting come back.</summary>
    void SourceBack()
    {
        if (!_sourceLost) return;

        _sourceLost = false;
        _resetPipeline = true;    // не разгораться из цветов, которые были до пропажи

        ProbeLog.Log(Loc.P("раскраска", "painting"),
                     Loc.P("источник кадров вернулся", "the frame source is back"));
    }

    /// <summary>Пауза заданной длины, точная в отличие от Thread.Sleep.</summary>
    void Pace(int ms)
    {
        if (_pacer != null) _pacer.Wait(ms);
        else Thread.Sleep(ms);
    }

    /// <summary>
    /// Waits for the next frame of own capture, or for the period to run out.
    ///
    /// Both handles go into one WaitAny: the timeout argument of a wait rounds up to the
    /// system tick the same way Thread.Sleep does, so the period is kept by the precision
    /// timer and the frame signal is simply the second thing worth waking on.
    /// </summary>
    void WaitFrame(int ms)
    {
        if (_pacer?.Handle is not WaitHandle tick || _capture == null) { Thread.Sleep(ms); return; }

        _pacer.Arm(ms);
        WaitHandle.WaitAny(new[] { tick, _capture.FrameSignal });
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
    bool TakeOwnFrame(int periodMs, double nowMs)
    {
        if (!EnsureCapture())
        {
            Status = Loc.P("экран для захвата не найден", "the screen to capture was not found");
            SourceInfo = Loc.P("нет источника", "no source");
            Thread.Sleep(500);
            return false;
        }

        _capture!.MinReduceIntervalMs = periodMs * ReduceSlack;

        // Cleared before the check, not after: a frame published in between still sets the
        // handle, so the wait below returns at once instead of missing it for a whole tick.
        _capture.FrameSignal.Reset();

        if (!_capture.TryGetImage(ref _image, ref _captureVersion, out int w, out int h, out int stride) || w <= 0 || h <= 0)
        {
            // A still screen produces no frames at all; keep what the LEDs already show.
            // Waiting on the frame rather than sleeping a fixed slice: a wait with a timeout
            // rounds up to 15.6 ms exactly like Thread.Sleep, so the timeout goes through the
            // precision timer and the frame signal is waited on beside it.
            WaitFrame(periodMs);
            return false;
        }

        FramesReceived++;
        LastFrameAgeMs = 0;
        SourceInfo = string.Format(Loc.P("свой захват ({0}), {1}×{2}, {3}", "own capture ({0}), {1}×{2}, {3}"),
                                   _scene.CaptureSource, w, h, _captureLabel);

        SampleFrame(w, h, stride, periodMs, nowMs);
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
            MinReduceIntervalMs = PeriodMs(_scene.MaxFps) * ReduceSlack,
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
    bool TakeSharedFrame(int periodMs, double nowMs)
    {
        if (!_bus.TryAttach())
        {
            SourceInfo = Loc.P("нет источника", "no source");

            // Шины нет вовсе: показывать нечего, и держать на корпусе последний кадр
            // означало бы светить тем, что было на экране когда-то давно.
            if (Environment.TickCount64 - _lastBusFrameTicks > BusSilenceMs)
                SourceGone(Loc.P("Rimlight не отдаёт кадры, подсветка погашена.",
                                 "Rimlight is not sending frames, the lighting is off."));
            else
                Status = Loc.P("ожидание кадров: ", "waiting for frames: ") + _bus.Status;

            Thread.Sleep(200);
            return false;
        }

        if (!_bus.TryRead(ref _image, out var info))
        {
            // No new frame is normal, because a still screen produces none at all, so
            // silence is not enough to act on. The publisher being gone is another matter:
            // mapping outlives it, and the case would keep the colours of the last picture
            // for as long as it stayed attached.
            if (PublisherGone())
                SourceGone(Loc.P("Rimlight не отдаёт кадры, подсветка погашена.",
                                 "Rimlight is not sending frames, the lighting is off."));

            Pace(periodMs);
            return false;
        }

        _lastBusFrameTicks = Environment.TickCount64;
        SourceBack();

        FramesReceived++;
        LastFrameAgeMs = info.AgeMs;
        BusMonitorDeviceName = info.MonitorDeviceName;
        SourceInfo = $"Rimlight, {info.Width}×{info.Height}, {ScreenChoice.Label(info.MonitorDeviceName)}";

        SampleFrame(info.Width, info.Height, info.Stride, periodMs, nowMs);
        KeepPreview(info.Width, info.Height, info.Stride);
        return true;
    }

    /// <summary>
    /// Whether the publisher behind the bus has gone, asked at most once a second and only
    /// after the bus has been quiet for a while.
    ///
    /// Not free: it comes down to looking up a process by id, and the answer changes about
    /// as often as Rimlight is started and closed.
    /// </summary>
    bool PublisherGone()
    {
        long now = Environment.TickCount64;

        if (now - _lastBusFrameTicks < BusSilenceMs) return false;
        if (now - _lastPublisherCheck < 1000) return _sourceLost;

        _lastPublisherCheck = now;
        return !_bus.PublisherRunning;
    }

    /// <summary>
    /// The paint period the setting comes to, in milliseconds. Zero means no ceiling, and
    /// what is left then is the floor of the loop itself.
    /// </summary>
    static int PeriodMs(int maxFps) => maxFps > 0
        ? Math.Max(MinPeriodMs, (int)Math.Round(1000.0 / Math.Clamp(maxFps, 1, 240)))
        : MinPeriodMs;

    /// <summary>When the detector last measured, so its hold time counts real elapsed time.</summary>
    double _lastCropMs;

    /// <summary>
    /// Measures the black bars, when that is switched on, and reads the zones off the frame.
    ///
    /// The detector runs before the sampling and only on a frame that is actually new: a
    /// still screen hands over no frames at all, and ageing the hold on those ticks would
    /// let a reading that nothing confirmed reach the case.
    /// </summary>
    void SampleFrame(int width, int height, int stride, int periodMs, double nowMs)
    {
        if (_scene.AdaptiveCrop)
        {
            double dt = nowMs - _lastCropMs;
            _lastCropMs = nowMs;

            if (_crop.Update(_image, width, height, stride, _scene.ToCropSettings(),
                             dt <= 0 || dt > 1000 ? periodMs : dt))
                RemapZones();
        }

        ZoneSampler.Sample(_image, width, height, stride, _sampleZones, _sampled);
    }

    /// <summary>Puts the sampling zones onto the picture, leaving the layout where it is.</summary>
    void RemapZones()
    {
        if (_sampleZones.Length != _zones.Length) _sampleZones = new LedZone[_zones.Length];

        if (_crop.Rect.IsFull) Array.Copy(_zones, _sampleZones, _zones.Length);
        else CropMapper.Apply(_zones, _sampleZones, _crop.Rect, _scene.CropStretch);
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
        MinBacklight = _scene.MinBacklight,
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
        RemapZones();
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
