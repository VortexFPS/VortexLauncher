namespace Launcher.Core;

/// <summary>Why the launcher is or is not offering an update. An enum rather than a bool because
/// four of these six are "no update" for reasons the player has to be told apart: an unreachable
/// feed is a network problem, a prerelease on the stable channel is a setting, and a release with
/// no package for this OS is the release's problem. Collapsing them to false is how a launcher ends
/// up saying "up to date" to someone who is not.</summary>
public enum UpdateStatus
{
    /// <summary>No manifest came back. Says nothing about the installed build — Play still works.</summary>
    FeedUnavailable,

    /// <summary>The newest release is a prerelease and this player is on stable. Not offered, and
    /// deliberately not hidden: the stable channel can still be SHOWN a prerelease when the API
    /// fallback is the only feed that returned anything.</summary>
    PrereleaseNeedsBetaChannel,

    /// <summary>The release exists but carries nothing for this platform key.</summary>
    NoPackageForPlatform,

    /// <summary>Nothing installed yet, and the release has something to install.</summary>
    NotInstalled,

    UpToDate,

    UpdateAvailable,
}

/// <summary>The verdict plus what it is about, so callers can render a message and act on the same
/// value without re-deriving either.</summary>
public sealed record UpdateVerdict(UpdateStatus Status, ReleaseManifest? Manifest, string Detail)
{
    /// <summary>True when there is something to download. Covers the first install too: from the
    /// installer's point of view they are the same operation.</summary>
    public bool CanInstall => Status is UpdateStatus.UpdateAvailable or UpdateStatus.NotInstalled;

    public string? Version => Manifest?.Version;
}

/// <summary>The one place that decides whether a fetched manifest means "update". Extracted from
/// the view model so the periodic checker, the UI and the tests all answer the question the same
/// way — a background check that disagreed with the window about what counts as an update would
/// produce a notification the launcher then refuses to act on.</summary>
public static class UpdateAvailability
{
    public static UpdateVerdict Evaluate(
        ReleaseManifest? manifest, string detail, InstalledState? installed,
        string platformKey, string? channel)
    {
        if (manifest is null)
            return new UpdateVerdict(UpdateStatus.FeedUnavailable, null, detail);

        if (manifest.Prerelease && !ReleaseChannels.IsBeta(channel))
            return new UpdateVerdict(UpdateStatus.PrereleaseNeedsBetaChannel, manifest, detail);

        var plat = manifest.PlatformFor(platformKey);
        if (plat is null || (plat.Bundle is null && plat.Core is null))
            return new UpdateVerdict(UpdateStatus.NoPackageForPlatform, manifest, detail);

        if (installed is null)
            return new UpdateVerdict(UpdateStatus.NotInstalled, manifest, detail);

        // String equality, not a version parse: the manifest's version is whatever the release tag
        // said, and a build the player has is either that string or it is not. Ordering releases
        // would invite a "newer" comparison that quietly refuses a deliberate rollback.
        return new UpdateVerdict(
            installed.Version == manifest.Version ? UpdateStatus.UpToDate : UpdateStatus.UpdateAvailable,
            manifest, detail);
    }
}

/// <summary>Re-checks the feed on a timer for as long as the launcher is running.
///
/// A loop with an injectable sleep rather than a <see cref="System.Threading.Timer"/>, for two
/// reasons. Tests can drive it without waiting hours of wall clock. And a timer would fire a second
/// check while the first is still in flight on a slow connection, which on the beta channel means
/// two unauthenticated GitHub API calls stacking against a 60/hour budget; a loop cannot overlap
/// with itself.
///
/// The interval is re-read every iteration, so changing it in settings takes effect at the next
/// tick without anything having to restart the scheduler.</summary>
public sealed class UpdateScheduler : IDisposable
{
    private readonly Func<CancellationToken, Task> _check;
    private readonly Func<int> _intervalMinutes;
    private readonly Func<TimeSpan, CancellationToken, Task> _sleep;
    private readonly object _gate = new();

    private CancellationTokenSource? _cts;
    private Task? _loop;

    /// <param name="check">The check itself. Exceptions out of it are swallowed — a failed check is
    /// an ordinary event (the box is asleep, the wifi is off) and must not stop the ones after it.</param>
    /// <param name="intervalMinutes">Read fresh each tick; <see cref="UpdateCheckInterval.Never"/>
    /// parks the loop instead of ending it, so turning the interval back on resumes checking.</param>
    /// <param name="sleep">Seam for tests. Defaults to <see cref="Task.Delay(TimeSpan, CancellationToken)"/>.</param>
    public UpdateScheduler(
        Func<CancellationToken, Task> check,
        Func<int> intervalMinutes,
        Func<TimeSpan, CancellationToken, Task>? sleep = null)
    {
        _check = check;
        _intervalMinutes = intervalMinutes;
        _sleep = sleep ?? Task.Delay;
    }

    /// <summary>How long a parked loop waits before re-reading the interval. Short enough that
    /// re-enabling checks in settings feels immediate, and it costs nothing — no network here.</summary>
    public static readonly TimeSpan ParkedPoll = TimeSpan.FromMinutes(1);

    /// <summary>Idempotent: a second call while running does nothing.</summary>
    public void Start()
    {
        lock (_gate)
        {
            if (_loop is not null)
                return;
            _cts = new CancellationTokenSource();
            _loop = RunAsync(_cts.Token);
        }
    }

    /// <summary>Cancel the loop and return immediately, without waiting for an in-flight check.
    ///
    /// This is the one to call from a UI thread, and the distinction is not academic. A check
    /// marshals itself onto the UI thread to touch bound state, so a shutdown path that blocked
    /// that thread waiting for the loop to unwind would be waiting on work that cannot run until it
    /// stops waiting. <see cref="StopAsync"/> is the same thing without that hazard, for callers
    /// that can actually await.</summary>
    public void Stop()
    {
        CancellationTokenSource? cts;
        lock (_gate)
        {
            cts = _cts;
            (_loop, _cts) = (null, null);
        }
        cts?.Cancel();
        cts?.Dispose();
    }

    /// <summary>Stops the loop and waits for the in-flight check to unwind, so a caller disposing
    /// the HttpClient behind <c>check</c> cannot race it. Never call this from a thread the check
    /// needs — see <see cref="Stop"/>.</summary>
    public async Task StopAsync()
    {
        Task? loop;
        CancellationTokenSource? cts;
        lock (_gate)
        {
            (loop, cts) = (_loop, _cts);
            (_loop, _cts) = (null, null);
        }
        if (cts is null)
            return;

        await cts.CancelAsync();
        try { if (loop is not null) await loop; }
        catch (OperationCanceledException) { }
        cts.Dispose();
    }

    private async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var minutes = UpdateCheckInterval.Normalize(_intervalMinutes());
            try
            {
                await _sleep(minutes == UpdateCheckInterval.Never
                    ? ParkedPoll
                    : TimeSpan.FromMinutes(minutes), ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            if (ct.IsCancellationRequested || minutes == UpdateCheckInterval.Never)
                continue;

            try
            {
                await _check(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch
            {
                // Deliberately broad. Whatever went wrong with one check, the next one still runs;
                // the check itself is responsible for reporting its own failure to the player.
            }
        }
    }

    /// <summary>Non-blocking, deliberately: <c>Dispose</c> is reached from arbitrary threads and
    /// blocking one of them on a check that may need it is the deadlock <see cref="Stop"/> exists
    /// to avoid.</summary>
    public void Dispose() => Stop();
}

/// <summary>Remembers which versions have already been announced, so a launcher left open for a
/// week does not toast the same release every four hours.
///
/// In memory and not on disk on purpose: the thing being deduplicated is a notification, and a
/// player who quit and reopened the launcher has plausibly forgotten. Once per version per run is
/// the behaviour that annoys nobody in either direction.</summary>
public sealed class AnnouncedVersions
{
    private readonly HashSet<string> _seen = new(StringComparer.Ordinal);

    /// <summary>True the first time it is asked about a version, false afterwards.</summary>
    public bool ShouldAnnounce(string? version)
    {
        if (string.IsNullOrEmpty(version))
            return false;
        lock (_seen)
            return _seen.Add(version);
    }
}
