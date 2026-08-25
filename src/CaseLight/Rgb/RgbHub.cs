using System;
using System.Collections.Generic;
using System.Linq;
using CaseLight.Core.Capture;
using CaseLight.Model;
using OpenRGB.NET;

using CaseLight.Core.Text;

namespace CaseLight.Rgb;

/// <summary>One zone of a controller, as the UI needs to see it.</summary>
public sealed record ZoneInfo(int Index, string Name, int LedCount, int FirstGlobalLed);

/// <summary>One controller, as the UI needs to see it.</summary>
public sealed record DeviceInfo(int Index, string Name, string Location, string Type,
                                int LedCount, ZoneInfo[] Zones);

/// <summary>
/// The only thing that talks to OpenRGB.
///
/// Two things it has to survive. The controller list is renumbered whenever detection
/// changes - disabling one GPU detector shifted every device below it - so bindings are
/// resolved by name and re-resolved after every reconnect. And the server itself dies:
/// three access violations in ten minutes during calibration, so a dropped connection is
/// an expected state rather than an error.
/// </summary>
public sealed class RgbHub : IDisposable
{
    OpenRgbClient? _client;
    Device[] _devices = Array.Empty<Device>();

    /// <summary>
    /// One socket, one writer at a time.
    ///
    /// The client library is not thread-safe, and two threads genuinely reach it here: the
    /// paint loop writing frames, and the interface re-reading the device list or switching
    /// modes. Interleaved writes put half of one packet inside another, and the server on
    /// the far end parses whatever comes out - which is a very plausible reading of the
    /// buffer overrun it died with.
    /// </summary>
    readonly object _io = new();

    /// <summary>Accumulated colour per device per LED, plus how many fixtures contributed.</summary>
    readonly Dictionary<int, (double[] r, double[] g, double[] b, int[] hits)> _frame = new();

    long _lastAttempt;

    /// <summary>
    /// Set from the client's own thread when OpenRGB announces that its device list moved.
    ///
    /// This is what keeps us from writing an array of the wrong length: UpdateLeds carries
    /// exactly as many colours as we last saw, and a zone resized on the server's side
    /// would make that a buffer overrun over there - which is precisely how OpenRGB died
    /// with 0xc0000409 in ucrtbase.
    /// </summary>
    volatile bool _listStale;

    /// <summary>
    /// Bumped on every re-read of the controller list. Anything caching resolved indices
    /// has to notice: reconnecting renumbers devices, and stale indices would paint the
    /// wrong hardware rather than fail loudly.
    /// </summary>
    public int Generation { get; private set; }

    public bool IsConnected => _client != null;

    /// <summary>
    /// Connected AND actually holding a device list.
    ///
    /// The two are not the same thing, and the difference cost a whole evening: the server
    /// opens its port before it has finished looking for hardware, so a client that
    /// connects at that moment gets an empty list and, if nobody asks again, keeps it
    /// forever. Readiness means devices, not a socket.
    /// </summary>
    public bool IsReady => _client != null && Devices.Length > 0;
    /// <summary>
    /// What the last exchange with the server ended with, kept apart from its wording: the
    /// window can change language at any moment, and a line composed once stayed in the
    /// language it was written in until the server was asked something again.
    /// </summary>
    enum State { Idle, Connected, NoConnection, Lost, ListFailed }

    State _state = State.Idle;
    string _detail = "";

    public string Status => _state switch
    {
        State.Connected => string.Format(Loc.P("подключено, контроллеров с диодами: {0}",
                                               "connected, controllers with LEDs: {0}"), Devices.Length),
        State.NoConnection => Loc.P("нет связи с OpenRGB: ", "no connection to OpenRGB: ") + _detail,
        State.Lost => Loc.P("связь потеряна: ", "connection lost: ") + _detail,
        State.ListFailed => Loc.P("не удалось перечитать список устройств: ",
                                  "could not re-read the device list: ") + _detail,
        _ => Loc.P("не подключено", "not connected")
    };

    void Report(State state, string detail = "") { _state = state; _detail = detail; }
    public DeviceInfo[] Devices { get; private set; } = Array.Empty<DeviceInfo>();

    /// <summary>Safe to call repeatedly; a failure is not retried for a couple of seconds.</summary>
    public bool Connect(bool force = false)
    {
        if (IsConnected && !force) return true;

        long now = Environment.TickCount64;
        if (!force && now - _lastAttempt < 2000) return false;
        _lastAttempt = now;

        try
        {
            lock (_io)
            {
                _client?.Dispose();
                _client = new OpenRgbClient(name: "CaseLight");
                _client.DeviceListUpdated += (_, _) => _listStale = true;
                _listStale = false;
                _directMode.Clear();
                RefreshLocked();
            }
        }
        catch (Exception ex)
        {
            _client = null;
            _devices = Array.Empty<Device>();
            Devices = Array.Empty<DeviceInfo>();
            Report(State.NoConnection, ex.Message);
            return false;
        }

        Report(State.Connected);
        return true;
    }

    /// <summary>
    /// Re-reads the controller list; call after anything that could renumber it.
    ///
    /// Never throws. The server closes the socket when it dies, and it dies on its own
    /// often enough that this is an ordinary event - but the call sits on the interface
    /// timer, where an escaping exception ends the process. One was collected the hard
    /// way: SocketException 10054 out of SendAll, and the whole program went with it.
    /// </summary>
    public void Refresh()
    {
        try
        {
            lock (_io)
            {
                if (_client == null) return;
                RefreshLocked();
            }
        }
        catch (Exception ex)
        {
            lock (_io) DropClient(ex);
        }
    }

    /// <summary>
    /// Re-reads the list, but only if the socket is free at this moment.
    ///
    /// The paint thread holds the same lock while it writes, and this runs on the interface
    /// timer: waiting behind a dying server would freeze the window. A missed read costs
    /// nothing, the next tick tries again.
    /// </summary>
    public bool TryRefresh(int waitMs = 50)
    {
        if (_client == null) return false;
        if (!System.Threading.Monitor.TryEnter(_io, waitMs)) return false;

        try
        {
            if (_client == null) return false;
            RefreshLocked();
            return true;
        }
        catch (Exception ex)
        {
            DropClient(ex);
            return false;
        }
        finally
        {
            System.Threading.Monitor.Exit(_io);
        }
    }

    /// <summary>Lets go of a connection that has stopped answering. Call under <c>_io</c>.</summary>
    void DropClient(Exception ex)
    {
        Report(State.Lost, ex.Message);

        try { _client?.Dispose(); } catch { /* уже мёртв */ }
        _client = null;

        _devices = Array.Empty<Device>();
        Devices = Array.Empty<DeviceInfo>();
        _directMode.Clear();
        Generation++;
    }

    /// <summary>
    /// Which devices have already been put into direct mode on this connection.
    ///
    /// Re-sending it on every read was hammering the server exactly while it was still
    /// finding hardware, which is its most fragile moment. A device needs the mode once,
    /// when it appears; a reconnect empties this and the whole list gets it again.
    /// </summary>
    readonly HashSet<string> _directMode = new();

    void RefreshLocked()
    {
        if (_client == null) return;

        _devices = _client.GetAllControllerData();

        var list = new List<DeviceInfo>();
        for (int i = 0; i < _devices.Length; i++)
        {
            var d = _devices[i];
            if (d.Leds.Length == 0) continue;   // empty stubs are not worth showing

            var zones = new List<ZoneInfo>();
            int running = 0;
            for (int z = 0; z < d.Zones.Length; z++)
            {
                zones.Add(new ZoneInfo(z, d.Zones[z].Name, (int)d.Zones[z].LedCount, running));
                running += (int)d.Zones[z].LedCount;
            }

            list.Add(new DeviceInfo(i, d.Name, d.Location, d.Type.ToString(), d.Leds.Length, zones.ToArray()));
        }

        Devices = list.ToArray();
        Generation++;

        // Status is what the window shows, and it used to be written only when the
        // connection was made - so a list that filled up afterwards left the line saying
        // "0 controllers" over a case that was lit and working.
        Report(State.Connected);

        // Per-LED control has to be re-established after every reconnect: a restarted
        // server brings its devices back in whatever mode they defaulted to. Only devices
        // that have not had it yet are touched - see _directMode.
        foreach (var info in Devices)
        {
            string key = info.Index + "|" + info.Name + "|" + info.Location;
            if (!_directMode.Add(key)) continue;

            try { _client.SetCustomMode(info.Index); }
            catch { /* одно упрямое устройство не должно ронять остальные */ }
        }
    }

    /// <summary>
    /// Asks the server to look for hardware again, over a socket of our own.
    ///
    /// The client library has no such call, but the protocol does: a bare 16-byte header
    /// with packet id 140. Kept for the record rather than for use: on this machine the
    /// request kills the server every time, and disconnecting first changes nothing, so
    /// the recovery path restarts the server instead.
    /// </summary>
    public static string RequestRescan(string host = "127.0.0.1", int port = 6742)
    {
        try
        {
            using var socket = new System.Net.Sockets.TcpClient();
            socket.Connect(host, port);

            using var stream = socket.GetStream();

            var packet = new byte[16];
            packet[0] = (byte)'O'; packet[1] = (byte)'R'; packet[2] = (byte)'G'; packet[3] = (byte)'B';
            // device index 0, size 0 - both already zero
            BitConverter.GetBytes(140u).CopyTo(packet, 8);   // REQUEST_RESCAN_DEVICES

            stream.Write(packet, 0, packet.Length);
            stream.Flush();

            // give the server a moment to pick the request up before the socket closes
            System.Threading.Thread.Sleep(300);

            ProbeLog.Log("OpenRGB", Loc.P("запрошено пересканирование устройств", "a device rescan was requested"));
            return Loc.P("запрошено пересканирование устройств", "a device rescan was requested");
        }
        catch (Exception ex)
        {
            ProbeLog.Log("OpenRGB", Loc.P("пересканирование не удалось: ", "the rescan failed: ") + ex.Message);
            return Loc.P("пересканирование не удалось: ", "the rescan failed: ") + ex.Message;
        }
    }

    /// <summary>
    /// Re-reads the list if the server said it changed. Called from the paint loop between
    /// frames, never in the middle of one, so buffers and lengths stay consistent.
    /// </summary>
    public bool RefreshIfStale()
    {
        if (!_listStale || _client == null) return false;

        _listStale = false;
        try { Refresh(); }
        catch (Exception ex) { Report(State.ListFailed, ex.Message); }
        return true;
    }

    /// <summary>
    /// Finds the live device a binding refers to. Matching is by name, with the location
    /// breaking ties between two identical controllers.
    /// </summary>
    public DeviceInfo? Find(Binding binding)
    {
        if (string.IsNullOrWhiteSpace(binding.DeviceName)) return null;

        var byName = Devices.Where(d => d.Name == binding.DeviceName).ToArray();
        if (byName.Length == 0) return null;
        if (byName.Length == 1 || string.IsNullOrEmpty(binding.DeviceLocation)) return byName[0];

        return byName.FirstOrDefault(d => d.Location == binding.DeviceLocation) ?? byName[0];
    }

    /// <summary>
    /// Resolves a binding once, so the paint loop can address LEDs by plain index instead
    /// of searching the device list for every LED of every frame.
    /// </summary>
    public bool TryResolve(Binding binding, out int deviceIndex, out int firstGlobalLed, out int available)
    {
        deviceIndex = -1; firstGlobalLed = 0; available = 0;

        var info = Find(binding);
        if (info == null) return false;
        if (binding.ZoneIndex < 0 || binding.ZoneIndex >= info.Zones.Length) return false;

        var zone = info.Zones[binding.ZoneIndex];

        deviceIndex = info.Index;
        firstGlobalLed = zone.FirstGlobalLed + binding.FirstLed;
        available = Math.Max(0, Math.Min(binding.LedCount, zone.LedCount - binding.FirstLed));
        return available > 0;
    }

    /// <summary>Contributes to an already-resolved LED. Same averaging as <see cref="Contribute"/>.</summary>
    public void ContributeAt(int deviceIndex, int globalLed, byte r, byte g, byte b)
    {
        if (!_frame.TryGetValue(deviceIndex, out var buf)) return;
        if (globalLed < 0 || globalLed >= buf.r.Length) return;

        buf.r[globalLed] += r;
        buf.g[globalLed] += g;
        buf.b[globalLed] += b;
        buf.hits[globalLed]++;
    }

    // ---- кадр -------------------------------------------------------------

    public void BeginFrame()
    {
        foreach (var info in Devices)
        {
            if (!_frame.TryGetValue(info.Index, out var buf) || buf.r.Length != info.LedCount)
            {
                buf = (new double[info.LedCount], new double[info.LedCount],
                       new double[info.LedCount], new int[info.LedCount]);
                _frame[info.Index] = buf;
            }

            Array.Clear(buf.r); Array.Clear(buf.g); Array.Clear(buf.b); Array.Clear(buf.hits);
        }
    }

    /// <summary>
    /// Contributes a colour to one LED of a binding.
    ///
    /// Several fixtures may legitimately land on the same LED - the three single fans are
    /// wired in parallel, so one run of 32 drives three frames in three different places.
    /// They cannot be lit differently, so their contributions are averaged instead of the
    /// last one silently winning.
    /// </summary>
    public void Contribute(Binding binding, int ledInBinding, byte r, byte g, byte b)
    {
        var info = Find(binding);
        if (info == null) return;
        if (binding.ZoneIndex < 0 || binding.ZoneIndex >= info.Zones.Length) return;

        int global = info.Zones[binding.ZoneIndex].FirstGlobalLed + binding.FirstLed + ledInBinding;
        if (global < 0 || global >= info.LedCount) return;

        if (!_frame.TryGetValue(info.Index, out var buf)) return;

        buf.r[global] += r;
        buf.g[global] += g;
        buf.b[global] += b;
        buf.hits[global]++;
    }

    /// <summary>
    /// Sends each device one full array; unlit LEDs go out black.
    ///
    /// <paramref name="onlyDevices"/> limits the write to those devices, which is how slow
    /// hardware is kept from holding up the rest: memory on the SMBus can be written a few
    /// times a second while the motherboard keeps its full rate.
    /// </summary>
    public bool EndFrame(IReadOnlyCollection<int>? onlyDevices = null)
    {
        try
        {
            lock (_io)
            {
                // re-checked inside the lock: recovery disposes the client from its own thread
                if (_client == null) return false;

                foreach (var info in Devices)
                {
                    if (onlyDevices != null && !onlyDevices.Contains(info.Index)) continue;
                    if (!_frame.TryGetValue(info.Index, out var buf)) continue;

                    // the list can be re-read between frames, leaving our buffer a size behind
                    if (buf.r.Length != info.LedCount) continue;

                    var colors = new Color[info.LedCount];
                    for (int i = 0; i < info.LedCount; i++)
                    {
                        int hits = buf.hits[i];
                        colors[i] = hits == 0
                            ? new Color(0, 0, 0)
                            : new Color((byte)(buf.r[i] / hits), (byte)(buf.g[i] / hits), (byte)(buf.b[i] / hits));
                    }

                    _client.UpdateLeds(info.Index, colors);
                }
            }
        }
        catch (Exception ex)
        {
            Report(State.Lost, ex.Message);
            lock (_io)
            {
                try { _client?.Dispose(); } catch { /* уже мёртв */ }
                _client = null;
            }
            return false;
        }

        return true;
    }

    /// <summary>Lights one fixture and blacks out everything else - used to identify it in the case.</summary>
    public void Highlight(Fixture fixture, byte r, byte g, byte b)
    {
        BeginFrame();
        for (int i = 0; i < fixture.Binding.LedCount; i++)
            Contribute(fixture.Binding, i, r, g, b);
        EndFrame();
    }

    /// <summary>Lights a single LED of a fixture, for finding which one is the bottom.</summary>
    public void HighlightLed(Fixture fixture, int led, byte r, byte g, byte b)
    {
        BeginFrame();
        if (led >= 0 && led < fixture.Binding.LedCount)
            Contribute(fixture.Binding, led, r, g, b);
        EndFrame();
    }

    public void Blackout()
    {
        BeginFrame();
        EndFrame();
    }

    /// <summary>
    /// Writes black to every device no fixture drives.
    ///
    /// After a cold boot the controllers come up in whatever mode they ship with, and that
    /// is usually a rainbow. Direct mode alone does not clear it - the previous frame stays
    /// on the LEDs until something writes over it - so a device left out of the layout
    /// would keep flowing through its factory effect beside a case that follows the screen.
    /// </summary>
    public bool BlackoutOthers(IReadOnlyCollection<int> driven)
    {
        try
        {
            lock (_io)
            {
                if (_client == null) return false;

                foreach (var info in Devices)
                {
                    if (driven.Contains(info.Index)) continue;

                    var colors = new Color[info.LedCount];
                    for (int i = 0; i < colors.Length; i++) colors[i] = new Color(0, 0, 0);

                    _client.UpdateLeds(info.Index, colors);
                }
            }
        }
        catch (Exception ex)
        {
            Report(State.Lost, ex.Message);
            return false;
        }

        return true;
    }

    public void Dispose()
    {
        lock (_io)
        {
            try { _client?.Dispose(); } catch { /* уже отвалилось */ }
            _client = null;
        }

        // stale names in the interface are worse than an honest empty list
        _devices = Array.Empty<Device>();
        Devices = Array.Empty<DeviceInfo>();
        _directMode.Clear();
        Generation++;
    }
}
