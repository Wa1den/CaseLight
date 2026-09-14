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
///
/// A server on protocol 6 is spoken to only through <see cref="ServerChannel"/>: the list,
/// direct mode and frames, all by controller id and with every wait limited. The client
/// library is connected all the same and serves servers older than OpenRGB 1.0, which is
/// why the connection state below still follows it.
/// </summary>
public sealed class RgbHub : IDisposable
{
    OpenRgbClient? _client;

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
    /// Set when OpenRGB announces that its device list moved.
    ///
    /// This is what keeps us from writing an array of the wrong length: UpdateLeds carries
    /// exactly as many colours as we last saw, and a zone resized on the server's side
    /// would make that a buffer overrun over there - which is precisely how OpenRGB died
    /// with 0xc0000409 in ucrtbase.
    /// </summary>
    volatile bool _listStale;

    /// <summary>
    /// Raised together with <see cref="_listStale"/>. A changed list may hold controllers
    /// that are new objects on the server - after a rescan every one of them is - and those
    /// come up in their default mode, so <see cref="_directMode"/> no longer describes them.
    /// </summary>
    volatile bool _modesStale;

    /// <summary>The protocol 6 connection. Made before the list is read and dropped with the main one.</summary>
    readonly ServerChannel _server = new();

    /// <summary>
    /// Controller ids by server index, from the last read of the list over protocol 6; null
    /// when the list came through the library. Touched only under <see cref="_io"/>.
    /// </summary>
    uint[]? _ids;

    /// <summary>The channel's counters at the last look; a change means the list has to be read again.</summary>
    int _refusedSeen, _listChangesSeen;

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
                _client = new OpenRgbClient(name: "CaseLight (protocol 4)");
                _client.DeviceListUpdated += (_, _) => { _listStale = true; _modesStale = true; };
                _listStale = false;
                _modesStale = false;
                _directMode.Clear();

                // после основного: отказ в подключении не должен стоить ожидания версии
                ConnectServer();
                RefreshLocked();
            }
        }
        catch (Exception ex)
        {
            _client = null;
            Devices = Array.Empty<DeviceInfo>();
            _ids = null;
            _server.Dispose();
            Report(State.NoConnection, ex.Message);
            return false;
        }

        Report(State.Connected);
        return true;
    }

    void ConnectServer()
    {
        try { _server.Connect(); }
        catch { /* без канала всё идёт через библиотеку, как раньше */ }

        _refusedSeen = 0;
        _listChangesSeen = 0;
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
        _server.Dispose();
        _ids = null;

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

        // сбрасывается до чтения: поиск, закончившийся во время него, поднимет флаг снова
        if (_modesStale)
        {
            _modesStale = false;
            _directMode.Clear();
        }

        if (_server.Supported)
        {
            if (!ReadListByIdLocked()) return;
        }
        else
        {
            ReadListByIndexLocked();
        }

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

            if (_ids != null)
            {
                _server.SetCustomMode(_ids[info.Index]);
                continue;
            }

            try { _client.SetCustomMode(info.Index); }
            catch { /* одно упрямое устройство не должно ронять остальные */ }
        }
    }

    /// <summary>
    /// The list over protocol 6, by id, each request with a limited wait.
    ///
    /// A controller that does not answer has gone between the count and its description -
    /// a rescan deletes them all - so the read is abandoned and the list kept as it was.
    /// The server announces the change, and that brings the next read.
    /// </summary>
    /// <returns>False if the read was abandoned.</returns>
    bool ReadListByIdLocked()
    {
        uint[]? ids = _server.RequestControllerIds();
        if (ids == null)
        {
            _listStale = true;
            return false;
        }

        var list = new List<DeviceInfo>();
        for (int i = 0; i < ids.Length; i++)
        {
            var d = _server.RequestController(ids[i]);
            if (d == null)
            {
                _listStale = true;
                return false;
            }

            if (d.LedCount == 0) continue;   // empty stubs are not worth showing

            var zones = new ZoneInfo[d.Zones.Length];
            int running = 0;
            for (int z = 0; z < zones.Length; z++)
            {
                zones[z] = new ZoneInfo(z, d.Zones[z].Name, d.Zones[z].LedCount, running);
                running += d.Zones[z].LedCount;
            }

            // the library's names for device types, so the window shows the same words either way
            list.Add(new DeviceInfo(i, d.Name, d.Location, ((DeviceType)d.Type).ToString(), d.LedCount, zones));
        }

        _ids = ids;
        Devices = list.ToArray();
        return true;
    }

    /// <summary>The list through the client library, for servers older than protocol 6.</summary>
    void ReadListByIndexLocked()
    {
        var devices = _client!.GetAllControllerData();

        var list = new List<DeviceInfo>();
        for (int i = 0; i < devices.Length; i++)
        {
            var d = devices[i];
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

        _ids = null;
        Devices = list.ToArray();
    }

    /// <summary>Whether the server takes rescan requests; see <see cref="ServerChannel"/>.</summary>
    public bool CanRescan
    {
        get
        {
            if (!_server.IsConnected && IsConnected)
            {
                ConnectServer();

                // the list comes over the channel with the next read
                _listStale = true;
            }

            return _server.Supported;
        }
    }

    /// <summary>When the server last said detection was over; 0 if it never did on this connection.</summary>
    public long LastDetectionEndTicks => _server.LastCompletedTicks;

    /// <summary>
    /// Asks the server to find its devices again and waits until it has.
    ///
    /// The controllers come back as new objects with new ids, in whatever mode they default
    /// to, so the list and direct mode are both marked for renewal whatever the outcome - a
    /// detection that timed out may still have replaced some of them.
    /// </summary>
    public bool Rescan(int startTimeoutMs = 5000, int completeTimeoutMs = 60000)
    {
        if (!CanRescan) return false;

        bool done = _server.RescanAndWait(startTimeoutMs, completeTimeoutMs);

        _modesStale = true;
        _listStale = true;

        ProbeLog.Log("OpenRGB", done
            ? Loc.P("пересканирование завершено", "the rescan is complete")
            : Loc.P("пересканирование не завершилось", "the rescan did not complete"));
        return done;
    }

    /// <summary>
    /// Re-reads the list if the server said it changed. Called from the paint loop between
    /// frames, never in the middle of one, so buffers and lengths stay consistent.
    /// </summary>
    public bool RefreshIfStale()
    {
        int changes = _server.ListChanges;
        if (changes != _listChangesSeen)
        {
            _listChangesSeen = changes;
            _listStale = true;
            _modesStale = true;
        }

        if (!_listStale || _client == null) return false;

        // A detection changes the list more than once on its way - empty first, then filling
        // up - so the read waits for its end; frames meanwhile go to ids the server may
        // refuse, which costs nothing.
        if (_server.DetectionRunning) return false;

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

                    var rgb = new byte[info.LedCount * 3];
                    for (int i = 0; i < info.LedCount; i++)
                    {
                        int hits = buf.hits[i];
                        if (hits == 0) continue;

                        rgb[i * 3] = (byte)(buf.r[i] / hits);
                        rgb[i * 3 + 1] = (byte)(buf.g[i] / hits);
                        rgb[i * 3 + 2] = (byte)(buf.b[i] / hits);
                    }

                    WriteLocked(info, rgb);
                }

                // a refused id means the list moved without our noticing; read it again
                int refused = _server.InvalidIdAcks;
                if (refused != _refusedSeen)
                {
                    _refusedSeen = refused;
                    _listStale = true;
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
                _ids = null;
            }
            _server.Dispose();
            return false;
        }

        return true;
    }

    /// <summary>
    /// Puts one array on one device: by id when the list came over protocol 6, through the
    /// library otherwise. Call under <see cref="_io"/>.
    /// </summary>
    /// <param name="rgb">Three bytes per LED.</param>
    void WriteLocked(DeviceInfo info, byte[] rgb)
    {
        var ids = _ids;
        if (ids != null)
        {
            // Through the library this would be dropped by the server without a word, so a
            // failed send is left as it is: the channel is gone, and the reconnect that
            // follows reads the list afresh.
            if (info.Index < ids.Length) _server.UpdateLeds(ids[info.Index], rgb, info.LedCount);
            return;
        }

        var colors = new Color[info.LedCount];
        for (int i = 0; i < colors.Length; i++)
            colors[i] = new Color(rgb[i * 3], rgb[i * 3 + 1], rgb[i * 3 + 2]);

        _client!.UpdateLeds(info.Index, colors);
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

    /// <summary>
    /// Writes black to every device, without going through the frame buffers.
    ///
    /// It used to be <c>BeginFrame(); EndFrame();</c>, and that is called from the interface
    /// thread while the paint thread is filling those very buffers: the clearing pass wiped
    /// half a frame out from under it, and the dictionary itself was being rewritten while
    /// the other thread read it. Composing the black frame here touches nothing shared.
    /// </summary>
    public bool Blackout()
    {
        try
        {
            lock (_io)
            {
                if (_client == null) return false;

                foreach (var info in Devices)
                    WriteLocked(info, new byte[info.LedCount * 3]);
            }
        }
        catch (Exception ex)
        {
            Report(State.Lost, ex.Message);
            return false;
        }

        return true;
    }

    /// <summary>
    /// Waits out the way from our socket to the hardware.
    ///
    /// A write returns once the bytes are in the socket; the server has still to pass them
    /// over USB. Closing the connection or letting the machine sleep in that moment drops
    /// the frame, and for a blackout there is no next frame to correct it.
    /// </summary>
    public static void Settle(int ms = 200) => System.Threading.Thread.Sleep(ms);

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
                    WriteLocked(info, new byte[info.LedCount * 3]);
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
            _ids = null;
        }

        _server.Dispose();

        // stale names in the interface are worse than an honest empty list
        Devices = Array.Empty<DeviceInfo>();
        _directMode.Clear();
        Generation++;
    }
}
