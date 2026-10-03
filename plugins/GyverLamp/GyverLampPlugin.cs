using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Threading;
using CaseLight.Plugins;

namespace CaseLight.GyverLamp;

/// <summary>
/// Lamps on the GyverLamp-Wa1den firmware, driven over the local network with DDP.
///
/// Lamps are found by a DDP status query broadcast every few seconds on every network the
/// computer is on; a lamp answers it in any effect, and the answer says whether it is on
/// and shows frames. A lamp that stops answering is dropped after a few rounds, so one lost
/// packet on WiFi does not take it away from its fixtures.
/// </summary>
public sealed class GyverLampPlugin : ILightPlugin
{
    const int ScanMs = 3000;
    const int ForgetMs = 10_000;

    readonly object _gate = new();
    readonly Dictionary<string, Entry> _lamps = new(StringComparer.OrdinalIgnoreCase);
    readonly ManualResetEvent _stop = new(false);
    Socket? _socket;
    Thread? _scan, _receive;

    sealed class Entry(Lamp lamp)
    {
        public Lamp Lamp = lamp;
        public long SeenAt = Environment.TickCount64;
    }

    public int ApiVersion => PluginApi.Version;
    public string Name => "GyverLamp";
    public string Description => PluginApi.Language == "ru"
        ? "Лампы на прошивке GyverLamp-Wa1den: матрица по локальной сети, протокол DDP."
        : "Lamps on the GyverLamp-Wa1den firmware: the matrix over the local network with DDP.";

    public IReadOnlyList<ILightDevice> Devices
    {
        get { lock (_gate) return _lamps.Values.Select(e => (ILightDevice)e.Lamp).ToArray(); }
    }

    public event EventHandler? DevicesChanged;

    public void Start()
    {
        _socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp) { EnableBroadcast = true };
        _socket.Bind(new IPEndPoint(IPAddress.Any, 0));

        _receive = new Thread(Receive) { IsBackground = true, Name = "GyverLamp receive" };
        _receive.Start();
        _scan = new Thread(Scan) { IsBackground = true, Name = "GyverLamp scan" };
        _scan.Start();
    }

    void Scan()
    {
        var query = Ddp.StatusQuery();
        do
        {
            foreach (var target in Broadcasts())
            {
                try { _socket!.SendTo(query, new IPEndPoint(target, Ddp.Port)); }
                catch (SocketException) { }                    // сеть только что пропала
                catch (ObjectDisposedException) { return; }
            }

            Forget();
        }
        while (!_stop.WaitOne(ScanMs));
    }

    /// <summary>
    /// The broadcast address of every IPv4 network the computer is on. A query to
    /// 255.255.255.255 goes out of one adapter only, the one Windows picks; with a VPN or a
    /// virtual switch that is often not the one the lamp is behind.
    /// </summary>
    static IEnumerable<IPAddress> Broadcasts()
    {
        var found = new HashSet<IPAddress>();
        NetworkInterface[] adapters;
        try { adapters = NetworkInterface.GetAllNetworkInterfaces(); }
        catch (NetworkInformationException) { adapters = []; }

        foreach (var adapter in adapters)
        {
            if (adapter.OperationalStatus != OperationalStatus.Up ||
                adapter.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;

            foreach (var unicast in adapter.GetIPProperties().UnicastAddresses)
            {
                if (unicast.Address.AddressFamily != AddressFamily.InterNetwork) continue;

                byte[] ip = unicast.Address.GetAddressBytes(), mask = unicast.IPv4Mask.GetAddressBytes();
                if (mask.All(b => b == 0)) continue;
                for (int i = 0; i < 4; i++) ip[i] |= (byte)~mask[i];
                found.Add(new IPAddress(ip));
            }
        }

        if (found.Count == 0) found.Add(IPAddress.Broadcast);
        return found;
    }

    void Receive()
    {
        var buffer = new byte[1500];
        EndPoint from = new IPEndPoint(IPAddress.Any, 0);

        while (!_stop.WaitOne(0))
        {
            int n;
            try { n = _socket!.ReceiveFrom(buffer, ref from); }
            catch (SocketException e) when (e.SocketErrorCode == SocketError.ConnectionReset)
            {
                continue;                                      // ICMP «порт недоступен» от прежнего адреса лампы
            }
            catch (SocketException)
            {
                Thread.Sleep(100);
                continue;
            }
            catch (ObjectDisposedException) { return; }

            var status = Ddp.ParseStatus(buffer.AsSpan(0, n));
            if (status != null) Seen(status, ((IPEndPoint)from).Address);
        }
    }

    void Seen(LampStatus status, IPAddress address)
    {
        bool changed = false;
        Lamp? replaced = null;

        lock (_gate)
        {
            if (_lamps.TryGetValue(status.Mac, out var entry))
            {
                entry.SeenAt = Environment.TickCount64;
                if (entry.Lamp.Update(status, address, out bool problemChanged))
                {
                    replaced = entry.Lamp;
                    entry.Lamp = new Lamp(_socket!, status, address);
                    changed = true;
                }
                else changed = problemChanged;
            }
            else
            {
                _lamps[status.Mac] = new Entry(new Lamp(_socket!, status, address));
                changed = true;
            }
        }

        replaced?.Dispose();
        if (changed) Changed();
    }

    void Forget()
    {
        var gone = new List<Lamp>();
        lock (_gate)
        {
            foreach (var (mac, entry) in _lamps.ToArray())
            {
                if (Environment.TickCount64 - entry.SeenAt < ForgetMs) continue;
                _lamps.Remove(mac);
                gone.Add(entry.Lamp);
            }
        }

        foreach (var lamp in gone) lamp.Dispose();
        if (gone.Count > 0) Changed();
    }

    void Changed() => DevicesChanged?.Invoke(this, EventArgs.Empty);

    public void Dispose()
    {
        _stop.Set();
        _scan?.Join(1000);

        Lamp[] all;
        lock (_gate)
        {
            all = _lamps.Values.Select(e => e.Lamp).ToArray();
            _lamps.Clear();
        }

        // лампы гасятся до закрытия сокета: без кадров они погасли бы только через 2,5 с
        foreach (var lamp in all) lamp.Dispose();

        _socket?.Dispose();
        _receive?.Join(1000);
        _stop.Dispose();
    }
}
