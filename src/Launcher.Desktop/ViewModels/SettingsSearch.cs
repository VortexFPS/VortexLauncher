namespace Launcher.Desktop.ViewModels;

/// <summary>The tabs down the left of the settings sheet. Strings rather than an enum because they
/// are also the thing the view binds against, and an enum would need a converter to do the same
/// job.</summary>
public static class SettingsTabs
{
    public const string Channel = "channel";
    public const string GameUpdates = "game";
    public const string LauncherUpdates = "launcher";
    public const string Notifications = "notifications";
    public const string Folders = "folders";
}

/// <summary>What each searchable row in the settings sheet can be found by.
///
/// Keywords, not labels. A player looking for the update interval types "how often" or "frequency",
/// not "Check every", and someone looking for where the game lives types "disk" or "drive" before
/// they type "install root". Matching only on visible text would fail all of those, so each row
/// carries the words somebody would actually reach for — including the words used by the settings
/// UI of whatever launcher they used last.
///
/// The rows are deliberately coarser than the controls. "Notification reach" is one row covering
/// three radio buttons, because a search that surfaced one radio button out of a group would show a
/// choice with no alternatives to choose between.</summary>
public static class SettingsSearch
{
    public const string Channel = "channel";
    public const string GameUpdates = "game-updates";
    public const string LauncherUpdates = "launcher-updates";
    public const string Reach = "reach";
    public const string Autostart = "autostart";
    public const string Interval = "interval";
    public const string InstallRoot = "install-root";
    public const string GameData = "game-data";

    private static readonly Dictionary<string, string> Keywords = new(StringComparer.Ordinal)
    {
        [Channel] =
            "channel stable beta prerelease pre-release release track early access test builds",
        [GameUpdates] =
            "game updates update download install automatic auto background ask before switching "
            + "notify only manual patch new version",
        [LauncherUpdates] =
            "launcher updates update self-update automatic auto notify off disable restart version",
        [Reach] =
            "notifications notify desktop system toast tray background banner alerts popup tell me",
        [Autostart] =
            "start with system startup login boot autostart run at launch windows startup",
        [Interval] =
            "check every interval minutes frequency how often poll refresh schedule background check",
        [InstallRoot] =
            "install location folder directory path where games builds stored disk drive move "
            + "relocate library ssd space",
        [GameData] =
            "game data user directory folder saves savegames config configuration screenshots logs "
            + "settings profile my documents appdata",
    };

    /// <summary>True when <paramref name="query"/> should surface the row named by
    /// <paramref name="key"/>.
    ///
    /// Every whitespace-separated term has to match something, so typing more words narrows rather
    /// than widens — "game folder" finds the game-data row and not every row mentioning "game".
    /// Matching is substring, so a half-typed word still hits while the player is mid-keystroke,
    /// which is the whole point of filtering as you type.</summary>
    public static bool Matches(string key, string? query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return true;
        if (!Keywords.TryGetValue(key, out var haystack))
            return false;

        foreach (var term in query.Split(' ', StringSplitOptions.RemoveEmptyEntries
                                              | StringSplitOptions.TrimEntries))
            if (!haystack.Contains(term, StringComparison.OrdinalIgnoreCase))
                return false;

        return true;
    }
}
