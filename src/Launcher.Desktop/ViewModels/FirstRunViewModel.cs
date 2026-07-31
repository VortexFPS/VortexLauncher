using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Launcher.Core;

namespace Launcher.Desktop.ViewModels;

/// <summary>The one question the launcher asks on first start: how far may an update notice
/// travel?
///
/// It is asked rather than defaulted because the honest answer depends on something no default can
/// know — whether this player wants a process of ours resident on their machine. Picking
/// <see cref="NotificationReaches.Background"/> for everyone would be presumptuous; picking
/// <see cref="NotificationReaches.InApp"/> for everyone quietly means the feature does not work for
/// anyone who does not already have the launcher open.
///
/// Everything else about updates ships with a working default and lives in Settings. This sheet
/// says what those defaults are so the choice is made in context, but does not turn them into four
/// more questions on a screen the player wants to get past.</summary>
public partial class FirstRunViewModel : ObservableObject
{
    private readonly LauncherSettingsStore _store;
    private LauncherSettings _settings;

    /// <summary>Raised once the choice is saved, so the main window can start using it.</summary>
    public event Action<LauncherSettings>? Chosen;

    [ObservableProperty] private bool _isOpen;
    [ObservableProperty] private string _statusText = "";

    [ObservableProperty] private bool _inAppReach = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StartWithSystemEnabled))]
    private bool _systemReach;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StartWithSystemEnabled))]
    private bool _backgroundReach;

    /// <summary>Ticked by default under the background reach, because a launcher that only checks
    /// while it happens to be running is the in-app reach with extra steps — but it is a checkbox
    /// and not an implication, because "run in the tray when I open it" and "start at login" are
    /// different amounts of consent.</summary>
    [ObservableProperty] private bool _startWithSystem = true;

    public bool StartWithSystemEnabled => BackgroundReach;

    public FirstRunViewModel(LauncherSettingsStore store, LauncherSettings settings)
    {
        _store = store;
        _settings = settings;
    }

    public void Open()
    {
        // Pre-select whatever is stored, so re-opening this from Settings is an edit and not a reset.
        var reach = NotificationReaches.Normalize(_settings.NotificationReach);
        InAppReach = reach is NotificationReaches.InApp or NotificationReaches.Unset;
        SystemReach = reach == NotificationReaches.System;
        BackgroundReach = reach == NotificationReaches.Background;
        StartWithSystem = _settings.StartWithSystem || reach != NotificationReaches.Background;
        StatusText = "";
        IsOpen = true;
    }

    [RelayCommand]
    private void Confirm()
    {
        var reach = BackgroundReach ? NotificationReaches.Background
            : SystemReach ? NotificationReaches.System
            : NotificationReaches.InApp;

        var wantsAutostart = reach == NotificationReaches.Background && StartWithSystem;
        var autostartProblem = Autostart.Set(wantsAutostart);

        var settings = _settings with
        {
            NotificationReach = reach,
            // Never store true for a setting that could not be applied: the checkbox would then
            // claim on next open that the launcher starts at login when nothing registered it.
            StartWithSystem = wantsAutostart && autostartProblem is null,
        };

        try
        {
            _store.Save(settings);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The choice still applies to this run; it just will not be remembered. Saying so beats
            // a sheet that refuses to close.
            StatusText = $"Couldn't save that choice ({ex.Message}) — it applies until you quit.";
            _settings = settings;
            Chosen?.Invoke(settings);
            IsOpen = false;
            return;
        }

        _settings = settings;
        Chosen?.Invoke(settings);
        if (autostartProblem is not null)
            StatusText = autostartProblem;
        IsOpen = false;
    }

    /// <summary>Keeps this sheet's copy in step when the choice is changed from Settings instead.</summary>
    public void Reload(LauncherSettings settings) => _settings = settings;
}
