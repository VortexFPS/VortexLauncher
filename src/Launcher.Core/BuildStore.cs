using System.Text.Json;

namespace Launcher.Core;

public static class BuildProviders
{
    /// <summary>Downloaded from the game's GitHub Releases.</summary>
    public const string Release = "release";
    /// <summary>Compiled locally by the SourceProvider (A8).</summary>
    public const string Source = "source";
}

/// <summary>One installed build. A build is the unit both providers produce and the unit an instance
/// pins, which is what lets pin, update and rollback behave identically for a downloaded release and a
/// locally compiled one.</summary>
public sealed record BuildRecord
{
    /// <summary>Stable identity. A release build's id is its version; a source build's is
    /// "source:{preset}:{ref}@{sha7}". Ids are not path-safe, which is why <see cref="DirName"/>
    /// exists.</summary>
    public required string Id { get; init; }

    /// <summary>Directory under <see cref="LauncherPaths.VersionsDir"/> holding this build.</summary>
    public required string DirName { get; init; }

    public required string Version { get; init; }
    public required string PlatformKey { get; init; }

    /// <summary>fat or core, see <see cref="InstalledState"/>.</summary>
    public required string Layout { get; init; }

    /// <summary>The zip's internal top-level directory; the game lives one level in.</summary>
    public required string Root { get; init; }

    public string? AssetsVersion { get; init; }
    public string Provider { get; init; } = BuildProviders.Release;
    public DateTimeOffset InstalledAt { get; init; }

    /// <summary>Strip anything that cannot appear in a directory name on any supported OS. Source ids
    /// carry ':' and '@', which Windows rejects outright and which make shell quoting a hazard
    /// everywhere else.</summary>
    public static string SafeDirName(string id)
    {
        var chars = id.Select(c =>
            char.IsLetterOrDigit(c) || c is '.' or '-' or '_' ? c : '-').ToArray();
        var name = new string(chars).Trim('-');
        return name.Length == 0 ? "build" : name;
    }

    public static BuildRecord ForRelease(InstalledState state, DateTimeOffset installedAt) => new()
    {
        Id = state.Version,
        DirName = state.Version,
        Version = state.Version,
        PlatformKey = state.PlatformKey,
        Layout = state.Layout,
        Root = state.Root,
        AssetsVersion = state.AssetsVersion,
        Provider = BuildProviders.Release,
        InstalledAt = installedAt,
    };
}

/// <summary>Side-by-side builds on disk, with pin, rollback and GC.
///
/// The list is derived from what is actually in versions/ and enriched from builds.json, rather than
/// trusted from the file alone. A metadata file that disagrees with the disk is the normal outcome of
/// a crash mid-install or an operator deleting a directory by hand, and in both cases the disk is
/// right. It also means installs made before this file existed are adopted rather than orphaned.</summary>
public sealed class BuildStore(LauncherPaths paths)
{
    /// <summary>Current plus one for rollback. The N-1 rule from ADR-0015 §6.</summary>
    public const int DefaultKeep = 2;

    private string MetadataPath => Path.Combine(paths.GameDir, "builds.json");

    public IReadOnlyList<BuildRecord> List()
    {
        if (!Directory.Exists(paths.VersionsDir))
            return [];

        var known = LoadMetadata();
        var builds = new List<BuildRecord>();

        foreach (var dir in new DirectoryInfo(paths.VersionsDir).GetDirectories())
        {
            if (known.TryGetValue(dir.Name, out var record))
            {
                builds.Add(record);
                continue;
            }

            // On disk with no metadata: adopt it. Version and dir name agree for every release build,
            // and Root is recoverable because the zip's top-level directory is the only thing in here.
            var root = dir.GetDirectories().Length == 1 ? dir.GetDirectories()[0].Name : "";
            builds.Add(new BuildRecord
            {
                Id = dir.Name,
                DirName = dir.Name,
                Version = dir.Name,
                PlatformKey = PlatformKey.Current,
                Layout = InstalledState.LayoutFat,
                Root = root,
                Provider = BuildProviders.Release,
                InstalledAt = dir.LastWriteTimeUtc,
            });
        }

        return builds.OrderByDescending(b => b.InstalledAt).ToList();
    }

    public BuildRecord? Get(string id) =>
        List().FirstOrDefault(b => string.Equals(b.Id, id, StringComparison.Ordinal));

    /// <summary>Directory the game binary lives in.</summary>
    public string GameDirOf(BuildRecord build) =>
        Path.Combine(paths.VersionsDir, build.DirName, build.Root);

    public string BuildDirOf(BuildRecord build) => Path.Combine(paths.VersionsDir, build.DirName);

    public void Register(BuildRecord build)
    {
        var known = LoadMetadata();
        known[build.DirName] = build;
        SaveMetadata(known);
    }

    /// <summary>Delete every build except the protected one and the <paramref name="keep"/>-1 most
    /// recent others. Returns what it removed.
    ///
    /// Deletion failures are swallowed on purpose: a stale directory costs disk, while throwing here
    /// would fail an install that has already succeeded.</summary>
    public IReadOnlyList<string> Gc(int keep = DefaultKeep, string? protectedId = null)
    {
        var all = List();
        var kept = new HashSet<string>(StringComparer.Ordinal);

        if (protectedId is not null)
        {
            var pinned = all.FirstOrDefault(b => b.Id == protectedId);
            if (pinned is not null)
                kept.Add(pinned.DirName);
        }

        foreach (var build in all.Where(b => !kept.Contains(b.DirName)).Take(Math.Max(0, keep - kept.Count)))
            kept.Add(build.DirName);

        var removed = new List<string>();
        foreach (var build in all.Where(b => !kept.Contains(b.DirName)))
        {
            try
            {
                Directory.Delete(BuildDirOf(build), recursive: true);
                removed.Add(build.Id);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        if (removed.Count > 0)
        {
            var known = LoadMetadata();
            foreach (var build in all.Where(b => removed.Contains(b.Id)))
                known.Remove(build.DirName);
            SaveMetadata(known);
        }

        return removed;
    }

    /// <summary>Bytes on disk. Walks the tree, so treat it as a report and not something to call in a
    /// loop.</summary>
    public long SizeOf(BuildRecord build)
    {
        var dir = new DirectoryInfo(BuildDirOf(build));
        return dir.Exists
            ? dir.EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length)
            : 0;
    }

    private Dictionary<string, BuildRecord> LoadMetadata()
    {
        try
        {
            if (!File.Exists(MetadataPath))
                return new(StringComparer.Ordinal);
            return JsonSerializer.Deserialize<Dictionary<string, BuildRecord>>(
                       File.ReadAllText(MetadataPath), ReleaseManifest.JsonOptions)
                   ?? new(StringComparer.Ordinal);
        }
        catch (JsonException)
        {
            return new(StringComparer.Ordinal); // corrupt metadata rebuilds from disk
        }
    }

    private void SaveMetadata(Dictionary<string, BuildRecord> builds)
    {
        Directory.CreateDirectory(paths.GameDir);
        var tmp = MetadataPath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(builds, ReleaseManifest.JsonOptions));
        File.Move(tmp, MetadataPath, overwrite: true);
    }
}
