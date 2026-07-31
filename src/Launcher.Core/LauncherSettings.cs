using System.Text.Json;
using System.Text.Json.Serialization;

namespace Launcher.Core;

/// <summary>The two release channels a player can pick between. Same names the CLI's
/// <c>--channel</c> option takes, because they end up describing the same thing.</summary>
public static class ReleaseChannels
{
    /// <summary>Full releases only.</summary>
    public const string Stable = "stable";

    /// <summary>Full releases plus prereleases. The GitHub API feed is the only one that sees a
    /// prerelease at all (releases/latest skips them), which is what makes beta a feed choice and
    /// not just a filter.</summary>
    public const string Beta = "beta";

    /// <summary>Anything unrecognised reads as stable, which is deliberately NOT the same as the
    /// shipped default (<see cref="LauncherSettings.Channel"/>, currently beta). The two answer
    /// different questions: a file with no channel at all gets the product decision, while a file
    /// carrying a value this launcher does not recognise — hand-edited, or written by a newer
    /// version — gets the reading that cannot hurt. A typo must never be the thing that opts a
    /// player into prerelease builds.</summary>
    public static string Normalize(string? channel) =>
        string.Equals(channel, Beta, StringComparison.OrdinalIgnoreCase) ? Beta : Stable;

    public static bool IsBeta(string? channel) => Normalize(channel) == Beta;
}

/// <summary>Player-owned launcher preferences, persisted as settings.json.</summary>
public sealed record LauncherSettings
{
    /// <summary>2 added the update and notification block. Nothing reads this to branch — every new
    /// field defaults to what a schema-1 file implies — but a file that has been through this
    /// launcher is worth being able to recognise, and the next migration that cannot be expressed as
    /// a default will need it.</summary>
    public const int CurrentSchema = 2;

    public int Schema { get; init; } = CurrentSchema;

    /// <summary>See <see cref="ReleaseChannels"/>.
    ///
    /// Beta while the game is pre-1.0: everything published is a prerelease or close enough to one
    /// that a player defaulted to stable would sit looking at an empty feed, and the point of the
    /// launcher right now is to get builds in front of people. Two things this drags along, both
    /// worth knowing before it flips back:
    ///
    /// Beta puts the DEFAULT path on <see cref="GitHubApiFeed"/> (<see cref="ChannelFeeds"/>),
    /// because the manifest redirect structurally cannot see a prerelease. That feed is
    /// unauthenticated GitHub API at 60 requests/hour, shared per source IP, where stable's is an
    /// unmetered redirect. It is the reason <see cref="UpdateCheckInterval"/> has a floor at all,
    /// and the reason that floor now applies to everyone rather than to the few who opted in.
    ///
    /// It does not change the launcher's own updates. Velopack's GithubSource treats prerelease as
    /// "consider these too", not "only these", so a beta launcher still finds the full releases
    /// <c>stable</c> publishes.
    ///
    /// Flipping this back to <see cref="ReleaseChannels.Stable"/> is a one-line change here, and
    /// leaves every player who explicitly chose a channel where they put themselves — only files
    /// with no channel key follow the default.</summary>
    public string Channel { get; init; } = ReleaseChannels.Beta;

    /// <summary>Override for <see cref="LauncherPaths"/>'s root; null means the per-user default.</summary>
    public string? InstallRoot { get; init; }

    /// <summary>See <see cref="GameUpdateModes"/>.</summary>
    public string GameUpdates { get; init; } = GameUpdateModes.Download;

    /// <summary>See <see cref="LauncherUpdateModes"/>.</summary>
    public string LauncherUpdates { get; init; } = LauncherUpdateModes.Automatic;

    /// <summary>See <see cref="NotificationReaches"/>. Defaults to unset so first run asks; it is the
    /// one preference here with no defensible default, because the answer turns on whether the player
    /// wants a resident process and nothing on disk can tell the launcher that.</summary>
    public string NotificationReach { get; init; } = NotificationReaches.Unset;

    /// <summary>Minutes between background feed checks; see <see cref="UpdateCheckInterval"/>.</summary>
    public int UpdateCheckMinutes { get; init; } = UpdateCheckInterval.DefaultMinutes;

    /// <summary>Start the launcher when the player logs in. Only meaningful under the background
    /// reach — a launcher that autostarts to show a window nobody asked for is a nuisance, so the
    /// two are set together and cleared together.</summary>
    public bool StartWithSystem { get; init; }

    /// <summary>Derived from <see cref="Channel"/>, and kept out of the file for that reason: a
    /// serialized copy is a second source of truth that only ever drifts. Someone editing the file by
    /// hand to leave beta would flip "channel" and leave this reading true, and the next reader has to
    /// know which one loses. Nothing can disagree with the channel if the channel is the only thing
    /// written.</summary>
    [JsonIgnore] public bool IsBeta => ReleaseChannels.IsBeta(Channel);

    /// <summary>Same rule as <see cref="IsBeta"/>: read off the stored reach, never stored beside it.</summary>
    [JsonIgnore] public bool WantsSystemNotifications =>
        NotificationReaches.WantsSystemNotifications(NotificationReach);

    [JsonIgnore] public bool WantsTray => NotificationReaches.WantsTray(NotificationReach);

    [JsonIgnore] public bool HasChosenNotificationReach =>
        NotificationReaches.IsChosen(NotificationReach);
}

/// <summary>Reads and writes settings.json.
///
/// The file lives at the DEFAULT launcher root and never under <see cref="LauncherSettings.InstallRoot"/>,
/// even after the player overrides that. The override is read out of this file, so a copy stored at the
/// overridden location could not be found until after it had already been read. That also means a root
/// change moves game data only — see <see cref="InstallRelocation"/> — and leaves this file alone.</summary>
public sealed class LauncherSettingsStore
{
    /// <summary>The per-user root, ignoring any override.</summary>
    public static string DefaultRoot { get; } = new LauncherPaths().Root;

    private readonly string _dir;

    public LauncherSettingsStore(string? directory = null) => _dir = directory ?? DefaultRoot;

    public string FilePath => Path.Combine(_dir, "settings.json");

    /// <summary>Never throws: an unreadable or corrupt settings file falls back to defaults, because
    /// the launcher still has to start.</summary>
    public LauncherSettings Load()
    {
        try
        {
            if (!File.Exists(FilePath))
                return new LauncherSettings();

            var loaded = JsonSerializer.Deserialize<LauncherSettings>(
                File.ReadAllText(FilePath), ReleaseManifest.JsonOptions) ?? new LauncherSettings();

            // Every string field goes through its own Normalize on the way in, so the rest of the
            // launcher can switch on these values without a default arm that means "corrupt file".
            // A schema-1 file simply has none of the update fields and picks up the defaults, which
            // is the whole migration: notify-reach lands on unset, so those players get asked once.
            return loaded with
            {
                Schema = LauncherSettings.CurrentSchema,
                Channel = ReleaseChannels.Normalize(loaded.Channel),
                InstallRoot = string.IsNullOrWhiteSpace(loaded.InstallRoot) ? null : loaded.InstallRoot.Trim(),
                GameUpdates = GameUpdateModes.Normalize(loaded.GameUpdates),
                LauncherUpdates = LauncherUpdateModes.Normalize(loaded.LauncherUpdates),
                NotificationReach = NotificationReaches.Normalize(loaded.NotificationReach),
                UpdateCheckMinutes = UpdateCheckInterval.Normalize(loaded.UpdateCheckMinutes),
                // A tray-only setting left true after the player moved off the background reach would
                // autostart a launcher that no longer has a reason to be resident.
                StartWithSystem = loaded.StartWithSystem
                    && NotificationReaches.WantsTray(loaded.NotificationReach),
            };
        }
        catch (JsonException)
        {
            return new LauncherSettings();
        }
        catch (IOException)
        {
            return new LauncherSettings();
        }
        catch (UnauthorizedAccessException)
        {
            return new LauncherSettings();
        }
    }

    public void Save(LauncherSettings settings)
    {
        Directory.CreateDirectory(_dir);
        // temp + move, same as current.json: a crash mid-write must not leave a torn settings file
        var tmp = FilePath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(settings, ReleaseManifest.JsonOptions));
        File.Move(tmp, FilePath, overwrite: true);
    }
}

/// <summary>Which feeds a channel asks, and in what order.</summary>
public static class ChannelFeeds
{
    /// <summary>Stable keeps the manifest-first order: a plain redirect fetch with no rate limit, and
    /// releases/latest already means "newest full release". Beta has to ask the API FIRST, because the
    /// manifest path structurally cannot see a prerelease; the manifest stays behind it as a fallback
    /// for when the API's unauthenticated 60/hr cap is spent.
    ///
    /// Stable does not filter here. When only a prerelease exists the API fallback still returns it,
    /// and the caller can say so — a launcher that reported "no releases found" while a prerelease sat
    /// on the repo would be telling the player something untrue.</summary>
    public static CompositeFeed FeedFor(HttpClient http, string? channel) =>
        ReleaseChannels.IsBeta(channel)
            ? new CompositeFeed(new GitHubApiFeed(http), new ManifestFeed(http))
            : LauncherHttp.DefaultFeed(http);
}

/// <summary>Moving the game data when the player points the install root somewhere else.
///
/// Chosen over the two alternatives on purpose. Leaving the old install behind orphans gigabytes the
/// launcher will never mention again and forces a full re-download; deleting it throws away a working
/// install to save a copy. Moving keeps the player playable across the change. It is still gated on
/// confirmation in the UI, because this can be a multi-gigabyte cross-volume copy and because it fails
/// outright while the game is running — both things worth knowing BEFORE it starts, not after.</summary>
public static class InstallRelocation
{
    /// <summary>What a root change carries: the installed builds and the shared asset store.
    ///
    /// runner/ and instances/ deliberately stay put. Those belong to the dedicated-server runner, and
    /// a player changing where the game installs has no business yanking a live server's state out
    /// from under it.</summary>
    private static string[] Subtrees(LauncherPaths paths) => [paths.GameDir, paths.AssetStoreDir];

    /// <summary>Bytes a move would have to carry; 0 when there is nothing at this root. Walks the
    /// tree, so call it off the UI thread.</summary>
    public static long SizeOf(LauncherPaths paths) => Subtrees(paths).Sum(DirectorySize);

    public static bool SameDirectory(string a, string b)
    {
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        return string.Equals(Normalize(a), Normalize(b), comparison);
    }

    /// <summary>True when <paramref name="child"/> sits inside <paramref name="parent"/>. Used to
    /// reject moving a root into its own subdirectory, which would otherwise try to move a directory
    /// into itself.</summary>
    public static bool IsInside(string child, string parent)
    {
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        var p = Normalize(parent) + Path.DirectorySeparatorChar;
        return Normalize(child).StartsWith(p, comparison);
    }

    /// <summary>Move game data from one launcher root to another. Returns warnings worth showing the
    /// player (leftovers that could not be cleaned up); throws if the move did not happen, in which
    /// case the old root is left exactly as it was.</summary>
    public static IReadOnlyList<string> Move(LauncherPaths from, LauncherPaths to,
        IProgress<(string Phase, double Fraction)>? progress = null, CancellationToken ct = default)
    {
        if (SameDirectory(from.Root, to.Root))
            return [];
        if (IsInside(to.Root, from.Root))
            throw new IOException(
                $"'{to.Root}' is inside the current install folder — pick a folder outside '{from.Root}'");

        var pairs = Subtrees(from).Zip(Subtrees(to))
            .Where(p => Directory.Exists(p.First))
            .ToList();

        // Check every destination before touching anything: a refusal halfway through is the messy case.
        foreach (var (_, dst) in pairs)
        {
            if (Directory.Exists(dst) && Directory.EnumerateFileSystemEntries(dst).Any())
                throw new IOException($"'{dst}' already exists and is not empty — pick an empty folder");
        }

        var warnings = new List<string>();
        var moved = new List<(string From, string To)>();
        progress?.Report(("Moving game files", 0));
        try
        {
            foreach (var (src, dst) in pairs)
            {
                warnings.AddRange(MoveTree(src, dst, progress, ct));
                moved.Add((src, dst));
            }
        }
        catch
        {
            // Put back whatever already made it across. A half-moved install — builds under the new
            // root, asset store under the old one — is precisely the orphaned state this code exists
            // to prevent, and the caller is about to keep pointing at the old root.
            foreach (var (src, dst) in moved)
            {
                try { MoveTree(dst, src, null, CancellationToken.None); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
            throw;
        }

        progress?.Report(("Moving game files", 1));
        return warnings;
    }

    private static List<string> MoveTree(string src, string dst,
        IProgress<(string Phase, double Fraction)>? progress, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
        if (Directory.Exists(dst))
            Directory.Delete(dst); // verified empty above; Directory.Move needs the name free

        try
        {
            // Same volume: a rename, however many gigabytes are underneath it.
            Directory.Move(src, dst);
            return [];
        }
        catch (IOException)
        {
            // Across volumes there is no rename — the bytes have to be copied. Falls through.
        }

        try
        {
            CopyTree(src, dst, progress, ct);
        }
        catch
        {
            try { Directory.Delete(dst, recursive: true); } catch (IOException) { } // no half-copy left behind
            throw;
        }

        try
        {
            Directory.Delete(src, recursive: true); // only once every byte is across
            return [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The copy is complete, so the move succeeded; the source is now just wasted disk. Reporting
            // it beats failing a relocation that has already done the part that matters.
            return [$"couldn't delete the old copy at {src} ({ex.Message}) — you can remove it by hand"];
        }
    }

    private static void CopyTree(string src, string dst,
        IProgress<(string Phase, double Fraction)>? progress, CancellationToken ct)
    {
        var files = new DirectoryInfo(src).EnumerateFiles("*", SearchOption.AllDirectories).ToList();
        var total = Math.Max(1, files.Sum(f => f.Length));
        long done = 0;

        Directory.CreateDirectory(dst);
        foreach (var dir in Directory.EnumerateDirectories(src, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Rebase(dir));

        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();
            file.CopyTo(Rebase(file.FullName), overwrite: true);
            done += file.Length;
            progress?.Report(("Moving game files", (double)done / total));
        }

        string Rebase(string path) => Path.Combine(dst, Path.GetRelativePath(src, path));
    }

    private static long DirectorySize(string dir)
    {
        var info = new DirectoryInfo(dir);
        return info.Exists ? info.EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length) : 0;
    }

    private static string Normalize(string path) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
}
