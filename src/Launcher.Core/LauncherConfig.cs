using Launcher.Protocol;

namespace Launcher.Core;

/// <summary>The one place the distribution endpoints live (ADR-0015 §5).</summary>
public static class LauncherConfig
{
    /// <summary>The GAME's repo. Everything below about feeds, manifests and artifacts is about
    /// this one, and so is the default clone source for <c>vortex source build</c>. The launcher's
    /// own releases are somewhere else — see <see cref="LauncherRepo"/>.</summary>
    public const string Repo = "VortexFPS/VortexArena";
    public const string RepoUrl = $"https://github.com/{Repo}";

    /// <summary>The LAUNCHER's own repo, and the only thing Velopack self-update should ever look
    /// at (<c>Launcher.Desktop/SelfUpdateService.cs</c>).
    ///
    /// Separate from <see cref="Repo"/> since the extraction, and the split is load-bearing in both
    /// directions. ADR-0015 §7 originally had launcher packages riding the game's release train,
    /// which made one constant correct for both; the launcher then became its own repo with its own
    /// cadence, and §7 is superseded. Pointing self-update back at the game repo does not merely
    /// fail to find packages — publishing launcher packages *there* would resolve
    /// <c>releases/latest</c> to a non-game release, 404 <see cref="LatestManifestUrl"/> and drop
    /// every launcher onto the rate-limited API fallback. The two release trains have to stay
    /// apart. <c>ReleaseRepoUrlsAreDistinct</c> in the test suite holds this open.</summary>
    public const string LauncherRepo = "VortexFPS/VortexLauncher";
    public const string LauncherRepoUrl = $"https://github.com/{LauncherRepo}";

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

    /// <summary>The minisign detached signature over <see cref="LatestManifestUrl"/>, attached to
    /// the same release under minisign's default output name. Requirements for the release job that
    /// produces it are in release-signing.md.</summary>
    public const string LatestManifestSignatureUrl = LatestManifestUrl + ".minisig";

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

    // The default root comes from Launcher.Protocol so that Launcher.WebServer, which cannot
    // reference this project, still finds runner.json in the same place the runner wrote it.
    public LauncherPaths(string? rootOverride = null) =>
        Root = rootOverride ?? RunnerLayout.DefaultDataRoot;

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

/// <summary>Where the GAME keeps the player's own files — settings, saves, screenshots, logs. Not
/// launcher-owned, which is the whole reason this is a computed path and not a setting.
///
/// Godot resolves <c>user://</c> to <c>&lt;platform data dir&gt;/godot/app_userdata/&lt;project
/// name&gt;</c>, and VortexArena's project.godot sets <c>config/name="VortexArena"</c> with no
/// <c>use_custom_user_dir</c>, so the path is derivable without asking the game. The launcher can
/// therefore open it, which is the useful part.
///
/// <b>It cannot move it.</b> Godot's <c>--userdir</c> takes a directory NAME, not a path, and the
/// game's own Main.cs does not parse a user-directory argument at all — it handles <c>--data</c>,
/// <c>--map</c>, <c>--dedicated</c>, <c>--port</c> and friends. Relocating this would mean rerouting
/// every <c>user://</c> access inside the game. So the settings sheet shows the path and opens it,
/// and does not offer a picker that could not be honoured.
///
/// The platform branches are written out rather than going through SpecialFolder because the
/// mapping is wrong for this purpose on two of the three: .NET maps
/// <c>SpecialFolder.ApplicationData</c> to <c>~/.config</c> on Linux and macOS, while Godot uses
/// the XDG *data* dir and <c>~/Library/Application Support</c> respectively.</summary>
public static class GameUserData
{
    /// <summary>project.godot's <c>config/name</c>. A rename there moves every player's saves, so
    /// it is a game-side decision this only mirrors.</summary>
    public const string ProjectName = "VortexArena";

    public static string Path
    {
        get
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

            // The engine directory is "Godot" on Windows and macOS and lowercase "godot" on Linux,
            // which only matters on the one of the three with a case-sensitive filesystem.
            var (baseDir, godot) = OperatingSystem.IsWindows()
                ? (Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Godot")
                : OperatingSystem.IsMacOS()
                    ? (System.IO.Path.Combine(home, "Library", "Application Support"), "Godot")
                    : (Environment.GetEnvironmentVariable("XDG_DATA_HOME") is { Length: > 0 } xdg
                        ? xdg
                        : System.IO.Path.Combine(home, ".local", "share"), "godot");

            return System.IO.Path.Combine(baseDir, godot, "app_userdata", ProjectName);
        }
    }
}
