namespace Launcher.Core;

/// <summary>Manifest platform keys and their per-OS facts (zip-name suffixes are identical to the
/// keys; roots mirror tools/package.sh + tools/make-manifest.py — keep the three in sync).</summary>
public static class PlatformKey
{
    public const string Windows = "windows-x86_64";
    public const string Linux = "linux-x86_64";
    public const string LinuxDedicated = "linux-dedicated-x86_64";
    public const string MacOS = "macos-universal";

    /// <summary>zip-name suffix → (platform key, zip internal root dir). Used by the API-fallback
    /// feed to synthesize a manifest from bare release-asset names.</summary>
    public static readonly IReadOnlyDictionary<string, (string Key, string Root)> ZipSuffixMap =
        new Dictionary<string, (string, string)>
        {
            [Windows] = (Windows, "windows-client"),
            [Linux] = (Linux, "linux-client"),
            [LinuxDedicated] = (LinuxDedicated, "linux-dedicated"),
            [MacOS] = (MacOS, "macos-client"),
        };

    /// <summary>The key for the machine we're running on (the launcher targets clients only).</summary>
    public static string Current => Resolve(
        OperatingSystem.IsWindows(), OperatingSystem.IsLinux(), OperatingSystem.IsMacOS());

    public static string Resolve(bool windows, bool linux, bool macos) =>
        windows ? Windows
        : linux ? Linux
        : macos ? MacOS
        : throw new PlatformNotSupportedException("Vortex Arena ships Windows/Linux/macOS clients only");

    /// <summary>Game binary path relative to the install's root dir (RELEASING.md "What ships"),
    /// for one artifact prefix. Names come from the game's export presets via tools/package.sh.</summary>
    public static string ExecutableRelativePath(string key, string prefix) => key switch
    {
        Windows => $"{prefix}.exe",
        Linux => $"{prefix}.x86_64",
        LinuxDedicated => $"{prefix.ToLowerInvariant()}-dedicated.x86_64",
        MacOS => Path.Combine($"{prefix}.app", "Contents", "MacOS", prefix),
        _ => throw new ArgumentException($"unknown platform key '{key}'", nameof(key)),
    };

    /// <summary>The name a NEW install is expected to carry. Use this for messages and defaults; use
    /// <see cref="ExecutableCandidates"/> to actually find the binary, because an install made before
    /// the artifact rename carries the older name and keeps working.</summary>
    public static string ExecutableRelativePath(string key) =>
        ExecutableRelativePath(key, LauncherConfig.CanonicalArtifactPrefix);

    /// <summary>Every binary name this platform could have on disk, newest prefix first.</summary>
    public static IEnumerable<string> ExecutableCandidates(string key) =>
        LauncherConfig.ArtifactPrefixes.Select(p => ExecutableRelativePath(key, p));

    /// <summary>Parse a release zip name ("&lt;prefix&gt;-&lt;version&gt;-&lt;suffix&gt;[-core].zip") given the
    /// release's version (versions can contain hyphens — 0.1.0-alpha — so the suffix can't be
    /// split out by pattern alone).</summary>
    public static bool TryParseZipName(string zipName, string version,
        out string key, out string root, out bool isCore)
    {
        key = root = ""; isCore = false;
        if (!zipName.EndsWith(".zip", StringComparison.Ordinal))
            return false;

        // Accept either artifact prefix: a release published on the far side of the rename is still
        // installable, and so is one published before it.
        var prefix = LauncherConfig.ArtifactPrefixes
            .Select(p => $"{p}-{version}-")
            .FirstOrDefault(p => zipName.StartsWith(p, StringComparison.Ordinal));
        if (prefix is null)
            return false;

        var suffix = zipName[prefix.Length..^4];
        if (suffix.EndsWith("-core", StringComparison.Ordinal))
        {
            isCore = true;
            suffix = suffix[..^5];
        }
        if (!ZipSuffixMap.TryGetValue(suffix, out var m))
            return false;
        (key, root) = m;
        return true;
    }
}
