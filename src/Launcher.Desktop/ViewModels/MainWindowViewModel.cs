using System.Diagnostics.CodeAnalysis;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Launcher.Core;

namespace Launcher.Desktop.ViewModels;

/// <summary>The launcher's one screen. State rules (ADR-0015 §6): Play is enabled whenever a
/// version is installed and nothing is mid-install — feed failures only change the status line,
/// never the Play button.</summary>
public partial class MainWindowViewModel : ObservableObject
{
    private readonly HttpClient _http = LauncherHttp.Create();
    private readonly SelfUpdateService _selfUpdate = new();
    private readonly string _platformKey = PlatformKey.Current;
    private readonly LauncherSettingsStore _settingsStore = new();

    // All four follow the settings, so they are rebuilt by Bind() and not fixed at construction.
    private LauncherSettings _settings;
    private CompositeFeed _feed;
    private InstallService _installs;
    private GameLauncher _game;

    private ReleaseManifest? _latest;
    private CancellationTokenSource? _installCts;

    [ObservableProperty] private string _statusText = "Starting…";
    [ObservableProperty] private string _installedText = "not installed";
    [ObservableProperty] private string _latestText = "checking…";
    [ObservableProperty] private string _channelText = "";
    [ObservableProperty] private string _notesTitle = "Release notes";
    [ObservableProperty] private string _notesText = "";
    [ObservableProperty] private double _progress;
    [ObservableProperty] private bool _progressVisible;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(PlayCommand))]
    [NotifyCanExecuteChangedFor(nameof(UpdateCommand))]
    [NotifyCanExecuteChangedFor(nameof(RefreshCommand))]
    [NotifyCanExecuteChangedFor(nameof(CancelCommand))]
    [NotifyCanExecuteChangedFor(nameof(OpenSettingsCommand))]
    private bool _busy;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(PlayCommand))]
    private InstalledState? _installed;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(UpdateCommand))]
    private bool _updateAvailable;

    /// <summary>The settings sheet, shown over this screen.</summary>
    public SettingsViewModel Settings { get; }

    public MainWindowViewModel()
    {
        _settings = _settingsStore.Load();
        Bind(_settings);

        Settings = new SettingsViewModel(_settingsStore, _settings);
        Settings.Applied += OnSettingsApplied;

        ShowInstalled(_installs.LoadCurrent());
        _ = InitializeAsync();
    }

    /// <summary>(Re)build everything the settings decide: which root the installs live under, and
    /// which feeds the channel asks in which order.</summary>
    [MemberNotNull(nameof(_installs), nameof(_game), nameof(_feed))]
    private void Bind(LauncherSettings settings)
    {
        _installs = new InstallService(new LauncherPaths(settings.InstallRoot), new DownloadService(_http));
        _game = new GameLauncher(_installs);
        _feed = ChannelFeeds.FeedFor(_http, settings.Channel);
        ChannelText = settings.IsBeta ? "beta — pre-releases included" : "stable";
    }

    private void OnSettingsApplied(LauncherSettings settings)
    {
        _settings = settings;
        Bind(settings);
        // The root may have moved, so what counts as installed has to be re-read, not assumed.
        ShowInstalled(_installs.LoadCurrent());
        _latest = null;
        UpdateAvailable = false;
        _ = RefreshAsync();
    }

    private void ShowInstalled(InstalledState? state)
    {
        Installed = state;
        InstalledText = state is null ? "not installed" : $"{state.Version} ({state.Layout})";
    }

    private async Task InitializeAsync()
    {
        _ = RunSelfUpdateAsync(); // fire-and-forget; inert for unpackaged dev builds
        await RefreshAsync();
    }

    private async Task RunSelfUpdateAsync()
    {
        var msg = await _selfUpdate.CheckAndApplyAsync(CancellationToken.None);
        StatusText = $"{StatusText}  ·  {msg}";
    }

    [RelayCommand(CanExecute = nameof(CanOpenSettings))]
    private void OpenSettings() => Settings.Open(Installed?.Version);

    /// <summary>Closed mid-install: changing the install root under a running download would strand
    /// the half-written staging directory at the old root.</summary>
    private bool CanOpenSettings() => !Busy;

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

            if (manifest is null)
            {
                LatestText = "unknown (offline?)";
                StatusText = Installed is null
                    ? $"Can't reach the release feed ({detail})."
                    : $"Can't reach the release feed — you can still play {Installed.Version}.";
                UpdateAvailable = false;
                return;
            }

            LatestText = manifest.Version + (manifest.Prerelease ? " (pre-release)" : "");
            NotesTitle = $"Release notes — {manifest.Tag}";
            NotesText = string.IsNullOrWhiteSpace(manifest.NotesBody)
                ? $"Notes: {manifest.NotesUrl}"
                : manifest.NotesBody!;

            // The stable channel can still be SHOWN a prerelease: when no full release exists yet, the
            // API fallback is all there is. Naming it and pointing at the setting beats pretending the
            // repo is empty (and beats installing it behind the player's back).
            if (manifest.Prerelease && !_settings.IsBeta)
            {
                UpdateAvailable = false;
                StatusText = $"The newest build, {manifest.Tag}, is a pre-release. "
                    + "Switch to the beta channel in Settings to install it.";
                return;
            }

            var plat = manifest.PlatformFor(_platformKey);
            if (plat is null || (plat.Fat is null && plat.Core is null))
            {
                UpdateAvailable = false;
                StatusText = $"{manifest.Tag} has no downloadable {_platformKey} package.";
                return;
            }

            UpdateAvailable = Installed is null || Installed.Version != manifest.Version;
            StatusText = UpdateAvailable
                ? Installed is null
                    ? $"Ready to install {manifest.Version}."
                    : $"Update available: {Installed.Version} → {manifest.Version}."
                : $"Up to date ({manifest.Version}).";
        }
        catch (Exception ex)
        {
            LatestText = "check failed";
            StatusText = $"Update check failed: {ex.Message}";
            UpdateAvailable = false;
        }
    }

    private bool CanRefresh() => !Busy;

    [RelayCommand(CanExecute = nameof(CanUpdate))]
    private async Task UpdateAsync()
    {
        if (_latest is null)
            return;
        Busy = true;
        ProgressVisible = true;
        _installCts = new CancellationTokenSource();
        try
        {
            var progress = new Progress<(string Phase, double Fraction)>(p =>
            {
                Progress = p.Fraction * 100;
                StatusText = p.Fraction > 0
                    ? $"{p.Phase} {_latest.Version}… {p.Fraction:P0}"
                    : $"{p.Phase} {_latest.Version}…";
            });
            // Prefer the split payload when the release carries it (ADR-0015 §4); fat otherwise.
            var state = await _installs.InstallAsync(_latest, _platformKey,
                preferCore: true, progress, _installCts.Token);
            ShowInstalled(state);
            UpdateAvailable = false;
            StatusText = $"Installed {state.Version} — ready to play.";
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

    private bool CanUpdate() => UpdateAvailable && !Busy;

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
            _game.Launch(Installed);
            StatusText = $"Launched Vortex Arena {Installed.Version}. Have fun!";
        }
        catch (Exception ex)
        {
            StatusText = $"Launch failed: {ex.Message}";
        }
    }

    private bool CanPlay() => Installed is not null && !Busy;
}
