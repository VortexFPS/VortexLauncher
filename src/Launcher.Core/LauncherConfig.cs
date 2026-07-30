namespace Launcher.Core;

/// <summary>The one place the distribution endpoints live (ADR-0015 §5).</summary>
public static class LauncherConfig
{
    public const string Repo = "VortexFPS/VortexArena";
    public const string RepoUrl = $"https://github.com/{Repo}";

    /// <summary>Prefixes on the game's release artifacts: "&lt;prefix&gt;-&lt;ver&gt;-&lt;target&gt;.zip",
    /// "&lt;prefix&gt;-assets-&lt;hash12&gt;.zip", and the binary names in <see cref="PlatformKey"/>.
    ///
    /// Both are accepted, newest first, and that is the point. tools/package.sh still emits
    /// XonoticGodot-* on every branch including migrated/*, and the rebrand reaches packaging on its
    /// own schedule. A launcher that understood only one name would break on whichever side of the
    /// cutover it was not compiled for: the old one cannot read the first VortexArena release, and a
    /// prematurely flipped one cannot read anything published before it. Accepting both spans the
    /// cutover the README's "artifact rename breaks update continuity" note warns about, including
    /// launching an install made under the other name.
    ///
    /// Drop "XonoticGodot" only once no supported install can still be carrying it.</summary>
    public static readonly IReadOnlyList<string> ArtifactPrefixes = ["VortexArena", "XonoticGodot"];

    /// <summary>The prefix new artifacts are expected to use. Error messages and the canonical binary
    /// name come from this; parsing always tries every entry in <see cref="ArtifactPrefixes"/>.</summary>
    public const string CanonicalArtifactPrefix = "VortexArena";

    /// <summary>Alternation for the compile-time-constant regex in <see cref="GitHubApiFeed"/>.
    /// Keep in sync with <see cref="ArtifactPrefixes"/>; ArtifactPrefixesMatchTheRegexPattern covers it.</summary>
    public const string ArtifactPrefixPattern = "VortexArena|XonoticGodot";

    /// <summary>Stable channel: the newest FULL release's manifest via the /releases/latest
    /// redirect — a plain HTTP fetch, no API call, no rate limit.</summary>
    public const string LatestManifestUrl = $"{RepoUrl}/releases/latest/download/latest.json";

    /// <summary>Fallback/beta: the Releases API listing (rate-limited 60/hr unauthenticated —
    /// never the default path). Also the only path that sees prereleases.</summary>
    public const string ReleasesApiUrl = $"https://api.github.com/repos/{Repo}/releases?per_page=10";

    public static string UserAgent =>
        $"VortexLauncher/{typeof(LauncherConfig).Assembly.GetName().Version?.ToString(3) ?? "dev"}";
}

/// <summary>Launcher-owned disk layout (ADR-0015 §6), rooted under LocalApplicationData
/// (%LOCALAPPDATA% / ~/.local/share / ~/Library/Application Support).</summary>
public sealed class LauncherPaths
{
    public string Root { get; }

    public LauncherPaths(string? rootOverride = null) =>
        Root = rootOverride ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "VortexArena", "Launcher");

    public string GameDir => Path.Combine(Root, "game");
    /// <summary>One extracted install per version — current + N-1 kept for rollback.</summary>
    public string VersionsDir => Path.Combine(GameDir, "versions");
    /// <summary>Download + extract scratch; safe to delete whole at any time the launcher isn't installing.</summary>
    public string StagingDir => Path.Combine(GameDir, "staging");
    public string CurrentJsonPath => Path.Combine(GameDir, "current.json");
    /// <summary>Shared content-addressed asset packs (core layout only): store/&lt;hash12&gt;/assets/data.</summary>
    public string AssetStoreDir => Path.Combine(Root, "assets", "store");

    /// <summary>Dedicated-server instances owned by the runner: instances/&lt;name&gt;/ (A2).</summary>
    public string InstancesDir => Path.Combine(Root, "instances");
    /// <summary>Runner-level state: bind config, auth token hash, Conductor link keypair (A2, A9).</summary>
    public string RunnerDir => Path.Combine(Root, "runner");

    public void EnsureCreated()
    {
        Directory.CreateDirectory(VersionsDir);
        Directory.CreateDirectory(StagingDir);
        Directory.CreateDirectory(AssetStoreDir);
    }
}
