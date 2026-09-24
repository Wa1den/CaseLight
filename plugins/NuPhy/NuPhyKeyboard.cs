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

    const byte CmdLedSyncDownload = 0xDD;
    const int DataMax = 56;

    readonly Model _model;
    readonly string _path;
    readonly Thread _thread;
    readonly AutoResetEvent _wake = new(false);
    readonly object _gate = new();

    /// <summary>The frame to show, in the keyboard's own layout: key LEDs, then side LEDs.</summary>
    readonly byte[] _frame;
    bool _dirty, _driving;
    volatile bool _stop;

    public NuPhyKeyboard(Model model, string path)
    {
        _model = model;
        _path = path;
        _frame = new byte[3 * (model.KeyLeds + model.SideLeds)];
        Zones = new[] { new LightZone("Keys", model.KeyLeds, model.Layout()) };

        _thread = new Thread(Run) { IsBackground = true, Name = "NuPhy " + model.Name };
        _thread.Start();
    }

    public string Name => _model.Name;
    public string Location => _path;
    public IReadOnlyList<LightZone> Zones { get; }

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
        long lastSent = 0;

        while (!_stop)
        {
            _wake.WaitOne(KeepAliveMs);
            if (_stop) break;

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

            if (Send(channel, copy)) lastSent = Environment.TickCount64;
            else
            {
                channel.Dispose();
                channel = null;
            }
        }

        channel?.Dispose();
    }

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
