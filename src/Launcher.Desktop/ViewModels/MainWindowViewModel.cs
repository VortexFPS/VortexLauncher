using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Launcher.Core;
using Launcher.Desktop.Notifications;

namespace Launcher.Desktop.ViewModels;

/// <summary>The launcher's one screen. State rules (ADR-0015 §6): Play is enabled whenever a
/// version is installed and nothing is mid-install — feed failures only change the status line,
/// never the Play button. Nothing an update does may weaken that, which is the constraint the whole
/// auto-update flow below is written around: a build is downloaded beside the one that is running
/// and only becomes the one Play launches when the player says so.</summary>
public partial class MainWindowViewModel : ObservableObject
{
    private readonly HttpClient _http = LauncherHttp.Create();
    private readonly SelfUpdateService _selfUpdate = new();
    private readonly string _platformKey = PlatformKey.Current;
    private readonly LauncherSettingsStore _settingsStore = new();
    private readonly AnnouncedVersions _announced = new();
    private readonly UpdateScheduler _scheduler;

    // All of these follow the settings, so they are rebuilt by Bind() and not fixed at construction.
    private LauncherSettings _settings;
    private CompositeFeed _feed;
    private InstallService _installs;
    private GameLauncher _game;
    private IUpdateNotifier _notifier;

    private ReleaseManifest? _latest;
    private CancellationTokenSource? _installCts;

    /// <summary>Downloaded and sitting in versions/ with current.json still pointing elsewhere.
    /// The "prompt to apply" half of the default update mode.</summary>
    private InstalledState? _staged;

    /// <summary>The game we launched, so an update never swaps the build out from under a running
    /// session. Only covers games this launcher started — one started from a desktop shortcut is
    /// invisible here, which is why <see cref="ApplyStaged"/> is still a deliberate press.</summary>
    private Process? _gameProcess;

    [ObservableProperty] private string _statusText = "Starting…";
    [ObservableProperty] private string _installedText = "not installed";
    [ObservableProperty] private string _latestText = "checking…";
    [ObservableProperty] private string _channelText = "";
    [ObservableProperty] private string _notesTitle = "Release notes";
    [ObservableProperty] private string _notesText = "";
    [ObservableProperty] private double _progress;
    [ObservableProperty] private bool _progressVisible;

    /// <summary>The banner. Separate from <see cref="StatusText"/> because the status line is
    /// overwritten by every download tick and an update notice has to survive that.</summary>
    [ObservableProperty] private bool _bannerVisible;
    [ObservableProperty] private string _bannerText = "";

    [ObservableProperty] private string _selfUpdateText = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RestartForLauncherUpdateCommand))]
    private bool _launcherUpdateReady;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(DownloadLauncherUpdateCommand))]
    private bool _launcherUpdatePending;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(PlayCommand))]
    [NotifyCanExecuteChangedFor(nameof(UpdateCommand))]
    [NotifyCanExecuteChangedFor(nameof(RefreshCommand))]
    [NotifyCanExecuteChangedFor(nameof(PrimaryActionCommand))]
    [NotifyCanExecuteChangedFor(nameof(CancelCommand))]
    [NotifyCanExecuteChangedFor(nameof(OpenSettingsCommand))]
    [NotifyCanExecuteChangedFor(nameof(OpenSourceBuildCommand))]
    [NotifyCanExecuteChangedFor(nameof(ApplyStagedCommand))]
    [NotifyCanExecuteChangedFor(nameof(RestartForLauncherUpdateCommand))]
    [NotifyCanExecuteChangedFor(nameof(DownloadLauncherUpdateCommand))]
    private bool _busy;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(PlayCommand))]
    private InstalledState? _installed;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(UpdateCommand))]
    [NotifyPropertyChangedFor(nameof(PrimaryActionText))]
    private bool _updateAvailable;

    /// <summary>Set once a build is downloaded and waiting for the player to switch to it.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ApplyStagedCommand))]
    [NotifyPropertyChangedFor(nameof(StagedText))]
    [NotifyPropertyChangedFor(nameof(PrimaryActionText))]
    private bool _stagedReady;

    public string StagedText => _staged is null ? "" : $"Version {_staged.Version} is downloaded and ready.";

    /// <summary>The label on the one button that is not Play. Checking and updating used to be two
    /// buttons plus a third for the swap, which put up to three verbs beside Play and made the
    /// player pick between them — when only ever one of the three is the right thing to press.
    /// The state already knew which: <see cref="StagedReady"/> means downloaded and waiting,
    /// <see cref="UpdateAvailable"/> means found but not fetched, neither means nothing has been
    /// looked for yet.</summary>
    public string PrimaryActionText => LabelFor(StagedReady, UpdateAvailable);

    /// <summary>The label as a function of the state, callable without a view model. This class
    /// cannot be constructed in a test — its constructor reads the real per-user settings file and
    /// starts the background update loop — and the one thing here worth pinning is which of the two
    /// words the player sees in each state.</summary>
    public static string LabelFor(bool stagedReady, bool updateAvailable) =>
        stagedReady || updateAvailable ? "Update Now" : "Check for Updates";

    /// <summary>The settings sheet, shown over this screen.</summary>
    public SettingsViewModel Settings { get; }

    /// <summary>The build-from-source sheet. Shown over this screen for the same reason Settings is:
    /// what is installed stays visible behind the thing that is about to produce another one.</summary>
    public SourceBuildViewModel SourceBuild { get; }

    /// <summary>The first-run notification question, shown over everything else.</summary>
    public FirstRunViewModel FirstRun { get; }

    /// <summary>Raised when something outside the window wants it on screen — the tray icon, or a
    /// second launch that found this instance already running.</summary>
    public event Action? ActivateRequested;

    /// <summary>Raised when the player quits from the tray, so the app can actually exit rather
    /// than hide the window again.</summary>
    public event Action? ExitRequested;

    public MainWindowViewModel()
    {
        _settings = _settingsStore.Load();

        // Before Bind, which rebinds it: the sheet has to exist by the time the first bind runs, and
        // building it here rather than inside Bind is what keeps a settings change from replacing the
        // view model out from under an open sheet.
        SourceBuild = new SourceBuildViewModel(new LauncherPaths(_settings.InstallRoot));

        Bind(_settings);

        // After Bind, because the Applied handler reads _installs and Bind is what assigns it. The
        // handler could not run before this point either way, but writing it above would be asking the
        // reader to know that.
        SourceBuild.IsGameRunning = () => GameIsRunning;
        SourceBuild.Applied += () => ShowInstalled(_installs.LoadCurrent());

        Settings = new SettingsViewModel(_settingsStore, _settings);
        Settings.Applied += OnSettingsApplied;

        FirstRun = new FirstRunViewModel(_settingsStore, _settings);
        FirstRun.Chosen += OnFirstRunChosen;

        // The interval is read through a lambda rather than captured, so a settings change reaches
        // the running loop without it having to be torn down and rebuilt.
        _scheduler = new UpdateScheduler(
            BackgroundCheckAsync,
            () => _settings.UpdateCheckMinutes);

        ShowInstalled(_installs.LoadCurrent());
        _ = InitializeAsync();
    }

    /// <summary>(Re)build everything the settings decide: which root the installs live under, which
    /// feeds the channel asks in which order, and how far a notice is allowed to travel.</summary>
    [MemberNotNull(nameof(_installs), nameof(_game), nameof(_feed), nameof(_notifier))]
    private void Bind(LauncherSettings settings)
    {
        var paths = new LauncherPaths(settings.InstallRoot);
        _installs = new InstallService(paths, new DownloadService(_http));
        _game = new GameLauncher(_installs);
        _feed = ChannelFeeds.FeedFor(_http, settings.Channel);
        _notifier = Notifiers.For(settings);
        ChannelText = settings.IsBeta ? "beta — pre-releases included" : "stable";

        // The checkouts, the build store and current.json all hang off the root, so a sheet still
        // pointed at the old one would build into one place and pin in another.
        SourceBuild.Bind(paths);
    }

    private void OnSettingsApplied(LauncherSettings settings)
    {
        _settings = settings;
        Bind(settings);
        // The root may have moved, so what counts as installed has to be re-read, not assumed. A
        // build staged under the old root is not staged under the new one either.
        ShowInstalled(_installs.LoadCurrent());
        ClearStaged();
        _latest = null;
        UpdateAvailable = false;
        _ = RefreshAsync();
    }

    private void OnFirstRunChosen(LauncherSettings settings)
    {
        _settings = settings;
        _notifier = Notifiers.For(settings);
        Settings.Reload(settings);
    }

    private void ShowInstalled(InstalledState? state)
    {
        Installed = state;
        InstalledText = state is null ? "not installed" : $"{state.Version} ({state.Layout})";
    }

    private async Task InitializeAsync()
    {
        // Ask before doing anything that would notify, so the first notification a player sees is
        // never one they had no chance to decline.
        if (!_settings.HasChosenNotificationReach)
            FirstRun.Open();

        _ = RunSelfUpdateAsync(); // inert for unpackaged dev builds
        await RefreshAsync();

        // Only after the first check, so the loop's first sleep is a full interval rather than
        // racing the startup check it would duplicate.
        _scheduler.Start();
    }

    /// <summary>The scheduler's callback. Hops to the UI thread because everything it touches is
    /// bound: the loop itself resumes on a thread pool thread after its delay.</summary>
    private Task BackgroundCheckAsync(CancellationToken ct) =>
        Dispatcher.UIThread.InvokeAsync(async () =>
        {
            if (Busy)
                return; // an install is running; it will refresh when it lands
            await RefreshAsync();
            await RunSelfUpdateAsync();
        });

    private async Task RunSelfUpdateAsync()
    {
        var result = await _selfUpdate.CheckAsync(_settings, CancellationToken.None);
        SelfUpdateText = result.Message;
        LauncherUpdateReady = result.State == SelfUpdateState.Ready;
        LauncherUpdatePending = result.State == SelfUpdateState.Available;

        // Restarting is the launcher vanishing and coming back. Doing that to someone mid-download
        // throws the download away; doing it silently makes the launcher look like it crashed.
        if (LauncherUpdateReady
            && LauncherUpdateModes.Normalize(_settings.LauncherUpdates) == LauncherUpdateModes.Automatic
            && CanRestartNow())
        {
            StatusText = $"Updating the launcher to {result.Version} — it will restart.";
            // A beat so the line above is actually readable before the process goes away.
            await Task.Delay(TimeSpan.FromSeconds(2));
            if (CanRestartNow())
                _selfUpdate.Apply();
        }
    }

    /// <summary>A restart is only safe when nothing is in flight and nothing is running: an install
    /// would lose its staging directory, and the game is a child process of this one.</summary>
    private bool CanRestartNow() => !Busy && !GameIsRunning && !FirstRun.IsOpen && !Settings.IsOpen;

    private bool GameIsRunning
    {
        get
        {
            try { return _gameProcess is { HasExited: false }; }
            catch (InvalidOperationException) { return false; } // never started
        }
    }

    [RelayCommand(CanExecute = nameof(CanOpenSettings))]
    private void OpenSettings() => Settings.Open(Installed?.Version);

    /// <summary>Closed mid-install: changing the install root under a running download would strand
    /// the half-written staging directory at the old root.</summary>
    private bool CanOpenSettings() => !Busy;

    /// <summary>Build the game from a git checkout. Same guard as Settings, and for a sharper version
    /// of the same reason: a build stages into versions/ and so does the download this would be
    /// running alongside.</summary>
    [RelayCommand(CanExecute = nameof(CanOpenSettings))]
    private void OpenSourceBuild() => SourceBuild.Open();

    [RelayCommand(CanExecute = nameof(CanRefresh))]
    private async Task RefreshAsync()
    {
        StatusText = "Checking for updates…";
        LatestText = "checking…";
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var (manifest, detail) = await _feed.FetchLatestAsync(cts.Token);
            _latest = manifest;

            var verdict = UpdateAvailability.Evaluate(
                manifest, detail, Installed, _platformKey, _settings.Channel);

            ShowVerdict(verdict);
            if (verdict.CanInstall)
                await ActOnUpdateAsync(verdict);
        }
        catch (Exception ex)
        {
            LatestText = "check failed";
            StatusText = $"Update check failed: {ex.Message}";
            UpdateAvailable = false;
        }
    }

    /// <summary>Turn a verdict into what the window says. Kept separate from acting on it so the
    /// four not-an-update outcomes each keep their own wording — see <see cref="UpdateStatus"/>.</summary>
    private void ShowVerdict(UpdateVerdict verdict)
    {
        UpdateAvailable = verdict.CanInstall;

        if (verdict.Manifest is { } manifest)
        {
            LatestText = manifest.Version + (manifest.Prerelease ? " (pre-release)" : "");
            NotesTitle = $"Release notes — {manifest.Tag}";
            NotesText = string.IsNullOrWhiteSpace(manifest.NotesBody)
                ? $"Notes: {manifest.NotesUrl}"
                : manifest.NotesBody!;
        }

        StatusText = verdict.Status switch
        {
            UpdateStatus.FeedUnavailable => Installed is null
                ? $"Can't reach the release feed ({verdict.Detail})."
                : $"Can't reach the release feed — you can still play {Installed.Version}.",

            // The stable channel can still be SHOWN a prerelease: when no full release exists yet,
            // the API fallback is all there is. Naming it and pointing at the setting beats
            // pretending the repo is empty (and beats installing it behind the player's back).
            UpdateStatus.PrereleaseNeedsBetaChannel =>
                $"The newest build, {verdict.Manifest!.Tag}, is a pre-release. "
                + "Switch to the beta channel in Settings to install it.",

            UpdateStatus.NoPackageForPlatform =>
                $"{verdict.Manifest!.Tag} has no downloadable {_platformKey} package.",

            UpdateStatus.NotInstalled => $"Ready to install {verdict.Version}.",
            UpdateStatus.UpdateAvailable => $"Update available: {Installed!.Version} → {verdict.Version}.",
            _ => $"Up to date ({verdict.Version}).",
        };

        if (verdict.Status == UpdateStatus.FeedUnavailable)
            LatestText = "unknown (offline?)";
    }

    /// <summary>What the game-update setting means in practice, once a verdict says there IS
    /// something to install.</summary>
    private async Task ActOnUpdateAsync(UpdateVerdict verdict)
    {
        Announce(verdict);

        // Already downloaded, waiting on the player. Nothing more to do until they press the button.
        if (StagedReady && _staged?.Version == verdict.Version)
            return;

        var mode = GameUpdateModes.Normalize(_settings.GameUpdates);
        if (mode == GameUpdateModes.Notify || Busy)
            return;

        // With nothing installed there is no session to protect and no build to disturb, so the
        // download and the swap are the same event — prompting here would just be a second click
        // between the player and a game they have none of.
        var applyImmediately = Installed is null
            || (mode == GameUpdateModes.Install && !GameIsRunning);

        await DownloadAsync(verdict.Manifest!, applyImmediately);
    }

    /// <summary>Banner plus, if the reach allows it, a native notification — once per version per
    /// run, so a launcher left open for a week does not repeat itself every four hours.</summary>
    private void Announce(UpdateVerdict verdict)
    {
        if (!_announced.ShouldAnnounce(verdict.Version))
            return;

        BannerText = Installed is null
            ? $"Vortex Arena {verdict.Version} is available to install."
            : $"Vortex Arena {verdict.Version} is available — you have {Installed.Version}.";
        BannerVisible = true;
        _notifier.Notify("Vortex Arena update", BannerText);
    }

    [RelayCommand]
    private void DismissBanner() => BannerVisible = false;

    /// <summary>Download and extract a release. <paramref name="apply"/> decides whether it also
    /// becomes the build Play launches, or waits in versions/ for <see cref="ApplyStaged"/>.</summary>
    private async Task DownloadAsync(ReleaseManifest manifest, bool apply)
    {
        Busy = true;
        ProgressVisible = true;
        _installCts = new CancellationTokenSource();
        try
        {
            var progress = new Progress<(string Phase, double Fraction)>(p =>
            {
                Progress = p.Fraction * 100;
                StatusText = p.Fraction > 0
                    ? $"{p.Phase} {manifest.Version}… {p.Fraction:P0}"
                    : $"{p.Phase} {manifest.Version}…";
            });

            // Prefer the split payload when the release carries it (ADR-0015 §4); fat otherwise.
            // Unconditionally, including for a player currently on a fat install: switching costs
            // the assets pack once, and staying fat costs ~1.5 GB on every release forever.
            var staged = await _installs.StageAsync(manifest, _platformKey,
                preferCore: true, progress, _installCts.Token);

            if (apply)
            {
                ShowInstalled(_installs.Apply(staged));
                ClearStaged();
                UpdateAvailable = false;
                BannerVisible = false;
                StatusText = $"Installed {staged.Version} — ready to play.";
            }
            else
            {
                _staged = staged;
                StagedReady = true;
                OnPropertyChanged(nameof(StagedText));
                StatusText = GameIsRunning
                    ? $"{staged.Version} is downloaded. It will be applied when you say so — "
                      + "close the game first."
                    : $"{staged.Version} is downloaded and ready to switch to.";
            }
        }
        catch (OperationCanceledException)
        {
            StatusText = "Update cancelled (partial download kept — it resumes next time).";
        }
        catch (Exception ex)
        {
            StatusText = $"Install failed: {ex.Message}";
        }
        finally
        {
            Busy = false;
            ProgressVisible = false;
            _installCts = null;
        }
    }

    private void ClearStaged()
    {
        _staged = null;
        StagedReady = false;
        OnPropertyChanged(nameof(StagedText));
    }

    /// <summary>The Update button. Under the default mode the download has usually already happened
    /// in the background, in which case this is the swap; otherwise it is the whole install.</summary>
    [RelayCommand(CanExecute = nameof(CanUpdate))]
    private async Task UpdateAsync()
    {
        if (StagedReady)
        {
            ApplyStaged();
            return;
        }
        if (_latest is not null)
            await DownloadAsync(_latest, apply: true);
    }

    private bool CanUpdate() => (UpdateAvailable || StagedReady) && !Busy;

    /// <summary>What the button labelled by <see cref="PrimaryActionText"/> does. A dispatcher over
    /// the three commands that used to have a button each, in the same precedence the label uses,
    /// so the two cannot disagree about which one is live.
    ///
    /// Deliberately always executable while not busy. The commands underneath keep their own
    /// narrower guards, but this one has to stay pressable in the "nothing found yet" state — that
    /// is the Check case, and a disabled button there would be the common state of the screen.</summary>
    [RelayCommand(CanExecute = nameof(CanPrimaryAction))]
    private async Task PrimaryActionAsync()
    {
        if (StagedReady)
        {
            ApplyStaged();
            return;
        }
        if (UpdateAvailable)
        {
            await UpdateAsync();
            return;
        }
        await RefreshAsync();
    }

    private bool CanPrimaryAction() => !Busy;

    /// <summary>Switch to the build that was downloaded in the background. Refuses while the game
    /// is running: the swap rewrites current.json and GCs old builds, and the running game is
    /// reading files out of one of them.</summary>
    [RelayCommand(CanExecute = nameof(CanApplyStaged))]
    private void ApplyStaged()
    {
        if (_staged is null)
            return;

        if (GameIsRunning)
        {
            StatusText = "Close Vortex Arena first — the update replaces the build it is running from.";
            return;
        }

        try
        {
            // Staged in an earlier session and since collected, or the root moved out from under it.
            if (!_installs.IsStaged(_staged))
            {
                var version = _staged.Version;
                ClearStaged();
                StatusText = $"The downloaded copy of {version} is gone — press Update to fetch it again.";
                return;
            }

            var applied = _installs.Apply(_staged);
            ShowInstalled(applied);
            ClearStaged();
            UpdateAvailable = false;
            BannerVisible = false;
            StatusText = $"Now on {applied.Version} — ready to play.";
        }
        catch (Exception ex)
        {
            StatusText = $"Couldn't switch to the new version: {ex.Message}";
        }
    }

    private bool CanApplyStaged() => StagedReady && !Busy;

    [RelayCommand(CanExecute = nameof(CanDownloadLauncherUpdate))]
    private async Task DownloadLauncherUpdateAsync()
    {
        SelfUpdateText = "downloading launcher update…";
        var result = await _selfUpdate.DownloadAsync(_settings, CancellationToken.None);
        SelfUpdateText = result.Message;
        LauncherUpdateReady = result.State == SelfUpdateState.Ready;
        LauncherUpdatePending = result.State == SelfUpdateState.Available;
    }

    private bool CanDownloadLauncherUpdate() => LauncherUpdatePending && !Busy;

    [RelayCommand(CanExecute = nameof(CanRestartForLauncherUpdate))]
    private void RestartForLauncherUpdate()
    {
        if (GameIsRunning)
        {
            StatusText = "Close Vortex Arena first — the launcher restart would take it with it.";
            return;
        }
        StatusText = "Restarting the launcher…";
        if (!_selfUpdate.Apply())
            StatusText = "Couldn't apply the launcher update. It will be retried on the next start.";
    }

    private bool CanRestartForLauncherUpdate() => LauncherUpdateReady && !Busy;

    private bool CanRefresh() => !Busy;

    [RelayCommand(CanExecute = nameof(CanCancel))]
    private void Cancel() => _installCts?.Cancel();

    private bool CanCancel() => Busy;

    [RelayCommand(CanExecute = nameof(CanPlay))]
    private void Play()
    {
        if (Installed is null)
            return;
        try
        {
            _gameProcess = _game.Launch(Installed);
            StatusText = $"Launched Vortex Arena {Installed.Version}. Have fun!";
        }
        catch (Exception ex)
        {
            StatusText = $"Launch failed: {ex.Message}";
        }
    }

    private bool CanPlay() => Installed is not null && !Busy;

    /// <summary>Tray: bring the window back.</summary>
    [RelayCommand]
    private void ActivateWindow() => ActivateRequested?.Invoke();

    /// <summary>Tray: check now, without waiting for the next tick.</summary>
    [RelayCommand(CanExecute = nameof(CanRefresh))]
    private Task CheckNowAsync() => RefreshAsync();

    /// <summary>Tray: actually quit, as opposed to closing the window.</summary>
    [RelayCommand]
    private void ExitApplication() => ExitRequested?.Invoke();

    /// <summary>Stop the background loop before the process goes away.
    ///
    /// Non-blocking on purpose. This is called from the app's Exit handler, which runs on the UI
    /// thread, and a background check marshals itself onto that same thread — waiting here for the
    /// loop to unwind would be waiting on work that cannot start until the wait ends.</summary>
    public void Shutdown() => _scheduler.Stop();
}
