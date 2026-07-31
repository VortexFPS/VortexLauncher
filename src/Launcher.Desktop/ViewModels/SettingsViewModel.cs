using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Launcher.Core;

namespace Launcher.Desktop.ViewModels;

/// <summary>The settings sheet: release channel and install location. Shown over the main window
/// rather than in its own window so the two states the player cares about — what is installed and
/// where it is going — stay on screen together.
///
/// Edits are held here and only written on Save, because saving a new install root is what triggers
/// the relocation, and that is not something to start on a keystroke.</summary>
public partial class SettingsViewModel : ObservableObject
{
    private readonly LauncherSettingsStore _store;
    private LauncherSettings _saved;
    private string? _installedVersion;
    private bool _moveConfirmed;

    /// <summary>Raised after settings are persisted (and any relocation finished), so the main window
    /// can rebind to the new root/channel.</summary>
    public event Action<LauncherSettings>? Applied;

    /// <summary>Supplied by the view: only a control can reach the TopLevel that owns a folder dialog,
    /// and this view model has no business holding one.</summary>
    public Func<string?, Task<string?>>? PickFolderAsync { get; set; }

    [ObservableProperty] private bool _isOpen;
    [ObservableProperty] private string _statusText = "";
    [ObservableProperty] private bool _confirmVisible;
    [ObservableProperty] private string _confirmText = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StableChannel))]
    private bool _betaChannel;

    // ── game updates ───────────────────────────────────────────────────────────────────────────
    [ObservableProperty] private bool _gameNotifyOnly;
    [ObservableProperty] private bool _gameDownloadThenAsk = true;
    [ObservableProperty] private bool _gameFullyAutomatic;

    // ── launcher updates ───────────────────────────────────────────────────────────────────────
    [ObservableProperty] private bool _launcherAutomatic = true;
    [ObservableProperty] private bool _launcherNotifyOnly;

    /// <summary>The off switch. Its warning is in the view rather than here, but the reason it has
    /// one: <c>latest.json</c> is a cross-repo contract, so a launcher left far enough behind can
    /// lose the ability to read the game's release feed at all.</summary>
    [ObservableProperty] private bool _launcherUpdatesOff;

    // ── notifications ──────────────────────────────────────────────────────────────────────────
    [ObservableProperty] private bool _inAppReach = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StartWithSystemEnabled))]
    private bool _systemReach;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StartWithSystemEnabled))]
    private bool _backgroundReach;

    [ObservableProperty] private bool _startWithSystem;

    /// <summary>Minutes between background checks, as text because it is bound to a TextBox and an
    /// empty box during editing must not read as zero (which means "startup only").</summary>
    [ObservableProperty] private string _checkIntervalText = "";

    public bool StartWithSystemEnabled => BackgroundReach;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(InstallRootHint))]
    private string _installRootText = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    [NotifyCanExecuteChangedFor(nameof(CancelCommand))]
    [NotifyCanExecuteChangedFor(nameof(BrowseCommand))]
    [NotifyCanExecuteChangedFor(nameof(UseDefaultLocationCommand))]
    [NotifyCanExecuteChangedFor(nameof(ConfirmMoveCommand))]
    [NotifyCanExecuteChangedFor(nameof(KeepCurrentLocationCommand))]
    private bool _busy;

    public SettingsViewModel(LauncherSettingsStore store, LauncherSettings current)
    {
        _store = store;
        _saved = current;
        LoadFromSaved();
    }

    /// <summary>Any change of destination — typed, browsed or reset — retracts the confirmation.
    /// Consent was given for one specific move, not for whatever is in the box when Save is pressed.</summary>
    partial void OnInstallRootTextChanged(string value)
    {
        ConfirmVisible = false;
        _moveConfirmed = false;
    }

    /// <summary>The other half of the channel radio pair.</summary>
    public bool StableChannel
    {
        get => !BetaChannel;
        set
        {
            if (value)
                BetaChannel = false;
        }
    }

    public string DefaultRootText => LauncherSettingsStore.DefaultRoot;

    public string InstallRootHint
    {
        get
        {
            try
            {
                return $"Game builds go in {new LauncherPaths(NormalizeRoot(InstallRootText)).VersionsDir}";
            }
            catch (ArgumentException)
            {
                return "That doesn't look like a valid folder path.";
            }
        }
    }

    /// <summary><paramref name="installedVersion"/> is what the confirmation text names when a root
    /// change has to move an existing install.</summary>
    public void Open(string? installedVersion)
    {
        _installedVersion = installedVersion;
        LoadFromSaved();
        IsOpen = true;
    }

    [RelayCommand(CanExecute = nameof(CanEdit))]
    private void Cancel()
    {
        LoadFromSaved();
        IsOpen = false;
    }

    [RelayCommand(CanExecute = nameof(CanEdit))]
    private void UseDefaultLocation() => InstallRootText = LauncherSettingsStore.DefaultRoot;

    [RelayCommand(CanExecute = nameof(CanEdit))]
    private async Task BrowseAsync()
    {
        if (PickFolderAsync is null)
            return;
        if (await PickFolderAsync(InstallRootText) is { Length: > 0 } picked)
            InstallRootText = picked;
    }

    /// <summary>Second press, after the player has read what the move will do.</summary>
    [RelayCommand(CanExecute = nameof(CanEdit))]
    private Task ConfirmMoveAsync()
    {
        _moveConfirmed = true;
        return SaveAsync();
    }

    [RelayCommand(CanExecute = nameof(CanEdit))]
    private void KeepCurrentLocation()
    {
        InstallRootText = _saved.InstallRoot ?? LauncherSettingsStore.DefaultRoot;
        StatusText = "Install location left unchanged.";
    }

    [RelayCommand(CanExecute = nameof(CanEdit))]
    private async Task SaveAsync()
    {
        string? chosen;
        try
        {
            chosen = NormalizeRoot(InstallRootText);
        }
        catch (ArgumentException)
        {
            StatusText = "That doesn't look like a valid folder path.";
            return;
        }

        if (chosen is not null && !Path.IsPathFullyQualified(chosen))
        {
            StatusText = "Enter a full path, for example D:\\Games\\VortexArena.";
            return;
        }

        var from = new LauncherPaths(_saved.InstallRoot);
        var to = new LauncherPaths(chosen);
        IReadOnlyList<string> warnings = [];
        var relocated = false;

        if (!InstallRelocation.SameDirectory(from.Root, to.Root))
        {
            if (InstallRelocation.IsInside(to.Root, from.Root))
            {
                StatusText = $"Pick a folder outside the current one ({from.Root}).";
                return;
            }

            Busy = true;
            StatusText = "Checking the current install…";
            try
            {
                var bytes = await Task.Run(() => InstallRelocation.SizeOf(from));
                if (bytes > 0 && !_moveConfirmed)
                {
                    var what = _installedVersion is null
                        ? "A game install"
                        : $"Vortex Arena {_installedVersion}";
                    ConfirmText = $"{what} is installed in {from.Root}. Saving moves "
                        + $"{FormatBytes(bytes)} to {to.Root} — the files are moved, not downloaded "
                        + "again. Close the game first: the move can take a while, and it fails while "
                        + "the game is running.";
                    ConfirmVisible = true;
                    StatusText = "";
                    return;
                }

                if (bytes > 0)
                {
                    var progress = new Progress<(string Phase, double Fraction)>(p =>
                        StatusText = $"{p.Phase}… {p.Fraction:P0}");
                    warnings = await Task.Run(() => InstallRelocation.Move(from, to, progress));
                    relocated = true;
                }
            }
            catch (Exception ex)
            {
                // Move() restores the old root before it throws, so the install is still where it was.
                StatusText = $"Couldn't move the install: {ex.Message}. "
                    + $"Nothing changed — the game is still installed in {from.Root}.";
                return;
            }
            finally
            {
                Busy = false;
            }

            // Only past the confirmation return above, or the sheet would hide the question it just asked.
            ConfirmVisible = false;
            _moveConfirmed = false;
        }

        var reach = BackgroundReach ? NotificationReaches.Background
            : SystemReach ? NotificationReaches.System
            : NotificationReaches.InApp;

        // Applied before the save so a failure can be reflected in what gets stored, the same way
        // the first-run sheet does it: the file must never claim a login entry that was not written.
        var wantsAutostart = reach == NotificationReaches.Background && StartWithSystem;
        var autostartProblem = wantsAutostart != (_saved.StartWithSystem && _saved.WantsTray)
            ? Autostart.Set(wantsAutostart)
            : null;

        var settings = _saved with
        {
            Channel = BetaChannel ? ReleaseChannels.Beta : ReleaseChannels.Stable,
            InstallRoot = chosen,
            GameUpdates = GameNotifyOnly ? GameUpdateModes.Notify
                : GameFullyAutomatic ? GameUpdateModes.Install
                : GameUpdateModes.Download,
            LauncherUpdates = LauncherUpdatesOff ? LauncherUpdateModes.Off
                : LauncherNotifyOnly ? LauncherUpdateModes.Notify
                : LauncherUpdateModes.Automatic,
            NotificationReach = reach,
            UpdateCheckMinutes = ParsedInterval(),
            StartWithSystem = wantsAutostart && autostartProblem is null,
        };

        try
        {
            _store.Save(settings);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            if (relocated)
            {
                // The files moved but the launcher would keep reading the old root on next start —
                // exactly the orphaned install this flow exists to avoid. Put them back.
                try { InstallRelocation.Move(to, from); }
                catch (Exception moveBack) when (moveBack is IOException or UnauthorizedAccessException)
                {
                    StatusText = $"Settings could not be saved ({ex.Message}) and the install is now in "
                        + $"{to.Root}. Set the install folder to that path before playing.";
                    return;
                }
            }
            StatusText = $"Couldn't save settings: {ex.Message}";
            return;
        }

        _saved = settings;
        Applied?.Invoke(settings);

        var notes = warnings.ToList();
        if (autostartProblem is not null)
            notes.Add(autostartProblem);

        if (notes.Count > 0)
        {
            // Saved and applied, but something needs saying — keep the sheet up so it gets read.
            StatusText = "Saved. " + string.Join(" ", notes);
            return;
        }

        StatusText = "";
        IsOpen = false;
    }

    private bool CanEdit() => !Busy;

    private void LoadFromSaved()
    {
        BetaChannel = _saved.IsBeta;
        InstallRootText = _saved.InstallRoot ?? LauncherSettingsStore.DefaultRoot;

        var game = GameUpdateModes.Normalize(_saved.GameUpdates);
        GameNotifyOnly = game == GameUpdateModes.Notify;
        GameDownloadThenAsk = game == GameUpdateModes.Download;
        GameFullyAutomatic = game == GameUpdateModes.Install;

        var launcher = LauncherUpdateModes.Normalize(_saved.LauncherUpdates);
        LauncherAutomatic = launcher == LauncherUpdateModes.Automatic;
        LauncherNotifyOnly = launcher == LauncherUpdateModes.Notify;
        LauncherUpdatesOff = launcher == LauncherUpdateModes.Off;

        // Unset shows as in-app: the sheet is a set of radio buttons and one of them has to be on.
        // It stays unset on disk until Save, so a player who opens Settings and cancels still gets
        // the first-run question.
        var reach = NotificationReaches.Normalize(_saved.NotificationReach);
        InAppReach = reach is NotificationReaches.InApp or NotificationReaches.Unset;
        SystemReach = reach == NotificationReaches.System;
        BackgroundReach = reach == NotificationReaches.Background;
        StartWithSystem = _saved.StartWithSystem;

        CheckIntervalText = _saved.UpdateCheckMinutes == UpdateCheckInterval.Never
            ? "0"
            : _saved.UpdateCheckMinutes.ToString();

        StatusText = "";
        ConfirmVisible = false;
        _moveConfirmed = false;
    }

    /// <summary>Adopt settings changed somewhere else — the first-run sheet writes the same file,
    /// and this one must not overwrite that choice with the copy it loaded at construction.</summary>
    public void Reload(LauncherSettings settings)
    {
        _saved = settings;
        if (!IsOpen)
            LoadFromSaved();
    }

    /// <summary>Blank or unparseable keeps whatever was saved rather than silently becoming 0,
    /// which would mean "stop checking" — a typo must not turn background checks off.</summary>
    private int ParsedInterval() =>
        int.TryParse(CheckIntervalText.Trim(), out var minutes)
            ? UpdateCheckInterval.Normalize(minutes)
            : _saved.UpdateCheckMinutes;

    /// <summary>Blank, or the default root itself, stores as null so the setting keeps tracking the
    /// platform default instead of freezing today's expansion of it.</summary>
    private static string? NormalizeRoot(string text)
    {
        var trimmed = text.Trim().Trim('"');
        if (trimmed.Length == 0)
            return null;
        var full = Path.IsPathFullyQualified(trimmed) ? Path.GetFullPath(trimmed) : trimmed;
        return InstallRelocation.SameDirectory(full, LauncherSettingsStore.DefaultRoot) ? null : full;
    }

    private static string FormatBytes(long bytes) => bytes switch
    {
        >= 1L << 30 => $"{bytes / (double)(1L << 30):0.#} GB",
        >= 1L << 20 => $"{bytes / (double)(1L << 20):0} MB",
        _ => $"{Math.Max(1, bytes / 1024)} KB",
    };
}
