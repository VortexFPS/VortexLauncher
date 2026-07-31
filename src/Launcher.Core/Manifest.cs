using System.Text.Json;
using System.Text.Json.Serialization;

namespace Launcher.Core;

/// <summary>One downloadable zip in the release manifest. <see cref="Root"/> is the zip's internal
/// top-level directory (tools/package.sh zips dist/&lt;target&gt;/, e.g. "windows-client").</summary>
public sealed record ManifestFile(string Name, string? Root, long Size, string Sha256, string Url);

/// <summary>A platform's packages: <see cref="Complete"/> = binary + runtime + all game data;
/// <see cref="Core"/> = binary + runtime only (pairs with the shared assets pack).
///
/// <see cref="Fat"/> is the same thing under its old name. latest.json is a cross-repo contract, so
/// the two keys have to coexist for as long as it takes the game repo's release job to switch and a
/// player to be looking at a release cut before it did. Nothing should read either field directly —
/// <see cref="Bundle"/> is the accessor, and it prefers the new key so a manifest emitting both
/// during the transition resolves to one answer.</summary>
public sealed record ManifestPlatform(ManifestFile? Complete, ManifestFile? Core, ManifestFile? Fat = null)
{
    /// <summary>The everything-in-one-zip package under whichever key this manifest used.</summary>
    [JsonIgnore] public ManifestFile? Bundle => Complete ?? Fat;
}

/// <summary>The content-addressed game-data pack. <see cref="Version"/> is the 12-char
/// download-assets.sh content hash (ADR-0015 §4) — the asset-store directory name.</summary>
public sealed record ManifestAssets(string Name, string Version, long Size, string Sha256, string Url);

/// <summary>latest.json (tools/make-manifest.py output — ADR-0015 §5), or the equivalent
/// synthesized from the GitHub Releases API by the fallback feed.</summary>
public sealed record ReleaseManifest
{
    public int Schema { get; init; } = 1;
    public required string Version { get; init; }
    public required string Tag { get; init; }
    public string Channel { get; init; } = "stable";
    public string? NotesUrl { get; init; }
    public ManifestAssets? Assets { get; init; }
    public Dictionary<string, ManifestPlatform> Platforms { get; init; } = new();

    /// <summary>Release-notes body — populated only by the API fallback feed (not in latest.json).</summary>
    [JsonIgnore] public string? NotesBody { get; init; }
    [JsonIgnore] public bool Prerelease { get; init; }

    /// <summary>How this manifest fared against the signature policy, in one line fit to show a
    /// player ("signed by key …", "unsigned (accepted: …)"). A manifest that FAILED verification
    /// never gets here — the feed throws — so this is a status, not a verdict to branch on. It
    /// exists so the transition to required signatures is observable from the UI instead of being
    /// a thing you find out about on flag day.</summary>
    [JsonIgnore] public string? SignatureStatus { get; init; }

    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static ReleaseManifest? Parse(string json) =>
        JsonSerializer.Deserialize<ReleaseManifest>(json, JsonOptions);

    public ManifestPlatform? PlatformFor(string platformKey) =>
        Platforms.TryGetValue(platformKey, out var p) ? p : null;
}
