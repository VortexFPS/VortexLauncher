namespace Launcher.Core;

/// <summary>What the launcher does when a new GAME version appears.
///
/// Three settings and not a checkbox, because the two things a player trades off here pull in
/// opposite directions: a download that happens before Play is pressed is the only way Play stays
/// instant, and a download that happens without being asked is the wrong answer on a metered
/// connection. Neither is right for everyone, so both are reachable.</summary>
public static class GameUpdateModes
{
    /// <summary>Say so, download nothing. The whole wait lands on the player at the moment they
    /// wanted to play, which is the point: nothing touches the network until they ask.</summary>
    public const string Notify = "notify";

    /// <summary>The default. Fetch in the background as soon as an update is seen, then ask before
    /// swapping the install. Play stays instant on the old build while it downloads, and the swap —
    /// the part that changes what happens next time the player joins a server — is still a decision.</summary>
    public const string Download = "download";

    /// <summary>Download and install without asking. Steam-shaped, and the reason it is not the
    /// default is that the swap can land in the seconds between reading "ready to play" and
    /// pressing Play.</summary>
    public const string Install = "install";

    /// <summary>Anything unrecognised reads as the shipped default rather than as the most eager
    /// option. A settings file written by a newer launcher, or hand-edited, must never be able to
    /// escalate what this one does behind the player's back.</summary>
    public static string Normalize(string? mode) => mode?.Trim().ToLowerInvariant() switch
    {
        Notify => Notify,
        Install => Install,
        _ => Download,
    };
}

/// <summary>What the launcher does about updates to ITSELF.
///
/// Fully controllable at the player's request, including off. That carries a real hazard and the
/// UI says so: <c>latest.json</c> is a cross-repo contract (README, "The contract with the game
/// repo"), so a launcher pinned far enough behind can end up unable to read the manifest and
/// therefore unable to install the game at all. <see cref="Off"/> still checks and still reports,
/// because a launcher that has silently stopped being able to do its job should at least say so.</summary>
public static class LauncherUpdateModes
{
    /// <summary>The default: fetch and stage a new launcher, then restart into it at a moment that
    /// is not mid-download. Never mid-install — see the restart gate in SelfUpdateService.</summary>
    public const string Automatic = "automatic";

    /// <summary>Check and say so; apply on a button press.</summary>
    public const string Notify = "notify";

    /// <summary>Do not download or apply. Still checks, so the version gap is visible.</summary>
    public const string Off = "off";

    public static string Normalize(string? mode) => mode?.Trim().ToLowerInvariant() switch
    {
        Notify => Notify,
        Off => Off,
        _ => Automatic,
    };
}

/// <summary>How far an update notice is allowed to travel. Asked once on first run, because the
/// honest answer depends on something the launcher cannot infer: whether this player wants a
/// background process on their machine.</summary>
public static class NotificationReaches
{
    /// <summary>Not yet chosen. Drives the first-run prompt, and is why the field is nullable in
    /// the file: a schema-1 settings file has no value here, and those players get asked too.</summary>
    public const string Unset = "unset";

    /// <summary>A banner in the launcher window. No new process, no OS integration, and the player
    /// finds out when they open the launcher — which is when they were going to play anyway.</summary>
    public const string InApp = "in-app";

    /// <summary>The banner plus a native OS notification, while the launcher is running.</summary>
    public const string System = "system";

    /// <summary>The launcher stays in the tray when its window is closed and keeps checking, so a
    /// notice arrives without the launcher being open. The only reach that costs a resident
    /// process, which is exactly why it is not a default.</summary>
    public const string Background = "background";

    /// <summary>Unrecognised reads as <see cref="InApp"/> — the least reach, never a silent
    /// promotion to a resident process. Missing/blank stays <see cref="Unset"/> so the first-run
    /// prompt still fires; only an explicit choice clears it.</summary>
    public static string Normalize(string? reach) => reach?.Trim().ToLowerInvariant() switch
    {
        null or "" or Unset => Unset,
        System => System,
        Background => Background,
        _ => InApp,
    };

    /// <summary>True once the player has answered the first-run question.</summary>
    public static bool IsChosen(string? reach) => Normalize(reach) != Unset;

    /// <summary>Whether this reach wants an OS-level notification at all. Both System and
    /// Background do; the tray one would be pointless without it.</summary>
    public static bool WantsSystemNotifications(string? reach) =>
        Normalize(reach) is System or Background;

    /// <summary>Whether closing the window should leave the launcher resident.</summary>
    public static bool WantsTray(string? reach) => Normalize(reach) == Background;
}

/// <summary>How often the launcher re-checks the feed while it is running.
///
/// Clamped rather than free, and the floor is not arbitrary: the beta channel asks
/// <see cref="GitHubApiFeed"/> FIRST (see <see cref="ChannelFeeds"/>) and that is unauthenticated
/// GitHub API at 60 requests/hour, shared with every other thing on the box using it. A launcher
/// left open for a week at a one-minute interval would spend that budget and degrade its own feed.</summary>
public static class UpdateCheckInterval
{
    /// <summary>Startup only — no repeating timer.</summary>
    public const int Never = 0;

    public const int MinimumMinutes = 15;
    public const int MaximumMinutes = 24 * 60;
    public const int DefaultMinutes = 4 * 60;

    /// <summary>Negative reads as <see cref="Never"/> rather than as an error, and anything inside
    /// the range is taken as given; only values below the floor are lifted to it.</summary>
    public static int Normalize(int minutes) => minutes switch
    {
        <= 0 => Never,
        < MinimumMinutes => MinimumMinutes,
        > MaximumMinutes => MaximumMinutes,
        _ => minutes,
    };
}
