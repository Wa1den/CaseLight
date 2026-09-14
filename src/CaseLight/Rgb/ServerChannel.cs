using System;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace CaseLight.Rgb;

/// <summary>
/// A second connection to the OpenRGB server, on protocol 6: frames by controller id,
/// rescans, and the start and end of detection.
///
/// OpenRGB.NET 3.1.1 negotiates protocol 4 at most, and it cannot simply be told to ask for
/// more. Its read loop looks every incoming packet id up in a table of the ids it knows; the
/// first ACK or detection notice throws there and ends the loop without a word, after which
/// every request waits forever. So the library keeps reading the list and setting modes, and
/// this socket carries what protocol 4 cannot.
///
/// Frames have to go here as well. OpenRGB 1.0 queues a LED update to the controller's own
/// thread and resolves it there by the thread's id - read as a list index when the client is
/// below protocol 6. The first controller starts with id 0 at index 0, so this works until
/// the list is rebuilt; after a rescan the board had id 1 at index 0, and every frame from
/// protocol 4 was dropped without an answer while the LEDs stayed dark. Addressed by id, the
/// same frame was acknowledged and lit the strips, after sleep too.
///
/// The socket has to be read all the time. A protocol 6 client is sent an echo of every
/// controller update, our own frames included - 26 packets of 490 bytes a second were
/// measured - and a reader that stopped would leave them piling up on the server's side.
/// No client flags are sent: detection notices come without them, and claiming support for
/// the log or settings APIs would only invite more traffic.
/// </summary>
public sealed class ServerChannel : IDisposable
{
    const uint Protocol = 6;

    const uint IdControllerCount = 0;
    const uint IdAck = 10;
    const uint IdProtocolVersion = 40;
    const uint IdClientName = 50;
    const uint IdDetectionStarted = 101;
    const uint IdDetectionComplete = 103;
    const uint IdRescanDevices = 140;
    const uint IdUpdateLeds = 1050;

    const uint StatusInvalidId = 4;

    const int HeaderBytes = 16;

    /// <summary>A size beyond this means the stream is out of step, not a real packet.</summary>
    const uint MaxPayloadBytes = 16 * 1024 * 1024;

    readonly object _gate = new();
    readonly object _write = new();

    TcpClient? _tcp;
    Thread? _reader;
    byte[] _frame = new byte[1024];

    bool _alive;
    bool _versionKnown;
    uint _serverVersion;
    bool _running;
    int _started;
    int _completed;
    long _completedAt;
    uint[] _ids = Array.Empty<uint>();
    int _idsReplies;
    int _invalidIdAcks;

    public bool IsConnected { get { lock (_gate) return _alive; } }

    /// <summary>The server answered with protocol 6 or later, so everything here works.</summary>
    public bool Supported { get { lock (_gate) return SupportedLocked; } }

    bool SupportedLocked => _alive && _versionKnown && _serverVersion >= Protocol;

    public bool DetectionRunning { get { lock (_gate) return _alive && _running; } }

    /// <summary>
    /// When the last detection ended, in <see cref="Environment.TickCount64"/> terms; 0 if
    /// none has ended on this connection.
    /// </summary>
    public long LastCompletedTicks { get { lock (_gate) return _completedAt; } }

    /// <summary>
    /// How many LED updates the server refused for an unknown controller id. A rise means
    /// the ids in hand are out of date and the list has to be read again.
    /// </summary>
    public int InvalidIdAcks { get { lock (_gate) return _invalidIdAcks; } }

    /// <summary>
    /// Connects and waits for the server's protocol version.
    ///
    /// An older server keeps the connection all the same, it just never reports anything:
    /// OpenRGB 0.9 died on client disconnects, so connecting only to hang up again at once
    /// is exactly what should not be done to it.
    /// </summary>
    /// <returns>Whether the server supports protocol 6.</returns>
    public bool Connect(string host = "127.0.0.1", int port = 6742, int timeoutMs = 1500)
    {
        Close();

        var tcp = new TcpClient { NoDelay = true };
        try
        {
            if (!tcp.ConnectAsync(host, port).Wait(timeoutMs))
            {
                tcp.Dispose();
                return false;
            }
        }
        catch
        {
            tcp.Dispose();
            return false;
        }

        lock (_gate)
        {
            _tcp = tcp;
            _alive = true;
            _versionKnown = false;
            _serverVersion = 0;
            _running = false;
            _started = 0;
            _completed = 0;
            _completedAt = 0;
            _ids = Array.Empty<uint>();
            _idsReplies = 0;
            _invalidIdAcks = 0;
        }

        var reader = new Thread(() => ReadLoop(tcp)) { IsBackground = true, Name = "OpenRGB protocol 6 channel" };
        _reader = reader;
        reader.Start();

        if (!Send(IdClientName, 0, Encoding.ASCII.GetBytes("CaseLight\0")) ||
            !Send(IdProtocolVersion, 0, BitConverter.GetBytes(Protocol)))
            return false;

        lock (_gate)
        {
            // протокол 0 на запрос версии не отвечает вовсе, отсюда ожидание с пределом
            WaitLocked(() => _versionKnown, timeoutMs);
            return SupportedLocked;
        }
    }

    /// <summary>
    /// The server's controller ids, in the order of its list - the same order the client
    /// library numbers the devices in.
    /// </summary>
    /// <returns>Null if the server does not support it or did not answer in time.</returns>
    public uint[]? RequestControllerIds(int timeoutMs = 1000)
    {
        int replies;
        lock (_gate)
        {
            if (!SupportedLocked) return null;
            replies = _idsReplies;
        }

        if (!Send(IdControllerCount, 0, Array.Empty<byte>())) return null;

        lock (_gate)
            return WaitLocked(() => _idsReplies > replies, timeoutMs) ? _ids : null;
    }

    /// <summary>
    /// Sends one full LED array to a controller.
    /// </summary>
    /// <param name="rgb">Three bytes per LED, at least <paramref name="count"/> of them.</param>
    /// <returns>False if nothing could be sent; the caller has another way to write.</returns>
    public bool UpdateLeds(uint controllerId, byte[] rgb, int count)
    {
        TcpClient? tcp;
        lock (_gate) tcp = SupportedLocked ? _tcp : null;
        if (tcp == null) return false;

        int payload = 4 + 2 + 4 * count;
        int total = HeaderBytes + payload;
        bool sent;

        lock (_write)
        {
            if (_frame.Length < total) _frame = new byte[total];
            var p = _frame;

            WriteHeader(p, IdUpdateLeds, controllerId, (uint)payload);
            BitConverter.TryWriteBytes(p.AsSpan(HeaderBytes), (uint)payload);
            BitConverter.TryWriteBytes(p.AsSpan(HeaderBytes + 4), (ushort)count);

            for (int i = 0; i < count; i++)
            {
                int s = i * 3, d = HeaderBytes + 6 + i * 4;
                p[d] = rgb[s];
                p[d + 1] = rgb[s + 1];
                p[d + 2] = rgb[s + 2];
                p[d + 3] = 0;
            }

            try
            {
                tcp.GetStream().Write(p, 0, total);
                sent = true;
            }
            catch
            {
                sent = false;
            }
        }

        if (!sent) Drop(tcp);
        return sent;
    }

    /// <summary>
    /// Asks the server to find its devices again and waits for that detection to end.
    ///
    /// OpenRGB 1.0 deletes every controller first and creates them anew, with new ids, so
    /// the handles left stale by sleep are gone afterwards. A request that arrives while a
    /// detection is already running is dropped by the server; the running one serves just
    /// as well, and its end is what gets waited for.
    /// </summary>
    /// <returns>False if the server does not support it, did not start, or did not finish in time.</returns>
    public bool RescanAndWait(int startTimeoutMs, int completeTimeoutMs)
    {
        int started, completed;
        lock (_gate)
        {
            if (!SupportedLocked) return false;
            started = _started;
            completed = _completed;
        }

        if (!Send(IdRescanDevices, 0, Array.Empty<byte>())) return false;

        lock (_gate)
        {
            if (!WaitLocked(() => _started > started || _running || _completed > completed, startTimeoutMs))
                return false;

            return WaitLocked(() => _completed > completed, completeTimeoutMs);
        }
    }

    /// <summary>Call under <c>_gate</c>. Gives up early if the connection goes.</summary>
    bool WaitLocked(Func<bool> done, int timeoutMs)
    {
        long deadline = Environment.TickCount64 + timeoutMs;

        while (!done())
        {
            if (!_alive) return false;

            long left = deadline - Environment.TickCount64;
            if (left <= 0) return false;

            Monitor.Wait(_gate, (int)Math.Min(left, int.MaxValue));
        }

        return true;
    }

    static void WriteHeader(byte[] packet, uint id, uint device, uint size)
    {
        packet[0] = (byte)'O'; packet[1] = (byte)'R'; packet[2] = (byte)'G'; packet[3] = (byte)'B';
        BitConverter.TryWriteBytes(packet.AsSpan(4), device);
        BitConverter.TryWriteBytes(packet.AsSpan(8), id);
        BitConverter.TryWriteBytes(packet.AsSpan(12), size);
    }

    bool Send(uint id, uint device, byte[] payload)
    {
        TcpClient? tcp;
        lock (_gate) tcp = _alive ? _tcp : null;
        if (tcp == null) return false;

        var packet = new byte[HeaderBytes + payload.Length];
        WriteHeader(packet, id, device, (uint)payload.Length);
        payload.CopyTo(packet, HeaderBytes);

        bool sent;
        lock (_write)
        {
            try
            {
                tcp.GetStream().Write(packet, 0, packet.Length);
                sent = true;
            }
            catch
            {
                sent = false;
            }
        }

        if (!sent) Drop(tcp);
        return sent;
    }

    void ReadLoop(TcpClient tcp)
    {
        var header = new byte[HeaderBytes];
        var payload = new byte[1024];

        try
        {
            var stream = tcp.GetStream();

            while (true)
            {
                stream.ReadExactly(header, 0, HeaderBytes);
                if (header[0] != 'O' || header[1] != 'R' || header[2] != 'G' || header[3] != 'B') break;

                uint id = BitConverter.ToUInt32(header, 8);
                uint size = BitConverter.ToUInt32(header, 12);
                if (size > MaxPayloadBytes) break;

                if (payload.Length < size) payload = new byte[size];
                stream.ReadExactly(payload, 0, (int)size);

                // эхо кадров идёт десятками в секунду, замок берётся только ради нужного
                if (id is not (IdControllerCount or IdAck or IdProtocolVersion or IdDetectionStarted or IdDetectionComplete))
                    continue;

                // из подтверждений нужны только отказы кадрам по неизвестному id
                if (id == IdAck &&
                    (size < 8 || BitConverter.ToUInt32(payload, 0) != IdUpdateLeds || BitConverter.ToUInt32(payload, 4) != StatusInvalidId))
                    continue;

                lock (_gate)
                {
                    if (_tcp != tcp) return;

                    switch (id)
                    {
                        case IdControllerCount when size >= 4:
                            uint count = BitConverter.ToUInt32(payload, 0);
                            var ids = new uint[Math.Min(count, (size - 4) / 4)];
                            for (int i = 0; i < ids.Length; i++) ids[i] = BitConverter.ToUInt32(payload, 4 + 4 * i);
                            _ids = ids;
                            _idsReplies++;
                            break;

                        case IdAck:
                            _invalidIdAcks++;
                            break;

                        case IdProtocolVersion when size >= 4:
                            _serverVersion = BitConverter.ToUInt32(payload, 0);
                            _versionKnown = true;
                            break;

                        case IdDetectionStarted:
                            _running = true;
                            _started++;
                            break;

                        case IdDetectionComplete:
                            _running = false;
                            _completed++;
                            _completedAt = Environment.TickCount64;
                            break;
                    }

                    Monitor.PulseAll(_gate);
                }
            }
        }
        catch { /* сокет закрыт или сервер ушёл - ниже одно и то же */ }

        Drop(tcp);
    }

    void Drop(TcpClient tcp)
    {
        lock (_gate)
        {
            if (_tcp != tcp) return;

            _tcp = null;
            _alive = false;
            _running = false;
            Monitor.PulseAll(_gate);
        }

        try { tcp.Dispose(); } catch { /* уже закрыт */ }
    }

    void Close()
    {
        TcpClient? tcp;
        lock (_gate) tcp = _tcp;
        if (tcp != null) Drop(tcp);

        var reader = _reader;
        _reader = null;
        if (reader != null && reader != Thread.CurrentThread) reader.Join(1000);
    }

    public void Dispose() => Close();
}
