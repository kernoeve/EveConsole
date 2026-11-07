using System.Text;

namespace EveConsole.Services.Mumble;

/// <summary>Mumble's control messages, by the number that goes on the wire.</summary>
public enum MumbleMessage : ushort
{
    Version = 0, UdpTunnel = 1, Authenticate = 2, Ping = 3, Reject = 4, ServerSync = 5,
    ChannelRemove = 6, ChannelState = 7, UserRemove = 8, UserState = 9, BanList = 10, TextMessage = 11,
    PermissionDenied = 12, Acl = 13, QueryUsers = 14, CryptSetup = 15, ContextActionModify = 16,
    ContextAction = 17, UserList = 18, VoiceTarget = 19, PermissionQuery = 20, CodecVersion = 21,
    UserStats = 22, RequestBlob = 23, ServerConfig = 24, SuggestConfig = 25,
}

/// <summary>
/// Writes the small part of Protocol Buffers that Mumble's messages use.
///
/// <para>By hand rather than a generated library: about thirty fields of a stable, published format are
/// all this client touches, and an unknown field from a newer server is simply skipped by the reader.</para>
/// </summary>
public sealed class PbWriter
{
    private readonly MemoryStream _out = new();

    public PbWriter UInt(int field, ulong value) { Key(field, 0); Varint(value); return this; }
    public PbWriter Bool(int field, bool value) => UInt(field, value ? 1UL : 0UL);
    public PbWriter Bytes(int field, ReadOnlySpan<byte> value) { Key(field, 2); Varint((ulong)value.Length); _out.Write(value); return this; }
    public PbWriter Text(int field, string value) => Bytes(field, Encoding.UTF8.GetBytes(value));
    public byte[] ToArray() => _out.ToArray();

    private void Key(int field, int wire) => Varint((ulong)(field << 3 | wire));

    private void Varint(ulong value)
    {
        while (value >= 0x80)
        {
            _out.WriteByte((byte)(value | 0x80));
            value >>= 7;
        }
        _out.WriteByte((byte)value);
    }
}

/// <summary>Reads a Protocol Buffers message into its fields. A field that repeats keeps every value.</summary>
public sealed class PbMessage
{
    private readonly Dictionary<int, List<ulong>> _numbers = [];
    private readonly Dictionary<int, List<byte[]>> _blobs = [];

    /// <exception cref="InvalidDataException">The bytes are not a well-formed message.</exception>
    public PbMessage(ReadOnlySpan<byte> data)
    {
        var at = 0;
        while (at < data.Length)
        {
            var key   = ReadVarint(data, ref at);
            var field = (int)(key >> 3);

            switch ((int)(key & 7))
            {
                case 0:
                    Add(_numbers, field, ReadVarint(data, ref at));
                    break;
                case 1:
                    if (at + 8 > data.Length) throw new InvalidDataException("A field runs past the end of the message.");
                    Add(_numbers, field, BitConverter.ToUInt64(data.Slice(at, 8)));
                    at += 8;
                    break;
                case 2:
                    var length = ReadVarint(data, ref at);
                    if (length > (ulong)(data.Length - at)) throw new InvalidDataException("A field runs past the end of the message.");
                    Add(_blobs, field, data.Slice(at, (int)length).ToArray());
                    at += (int)length;
                    break;
                case 5:
                    if (at + 4 > data.Length) throw new InvalidDataException("A field runs past the end of the message.");
                    Add(_numbers, field, BitConverter.ToUInt32(data.Slice(at, 4)));
                    at += 4;
                    break;
                default:
                    throw new InvalidDataException($"Unknown field type {key & 7}.");
            }
        }
    }

    private static void Add<T>(Dictionary<int, List<T>> map, int field, T value)
    {
        if (!map.TryGetValue(field, out var list)) map[field] = list = [];
        list.Add(value);
    }

    private static ulong ReadVarint(ReadOnlySpan<byte> data, ref int at)
    {
        ulong value = 0;
        for (var shift = 0; shift < 70; shift += 7)
        {
            if (at >= data.Length) throw new InvalidDataException("A number runs past the end of the message.");
            var b = data[at++];
            value |= (ulong)(b & 0x7F) << shift;
            if ((b & 0x80) == 0) return value;
        }
        throw new InvalidDataException("A number is too long.");
    }

    public bool Has(int field) => _numbers.ContainsKey(field) || _blobs.ContainsKey(field);
    public ulong? UInt(int field) => _numbers.TryGetValue(field, out var v) ? v[^1] : null;
    public bool? Bool(int field) => _numbers.TryGetValue(field, out var v) ? v[^1] != 0 : null;
    public byte[]? Bytes(int field) => _blobs.TryGetValue(field, out var v) ? v[^1] : null;
    public string? Text(int field) => _blobs.TryGetValue(field, out var v) ? Encoding.UTF8.GetString(v[^1]) : null;
    public IReadOnlyList<ulong> UInts(int field) => _numbers.TryGetValue(field, out var v) ? v : [];
    public IReadOnlyList<byte[]> AllBytes(int field) => _blobs.TryGetValue(field, out var v) ? v : [];
}

/// <summary>
/// The variable-length number Mumble's voice packets use, which is its own scheme and not Protocol
/// Buffers': the leading bits of the first byte say how long it is.
/// </summary>
public static class MumbleVarint
{
    public static void Write(Stream to, long value)
    {
        var i = value;

        if (i < 0)
        {
            i = ~i;
            if (i <= 0x3)
            {
                to.WriteByte((byte)(0xFC | i));
                return;
            }
            to.WriteByte(0xF8);
        }

        if (i < 0x80)
        {
            to.WriteByte((byte)i);
        }
        else if (i < 0x4000)
        {
            to.WriteByte((byte)((i >> 8) | 0x80));
            to.WriteByte((byte)(i & 0xFF));
        }
        else if (i < 0x200000)
        {
            to.WriteByte((byte)((i >> 16) | 0xC0));
            to.WriteByte((byte)((i >> 8) & 0xFF));
            to.WriteByte((byte)(i & 0xFF));
        }
        else if (i < 0x10000000)
        {
            to.WriteByte((byte)((i >> 24) | 0xE0));
            to.WriteByte((byte)((i >> 16) & 0xFF));
            to.WriteByte((byte)((i >> 8) & 0xFF));
            to.WriteByte((byte)(i & 0xFF));
        }
        else if (i <= uint.MaxValue)
        {
            to.WriteByte(0xF0);
            for (var s = 24; s >= 0; s -= 8) to.WriteByte((byte)((i >> s) & 0xFF));
        }
        else
        {
            to.WriteByte(0xF4);
            for (var s = 56; s >= 0; s -= 8) to.WriteByte((byte)((i >> s) & 0xFF));
        }
    }

    /// <exception cref="InvalidDataException">The packet ends before the number does.</exception>
    public static long Read(ReadOnlySpan<byte> data, ref int at)
    {
        byte Next(ReadOnlySpan<byte> d, ref int a) =>
            a < d.Length ? d[a++] : throw new InvalidDataException("A voice packet ended part way through a number.");

        long Bytes(ReadOnlySpan<byte> d, ref int a, int n, long start)
        {
            var value = start;
            for (var k = 0; k < n; k++) value = value << 8 | Next(d, ref a);
            return value;
        }

        var first = Next(data, ref at);

        if ((first & 0x80) == 0x00) return first & 0x7F;
        if ((first & 0xC0) == 0x80) return Bytes(data, ref at, 1, first & 0x3F);
        if ((first & 0xE0) == 0xC0) return Bytes(data, ref at, 2, first & 0x1F);
        if ((first & 0xF0) == 0xE0) return Bytes(data, ref at, 3, first & 0x0F);
        if ((first & 0xFC) == 0xF0) return Bytes(data, ref at, 4, 0);
        if ((first & 0xFC) == 0xF4) return Bytes(data, ref at, 8, 0);

        // ⚠️ One negation and no more. Each one otherwise recurses, so a packet of a megabyte of this byte overflows
        // the stack, which nothing can catch and which takes the whole app with it.
        if ((first & 0xFC) == 0xF8)
        {
            if (at < data.Length && (data[at] & 0xF8) == 0xF8) throw new InvalidDataException("A voice packet has a number negated twice.");
            return ~Read(data, ref at);
        }
        return ~(long)(first & 0x03);
    }
}

/// <summary>One voice packet in the format servers keep for clients of 1.4 and earlier.</summary>
/// <param name="Target">0 for the channel, 1 to 30 a saved voice target, 31 the server's loopback.</param>
/// <param name="Session">The speaker, on the way in. Absent on the way out.</param>
/// <param name="Last">The speaker has let go of the key.</param>
public readonly record struct MumbleVoicePacket(int Target, uint Session, long Sequence, byte[] Opus, bool Last)
{
    private const int OpusType = 4;
    private const int LastFrameBit = 0x2000;

    /// <summary>What this client sends: no session, since the server knows who it came from.</summary>
    public byte[] Encode()
    {
        using var to = new MemoryStream(Opus.Length + 8);
        to.WriteByte((byte)(OpusType << 5 | Target & 0x1F));
        MumbleVarint.Write(to, Sequence);
        MumbleVarint.Write(to, Opus.Length | (Last ? LastFrameBit : 0));
        to.Write(Opus);
        return to.ToArray();
    }

    /// <summary>What the server relays. Null for a type this client does not play.</summary>
    /// <exception cref="InvalidDataException">The packet is malformed.</exception>
    public static MumbleVoicePacket? Decode(ReadOnlySpan<byte> data)
    {
        if (data.Length < 2) return null;

        var header = data[0];
        if (header >> 5 != OpusType) return null;   // old codecs and pings: nothing to play

        var at       = 1;
        var session  = MumbleVarint.Read(data, ref at);
        var sequence = MumbleVarint.Read(data, ref at);
        var size     = MumbleVarint.Read(data, ref at);

        var length = (int)(size & 0x1FFF);
        if (length > data.Length - at) throw new InvalidDataException("A voice packet says it is longer than it is.");

        return new MumbleVoicePacket(header & 0x1F, (uint)session, sequence, data.Slice(at, length).ToArray(), (size & LastFrameBit) != 0);
    }
}
