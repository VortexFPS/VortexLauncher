using System.Buffers.Binary;

namespace Launcher.Core.GameControl;

/// <summary>MD4 and HMAC-MD4 (RFC 1320, RFC 2104).
///
/// Present because DarkPlaces srcon authenticates with HMAC-MD4 and the BCL dropped MD4 long ago. This
/// is an interop obligation, not a choice: it exists to speak an inherited wire protocol to a game
/// server on loopback, and it must never be used for anything security-relevant. MD4 has been broken
/// for collisions since 1995.
///
/// Mirrors src/VortexArena.Net/Md4.cs in the game repo. Shared golden vectors keep the two honest;
/// duplicating 90 lines is a better seam than making the game depend on the launcher.</summary>
public static class Md4
{
    private const int BlockSize = 64;

    public static byte[] Hash(ReadOnlySpan<byte> message)
    {
        uint a = 0x67452301, b = 0xefcdab89, c = 0x98badcfe, d = 0x10325476;

        var padded = Pad(message);
        Span<uint> x = stackalloc uint[16];

        for (var offset = 0; offset < padded.Length; offset += BlockSize)
        {
            for (var i = 0; i < 16; i++)
                x[i] = BinaryPrimitives.ReadUInt32LittleEndian(padded.AsSpan(offset + i * 4, 4));

            uint aa = a, bb = b, cc = c, dd = d;

            // Round 1: F(x,y,z) = (x & y) | (~x & z), words in order, shifts cycling 3/7/11/19.
            ReadOnlySpan<int> s1 = [3, 7, 11, 19];
            for (var i = 0; i < 16; i++)
            {
                var shift = s1[i % 4];
                switch (i % 4)
                {
                    case 0: a = Rol(a + F(b, c, d) + x[i], shift); break;
                    case 1: d = Rol(d + F(a, b, c) + x[i], shift); break;
                    case 2: c = Rol(c + F(d, a, b) + x[i], shift); break;
                    default: b = Rol(b + F(c, d, a) + x[i], shift); break;
                }
            }

            // Round 2: G, column order, shifts 3/5/9/13, constant 0x5A827999.
            ReadOnlySpan<int> k2 = [0, 4, 8, 12, 1, 5, 9, 13, 2, 6, 10, 14, 3, 7, 11, 15];
            ReadOnlySpan<int> s2 = [3, 5, 9, 13];
            for (var i = 0; i < 16; i++)
            {
                var shift = s2[i % 4];
                var w = x[k2[i]] + 0x5A827999u;
                switch (i % 4)
                {
                    case 0: a = Rol(a + G(b, c, d) + w, shift); break;
                    case 1: d = Rol(d + G(a, b, c) + w, shift); break;
                    case 2: c = Rol(c + G(d, a, b) + w, shift); break;
                    default: b = Rol(b + G(c, d, a) + w, shift); break;
                }
            }

            // Round 3: H (xor), bit-reversed order, shifts 3/9/11/15, constant 0x6ED9EBA1.
            ReadOnlySpan<int> k3 = [0, 8, 4, 12, 2, 10, 6, 14, 1, 9, 5, 13, 3, 11, 7, 15];
            ReadOnlySpan<int> s3 = [3, 9, 11, 15];
            for (var i = 0; i < 16; i++)
            {
                var shift = s3[i % 4];
                var w = x[k3[i]] + 0x6ED9EBA1u;
                switch (i % 4)
                {
                    case 0: a = Rol(a + H(b, c, d) + w, shift); break;
                    case 1: d = Rol(d + H(a, b, c) + w, shift); break;
                    case 2: c = Rol(c + H(d, a, b) + w, shift); break;
                    default: b = Rol(b + H(c, d, a) + w, shift); break;
                }
            }

            a += aa; b += bb; c += cc; d += dd;
        }

        var digest = new byte[16];
        BinaryPrimitives.WriteUInt32LittleEndian(digest.AsSpan(0), a);
        BinaryPrimitives.WriteUInt32LittleEndian(digest.AsSpan(4), b);
        BinaryPrimitives.WriteUInt32LittleEndian(digest.AsSpan(8), c);
        BinaryPrimitives.WriteUInt32LittleEndian(digest.AsSpan(12), d);
        return digest;
    }

    /// <summary>HMAC-MD4 (RFC 2104). DP keys this with rcon_password and hashes exactly
    /// "&lt;time-or-challenge&gt; &lt;command&gt;".</summary>
    public static byte[] Hmac(ReadOnlySpan<byte> key, ReadOnlySpan<byte> message)
    {
        Span<byte> block = stackalloc byte[BlockSize];
        if (key.Length > BlockSize)
            Hash(key).CopyTo(block);
        else
            key.CopyTo(block);

        Span<byte> inner = stackalloc byte[BlockSize];
        Span<byte> outer = stackalloc byte[BlockSize];
        for (var i = 0; i < BlockSize; i++)
        {
            inner[i] = (byte)(block[i] ^ 0x36);
            outer[i] = (byte)(block[i] ^ 0x5c);
        }

        var innerInput = new byte[BlockSize + message.Length];
        inner.CopyTo(innerInput);
        message.CopyTo(innerInput.AsSpan(BlockSize));
        var innerHash = Hash(innerInput);

        var outerInput = new byte[BlockSize + innerHash.Length];
        outer.CopyTo(outerInput);
        innerHash.CopyTo(outerInput.AsSpan(BlockSize));
        return Hash(outerInput);
    }

    private static byte[] Pad(ReadOnlySpan<byte> message)
    {
        var padLength = (message.Length % BlockSize < 56 ? 56 : 120) - message.Length % BlockSize;
        var padded = new byte[message.Length + padLength + 8];
        message.CopyTo(padded);
        padded[message.Length] = 0x80;
        BinaryPrimitives.WriteUInt64LittleEndian(
            padded.AsSpan(padded.Length - 8), (ulong)message.Length * 8);
        return padded;
    }

    private static uint F(uint x, uint y, uint z) => (x & y) | (~x & z);
    private static uint G(uint x, uint y, uint z) => (x & y) | (x & z) | (y & z);
    private static uint H(uint x, uint y, uint z) => x ^ y ^ z;
    private static uint Rol(uint value, int bits) => (value << bits) | (value >> (32 - bits));
}
