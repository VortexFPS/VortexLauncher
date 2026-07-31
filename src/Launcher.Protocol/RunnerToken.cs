using System.Security.Cryptography;
using System.Text;

namespace Launcher.Protocol;

/// <summary>Where a runner keeps the state a control plane on the same box has to read.
///
/// This is here rather than in Launcher.Core's LauncherPaths because Launcher.WebServer must not
/// reference Core, and both ends have to agree on the path to a byte. LauncherPaths composes its
/// layout from <see cref="DefaultDataRoot"/> so there is still exactly one definition.</summary>
public static class RunnerLayout
{
    /// <summary>Per-user data root when `--data-root` does not override it.</summary>
    public static string DefaultDataRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "VortexArena", "Launcher");

    /// <summary>runner.json: written by the runner, read by the control plane for the token hash.</summary>
    public static string RunnerConfigPath(string dataRoot) =>
        Path.Combine(dataRoot, "runner", "runner.json");

    public static string DefaultRunnerConfigPath => RunnerConfigPath(DefaultDataRoot);
}

/// <summary>The stored half of a control plane token: a hash, and enough clear text to identify it.
///
/// Same shape and the same reasoning as Conductor's ApiKey. A token readable from disk is a token
/// that leaks with a backup, and rotation only means something if the old value stops working.</summary>
public sealed record RunnerWebToken
{
    /// <summary>First few characters of the token, in clear, so a human can tell two apart in a log
    /// or a support thread without either being usable.</summary>
    public required string Prefix { get; init; }

    public required string Sha256 { get; init; }

    public DateTimeOffset IssuedAt { get; init; }
}

/// <summary>Mint, store and check the bearer token the local control plane authenticates with.
///
/// The rule lives in Launcher.Protocol because the two ends are separate processes that may be
/// separately versioned: `vortex runner install-service` mints and hashes, Launcher.WebServer hashes
/// what a caller presented and compares. If those two ever disagreed about the hash input, every
/// request would 401 with nothing on either side looking wrong.</summary>
public static class RunnerToken
{
    /// <summary>Prefixed so a token found in a shell history or a proxy log is recognisable as one,
    /// and as ours. "vortex web token".</summary>
    public const string Scheme = "vwt_";

    /// <summary>32 bytes of CSPRNG output. Well past anything guessable, and short enough to paste.</summary>
    private const int EntropyBytes = 32;

    /// <summary>Enough to disambiguate two tokens, far too little to help an attacker: 8 hex
    /// characters after the scheme is 32 bits of a 256-bit secret.</summary>
    private const int PrefixLength = 12;

    public static string Issue() =>
        Scheme + Convert.ToHexString(RandomNumberGenerator.GetBytes(EntropyBytes)).ToLowerInvariant();

    /// <summary>Plain sha256, deliberately not a password hash.
    ///
    /// The token is 32 bytes of CSPRNG output, so there is no dictionary to attack and nothing a slow
    /// KDF would buy. It is also checked on every request including the WebSocket upgrade, and a
    /// bcrypt on that path would make authentication the most expensive thing the panel does.</summary>
    public static string Hash(string token) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))).ToLowerInvariant();

    /// <summary>What gets written to runner.json for a token that was just shown to an operator.</summary>
    public static RunnerWebToken Describe(string token) => new()
    {
        Prefix = token.Length <= PrefixLength ? token : token[..PrefixLength],
        Sha256 = Hash(token),
        IssuedAt = DateTimeOffset.UtcNow,
    };

    /// <summary>Constant-time check of a presented token against a stored hash.
    ///
    /// The presented value is hashed first so the comparison is always over two 64-character digests.
    /// Comparing the raw token instead would leak its length through timing, and the previous
    /// pad-and-truncate-to-64 version silently ignored everything past the 64th character, which for
    /// a 68-character token meant four hex digits that did not have to match.</summary>
    public static bool Verify(string? presented, string? storedSha256)
    {
        if (string.IsNullOrEmpty(presented) || string.IsNullOrEmpty(storedSha256))
            return false;

        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(Hash(presented)),
            Encoding.UTF8.GetBytes(storedSha256));
    }
}
