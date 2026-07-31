using Launcher.Core;
using Velopack;
using Velopack.Sources;

namespace Launcher.Desktop;

/// <summary>Where a self-update has got to. The interesting one is <see cref="Ready"/>: the new
/// launcher is on disk and the only thing left is a restart, which is the step this class refuses
/// to take on its own.</summary>
public enum SelfUpdateState
{
    /// <summary>Not a packaged build (<c>dotnet run</c>), so there is nothing to update.</summary>
    Inert,

    UpToDate,

    /// <summary>A newer launcher exists and has not been fetched — either the mode says do not
    /// fetch it, or the fetch has not happened yet.</summary>
    Available,

    /// <summary>Downloaded and staged. Restarting applies it.</summary>
    Ready,

    Failed,
}

public sealed record SelfUpdateResult(SelfUpdateState State, string Message, string? Version = null);

/// <summary>The launcher's OWN update path — Velopack against this repo's releases (launcher
/// packages ship on the same v* release train as the game, ADR-0015 §7).
///
/// Two things here are deliberate and were not true of the first version of this file.
///
/// <b>Checking is separate from restarting.</b> The old code called
/// <c>ApplyUpdatesAndRestart</c> straight out of a fire-and-forget startup check, which is a call
/// that terminates the process. Nothing consulted whether a game download was in flight, so a
/// launcher that found a self-update at the wrong moment would kill a partial install and take the
/// window with it. Now a check can reach <see cref="SelfUpdateState.Ready"/> and stop there; the
/// view model restarts when it knows nothing is mid-flight, and the player is told it is happening.
///
/// <b>The channel is honoured.</b> The old code passed <c>prerelease: true</c> unconditionally, so
/// a player on the stable channel was served prerelease launchers — the exact opposite of what
/// <see cref="ReleaseChannels"/> promises, and invisible because a launcher does not show its own
/// version prominently.
///
/// Still inert for unpackaged dev builds: <c>UpdateManager.IsInstalled</c> is false under
/// <c>dotnet run</c>, which is what makes this safe to call unconditionally at startup.</summary>
public sealed class SelfUpdateService
{
    /// <summary>The staged update, held between the check that found it and the restart that
    /// applies it. Velopack wants the same <c>UpdateInfo</c> back.</summary>
    private UpdateInfo? _pending;
    private bool _downloaded;

    /// <summary>Set once <see cref="Apply"/> has been called, so a second press cannot start a
    /// second restart while the first is unwinding.</summary>
    public bool Restarting { get; private set; }

    /// <summary>True when a restart would install something.</summary>
    public bool IsReady => _pending is not null && _downloaded;

    public string? PendingVersion => _pending?.TargetFullRelease?.Version?.ToString();

    /// <summary>Check, and under <see cref="LauncherUpdateModes.Automatic"/> download too. Never
    /// restarts.
    ///
    /// <see cref="LauncherUpdateModes.Off"/> still checks. The player asked for no automatic
    /// launcher updates, not to be kept in the dark: <c>latest.json</c> is a cross-repo contract,
    /// and a launcher old enough to stop being able to read it should be able to say so rather than
    /// just failing to find the game.</summary>
    public async Task<SelfUpdateResult> CheckAsync(LauncherSettings settings, CancellationToken ct)
    {
        var mode = LauncherUpdateModes.Normalize(settings.LauncherUpdates);
        try
        {
            // Rebuilt per check rather than cached: the source encodes the channel, so a player who
            // switches to beta in settings would otherwise keep asking the stable source until restart.
            var mgr = new UpdateManager(
                new GithubSource(LauncherConfig.RepoUrl, accessToken: null, prerelease: settings.IsBeta));

            if (!mgr.IsInstalled)
                return new SelfUpdateResult(SelfUpdateState.Inert, "dev build — self-update inert");

            if (IsReady)
                return new SelfUpdateResult(SelfUpdateState.Ready,
                    $"launcher {PendingVersion} ready — restart to finish", PendingVersion);

            var update = await mgr.CheckForUpdatesAsync();
            if (update is null)
            {
                _pending = null;
                _downloaded = false;
                return new SelfUpdateResult(SelfUpdateState.UpToDate, "launcher up to date");
            }

            _pending = update;
            _downloaded = false;
            var version = PendingVersion;

            if (mode != LauncherUpdateModes.Automatic)
                return new SelfUpdateResult(SelfUpdateState.Available,
                    mode == LauncherUpdateModes.Off
                        ? $"launcher {version} is available (automatic launcher updates are off)"
                        : $"launcher {version} is available",
                    version);

            await mgr.DownloadUpdatesAsync(update, cancelToken: ct);
            _downloaded = true;
            return new SelfUpdateResult(SelfUpdateState.Ready,
                $"launcher {version} ready — restart to finish", version);
        }
        catch (OperationCanceledException)
        {
            return new SelfUpdateResult(SelfUpdateState.Failed, "launcher update check cancelled");
        }
        catch (Exception ex)
        {
            // Self-update must never take the launcher down with it. A player whose launcher cannot
            // update itself can still install and play the game, which is the job.
            return new SelfUpdateResult(SelfUpdateState.Failed, $"self-update check failed: {ex.Message}");
        }
    }

    /// <summary>Fetch an update that <see cref="CheckAsync"/> found but did not download, for the
    /// notify and off modes where the player presses a button instead.</summary>
    public async Task<SelfUpdateResult> DownloadAsync(LauncherSettings settings, CancellationToken ct)
    {
        if (_pending is null)
            return await CheckAsync(settings with { LauncherUpdates = LauncherUpdateModes.Automatic }, ct);
        if (_downloaded)
            return new SelfUpdateResult(SelfUpdateState.Ready,
                $"launcher {PendingVersion} ready — restart to finish", PendingVersion);

        try
        {
            var mgr = new UpdateManager(
                new GithubSource(LauncherConfig.RepoUrl, accessToken: null, prerelease: settings.IsBeta));
            await mgr.DownloadUpdatesAsync(_pending, cancelToken: ct);
            _downloaded = true;
            return new SelfUpdateResult(SelfUpdateState.Ready,
                $"launcher {PendingVersion} ready — restart to finish", PendingVersion);
        }
        catch (Exception ex)
        {
            return new SelfUpdateResult(SelfUpdateState.Failed, $"launcher download failed: {ex.Message}");
        }
    }

    /// <summary>Restart into the staged launcher. Terminates this process on success.
    ///
    /// The caller is responsible for the one precondition this cannot check for itself — that no
    /// install is in flight. Returns false rather than throwing when there is nothing staged, so a
    /// button wired to it is harmless.</summary>
    public bool Apply()
    {
        if (!IsReady || Restarting)
            return false;

        Restarting = true;
        try
        {
            // The channel is irrelevant here — the update was already chosen and downloaded; this
            // source exists only because ApplyUpdatesAndRestart hangs off an UpdateManager.
            var mgr = new UpdateManager(
                new GithubSource(LauncherConfig.RepoUrl, accessToken: null, prerelease: false));
            mgr.ApplyUpdatesAndRestart(_pending!); // exits this process
            return true;
        }
        catch (Exception)
        {
            Restarting = false;
            return false;
        }
    }
}
