namespace Launcher.Core.Signing;

/// <summary>How hard the launcher insists on a valid manifest signature.</summary>
public enum ManifestSignaturePolicy
{
    /// <summary>Do not fetch or check signatures. Escape hatch for local testing against a manifest
    /// served off a dev machine; not a state any shipped default should sit in.</summary>
    Off,

    /// <summary>Check a signature when the release carries one, and refuse the release if that check
    /// fails — but accept a release with no signature at all. The transition state (see
    /// <see cref="ReleaseSigning.DefaultPolicy"/>).</summary>
    VerifyIfPresent,

    /// <summary>No valid signature, no install. The end state, once the game repo's release job
    /// signs every release.</summary>
    Required,
}

/// <summary>Thrown when a release fails the signature policy. Deliberately not an
/// <see cref="HttpRequestException"/>: <see cref="CompositeFeed"/> swallows those and moves to the
/// next feed, and a signature failure must stop the update, not shop around for a softer source.</summary>
public class ManifestSignatureException(string message) : Exception(message);

/// <summary>A feed that structurally cannot carry a signature, asked for under
/// <see cref="ManifestSignaturePolicy.Required"/>.
///
/// Separate from its base class because the two need opposite handling: a failed signature check
/// stops the update, while a feed with no signature to check is merely unusable, and the next feed
/// in the chain may still have a signed manifest. The beta channel makes that distinction load
/// bearing — it asks the unsignable API feed FIRST (ChannelFeeds.FeedFor), so treating this as an
/// attack would take the whole chain down before the signed path was ever tried.</summary>
public sealed class UnsignableFeedException(string message) : ManifestSignatureException(message);

/// <summary>The launcher's release trust anchor: which keys it accepts, how strictly it insists on a
/// signature, and the check itself.
///
/// <b>Why the manifest is signed and the zips are not.</b> One signature covers a whole release
/// because latest.json already carries every file's size, URL and sha256, and DownloadService
/// refuses to hand the installer any file that has no published checksum or whose bytes do not match
/// it. So the chain runs: trusted key → signature over the exact bytes of latest.json → the sha256
/// in those bytes → the bytes on disk. Nothing in the middle is optional, and the installer only
/// ever fetches URLs that came out of the manifest, so there is no path to an installed file that
/// the signature does not transitively cover. Signing each zip instead would be N signatures and N
/// extra fetches, and would still leave the document that decides <i>which</i> zip you get and where
/// it lives unsigned — an attacker could serve a genuinely signed but older or wrong-platform build
/// and be within the rules.
///
/// What this does not cover: replaying an older, validly signed manifest. A signature proves who
/// wrote the manifest, not that it is the newest one. That is a rollback attack and it needs a
/// freshness rule (a monotonic version floor recorded on disk), tracked separately in
/// release-signing.md rather than smuggled in here.</summary>
public static class ReleaseSigning
{
    /// <summary>THE place the release public keys live.
    ///
    /// Each entry is the base64 line from a minisign .pub file (the line after "untrusted comment:").
    /// The list is empty because the game repo has not generated a release key yet — the launcher
    /// therefore accepts unsigned releases today under <see cref="DefaultPolicy"/>, which is exactly
    /// the current situation, and hard-fails any release that shows up signed by a key it does not
    /// know. Adding the real key here is a one-line change.
    ///
    /// <b>Rotation.</b> A hardcoded key with no rotation plan is a future outage, so: this is a
    /// LIST, and trusting two keys at once is how a key gets replaced. The order is not negotiable —
    ///   1. generate the new keypair, keep the secret half in the game repo's Actions secrets;
    ///   2. add its public line here and ship a launcher release;
    ///   3. wait for the installed launcher population to pick that release up;
    ///   4. only then switch the release job over to signing with the new key;
    ///   5. drop the old line in a later launcher release.
    /// Doing 4 before 2 breaks every launcher in the field at once: a signature from a key nobody
    /// has is not "unsigned", it is "signed by an unknown key", and that fails closed by design.
    ///
    /// <b>Compromise.</b> There is no revocation channel here — no OCSP, no CRL, nothing the
    /// launcher polls. If the secret key leaks, the response is a launcher release that removes the
    /// line, delivered over the launcher's own Velopack self-update, plus re-signing anything that
    /// still needs to be reachable. That makes the self-update path the recovery channel for this
    /// one, which is worth knowing before it is needed (release-signing.md spells out the limits).</summary>
    private static readonly string[] TrustedKeyLines =
    [
        // shape, deliberately truncated so it cannot be uncommented into a live key:
        //   "RWQf6LRCGA9i53ml…"  — the one base64 line out of vortex-release.pub
    ];

    /// <summary>Parsed form of <see cref="TrustedKeyLines"/>. A malformed line throws here, on first
    /// use, rather than turning into a mysterious verification failure later.</summary>
    public static readonly IReadOnlyList<MinisignPublicKey> TrustedKeys =
        [.. TrustedKeyLines.Select(MinisignPublicKey.Parse)];

    /// <summary>Set <c>VORTEX_LAUNCHER_SIGNATURE_POLICY</c> to off / verify-if-present / required to
    /// override the compiled-in default. This exists so the release job's signing can be validated
    /// end to end against a real launcher build before the default is tightened — otherwise the only
    /// way to test "required" is to ship it, which is the opposite of how a security default should
    /// be rolled out. An unrecognized value resolves to <see cref="ManifestSignaturePolicy.Required"/>:
    /// if we cannot tell what was asked for, the strict reading is the safe one, and it fails
    /// visibly on the next update check instead of quietly relaxing anything.
    ///
    /// An environment variable rather than a field in <see cref="LauncherSettings"/>, even though
    /// settings.json now exists: this is not a player preference. It is a rollout dial that WE turn
    /// once, on the schedule in release-signing.md, and putting it in the settings file would put
    /// "verify release signatures" in a list next to install location, where the only thing anyone
    /// would ever do with it is switch it off. Set per-run by whoever starts the process, it leaves
    /// no persistent weakened state behind.</summary>
    public const string PolicyEnvironmentVariable = "VORTEX_LAUNCHER_SIGNATURE_POLICY";

    /// <summary>Verify when a signature is there, tolerate a release without one.
    ///
    /// Not <see cref="ManifestSignaturePolicy.Required"/>, because no release published so far is
    /// signed and a launcher that required a signature today would refuse every release in
    /// existence — including the ones it would need to fetch to fix itself. And not a flag day
    /// either: the enforcing half of this is a binary already installed on players' machines, so
    /// "everyone flips at once" is not a thing that can happen. The two halves can only be changed
    /// in some order, and only one order is survivable — publish signatures first, tighten the
    /// policy afterwards, with this state spanning the gap. Full reasoning in release-signing.md.
    ///
    /// This becomes <see cref="ManifestSignaturePolicy.Required"/> once the release job signs every
    /// release and a launcher carrying the key has reached the installed population.</summary>
    public const ManifestSignaturePolicy DefaultPolicy = ManifestSignaturePolicy.VerifyIfPresent;

    public static ManifestSignaturePolicy ResolvePolicy() =>
        Environment.GetEnvironmentVariable(PolicyEnvironmentVariable)?.Trim().ToLowerInvariant() switch
        {
            null or "" => DefaultPolicy,
            "off" => ManifestSignaturePolicy.Off,
            "verify-if-present" => ManifestSignaturePolicy.VerifyIfPresent,
            "required" => ManifestSignaturePolicy.Required,
            _ => ManifestSignaturePolicy.Required,
        };

    /// <summary>Applies <paramref name="policy"/> to a fetched manifest and returns a one-line status
    /// for the UI. Throws <see cref="ManifestSignatureException"/> if the release may not be used.
    /// <paramref name="signatureFile"/> is null when the release carries no .minisig;
    /// <paramref name="keys"/> defaults to <see cref="TrustedKeys"/> and is a parameter only so tests
    /// can exercise this with a keypair that is not the real release key.</summary>
    public static string Check(ManifestSignaturePolicy policy, ReadOnlySpan<byte> manifestBytes,
        string? signatureFile, string manifestName, IReadOnlyList<MinisignPublicKey>? keys = null)
    {
        keys ??= TrustedKeys;

        if (policy == ManifestSignaturePolicy.Off)
            return "not checked (signature policy is off)";

        if (signatureFile is null)
        {
            if (policy == ManifestSignaturePolicy.Required)
                throw new ManifestSignatureException(
                    $"{manifestName} carries no minisign signature and this launcher requires one. " +
                    "Either the release was published without signing it, or something is serving " +
                    "you a release we did not publish.");
            return "unsigned (accepted: releases are not signed yet)";
        }

        // A signature we hold no key for is not a pass. Say so before checking it, because
        // "signature is present but this build predates the key" is a different problem for the
        // player than "the signature is wrong", and the fix is different too.
        if (keys.Count == 0)
            throw new ManifestSignatureException(
                $"{manifestName} is signed, but this launcher carries no release key to check it " +
                "with — it is older than the signing key. Update the launcher.");

        if (!Minisign.Verify(manifestBytes, signatureFile, keys, out var detail))
            throw new ManifestSignatureException(
                $"{manifestName} failed signature verification: {detail}. Refusing to install " +
                "anything from it — your installed version still plays.");

        return detail;
    }

    /// <summary>Guards feeds that cannot be signed at all. The GitHub API fallback builds its
    /// manifest client-side out of an API listing, so there is no document for anyone to have signed;
    /// under <see cref="ManifestSignaturePolicy.Required"/> it has to be refused rather than used,
    /// or "required" means nothing — deleting latest.json.minisig from the host would silently push
    /// every launcher onto the unsigned path, which is a downgrade attack with no attacker skill
    /// required.</summary>
    public static void EnsureUnsignedFeedAllowed(ManifestSignaturePolicy policy, string feedName)
    {
        if (policy == ManifestSignaturePolicy.Required)
            throw new UnsignableFeedException(
                $"{feedName} produces a manifest nobody signed, and this launcher requires a signed " +
                "release manifest, so it cannot be used.");
    }
}
