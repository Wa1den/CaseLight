using System;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace CaseLight.Rgb;

/// <summary>
/// A second connection to the OpenRGB server, on protocol 6, for what the client library
/// cannot do: ask for a rescan and learn when detection starts and ends.
///
/// OpenRGB.NET 3.1.1 negotiates protocol 4 at most, and it cannot simply be told to ask for
/// more. Its read loop looks every incoming packet id up in a table of the ids it knows; the
/// first ACK or detection notice throws there and ends the loop without a word, after which
/// every request waits forever. The server sends those packets only to clients on protocol 6,
/// so painting stays on the library at 4 and this socket carries the rest.
///
/// The socket has to be read all the time. A protocol 6 client is sent an echo of every
/// controller update, our own frames included - 26 packets of 490 bytes a second were
/// measured - and a reader that stopped would leave them piling up on the server's side.
/// No client flags are sent: detection notices come without them, and claiming support for
/// the log or settings APIs would only invite more traffic.
/// </summary>
public sealed class DetectionChannel : IDisposable
{
    const uint Protocol = 6;

    const uint IdProtocolVersion = 40;
    const uint IdClientName = 50;
    const uint IdDetectionStarted = 101;
    const uint IdDetectionComplete = 103;
    const uint IdRescanDevices = 140;

    const int HeaderBytes = 16;

    /// <summary>A size beyond this means the stream is out of step, not a real packet.</summary>
    const uint MaxPayloadBytes = 16 * 1024 * 1024;

    readonly object _gate = new();
    readonly object _write = new();

    TcpClient? _tcp;
    Thread? _reader;

    bool _alive;
    bool _versionKnown;
    uint _serverVersion;
    bool _running;
    int _started;
    int _completed;
    long _completedAt;

    public bool IsConnected { get { lock (_gate) return _alive; } }

    /// <summary>The server answered with protocol 6 or later, so rescans and notices work.</summary>
    public bool Supported { get { lock (_gate) return SupportedLocked; } }

    bool SupportedLocked => _alive && _versionKnown && _serverVersion >= Protocol;

    public bool DetectionRunning { get { lock (_gate) return _alive && _running; } }

    /// <summary>
    /// When the last detection ended, in <see cref="Environment.TickCount64"/> terms; 0 if
    /// none has ended on this connection.
    /// </summary>
    public long LastCompletedTicks { get { lock (_gate) return _completedAt; } }

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
        }

        var reader = new Thread(() => ReadLoop(tcp)) { IsBackground = true, Name = "OpenRGB detection channel" };
        _reader = reader;
        reader.Start();

        if (!Send(IdClientName, Encoding.ASCII.GetBytes("CaseLight detection\0")) ||
            !Send(IdProtocolVersion, BitConverter.GetBytes(Protocol)))
            return false;

        lock (_gate)
        {
            // протокол 0 на запрос версии не отвечает вовсе, отсюда ожидание с пределом
            WaitLocked(() => _versionKnown, timeoutMs);
            return SupportedLocked;
        }
    }

    /// <summary>
    /// Asks the server to find its devices again and waits for that detection to end.
    ///
    /// OpenRGB 1.0 deletes every controller first and creates them anew, so the handles
    /// left stale by sleep are gone afterwards. A request that arrives while a detection is
    /// already running is dropped by the server; the running one serves just as well, and
    /// its end is what gets waited for.
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

        if (!Send(IdRescanDevices, Array.Empty<byte>())) return false;

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

    bool Send(uint id, byte[] payload)
    {
        TcpClient? tcp;
        lock (_gate) tcp = _alive ? _tcp : null;
        if (tcp == null) return false;

        var packet = new byte[HeaderBytes + payload.Length];
        packet[0] = (byte)'O'; packet[1] = (byte)'R'; packet[2] = (byte)'G'; packet[3] = (byte)'B';
        // номер устройства 0 - уже нули
        BitConverter.GetBytes(id).CopyTo(packet, 8);
        BitConverter.GetBytes((uint)payload.Length).CopyTo(packet, 12);
        payload.CopyTo(packet, HeaderBytes);

        try
        {
            lock (_write) tcp.GetStream().Write(packet, 0, packet.Length);
            return true;
        }
        catch
        {
            Drop(tcp);
            return false;
        }
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
                if (id is not (IdProtocolVersion or IdDetectionStarted or IdDetectionComplete)) continue;

                lock (_gate)
                {
                    if (_tcp != tcp) return;

                    switch (id)
                    {
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
