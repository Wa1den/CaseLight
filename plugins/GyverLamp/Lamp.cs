using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using CaseLight.Plugins;

namespace CaseLight.GyverLamp;

/// <summary>
/// One lamp. Frames go out on a thread of its own, so <see cref="Write"/> only copies.
///
/// The firmware goes dark when no frame comes for 2.5 s, so the last frame is sent again
/// every second while the screen stands still. Frames are capped at 60 a second: the lamp
/// takes about 8 ms to put one on the strip, and a 144 Hz screen would only fill the air.
/// </summary>
sealed class Lamp : ILightDevice, IDisposable
{
    const int KeepAliveMs = 1000;
    const int MinFrameMs = 16;

    readonly Socket _socket;
    readonly Thread _thread;
    readonly AutoResetEvent _wake = new(false);
    readonly object _gate = new();
    readonly byte[] _frame;

    IPEndPoint _endpoint;
    LampStatus _status;
    bool _dirty, _driving, _blackPending;
    volatile bool _stop;

    public Lamp(Socket socket, LampStatus status, IPAddress address)
    {
        _socket = socket;
        _status = status;
        _endpoint = new IPEndPoint(address, Ddp.Port);
        _frame = new byte[3 * status.Width * status.Height];
        Zones = new[] { new LightZone("Matrix", status.Width * status.Height, Layout(status.Width, status.Height)) };

        _thread = new Thread(Run) { IsBackground = true, Name = "GyverLamp " + status.Name };
        _thread.Start();
    }

    /// <summary>Bound to by fixtures: the host name, so two lamps are told apart by what the user calls them.</summary>
    public string Name => "GyverLamp " + _status.Name;

    /// <summary>The MAC address: unlike the IP address it stays the same after the router hands out another.</summary>
    public string Location => _status.Mac;

    public IReadOnlyList<LightZone> Zones { get; }

    public string Problem
    {
        get
        {
            var s = _status;
            bool ru = PluginApi.Language == "ru";
            if (!s.On) return ru ? "Лампа выключена." : "The lamp is switched off.";
            if (!s.Live)
                return ru
                    ? "На лампе не выбран эффект «Кадры с компьютера»."
                    : "The effect \"Кадры с компьютера\" (frames from a computer) is not selected on the lamp.";
            return "";
        }
    }

    public LampStatus Status => _status;

    /// <summary>
    /// Takes a newer answer of the lamp. Returns whether the lamp has to be dropped and made
    /// anew: a changed name or matrix size is a different device to the program.
    /// </summary>
    public bool Update(LampStatus status, IPAddress address, out bool problemChanged)
    {
        problemChanged = false;
        if (status.Name != _status.Name || status.Width != _status.Width || status.Height != _status.Height)
            return true;

        string before = Problem;
        lock (_gate)
        {
            _status = status;
            if (!_endpoint.Address.Equals(address)) _endpoint = new IPEndPoint(address, Ddp.Port);
        }

        problemChanged = before != Problem;
        return false;
    }

    /// <summary>
    /// Pixels row by row from the top, left to right in a row, as the firmware expects
    /// them. The matrix is rolled into a cylinder, so the rectangle is its surface unrolled.
    /// </summary>
    static LedRect[] Layout(int width, int height)
    {
        var rects = new LedRect[width * height];
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
                rects[y * width + x] = new LedRect(x, y, 1, 1);
        return rects;
    }

    public void Write(ReadOnlySpan<byte> rgb)
    {
        lock (_gate)
        {
            int n = Math.Min(rgb.Length, _frame.Length);
            rgb[..n].CopyTo(_frame);
            _frame.AsSpan(n).Clear();
            _dirty = true;
            _driving = true;
            _blackPending = false;
        }

        _wake.Set();
    }

    public void Release()
    {
        // лампа без кадров гаснет сама через 2,5 с, чёрный кадр гасит её сразу
        lock (_gate)
        {
            if (!_driving) return;
            _driving = false;
            _blackPending = true;
        }

        _wake.Set();
    }

    void Run()
    {
        var copy = new byte[_frame.Length];
        long lastSent = 0;
        byte sequence = 0;

        while (!_stop)
        {
            _wake.WaitOne(KeepAliveMs);
            if (_stop) break;

            IPEndPoint endpoint;
            lock (_gate)
            {
                if (_blackPending)
                {
                    _blackPending = false;
                    Array.Clear(copy);
                }
                else
                {
                    if (!_driving) continue;
                    if (!_dirty && Environment.TickCount64 - lastSent < KeepAliveMs) continue;
                    _frame.CopyTo(copy, 0);
                    _dirty = false;
                }

                endpoint = _endpoint;
            }

            long wait = MinFrameMs - (Environment.TickCount64 - lastSent);
            if (wait > 0) Thread.Sleep((int)wait);

            sequence = (byte)(sequence % 15 + 1);              // 1..15; ноль значит «без номера»
            try
            {
                _socket.SendTo(Ddp.Frame(sequence, copy), endpoint);
            }
            catch (SocketException)
            {
                // сеть пропала или адрес недоступен: кадр уйдёт следующим тактом, лампу уберёт поиск
            }
            catch (ObjectDisposedException)
            {
                break;
            }

            lastSent = Environment.TickCount64;
        }
    }

    /// <summary>Stops the thread; a lamp still showing frames is sent black, so it goes dark at once.</summary>
    public void Dispose()
    {
        bool black;
        lock (_gate)
        {
            black = _driving || _blackPending;
            _driving = _blackPending = false;
        }

        _stop = true;
        _wake.Set();
        _thread.Join(1000);
        _wake.Dispose();

        if (!black) return;
        try { _socket.SendTo(Ddp.Frame(0, new byte[_frame.Length]), _endpoint); }
        catch (Exception e) when (e is SocketException or ObjectDisposedException) { }
    }
}
