using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace CaseLight.NuPhy;

/// <summary>
/// The vendor interface of a keyboard: 64-byte reports with report id 0 both ways.
///
/// Input reports are read on a thread of their own and queued. The keyboard answers every
/// command, and a queue nobody empties would hand the next command the answer to an old
/// one; so every exchange empties it first and then waits for the answer with its own
/// command code and offset. NuPhyIO, if it is running, gets the same answers and sends its
/// own commands, which is why both are checked rather than taking the first report.
/// </summary>
sealed class HidChannel : IDisposable
{
    const int ReportSize = 65;

    readonly FileStream _stream;
    readonly Thread _reader;
    readonly BlockingCollection<byte[]> _replies = new(64);
    volatile bool _closed;

    HidChannel(FileStream stream)
    {
        _stream = stream;
        _reader = new Thread(ReadLoop) { IsBackground = true, Name = "NuPhy HID reader" };
        _reader.Start();
    }

    /// <summary>True once the device has gone: unplugged, or the machine slept.</summary>
    public bool Closed => _closed;

    public static HidChannel? Open(string path)
    {
        var handle = CreateFile(path, GenericRead | GenericWrite, FileShareRead | FileShareWrite,
                                IntPtr.Zero, OpenExisting, FileFlagOverlapped, IntPtr.Zero);
        if (handle.IsInvalid) return null;

        try { return new HidChannel(new FileStream(handle, FileAccess.ReadWrite, 0, isAsync: true)); }
        catch
        {
            handle.Dispose();
            return null;
        }
    }

    void ReadLoop()
    {
        var buf = new byte[ReportSize];
        try
        {
            while (!_closed)
            {
                int n = _stream.Read(buf, 0, buf.Length);
                if (n <= 1) continue;

                // без номера репорта: дальше пакет считается с нуля, как в протоколе
                var packet = buf[1..n];
                if (!_replies.TryAdd(packet)) { _replies.TryTake(out _); _replies.TryAdd(packet); }
            }
        }
        catch
        {
            _closed = true;
        }
    }

    /// <summary>Sends a packet and waits for the answer to it; null if none came in time.</summary>
    public byte[]? Transact(byte[] packet, int timeoutMs)
    {
        while (_replies.TryTake(out _)) { }

        var report = new byte[ReportSize];
        packet.AsSpan(0, Math.Min(packet.Length, ReportSize - 1)).CopyTo(report.AsSpan(1));

        try { _stream.Write(report, 0, report.Length); }
        catch
        {
            _closed = true;
            return null;
        }

        long deadline = Environment.TickCount64 + timeoutMs;
        while (true)
        {
            int left = (int)(deadline - Environment.TickCount64);
            if (left <= 0 || _closed) return null;

            // Смещение сверяется вместе с командой: другая программа, читающая кадр той же
            // командой, получает и наши ответы, а мы её, и два потока путали куски кадра.
            if (_replies.TryTake(out var reply, left) && reply.Length > 6
                && reply[1] == packet[1] && reply[5] == packet[5] && reply[6] == packet[6])
                return reply;
        }
    }

    public void Dispose()
    {
        _closed = true;
        try { _stream.Dispose(); } catch { /* устройство уже ушло */ }
        _reader.Join(500);
        _replies.Dispose();
    }

    // ---- поиск ------------------------------------------------------------

    static readonly Guid HidInterface = new("4d1e55b2-f16f-11cf-88cb-001111000030");

    /// <summary>Paths of the HID interfaces present now whose path contains <paramref name="match"/>.</summary>
    public static List<string> Find(string match)
    {
        var found = new List<string>();
        var guid = HidInterface;

        for (int attempt = 0; attempt < 3; attempt++)
        {
            if (CM_Get_Device_Interface_List_Size(out uint size, ref guid, null, 0) != 0 || size <= 1) return found;

            var chars = new char[size];
            // список может вырасти между двумя вызовами, тогда просим заново
            if (CM_Get_Device_Interface_List(ref guid, null, chars, size, 0) != 0) continue;

            foreach (var path in new string(chars).Split('\0', StringSplitOptions.RemoveEmptyEntries))
                if (path.Contains(match, StringComparison.OrdinalIgnoreCase))
                    found.Add(path);
            return found;
        }

        return found;
    }

    const uint GenericRead = 0x80000000, GenericWrite = 0x40000000;
    const uint FileShareRead = 1, FileShareWrite = 2, OpenExisting = 3, FileFlagOverlapped = 0x40000000;

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security,
                                            uint creation, uint flags, IntPtr template);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    static extern int CM_Get_Device_Interface_List_Size(out uint size, ref Guid guid, string? deviceId, uint flags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    static extern int CM_Get_Device_Interface_List(ref Guid guid, string? deviceId, [Out] char[] buffer, uint length, uint flags);
}
