using System;
using System.Buffers.Binary;
using System.Text.Json;

namespace CaseLight.GyverLamp;

/// <summary>
/// Packets of DDP, the Distributed Display Protocol (3waylabs.com/ddp), as far as the lamp
/// needs it: a frame of RGB pixels in one packet and the status query that finds lamps.
///
/// A header is ten bytes: flags, sequence, data type, destination, offset (4 bytes) and data
/// length (2 bytes), both big-endian. A frame of 16x16 is 768 bytes and fits one packet.
/// </summary>
static class Ddp
{
    public const int Port = 4048;
    public const int HeaderLength = 10;

    const byte Version1 = 0x40, VersionMask = 0xC0;
    const byte FlagTimecode = 0x10, FlagReply = 0x04, FlagQuery = 0x02, FlagPush = 0x01;

    /// <summary>Type byte: standard type RGB (1) in bits 3-5, 8 bits a channel (3) in bits 0-2.</summary>
    const byte TypeRgb8 = 0x0B;

    const byte IdDisplay = 1, IdStatus = 251;

    /// <summary>A whole frame starting at offset 0, shown on arrival.</summary>
    public static byte[] Frame(byte sequence, ReadOnlySpan<byte> rgb)
    {
        var p = new byte[HeaderLength + rgb.Length];
        p[0] = Version1 | FlagPush;
        p[1] = (byte)(sequence & 0x0F);
        p[2] = TypeRgb8;
        p[3] = IdDisplay;
        BinaryPrimitives.WriteUInt16BigEndian(p.AsSpan(8), (ushort)rgb.Length);
        rgb.CopyTo(p.AsSpan(HeaderLength));
        return p;
    }

    /// <summary>The status query a lamp answers in any effect.</summary>
    public static byte[] StatusQuery()
    {
        var p = new byte[HeaderLength];
        p[0] = Version1 | FlagQuery;
        p[3] = IdStatus;
        return p;
    }

    /// <summary>
    /// The lamp's answer to <see cref="StatusQuery"/>, or null for anything else: another
    /// DDP device, a malformed packet, firmware that is not GyverLamp.
    /// </summary>
    public static LampStatus? ParseStatus(ReadOnlySpan<byte> packet)
    {
        if (packet.Length < HeaderLength) return null;

        byte flags = packet[0];
        if ((flags & VersionMask) != Version1 || (flags & FlagReply) == 0 || packet[3] != IdStatus) return null;

        int header = (flags & FlagTimecode) != 0 ? HeaderLength + 4 : HeaderLength;
        int length = BinaryPrimitives.ReadUInt16BigEndian(packet[8..]);
        if (packet.Length < header + length) return null;

        try
        {
            using var doc = JsonDocument.Parse(packet.Slice(header, length).ToArray());
            if (!doc.RootElement.TryGetProperty("status", out var s)) return null;

            string man = Str(s, "man");
            if (!man.StartsWith("GyverLamp", StringComparison.Ordinal)) return null;

            int w = Int(s, "w"), h = Int(s, "h");
            string mac = Str(s, "mac");
            if (w <= 0 || h <= 0 || w * h > 480 || mac.Length == 0) return null;

            return new LampStatus(mac, Str(s, "name"), Str(s, "ver"), w, h, Bool(s, "on"), Bool(s, "live"));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    static string Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    static int Int(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.TryGetInt32(out int i) ? i : 0;

    static bool Bool(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;
}

/// <summary>What a lamp says about itself.</summary>
/// <param name="Mac">Identifies the lamp across changes of address and name.</param>
/// <param name="Name">The lamp's host name, as set on its settings page.</param>
/// <param name="On">The lamp is switched on.</param>
/// <param name="Live">The effect that shows frames is selected.</param>
sealed record LampStatus(string Mac, string Name, string Version, int Width, int Height, bool On, bool Live);
