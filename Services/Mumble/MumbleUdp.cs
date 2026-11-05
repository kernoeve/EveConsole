using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;

namespace EveConsole.Services.Mumble;

/// <summary>
/// How Mumble encrypts voice over UDP: AES-128 in OCB2 mode, with a 3-byte tag and one byte of the nonce on each
/// packet. Ported from the protocol's reference implementation, which is what it must agree with byte for byte.
///
/// <para>⚠️ OCB2 has a published weakness, which the reference closes by refusing the block pattern the attack
/// needs: section 9 of https://eprint.iacr.org/2019/311. That is kept here. Nothing better is on offer while
/// servers speak this: voice is what it protects, and the login, text and everything else stay on TLS.</para>
/// </summary>
public sealed class MumbleCrypt : IDisposable
{
    private const int Block = 16;
    public const int Overhead = 4;

    private readonly object _gate = new();
    private readonly byte[] _encryptIv = new byte[Block];
    private readonly byte[] _decryptIv = new byte[Block];
    private readonly byte[] _history = new byte[256];
    private Aes? _aes;

    public bool IsReady { get { lock (_gate) return _aes is not null; } }

    /// <summary>Packets that decrypted and that did not. A run of bad ones means the nonces drifted apart.</summary>
    public int Good { get; private set; }
    public int Late { get; private set; }
    public int Lost { get; private set; }

    public void SetKey(ReadOnlySpan<byte> key, ReadOnlySpan<byte> clientNonce, ReadOnlySpan<byte> serverNonce)
    {
        if (key.Length != Block || clientNonce.Length != Block || serverNonce.Length != Block)
            throw new InvalidDataException("A voice key or nonce is the wrong length.");

        lock (_gate)
        {
            _aes?.Dispose();
            _aes = Aes.Create();
            _aes.Key = key.ToArray();
            clientNonce.CopyTo(_encryptIv);
            serverNonce.CopyTo(_decryptIv);
            Array.Clear(_history);
        }
    }

    /// <summary>What to tell the server when it asks where this client's nonce has got to.</summary>
    public byte[] EncryptNonce() { lock (_gate) return [.. _encryptIv]; }

    /// <summary>The server has said where its nonce has got to.</summary>
    public void SetDecryptNonce(ReadOnlySpan<byte> nonce)
    {
        if (nonce.Length != Block) throw new InvalidDataException("A voice nonce is the wrong length.");
        lock (_gate) nonce.CopyTo(_decryptIv);
    }

    /// <summary>A packet ready to send, or null with no key yet.</summary>
    public byte[]? Encrypt(ReadOnlySpan<byte> plain)
    {
        lock (_gate)
        {
            if (_aes is null) return null;

            for (var i = 0; i < Block; i++)
                if (++_encryptIv[i] != 0) break;

            var packet = new byte[plain.Length + Overhead];
            Span<byte> tag = stackalloc byte[Block];
            OcbEncrypt(_aes, plain, packet.AsSpan(Overhead), _encryptIv, tag, modifyOnAttack: true);

            packet[0] = _encryptIv[0];
            tag[..3].CopyTo(packet.AsSpan(1));
            return packet;
        }
    }

    /// <summary>The words in a packet, or null if it is not genuine, is a replay, or has drifted too far.</summary>
    public byte[]? Decrypt(ReadOnlySpan<byte> packet)
    {
        if (packet.Length < Overhead) return null;

        lock (_gate)
        {
            if (_aes is null) return null;

            var saved   = (byte[])_decryptIv.Clone();
            var ivByte  = packet[0];
            var restore = false;
            int late = 0, lost = 0;

            if ((byte)(_decryptIv[0] + 1) == ivByte)
            {
                // In order, as expected.
                if (ivByte > _decryptIv[0]) _decryptIv[0] = ivByte;
                else if (ivByte < _decryptIv[0])
                {
                    _decryptIv[0] = ivByte;
                    Carry();
                }
                else return null;
            }
            else
            {
                // Out of order, or a repeat.
                var diff = ivByte - _decryptIv[0];
                if (diff > 128) diff -= 256;
                else if (diff < -128) diff += 256;

                if (ivByte < _decryptIv[0] && diff is > -30 and < 0)
                {
                    // Late, with no wraparound.
                    late = 1; lost = -1;
                    _decryptIv[0] = ivByte;
                    restore = true;
                }
                else if (ivByte > _decryptIv[0] && diff is > -30 and < 0)
                {
                    // Late, from before the last wraparound.
                    late = 1; lost = -1;
                    _decryptIv[0] = ivByte;
                    for (var i = 1; i < Block; i++)
                        if (_decryptIv[i]-- != 0) break;
                    restore = true;
                }
                else if (ivByte > _decryptIv[0] && diff > 0)
                {
                    // A few lost, with no wraparound.
                    lost = ivByte - _decryptIv[0] - 1;
                    _decryptIv[0] = ivByte;
                }
                else if (ivByte < _decryptIv[0] && diff > 0)
                {
                    // A few lost, across a wraparound.
                    lost = 256 - _decryptIv[0] + ivByte - 1;
                    _decryptIv[0] = ivByte;
                    Carry();
                }
                else return null;

                if (_history[_decryptIv[0]] == _decryptIv[1])
                {
                    saved.CopyTo(_decryptIv, 0);
                    return null;
                }
            }

            var plain = new byte[packet.Length - Overhead];
            Span<byte> tag = stackalloc byte[Block];
            var clean = OcbDecrypt(_aes, packet[Overhead..], plain, _decryptIv, tag);

            if (!clean || !tag[..3].SequenceEqual(packet.Slice(1, 3)))
            {
                saved.CopyTo(_decryptIv, 0);
                return null;
            }

            _history[_decryptIv[0]] = _decryptIv[1];
            if (restore) saved.CopyTo(_decryptIv, 0);

            Good++;
            Late += late;
            Lost += lost;
            return plain;
        }

        void Carry()
        {
            for (var i = 1; i < Block; i++)
                if (++_decryptIv[i] != 0) break;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _aes?.Dispose();
            _aes = null;
        }
    }

    // ── OCB2 ──────────────────────────────────────────────────────────────────

    private static void Times2(Span<byte> block)
    {
        var carry = block[0] >> 7;
        for (var i = 0; i < Block - 1; i++) block[i] = (byte)(block[i] << 1 | block[i + 1] >> 7);
        block[Block - 1] = (byte)(block[Block - 1] << 1 ^ carry * 0x87);
    }

    private static void Times3(Span<byte> block)
    {
        Span<byte> copy = stackalloc byte[Block];
        block.CopyTo(copy);
        Times2(block);
        for (var i = 0; i < Block; i++) block[i] ^= copy[i];
    }

    private static void Xor(Span<byte> to, ReadOnlySpan<byte> a, ReadOnlySpan<byte> b)
    {
        for (var i = 0; i < Block; i++) to[i] = (byte)(a[i] ^ b[i]);
    }

    private static void Encipher(Aes aes, ReadOnlySpan<byte> from, Span<byte> to)
    {
        Span<byte> buffer = stackalloc byte[Block];
        aes.EncryptEcb(from, buffer, PaddingMode.None);
        buffer.CopyTo(to);
    }

    private static void Decipher(Aes aes, ReadOnlySpan<byte> from, Span<byte> to)
    {
        Span<byte> buffer = stackalloc byte[Block];
        aes.DecryptEcb(from, buffer, PaddingMode.None);
        buffer.CopyTo(to);
    }

    /// <summary>The cipher itself, public so the published test vectors can be run against it.</summary>
    public static void OcbEncrypt(Aes aes, ReadOnlySpan<byte> plain, Span<byte> encrypted, ReadOnlySpan<byte> nonce, Span<byte> tag, bool modifyOnAttack)
    {
        Span<byte> delta = stackalloc byte[Block], checksum = stackalloc byte[Block], tmp = stackalloc byte[Block], pad = stackalloc byte[Block];
        Encipher(aes, nonce, delta);
        checksum.Clear();

        var offset = 0;
        var length = plain.Length;
        while (length > Block)
        {
            // The attack needs a second-last block of all zero but its last byte, and flipping one bit of
            // such a block costs voice almost nothing.
            var flip = false;
            var block = plain.Slice(offset, Block);
            if (modifyOnAttack && length - Block <= Block)
            {
                byte any = 0;
                for (var i = 0; i < Block - 1; i++) any |= block[i];
                flip = any == 0;
            }

            Times2(delta);
            Xor(tmp, delta, block);
            if (flip) tmp[0] ^= 1;
            Encipher(aes, tmp, tmp);
            Xor(encrypted.Slice(offset, Block), delta, tmp);
            Xor(checksum, checksum, block);
            if (flip) checksum[0] ^= 1;

            offset += Block;
            length -= Block;
        }

        Times2(delta);
        tmp.Clear();
        tmp[Block - 1] = (byte)(length * 8);
        Xor(tmp, tmp, delta);
        Encipher(aes, tmp, pad);

        pad.CopyTo(tmp);
        plain.Slice(offset, length).CopyTo(tmp);
        Xor(checksum, checksum, tmp);
        Xor(tmp, pad, tmp);
        tmp[..length].CopyTo(encrypted[offset..]);

        Times3(delta);
        Xor(tmp, delta, checksum);
        Encipher(aes, tmp, tag);
    }

    /// <summary>False if the packet has the pattern the published attack depends on, whatever its tag says.</summary>
    public static bool OcbDecrypt(Aes aes, ReadOnlySpan<byte> encrypted, Span<byte> plain, ReadOnlySpan<byte> nonce, Span<byte> tag)
    {
        Span<byte> delta = stackalloc byte[Block], checksum = stackalloc byte[Block], tmp = stackalloc byte[Block], pad = stackalloc byte[Block];
        Encipher(aes, nonce, delta);
        checksum.Clear();

        var offset = 0;
        var length = encrypted.Length;
        while (length > Block)
        {
            Times2(delta);
            Xor(tmp, delta, encrypted.Slice(offset, Block));
            Decipher(aes, tmp, tmp);
            Xor(plain.Slice(offset, Block), delta, tmp);
            Xor(checksum, checksum, plain.Slice(offset, Block));
            offset += Block;
            length -= Block;
        }

        Times2(delta);
        tmp.Clear();
        tmp[Block - 1] = (byte)(length * 8);
        Xor(tmp, tmp, delta);
        Encipher(aes, tmp, pad);

        tmp.Clear();
        encrypted.Slice(offset, length).CopyTo(tmp);
        Xor(tmp, tmp, pad);
        Xor(checksum, checksum, tmp);
        tmp[..length].CopyTo(plain[offset..]);

        // In an attack the last block decrypts to delta and the length. The length is at most 128, so all but the
        // last byte being equal is enough to say so.
        var clean = !tmp[..(Block - 1)].SequenceEqual(delta[..(Block - 1)]);

        Times3(delta);
        Xor(tmp, delta, checksum);
        Encipher(aes, tmp, tag);
        return clean;
    }
}

/// <summary>
/// Voice over UDP, for as long as it gets through. ⚠️ Plenty of networks block it, so it is only used once the
/// server has answered a ping on it, and given up the moment it stops: the TLS connection carries voice otherwise,
/// and telling the server so by sending there is all it takes for it to answer the same way.
/// </summary>
public sealed class MumbleUdpChannel : IDisposable
{
    private const int PingType = 1;
    private const int MaxDatagram = 1024;

    private static readonly TimeSpan PingEvery = TimeSpan.FromSeconds(5);

    /// <summary>Three pings unanswered and it is no longer to be trusted.</summary>
    private static readonly TimeSpan Silence = TimeSpan.FromSeconds(16);

    /// <summary>Bad packets for this long, and the nonces need putting right.</summary>
    private static readonly TimeSpan ResyncAfter = TimeSpan.FromSeconds(5);

    private readonly MumbleCrypt _crypt;
    private readonly UdpClient _socket;
    private readonly Func<byte[], Task> _deliver;
    private readonly Action _askForResync;
    private readonly Action _onSilent;
    private readonly CancellationTokenSource _stop = new();
    private long _lastAnswerTicks;
    private long _lastGoodTicks = DateTime.UtcNow.Ticks;
    private long _lastResyncTicks = DateTime.UtcNow.Ticks;

    private MumbleUdpChannel(UdpClient socket, MumbleCrypt crypt, Func<byte[], Task> deliver, Action askForResync, Action onSilent)
    {
        _onSilent = onSilent;
        _socket = socket;
        _crypt = crypt;
        _deliver = deliver;
        _askForResync = askForResync;
    }

    /// <summary>Whether the server has answered here lately, so voice can go this way.</summary>
    public bool IsWorking => DateTime.UtcNow.Ticks - Interlocked.Read(ref _lastAnswerTicks) < Silence.Ticks;

    /// <summary>Opens the socket and starts asking. Null if this machine cannot: TCP carries on regardless.</summary>
    public static async Task<MumbleUdpChannel?> OpenAsync(string host, int port, MumbleCrypt crypt, Func<byte[], Task> deliver, Action askForResync, Action onSilent, CancellationToken ct)
    {
        try
        {
            var addresses = await Dns.GetHostAddressesAsync(host, ct);
            var address = addresses.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork) ?? addresses.FirstOrDefault();
            if (address is null) return null;

            var socket = new UdpClient(address.AddressFamily);
            socket.Connect(address, port);

            var channel = new MumbleUdpChannel(socket, crypt, deliver, askForResync, onSilent);
            _ = channel.ReceiveLoopAsync();
            _ = channel.PingLoopAsync();
            return channel;
        }
        catch (Exception e) when (e is SocketException or ArgumentException) { return null; }
    }

    /// <summary>Sends one voice packet, in the same form as on TCP. False if it could not go this way.</summary>
    public bool TrySend(ReadOnlySpan<byte> voice)
    {
        if (!IsWorking || _crypt.Encrypt(voice) is not { } packet) return false;

        try
        {
            _socket.Send(packet, packet.Length);
            return true;
        }
        catch (Exception e) when (e is SocketException or ObjectDisposedException) { return false; }
    }

    private async Task PingLoopAsync()
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                using (var to = new MemoryStream(12))
                {
                    to.WriteByte(PingType << 5);
                    MumbleVarint.Write(to, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                    if (_crypt.Encrypt(to.ToArray()) is { } packet) await _socket.SendAsync(packet, _stop.Token);
                }

                await Task.Delay(PingEvery, _stop.Token);

                if (!IsWorking) _onSilent();
            }
        }
        catch (OperationCanceledException) { /* closing */ }
        catch (ObjectDisposedException) { /* closing */ }
        catch (SocketException) { /* the network is down: the TLS side will find that out and reconnect */ }
    }

    private async Task ReceiveLoopAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            byte[] packet;
            try { packet = (await _socket.ReceiveAsync(_stop.Token)).Buffer; }
            catch (OperationCanceledException) { return; }
            catch (ObjectDisposedException) { return; }
            catch (SocketException)
            {
                // Windows reports an earlier send refused by the far end as a failed receive. Not the end of anything.
                try { await Task.Delay(TimeSpan.FromSeconds(1), _stop.Token); }
                catch (OperationCanceledException) { return; }
                continue;
            }

            if (packet.Length > MaxDatagram) continue;

            var plain = _crypt.Decrypt(packet);
            var now   = DateTime.UtcNow.Ticks;

            if (plain is null)
            {
                // Not ours, or the nonces have drifted. Asking is cheap, so long as it is not on every packet.
                if (now - Interlocked.Read(ref _lastGoodTicks) > ResyncAfter.Ticks && now - Interlocked.Read(ref _lastResyncTicks) > ResyncAfter.Ticks)
                {
                    Interlocked.Exchange(ref _lastResyncTicks, now);
                    _askForResync();
                }
                continue;
            }

            Interlocked.Exchange(ref _lastGoodTicks, now);
            if (plain.Length == 0) continue;

            if (plain[0] >> 5 == PingType)
            {
                Interlocked.Exchange(ref _lastAnswerTicks, now);
                continue;
            }

            // Hearing anything back is as good an answer as a ping.
            Interlocked.Exchange(ref _lastAnswerTicks, now);

            // ⚠️ Whatever goes wrong costs only this packet. Ending this loop would leave the server still sending
            // here and nobody listening.
            try { await _deliver(plain); }
            catch (Exception e) when (e is not OperationCanceledException) { _ = e; }
        }
    }

    public void Dispose()
    {
        _stop.Cancel();
        _socket.Dispose();
    }
}
