using System.Buffers.Binary;
using System.Security.Cryptography;

namespace AsheTrader.Core.Networking;

/// <summary>
/// One direction of a session. Header length and sequence are authenticated. Session keys
/// and nonce prefixes are derived independently per direction from a fresh handshake.
/// One writer/reader owns each codec; it is deliberately not shared across threads.
/// </summary>
internal sealed class SecureFrameCodec : IDisposable
{
    internal const int HeaderLength = 12;
    internal const int TagLength = 16;
    private readonly AesGcm cipher;
    private readonly byte[] noncePrefix;
    private ulong nextSequence;

    internal SecureFrameCodec(ReadOnlySpan<byte> key, ReadOnlySpan<byte> noncePrefix)
    {
        if (key.Length != 32 || noncePrefix.Length != 4) throw new ArgumentException("Invalid session key material.");
        cipher = new AesGcm(key, TagLength);
        this.noncePrefix = noncePrefix.ToArray();
    }

    internal byte[] Seal(ReadOnlySpan<byte> plaintext)
    {
        if (plaintext.Length is < 1 or > LanPeer.MaxMessageBytes) throw new InvalidDataException("Message exceeds the transport limit.");
        if (nextSequence == ulong.MaxValue) throw new CryptographicException("Session sequence exhausted.");
        var frame = new byte[HeaderLength + plaintext.Length + TagLength];
        BinaryPrimitives.WriteUInt32BigEndian(frame, (uint)plaintext.Length);
        BinaryPrimitives.WriteUInt64BigEndian(frame.AsSpan(4), nextSequence);
        Span<byte> nonce = stackalloc byte[12];
        noncePrefix.CopyTo(nonce);
        BinaryPrimitives.WriteUInt64BigEndian(nonce[4..], nextSequence);
        cipher.Encrypt(nonce, plaintext, frame.AsSpan(HeaderLength, plaintext.Length), frame.AsSpan(HeaderLength + plaintext.Length), frame.AsSpan(0, HeaderLength));
        nextSequence++;
        return frame;
    }

    internal byte[] Open(ReadOnlySpan<byte> frame)
    {
        if (frame.Length < HeaderLength + TagLength + 1) throw new InvalidDataException("Truncated encrypted frame.");
        var length = ReadPayloadLength(frame[..HeaderLength]);
        if (frame.Length != HeaderLength + length + TagLength) throw new InvalidDataException("Incorrect encrypted frame length.");
        var sequence = BinaryPrimitives.ReadUInt64BigEndian(frame[4..]);
        if (sequence != nextSequence || sequence == ulong.MaxValue) throw new CryptographicException("Unexpected or replayed frame sequence.");
        Span<byte> nonce = stackalloc byte[12];
        noncePrefix.CopyTo(nonce);
        BinaryPrimitives.WriteUInt64BigEndian(nonce[4..], sequence);
        var plaintext = new byte[length];
        cipher.Decrypt(nonce, frame.Slice(HeaderLength, length), frame[(HeaderLength + length)..], plaintext, frame[..HeaderLength]);
        nextSequence++;
        return plaintext;
    }

    internal static int ReadPayloadLength(ReadOnlySpan<byte> header)
    {
        if (header.Length != HeaderLength) throw new InvalidDataException("Invalid encrypted header.");
        var length = BinaryPrimitives.ReadUInt32BigEndian(header);
        if (length is 0 or > LanPeer.MaxMessageBytes) throw new InvalidDataException("Encrypted frame exceeds the transport limit.");
        return checked((int)length);
    }

    public void Dispose()
    {
        cipher.Dispose();
        CryptographicOperations.ZeroMemory(noncePrefix);
    }
}
