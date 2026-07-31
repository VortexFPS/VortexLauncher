using System.Numerics;
using System.Security.Cryptography;

namespace Launcher.Core.Signing;

/// <summary>Ed25519 signature <b>verification</b> (RFC 8032, PureEdDSA), written against the BCL.
///
/// Why this exists instead of a NuGet package: net8.0's System.Security.Cryptography has no Ed25519
/// (RSA, ECDsa over the NIST curves, and the PQC families — nothing on Curve25519), and
/// Launcher.Core is BCL-only by a rule the build enforces
/// (tests/Launcher.Tests/ArchitectureTests.cs, Bcl_only_projects_take_no_package_references).
/// Putting a package-backed verifier anywhere else means it cannot be called from ReleaseFeeds,
/// which is the one place a manifest enters the process — verification would become something each
/// front end (Desktop, Cli, anything later) has to remember to wire up, and the failure mode of
/// forgetting is a silently unverified download.
///
/// Why rolling this is defensible, when "don't roll your own crypto" normally is not: verification
/// consumes only public inputs — public key, signature, message. There is no secret in the process
/// to leak, so the whole class of failures that rule exists to prevent (key material mishandled,
/// secrets recovered through timing or cache side channels, a bad RNG) has no target here. What is
/// left is ordinary arithmetic, which is testable, and it is tested: SigningTests pins this to the
/// RFC 8032 §7.1 vectors. Note there is no signing half in this file, deliberately — the private
/// key lives in the release job and must never have a code path here.
///
/// Performance is BigInteger-grade, roughly 10 ms per verification, because the field arithmetic is
/// plain modular BigInteger rather than packed limbs. That buys a great deal of readability for a
/// cost paid twice per update check.</summary>
public static class Ed25519
{
    // Curve constants (RFC 8032 §5.1). Declaration order matters — these initialize in sequence.
    private static readonly BigInteger P = BigInteger.Pow(2, 255) - 19;

    /// <summary>Order of the base point's prime-order subgroup.</summary>
    private static readonly BigInteger L =
        BigInteger.Pow(2, 252) + BigInteger.Parse("27742317777372353535851937790883648493");

    private static readonly BigInteger D = Mod(-121665 * Inv(121666));
    private static readonly BigInteger SqrtMinusOne = BigInteger.ModPow(2, (P - 1) / 4, P);

    /// <summary>Base point. Only y is written out: x is recovered from the curve equation, which
    /// makes the constant self-checking rather than a 78-digit literal nobody can eyeball.</summary>
    private static readonly Point BasePoint = DecodeBasePoint();

    private static readonly Point Identity = new(BigInteger.Zero, BigInteger.One, BigInteger.One, BigInteger.Zero);

    /// <summary>True if <paramref name="signature"/> (64 bytes, R||S) is a valid signature over
    /// <paramref name="message"/> for <paramref name="publicKey"/> (32 bytes). Malformed inputs —
    /// wrong lengths, non-canonical field elements, points not on the curve — return false rather
    /// than throwing; to a caller they are the same answer.</summary>
    public static bool Verify(ReadOnlySpan<byte> signature, ReadOnlySpan<byte> message,
        ReadOnlySpan<byte> publicKey)
    {
        if (signature.Length != 64 || publicKey.Length != 32)
            return false;

        if (!TryDecodePoint(publicKey, out var a) || !TryDecodePoint(signature[..32], out var r))
            return false;

        var s = new BigInteger(signature[32..], isUnsigned: true, isBigEndian: false);
        if (s >= L)
            return false; // RFC 8032 §5.1.7 step 1: a non-canonical S is a malleated signature

        // k = SHA-512(R || A || M) mod L
        var buffer = new byte[64 + message.Length];
        signature[..32].CopyTo(buffer);
        publicKey.CopyTo(buffer.AsSpan(32));
        message.CopyTo(buffer.AsSpan(64));
        var k = Mod(new BigInteger(SHA512.HashData(buffer), isUnsigned: true, isBigEndian: false), L);

        // [S]B == R + [k]A, compared as encodings so the projective representation cannot differ for
        // equal points. This is the cofactorless check; the cofactored variant would also accept
        // signatures that differ by a small-order component. Small-order A is not a concern the way
        // it is for a general-purpose library: the public keys here are compiled-in constants
        // (ReleaseSigning.TrustedKeys), so who chooses A is us, not a caller.
        return Encode(ScalarMultiply(s, BasePoint)).AsSpan()
            .SequenceEqual(Encode(Add(r, ScalarMultiply(k, a))));
    }

    /// <summary>Extended twisted Edwards coordinates: x = X/Z, y = Y/Z, with T = XY/Z carried along
    /// so addition needs no inversion. Inversion happens once, in <see cref="Encode"/>.</summary>
    private readonly record struct Point(BigInteger X, BigInteger Y, BigInteger Z, BigInteger T);

    /// <summary>The "add-2008-hwcd-3" formula for a = -1. It is strongly unified, meaning it is also
    /// correct when both operands are the same point, which is why doubling below is just Add(q, q)
    /// instead of a second formula to get wrong.</summary>
    private static Point Add(in Point p1, in Point p2)
    {
        var a = Mod((p1.Y - p1.X) * (p2.Y - p2.X));
        var b = Mod((p1.Y + p1.X) * (p2.Y + p2.X));
        var c = Mod(p1.T * 2 * D * p2.T);
        var d = Mod(p1.Z * 2 * p2.Z);
        var e = b - a;
        var f = d - c;
        var g = d + c;
        var h = b + a;
        return new Point(Mod(e * f), Mod(g * h), Mod(f * g), Mod(e * h));
    }

    private static Point ScalarMultiply(BigInteger k, in Point point)
    {
        var result = Identity;
        for (var i = (int)k.GetBitLength() - 1; i >= 0; i--)
        {
            result = Add(result, result);
            if (!((k >> i) & BigInteger.One).IsZero)
                result = Add(result, point);
        }
        return result;
    }

    /// <summary>32-byte little-endian y with the low bit of x in the top bit (RFC 8032 §5.1.2).</summary>
    private static byte[] Encode(in Point point)
    {
        var zInverse = Inv(point.Z);
        var x = Mod(point.X * zInverse);
        var y = Mod(point.Y * zInverse);

        var encoded = new byte[32];
        y.TryWriteBytes(encoded, out _, isUnsigned: true, isBigEndian: false);
        if (!(x & BigInteger.One).IsZero)
            encoded[31] |= 0x80;
        return encoded;
    }

    /// <summary>Point decompression (RFC 8032 §5.1.3). False for anything that is not a valid
    /// encoding of a curve point, including a y ≥ p, which is a non-canonical encoding that some
    /// implementations historically accepted.</summary>
    private static bool TryDecodePoint(ReadOnlySpan<byte> encoded, out Point point)
    {
        point = default;

        Span<byte> bytes = stackalloc byte[32];
        encoded[..32].CopyTo(bytes);
        var signBit = (bytes[31] >> 7) & 1;
        bytes[31] &= 0x7f;

        var y = new BigInteger(bytes, isUnsigned: true, isBigEndian: false);
        if (y >= P)
            return false;

        // x² = (y² - 1) / (d·y² + 1)
        var ySquared = Mod(y * y);
        if (!TrySqrtRatio(Mod(ySquared - 1), Mod(D * ySquared + 1), out var x))
            return false;

        if (x.IsZero && signBit == 1)
            return false; // x = 0 has only one root; claiming the negative one is not canonical
        if ((int)(x & BigInteger.One) != signBit)
            x = P - x;

        point = new Point(x, y, BigInteger.One, Mod(x * y));
        return true;
    }

    /// <summary>x = sqrt(u/v) when it exists. Uses the p ≡ 5 (mod 8) shortcut: the candidate root is
    /// u·v³·(u·v⁷)^((p-5)/8), corrected by sqrt(-1) when it lands on the wrong square root.</summary>
    private static bool TrySqrtRatio(BigInteger u, BigInteger v, out BigInteger x)
    {
        var v3 = Mod(v * v * v);
        var v7 = Mod(v3 * v3 * v);
        x = Mod(u * v3 * BigInteger.ModPow(Mod(u * v7), (P - 5) / 8, P));

        var check = Mod(v * x * x);
        if (check == u)
            return true;
        if (check == Mod(-u))
        {
            x = Mod(x * SqrtMinusOne);
            return true;
        }
        return false; // u/v is not a square: the encoded y is not on the curve
    }

    private static Point DecodeBasePoint()
    {
        // y = 4/5 (RFC 8032 §5.1); the x with an even low bit is the standard base point.
        var y = Mod(4 * Inv(5));
        Span<byte> encoded = stackalloc byte[32];
        y.TryWriteBytes(encoded, out _, isUnsigned: true, isBigEndian: false);
        return TryDecodePoint(encoded, out var point)
            ? point
            : throw new InvalidOperationException("Ed25519 base point failed to decode");
    }

    private static BigInteger Inv(BigInteger a) => BigInteger.ModPow(Mod(a), P - 2, P);

    private static BigInteger Mod(BigInteger x) => Mod(x, P);

    private static BigInteger Mod(BigInteger x, BigInteger m)
    {
        var r = x % m;
        return r.Sign < 0 ? r + m : r;
    }
}
