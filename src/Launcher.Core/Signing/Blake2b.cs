namespace Launcher.Core.Signing;

/// <summary>BLAKE2b-512 (RFC 7693), unkeyed, one-shot.
///
/// It is here for exactly one reason: minisign's prehashed signature mode ("ED") signs
/// BLAKE2b-512 of the file rather than the file itself, and the BCL has no BLAKE2b. Supporting only
/// the legacy "Ed" mode would have saved this file at the cost of a booby trap — whether the release
/// job produces a signature this launcher can read would depend on a minisign flag, and getting it
/// wrong would be discovered by players failing to update, not by CI.
///
/// A hash is a much safer thing to hand-write than a signature scheme: a mistake produces a wrong
/// digest, which fails verification closed. Pinned to the RFC 7693 vectors in SigningTests.</summary>
public static class Blake2b
{
    private static readonly ulong[] Iv =
    [
        0x6a09e667f3bcc908UL, 0xbb67ae8584caa73bUL, 0x3c6ef372fe94f82bUL, 0xa54ff53a5f1d36f1UL,
        0x510e527fade682d1UL, 0x9b05688c2b3e6c1fUL, 0x1f83d9abfb41bd6bUL, 0x5be0cd19137e2179UL,
    ];

    private static readonly byte[][] Sigma =
    [
        [0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15],
        [14, 10, 4, 8, 9, 15, 13, 6, 1, 12, 0, 2, 11, 7, 5, 3],
        [11, 8, 12, 0, 5, 2, 15, 13, 10, 14, 3, 6, 7, 1, 9, 4],
        [7, 9, 3, 1, 13, 12, 11, 14, 2, 6, 5, 10, 4, 0, 15, 8],
        [9, 0, 5, 7, 2, 4, 10, 15, 14, 1, 11, 12, 6, 8, 3, 13],
        [2, 12, 6, 10, 0, 11, 8, 3, 4, 13, 7, 5, 15, 14, 1, 9],
        [12, 5, 1, 15, 14, 13, 4, 10, 0, 7, 6, 3, 9, 2, 8, 11],
        [13, 11, 7, 14, 12, 1, 3, 9, 5, 0, 15, 4, 8, 6, 2, 10],
        [6, 15, 14, 9, 11, 3, 0, 8, 12, 2, 13, 7, 1, 4, 10, 5],
        [10, 2, 8, 4, 7, 6, 1, 5, 15, 11, 9, 14, 3, 12, 13, 0],
        // Rounds 10 and 11 reuse the first two permutations (RFC 7693 §2.7).
        [0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15],
        [14, 10, 4, 8, 9, 15, 13, 6, 1, 12, 0, 2, 11, 7, 5, 3],
    ];

    private const int BlockBytes = 128;
    private const int DigestBytes = 64;

    /// <summary>64-byte BLAKE2b digest of <paramref name="input"/>.</summary>
    public static byte[] Hash512(ReadOnlySpan<byte> input)
    {
        var h = (ulong[])Iv.Clone();
        h[0] ^= 0x01010000UL ^ DigestBytes; // parameter block: no key, 64-byte output, fanout/depth 1

        // The final block is always compressed with the "last" flag, so full blocks are only fed to
        // the loop while more input remains after them. That also gives the empty input the right
        // answer: one zero-filled block at counter 0.
        var offset = 0;
        ulong counter = 0;
        while (input.Length - offset > BlockBytes)
        {
            counter += BlockBytes;
            Compress(h, input.Slice(offset, BlockBytes), counter, last: false);
            offset += BlockBytes;
        }

        Span<byte> finalBlock = stackalloc byte[BlockBytes];
        finalBlock.Clear();
        input[offset..].CopyTo(finalBlock);
        counter += (ulong)(input.Length - offset);
        Compress(h, finalBlock, counter, last: true);

        var digest = new byte[DigestBytes];
        for (var i = 0; i < 8; i++)
            BitConverter.TryWriteBytes(digest.AsSpan(i * 8), h[i]); // little-endian on every target we ship
        return digest;
    }

    private static void Compress(ulong[] h, ReadOnlySpan<byte> block, ulong counter, bool last)
    {
        Span<ulong> m = stackalloc ulong[16];
        for (var i = 0; i < 16; i++)
            m[i] = BitConverter.ToUInt64(block[(i * 8)..]);

        Span<ulong> v = stackalloc ulong[16];
        for (var i = 0; i < 8; i++)
        {
            v[i] = h[i];
            v[i + 8] = Iv[i];
        }
        v[12] ^= counter;
        // v[13] ^= counter >> 64 — the high half of the 128-bit counter, which stays zero until a
        // single input exceeds 16 exabytes.
        if (last)
            v[14] ^= ulong.MaxValue;

        foreach (var s in Sigma)
        {
            G(v, 0, 4, 8, 12, m[s[0]], m[s[1]]);
            G(v, 1, 5, 9, 13, m[s[2]], m[s[3]]);
            G(v, 2, 6, 10, 14, m[s[4]], m[s[5]]);
            G(v, 3, 7, 11, 15, m[s[6]], m[s[7]]);
            G(v, 0, 5, 10, 15, m[s[8]], m[s[9]]);
            G(v, 1, 6, 11, 12, m[s[10]], m[s[11]]);
            G(v, 2, 7, 8, 13, m[s[12]], m[s[13]]);
            G(v, 3, 4, 9, 14, m[s[14]], m[s[15]]);
        }

        for (var i = 0; i < 8; i++)
            h[i] ^= v[i] ^ v[i + 8];
    }

    private static void G(Span<ulong> v, int a, int b, int c, int d, ulong x, ulong y)
    {
        v[a] = v[a] + v[b] + x;
        v[d] = ulong.RotateRight(v[d] ^ v[a], 32);
        v[c] += v[d];
        v[b] = ulong.RotateRight(v[b] ^ v[c], 24);
        v[a] = v[a] + v[b] + y;
        v[d] = ulong.RotateRight(v[d] ^ v[a], 16);
        v[c] += v[d];
        v[b] = ulong.RotateRight(v[b] ^ v[c], 63);
    }
}
