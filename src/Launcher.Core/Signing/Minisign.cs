using System.Text;

namespace Launcher.Core.Signing;

/// <summary>A minisign public key: the 8-byte key id it is announced under, and the 32-byte Ed25519
/// key itself.</summary>
public sealed record MinisignPublicKey(byte[] KeyId, byte[] PublicKey)
{
    public string KeyIdHex => MinisignFormat.KeyIdHex(KeyId);

    /// <summary>Parses a minisign .pub file, or just its base64 line. Throws on anything malformed:
    /// the only callers are compiled-in trust anchors, where a bad key is a build mistake and the
    /// loudest possible failure is the right one.</summary>
    public static MinisignPublicKey Parse(string text)
    {
        var payload = MinisignFormat.DecodeLines(text, expectedLines: 1, out _)[0];
        if (payload.Length != 42)
            throw new FormatException($"minisign public key is {payload.Length} bytes, expected 42");
        if (payload[0] != (byte)'E' || payload[1] != (byte)'d')
            throw new FormatException(
                $"unsupported minisign public key algorithm '{(char)payload[0]}{(char)payload[1]}'");
        return new MinisignPublicKey(payload[2..10], payload[10..42]);
    }
}

/// <summary>A parsed .minisig file.</summary>
/// <param name="Prehashed">minisign's "ED" mode: the Ed25519 signature covers BLAKE2b-512 of the
/// file instead of the file. Which mode a signature uses is the signer's choice, so both are read.</param>
/// <param name="TrustedComment">The comment covered by <paramref name="GlobalSignature"/>. minisign
/// puts the timestamp and original filename here; nothing in the launcher trusts its contents, but
/// its signature is still checked, because a file that fails that check did not come from minisign.</param>
public sealed record MinisignSignature(
    bool Prehashed, byte[] KeyId, byte[] Signature, string TrustedComment, byte[] GlobalSignature);

/// <summary>minisign signature verification. The format is four lines: an untrusted comment, the
/// base64 signature block, a trusted comment, and the base64 signature over that comment.</summary>
public static class Minisign
{
    /// <summary>Checks <paramref name="signatureFile"/> against <paramref name="content"/> using the
    /// first trusted key whose id matches. <paramref name="detail"/> always explains the outcome —
    /// it ends up in front of a player, so it says what happened rather than "verification failed".
    ///
    /// A signature whose key id is not in <paramref name="trustedKeys"/> is a failure, never a
    /// pass-through to "unsigned". Treating an unrecognized signer as absent would hand an attacker
    /// the downgrade for free: sign with any key at all and the check evaporates.</summary>
    public static bool Verify(ReadOnlySpan<byte> content, string signatureFile,
        IReadOnlyList<MinisignPublicKey> trustedKeys, out string detail)
    {
        if (!TryParse(signatureFile, out var signature, out detail))
            return false;

        var key = trustedKeys.FirstOrDefault(k => k.KeyId.AsSpan().SequenceEqual(signature!.KeyId));
        if (key is null)
        {
            var known = trustedKeys.Count == 0
                ? "this build carries no release keys"
                : "trusted: " + string.Join(", ", trustedKeys.Select(k => k.KeyIdHex));
            detail = $"signed by unknown key {MinisignFormat.KeyIdHex(signature!.KeyId)} ({known})";
            return false;
        }

        var signed = signature!.Prehashed ? Blake2b.Hash512(content) : content;
        if (!Ed25519.Verify(signature.Signature, signed, key.PublicKey))
        {
            detail = $"signature from key {key.KeyIdHex} does not match the file contents";
            return false;
        }

        // The global signature covers signature||trusted-comment, so a comment cannot be rewritten
        // under a valid signature.
        var trailer = new byte[64 + Encoding.UTF8.GetByteCount(signature.TrustedComment)];
        signature.Signature.CopyTo(trailer, 0);
        Encoding.UTF8.GetBytes(signature.TrustedComment, trailer.AsSpan(64));
        if (!Ed25519.Verify(signature.GlobalSignature, trailer, key.PublicKey))
        {
            detail = $"trusted comment is not signed by key {key.KeyIdHex}";
            return false;
        }

        detail = $"signed by key {key.KeyIdHex} ({signature.TrustedComment})";
        return true;
    }

    /// <summary>Parses a .minisig. Everything here is attacker-supplied, so every shape error is a
    /// false with a reason rather than an exception.</summary>
    public static bool TryParse(string signatureFile, out MinisignSignature? signature, out string error)
    {
        signature = null;
        byte[][] blocks;
        string trustedComment;
        try
        {
            blocks = MinisignFormat.DecodeLines(signatureFile, expectedLines: 2, out trustedComment);
        }
        catch (FormatException ex)
        {
            error = "signature file is malformed: " + ex.Message;
            return false;
        }

        if (blocks[0].Length != 74)
        {
            error = $"signature block is {blocks[0].Length} bytes, expected 74";
            return false;
        }
        if (blocks[1].Length != 64)
        {
            error = $"trusted-comment signature is {blocks[1].Length} bytes, expected 64";
            return false;
        }

        // "Ed" signs the file, "ED" signs its BLAKE2b-512 hash. Anything else is a future algorithm
        // this launcher predates, and guessing is not an option for a signature check.
        var prehashed = blocks[0][0] == (byte)'E' && blocks[0][1] == (byte)'D';
        if (!prehashed && !(blocks[0][0] == (byte)'E' && blocks[0][1] == (byte)'d'))
        {
            error = $"unsupported signature algorithm '{(char)blocks[0][0]}{(char)blocks[0][1]}'";
            return false;
        }

        signature = new MinisignSignature(prehashed, blocks[0][2..10], blocks[0][10..74],
            trustedComment, blocks[1]);
        error = "";
        return true;
    }
}

internal static class MinisignFormat
{
    private const string UntrustedPrefix = "untrusted comment:";
    private const string TrustedPrefix = "trusted comment:";

    /// <summary>Uppercase hex, bytes reversed: minisign treats the id as a little-endian 64-bit
    /// number and prints it big-endian, so this is the form that can be pasted straight into a
    /// comparison with the comment line of a .pub file.
    ///
    /// Reverses a copy in place rather than writing `keyId.Reverse().ToArray()`. On an array that
    /// expression is not stably bound: from C# 14 the first-class span conversion makes
    /// MemoryExtensions.Reverse (in-place, returns void) a better match than Enumerable.Reverse, so
    /// the LINQ form stops compiling — and if it ever bound to the span overload without the
    /// trailing call it would silently mutate the caller's key id instead.</summary>
    internal static string KeyIdHex(byte[] keyId)
    {
        var reversed = (byte[])keyId.Clone();
        Array.Reverse(reversed);
        return Convert.ToHexString(reversed);
    }

    /// <summary>Pulls the base64 payload lines out of a .pub or .minisig, skipping comment lines and
    /// returning the trusted comment when there is one. Carriage returns are stripped: the files are
    /// LF-only as minisign writes them, but they travel through Windows editors and web servers, and
    /// a CR that survived that trip is not a signature failure worth reporting to a player.</summary>
    internal static byte[][] DecodeLines(string text, int expectedLines, out string trustedComment)
    {
        trustedComment = "";
        var payloads = new List<byte[]>();

        foreach (var raw in text.Split('\n'))
        {
            var line = raw.EndsWith('\r') ? raw[..^1] : raw;
            if (line.Length == 0 || line.StartsWith(UntrustedPrefix, StringComparison.Ordinal))
                continue;
            if (line.StartsWith(TrustedPrefix, StringComparison.Ordinal))
            {
                // minisign's prefix is "trusted comment: " and it signs everything after it
                // verbatim — no trimming, or the bytes checked stop being the bytes signed.
                var rest = line[TrustedPrefix.Length..];
                trustedComment = rest.StartsWith(' ') ? rest[1..] : rest;
                continue;
            }
            payloads.Add(Convert.FromBase64String(line.Trim()));
        }

        if (payloads.Count != expectedLines)
            throw new FormatException($"expected {expectedLines} base64 line(s), found {payloads.Count}");
        return [.. payloads];
    }
}
