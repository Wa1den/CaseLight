using System;
using System.Collections.Generic;
using System.Threading;
using CaseLight.Plugins;

namespace CaseLight.NuPhy;

/// <summary>
/// One keyboard. Frames go out on a thread of its own, so <see cref="Write"/> only copies.
///
/// The firmware shows a frame for 1.6 s and then goes back to its own effect, so the last
/// frame is sent again well within that. A frame of 96 LEDs is six packets and takes about
/// 12 ms with the answers, measured on an Air75 HE at 83 frames a second.
/// </summary>
sealed class NuPhyKeyboard : ILightDevice, IDisposable
{
    /// <summary>Resend interval; the firmware keeps a frame for 1.6 s.</summary>
    const int KeepAliveMs = 500;

    /// <summary>How long to wait for the answer to one packet.</summary>
    const int ReplyTimeoutMs = 100;

    const byte CmdLedSyncDownload = 0xDD, CmdLedSyncUpload = 0xDE;
    const int DataMax = 56;

    /// <summary>How often the frame is read back to see whether the keyboard shows it.</summary>
    const int CheckMs = 2000;

    /// <summary>
    /// Read-backs in a row that must come out black while colour was sent before the frame
    /// counts as not shown. One is not enough: a dark scene sends black on its own, and the
    /// buffer read can still hold the frame before the one just sent.
    /// </summary>
    const int MissesForProblem = 3;

    /// <summary>
    /// How long frames stop after Caps Lock goes off.
    ///
    /// The left strip is the Caps Lock indicator. The firmware lights it at once when Caps
    /// Lock goes on, but clears it only with the next step of its own effect, and the effect
    /// stands still for as long as frames keep coming: the strip stayed lit after Caps Lock
    /// was off. Nothing tried ends that state sooner than its timeout - QuickCommEnd, an
    /// empty frame, QuickCommStart and End - the strip moved again after 1.52 to 1.57 s in
    /// every case. The keys hold the last frame for most of the pause and show their own
    /// effect only for the remainder.
    /// </summary>
    const int IndicatorPauseMs = 1700;

    /// <summary>How often the thread looks at Caps Lock while no frame wakes it.</summary>
    const int PollMs = 100;

    readonly Model _model;
    readonly string _path;
    readonly Action _problemChanged;
    volatile string _problem = "";
    readonly Thread _thread;
    readonly AutoResetEvent _wake = new(false);
    readonly object _gate = new();

    /// <summary>The frame to show, in the keyboard's own layout: key LEDs, then side LEDs.</summary>
    readonly byte[] _frame;
    bool _dirty, _driving;
    volatile bool _stop;

    public NuPhyKeyboard(Model model, string path, Action problemChanged)
    {
        _model = model;
        _path = path;
        _problemChanged = problemChanged;
        _frame = new byte[3 * (model.KeyLeds + model.SideLeds)];
        Zones = new[] { new LightZone("Keys", model.KeyLeds, model.Layout()) };

        _thread = new Thread(Run) { IsBackground = true, Name = "NuPhy " + model.Name };
        _thread.Start();
    }

    public string Name => _model.Name;
    public string Location => _path;
    public IReadOnlyList<LightZone> Zones { get; }

    public string Problem => _problem;

    public string Path => _path;

    public void Write(ReadOnlySpan<byte> rgb)
    {
        lock (_gate)
        {
            int keys = Math.Min(rgb.Length, 3 * _model.KeyLeds);
            rgb[..keys].CopyTo(_frame);
            _dirty = true;
            _driving = true;
        }

        _wake.Set();
    }

    public void Release()
    {
        // отправка прекращается, через 1,6 с клавиатура сама вернётся к своему эффекту
        lock (_gate) _driving = false;
    }

    void Run()
    {
        HidChannel? channel = null;
        var copy = new byte[_frame.Length];
        long lastSent = 0, lastCheck = 0, pauseUntil = 0;
        int misses = 0;
        bool capsWasOn = CapsLockOn();

        while (!_stop)
        {
            _wake.WaitOne(PollMs);
            if (_stop) break;

            bool caps = CapsLockOn();
            if (capsWasOn && !caps) pauseUntil = Environment.TickCount64 + IndicatorPauseMs;
            capsWasOn = caps;

            // кадр остаётся несданным и уйдёт первым после паузы
            if (Environment.TickCount64 < pauseUntil) continue;

            lock (_gate)
            {
                if (!_driving) continue;

                bool due = _dirty || Environment.TickCount64 - lastSent >= KeepAliveMs;
                if (!due) continue;

                _frame.CopyTo(copy, 0);
                _dirty = false;
            }

            if (channel == null || channel.Closed)
            {
                channel?.Dispose();
                channel = HidChannel.Open(_path);

                // устройство ушло или ещё не вернулось после сна: кадр повторится на следующем такте
                if (channel == null) { Thread.Sleep(KeepAliveMs); continue; }
            }

            if (!Send(channel, copy))
            {
                channel.Dispose();
                channel = null;
                continue;
            }

            lastSent = Environment.TickCount64;
            if (lastSent - lastCheck < CheckMs) continue;
            lastCheck = lastSent;

            switch (Shown(channel, copy))
            {
                case true: misses = 0; SetProblem(""); break;
                case false: if (++misses >= MissesForProblem) SetProblem(LightingOff()); break;
            }
        }

        channel?.Dispose();
    }

    /// <summary>
    /// Whether the keyboard shows the frame, judged by the first packet of it read back: the
    /// firmware with key lighting switched off in NuPhyIO answers every packet of a frame
    /// and keeps its colour buffer black, which is how it was found on an Air75 HE with
    /// firmware 1.10. Null when it cannot be told: black was sent, or the read failed.
    /// </summary>
    static bool? Shown(HidChannel channel, byte[] sent)
    {
        if (!sent.AsSpan(0, DataMax).ContainsAnyExcept((byte)0)) return null;

        var reply = channel.Transact(Packet(CmdLedSyncUpload, 0, new byte[DataMax]), ReplyTimeoutMs);
        if (reply == null || reply.Length < 8 + DataMax) return null;

        return reply.AsSpan(8, DataMax).ContainsAnyExcept((byte)0);
    }

    static bool CapsLockOn() => (GetKeyState(VkCapital) & 1) != 0;

    const int VkCapital = 0x14;

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    static extern short GetKeyState(int virtualKey);

    void SetProblem(string problem)
    {
        if (_problem == problem) return;
        _problem = problem;
        _problemChanged();
    }

    // Буфер отдаёт цвета уже умноженными на яркость подсветки (200 читается как 105), так что
    // нулевая яркость выглядит так же, как выключенная подсветка.
    static string LightingOff() => PluginApi.Language == "ru"
        ? "Кадры на клавиатуре не видны: в NuPhyIO подсветка клавиш выключена или её яркость на нуле."
        : "The frames do not show on the keyboard: key lighting is switched off in NuPhyIO or its brightness is at zero.";

    static bool Send(HidChannel channel, byte[] frame)
    {
        for (int offset = 0; offset < frame.Length; offset += DataMax)
        {
            int length = Math.Min(DataMax, frame.Length - offset);
            var packet = Packet(CmdLedSyncDownload, offset, frame.AsSpan(offset, length));
            if (channel.Transact(packet, ReplyTimeoutMs) == null) return false;
        }

        return true;
    }

    /// <summary>
    /// A command packet: 55, command, 0, checksum, length, offset low and high, 0, data. The
    /// checksum is the low byte of the sum of bytes 4..63.
    /// </summary>
    static byte[] Packet(byte command, int offset, ReadOnlySpan<byte> data)
    {
        var p = new byte[64];
        p[0] = 0x55;
        p[1] = command;
        p[4] = (byte)data.Length;
        p[5] = (byte)offset;
        p[6] = (byte)(offset >> 8);
        data.CopyTo(p.AsSpan(8));

        int sum = 0;
        for (int i = 4; i < p.Length; i++) sum += p[i];
        p[3] = (byte)sum;

        return p;
    }

    public void Dispose()
    {
        Release();
        _stop = true;
        _wake.Set();
        _thread.Join(1000);
        _wake.Dispose();
    }
}
