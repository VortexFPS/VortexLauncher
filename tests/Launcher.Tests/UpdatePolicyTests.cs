using Launcher.Core;
using Xunit;

namespace Launcher.Tests;

/// <summary>The settings file is the thing a hand-edit or a downgrade reaches first, so these are
/// mostly about what a value the launcher does not recognise is allowed to mean. The rule is one
/// direction: an unreadable or unknown setting may never leave the launcher doing MORE than the
/// shipped default — never auto-installing when the default asks first, never notifying before the
/// player has been asked, never resident when they did not choose resident.</summary>
public class UpdatePolicyTests
{
    [Theory]
    [InlineData(null, GameUpdateModes.Download)]
    [InlineData("", GameUpdateModes.Download)]
    [InlineData("  ", GameUpdateModes.Download)]
    [InlineData("nonsense", GameUpdateModes.Download)]
    [InlineData("INSTALL", GameUpdateModes.Install)]
    [InlineData(" notify ", GameUpdateModes.Notify)]
    public void Game_update_mode_falls_back_to_the_shipped_default(string? stored, string expected) =>
        Assert.Equal(expected, GameUpdateModes.Normalize(stored));

    [Theory]
    [InlineData(null, LauncherUpdateModes.Automatic)]
    [InlineData("whatever", LauncherUpdateModes.Automatic)]
    [InlineData("OFF", LauncherUpdateModes.Off)]
    [InlineData("notify", LauncherUpdateModes.Notify)]
    public void Launcher_update_mode_falls_back_to_automatic(string? stored, string expected) =>
        Assert.Equal(expected, LauncherUpdateModes.Normalize(stored));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("unset")]
    public void A_missing_notification_reach_stays_unset_so_first_run_still_asks(string? stored)
    {
        Assert.Equal(NotificationReaches.Unset, NotificationReaches.Normalize(stored));
        Assert.False(NotificationReaches.IsChosen(stored));
        // Unset must not notify: the player has not been asked yet.
        Assert.False(NotificationReaches.WantsSystemNotifications(stored));
        Assert.False(NotificationReaches.WantsTray(stored));
    }

    [Fact]
    public void An_unrecognised_notification_reach_reads_as_the_least_reach()
    {
        // Not unset — that would re-ask forever — but never a silent promotion to a resident process.
        Assert.Equal(NotificationReaches.InApp, NotificationReaches.Normalize("telepathy"));
        Assert.True(NotificationReaches.IsChosen("telepathy"));
        Assert.False(NotificationReaches.WantsTray("telepathy"));
    }

    [Fact]
    public void Only_the_background_reach_wants_a_tray_and_both_loud_reaches_notify()
    {
        Assert.True(NotificationReaches.WantsSystemNotifications(NotificationReaches.System));
        Assert.True(NotificationReaches.WantsSystemNotifications(NotificationReaches.Background));
        Assert.False(NotificationReaches.WantsSystemNotifications(NotificationReaches.InApp));

        Assert.True(NotificationReaches.WantsTray(NotificationReaches.Background));
        Assert.False(NotificationReaches.WantsTray(NotificationReaches.System));
    }

    [Theory]
    [InlineData(0, UpdateCheckInterval.Never)]
    [InlineData(-5, UpdateCheckInterval.Never)]
    [InlineData(1, UpdateCheckInterval.MinimumMinutes)]
    [InlineData(14, UpdateCheckInterval.MinimumMinutes)]
    [InlineData(60, 60)]
    [InlineData(99999, UpdateCheckInterval.MaximumMinutes)]
    public void The_check_interval_is_clamped(int stored, int expected) =>
        // The floor is not cosmetic: the beta channel asks the unauthenticated GitHub API first, at
        // 60 requests/hour, and a launcher left open at a one-minute interval would spend it.
        Assert.Equal(expected, UpdateCheckInterval.Normalize(stored));
}

/// <summary>Round-tripping settings.json, including the migration that matters: a schema-1 file
/// written before any of this existed.</summary>
public sealed class LauncherSettingsStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "xglauncher-tests", Path.GetRandomFileName());

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort */ }
    }

    private LauncherSettingsStore Store() => new(_dir);

    [Fact]
    public void A_schema_1_file_upgrades_to_the_defaults_and_still_gets_asked()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "settings.json"),
            """{"schema":1,"channel":"beta","installRoot":null}""");

        var loaded = Store().Load();

        // What the old file said is kept…
        Assert.True(loaded.IsBeta);
        // …and everything it could not have said lands on the shipped default.
        Assert.Equal(GameUpdateModes.Download, loaded.GameUpdates);
        Assert.Equal(LauncherUpdateModes.Automatic, loaded.LauncherUpdates);
        Assert.Equal(UpdateCheckInterval.DefaultMinutes, loaded.UpdateCheckMinutes);
        // The one thing with no defensible default: an existing player is asked once, same as a new one.
        Assert.False(loaded.HasChosenNotificationReach);
        Assert.Equal(LauncherSettings.CurrentSchema, loaded.Schema);
    }

    [Fact]
    public void Settings_round_trip()
    {
        var written = new LauncherSettings
        {
            Channel = ReleaseChannels.Beta,
            GameUpdates = GameUpdateModes.Install,
            LauncherUpdates = LauncherUpdateModes.Off,
            NotificationReach = NotificationReaches.Background,
            UpdateCheckMinutes = 30,
            StartWithSystem = true,
        };
        Store().Save(written);

        var loaded = Store().Load();

        Assert.Equal(written.GameUpdates, loaded.GameUpdates);
        Assert.Equal(written.LauncherUpdates, loaded.LauncherUpdates);
        Assert.Equal(written.NotificationReach, loaded.NotificationReach);
        Assert.Equal(30, loaded.UpdateCheckMinutes);
        Assert.True(loaded.StartWithSystem);
        Assert.True(loaded.WantsTray);
    }

    [Fact]
    public void A_hand_edited_file_cannot_escalate_what_the_launcher_does()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "settings.json"), """
            {"schema":9,"gameUpdates":"YOLO","launcherUpdates":"???",
             "notificationReach":"satellite","updateCheckMinutes":1,"startWithSystem":true}
            """);

        var loaded = Store().Load();

        Assert.Equal(GameUpdateModes.Download, loaded.GameUpdates);
        Assert.Equal(LauncherUpdateModes.Automatic, loaded.LauncherUpdates);
        Assert.Equal(NotificationReaches.InApp, loaded.NotificationReach);
        Assert.Equal(UpdateCheckInterval.MinimumMinutes, loaded.UpdateCheckMinutes);
        // startWithSystem was true, but the reach it belongs to is not the tray one — so an autostart
        // entry cannot be justified by a setting that no longer has a reason to exist.
        Assert.False(loaded.StartWithSystem);
    }

    [Fact]
    public void A_corrupt_file_falls_back_to_defaults_rather_than_failing_to_start()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "settings.json"), "{ this is not json");

        var loaded = Store().Load();

        Assert.Equal(ReleaseChannels.Stable, loaded.Channel);
        Assert.Equal(GameUpdateModes.Download, loaded.GameUpdates);
        Assert.False(loaded.HasChosenNotificationReach);
    }
}

/// <summary>The verdict the window, the background check and the tray all read.</summary>
public class UpdateAvailabilityTests
{
    private static ReleaseManifest Manifest(string version, bool prerelease = false,
        string platform = PlatformKey.Windows, bool withPackage = true) => new()
    {
        Version = version,
        Tag = $"v{version}",
        Prerelease = prerelease,
        Platforms =
        {
            [platform] = withPackage
                ? new ManifestPlatform(
                    new ManifestFile($"g-{version}.zip", "root", 1, new string('a', 64), "https://t.invalid/g"),
                    null)
                : new ManifestPlatform(null, null),
        },
    };

    private static InstalledState Installed(string version) =>
        new(version, InstalledState.LayoutFat, PlatformKey.Windows, "root", null);

    [Fact]
    public void No_manifest_is_a_feed_problem_and_never_an_update()
    {
        var v = UpdateAvailability.Evaluate(null, "offline", Installed("1.0.0"),
            PlatformKey.Windows, ReleaseChannels.Stable);

        Assert.Equal(UpdateStatus.FeedUnavailable, v.Status);
        Assert.False(v.CanInstall);
        Assert.Equal("offline", v.Detail);
    }

    [Fact]
    public void A_prerelease_is_not_offered_on_stable_but_is_on_beta()
    {
        var m = Manifest("1.1.0", prerelease: true);

        var stable = UpdateAvailability.Evaluate(m, "", Installed("1.0.0"),
            PlatformKey.Windows, ReleaseChannels.Stable);
        Assert.Equal(UpdateStatus.PrereleaseNeedsBetaChannel, stable.Status);
        Assert.False(stable.CanInstall);
        // Still carries the manifest: the UI names the tag it is declining to install.
        Assert.Equal("1.1.0", stable.Version);

        var beta = UpdateAvailability.Evaluate(m, "", Installed("1.0.0"),
            PlatformKey.Windows, ReleaseChannels.Beta);
        Assert.Equal(UpdateStatus.UpdateAvailable, beta.Status);
        Assert.True(beta.CanInstall);
    }

    [Fact]
    public void A_release_with_nothing_for_this_platform_is_not_an_update()
    {
        var v = UpdateAvailability.Evaluate(Manifest("1.1.0", withPackage: false), "",
            Installed("1.0.0"), PlatformKey.Windows, ReleaseChannels.Stable);

        Assert.Equal(UpdateStatus.NoPackageForPlatform, v.Status);
        Assert.False(v.CanInstall);
    }

    [Fact]
    public void Nothing_installed_is_installable_but_is_not_an_update()
    {
        var v = UpdateAvailability.Evaluate(Manifest("1.0.0"), "", null,
            PlatformKey.Windows, ReleaseChannels.Stable);

        // The distinction drives the default mode: with nothing installed there is no session to
        // protect, so the download and the swap are one event instead of a download and a prompt.
        Assert.Equal(UpdateStatus.NotInstalled, v.Status);
        Assert.True(v.CanInstall);
    }

    [Fact]
    public void Same_version_is_up_to_date_and_a_different_one_is_an_update()
    {
        Assert.Equal(UpdateStatus.UpToDate, UpdateAvailability.Evaluate(
            Manifest("1.0.0"), "", Installed("1.0.0"),
            PlatformKey.Windows, ReleaseChannels.Stable).Status);

        Assert.Equal(UpdateStatus.UpdateAvailable, UpdateAvailability.Evaluate(
            Manifest("1.1.0"), "", Installed("1.0.0"),
            PlatformKey.Windows, ReleaseChannels.Stable).Status);
    }

    [Fact]
    public void A_version_older_than_the_installed_one_still_counts_as_a_change()
    {
        // Deliberate: the feed is the source of truth for what should be installed, and a release
        // pulled and republished at a lower version is a rollback the launcher must be able to
        // follow. Ordering versions here would silently refuse it.
        var v = UpdateAvailability.Evaluate(Manifest("0.9.0"), "", Installed("1.0.0"),
            PlatformKey.Windows, ReleaseChannels.Stable);

        Assert.Equal(UpdateStatus.UpdateAvailable, v.Status);
    }
}

public class AnnouncedVersionsTests
{
    [Fact]
    public void A_version_is_announced_once_per_run()
    {
        var announced = new AnnouncedVersions();

        Assert.True(announced.ShouldAnnounce("1.1.0"));
        Assert.False(announced.ShouldAnnounce("1.1.0"));
        Assert.True(announced.ShouldAnnounce("1.2.0"));
    }

    [Fact]
    public void Nothing_is_announced_for_a_missing_version()
    {
        var announced = new AnnouncedVersions();

        Assert.False(announced.ShouldAnnounce(null));
        Assert.False(announced.ShouldAnnounce(""));
    }
}

/// <summary>The polling loop, driven by a fake sleep so a four-hour interval takes no time.</summary>
public class UpdateSchedulerTests
{
    /// <summary>Stands in for Task.Delay: records what it was asked to wait and returns at once.</summary>
    private sealed class FakeSleep
    {
        public List<TimeSpan> Waits { get; } = new();
        private readonly TaskCompletionSource _drained = new();
        private readonly int _stopAfter;

        public FakeSleep(int stopAfter) => _stopAfter = stopAfter;

        public Task Drained => _drained.Task;

        public async Task SleepAsync(TimeSpan delay, CancellationToken ct)
        {
            lock (Waits)
            {
                Waits.Add(delay);
                if (Waits.Count >= _stopAfter)
                    _drained.TrySetResult();
            }
            // Yield rather than block, so the loop stays a loop and the test does not spin a thread.
            await Task.Yield();
            ct.ThrowIfCancellationRequested();
        }
    }

    [Fact]
    public async Task It_checks_on_the_configured_interval()
    {
        var checks = 0;
        var sleep = new FakeSleep(stopAfter: 3);
        using var scheduler = new UpdateScheduler(
            _ => { Interlocked.Increment(ref checks); return Task.CompletedTask; },
            () => 60,
            sleep.SleepAsync);

        scheduler.Start();
        await sleep.Drained;
        await scheduler.StopAsync();

        Assert.True(checks >= 2, $"expected repeated checks, got {checks}");
        Assert.All(sleep.Waits, w => Assert.Equal(TimeSpan.FromMinutes(60), w));
    }

    [Fact]
    public async Task A_failing_check_does_not_stop_the_ones_after_it()
    {
        var checks = 0;
        var sleep = new FakeSleep(stopAfter: 4);
        using var scheduler = new UpdateScheduler(
            _ =>
            {
                Interlocked.Increment(ref checks);
                // The realistic case: the box woke from sleep with no network yet.
                throw new HttpRequestException("no route to host");
            },
            () => 30,
            sleep.SleepAsync);

        scheduler.Start();
        await sleep.Drained;
        await scheduler.StopAsync();

        Assert.True(checks >= 2, $"a throwing check killed the loop after {checks}");
    }

    [Fact]
    public async Task An_interval_of_zero_parks_the_loop_without_checking()
    {
        var checks = 0;
        var sleep = new FakeSleep(stopAfter: 3);
        using var scheduler = new UpdateScheduler(
            _ => { Interlocked.Increment(ref checks); return Task.CompletedTask; },
            () => UpdateCheckInterval.Never,
            sleep.SleepAsync);

        scheduler.Start();
        await sleep.Drained;
        await scheduler.StopAsync();

        Assert.Equal(0, checks);
        // Parked, not dead: it keeps re-reading the interval so turning checks back on resumes them.
        Assert.All(sleep.Waits, w => Assert.Equal(UpdateScheduler.ParkedPoll, w));
    }

    [Fact]
    public async Task The_interval_is_re_read_each_tick_so_a_settings_change_lands()
    {
        var minutes = 60;
        var sleep = new FakeSleep(stopAfter: 4);
        using var scheduler = new UpdateScheduler(
            _ =>
            {
                minutes = 15; // as if the player had just changed it in Settings
                return Task.CompletedTask;
            },
            () => minutes,
            sleep.SleepAsync);

        scheduler.Start();
        await sleep.Drained;
        await scheduler.StopAsync();

        Assert.Equal(TimeSpan.FromMinutes(60), sleep.Waits[0]);
        Assert.Contains(sleep.Waits, w => w == TimeSpan.FromMinutes(15));
    }

    [Fact]
    public async Task Start_is_idempotent()
    {
        var sleep = new FakeSleep(stopAfter: 2);
        using var scheduler = new UpdateScheduler(_ => Task.CompletedTask, () => 60, sleep.SleepAsync);

        scheduler.Start();
        scheduler.Start(); // a second reach for the same loop must not produce two of them

        await sleep.Drained;
        await scheduler.StopAsync();
    }
}
