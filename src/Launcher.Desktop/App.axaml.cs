using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Platform;
using Launcher.Core;
using Launcher.Desktop.ViewModels;
using Launcher.Desktop.Views;

namespace Launcher.Desktop;

public class App : Application
{
    private MainWindowViewModel? _vm;
    private MainWindow? _window;
    private TrayIcon? _tray;

    /// <summary>Set when the player quits deliberately, so the close handler stops intercepting.</summary>
    private bool _exiting;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            _vm = new MainWindowViewModel();
            _window = new MainWindow { DataContext = _vm };
            desktop.MainWindow = _window;

            _vm.ActivateRequested += ShowWindow;
            _vm.ExitRequested += () =>
            {
                _exiting = true;
                desktop.Shutdown();
            };

            // The tray reach keeps the process alive with no window; without this the app would
            // exit the moment the window it is hiding was closed.
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            desktop.Exit += (_, _) =>
            {
                _tray?.Dispose();
                _vm.Shutdown();
            };

            _window.Closing += OnWindowClosing;

            var settings = new LauncherSettingsStore().Load();
            if (settings.WantsTray)
                InstallTray();

            // Started by its own autostart entry: come up in the tray rather than opening a window
            // in front of someone who has just logged in. Ignored when the reach does not want a
            // tray, so a stale login entry cannot produce a launcher with no way to reach it.
            var startHidden = settings.WantsTray
                && (desktop.Args ?? []).Contains(Autostart.TrayFlag, StringComparer.Ordinal);
            if (!startHidden)
                _window.Show();

            // The tray can be turned on and off from Settings without a restart.
            _vm.Settings.Applied += s =>
            {
                if (s.WantsTray)
                    InstallTray();
                else
                {
                    _tray?.Dispose();
                    _tray = null;
                }
            };
            _vm.FirstRun.Chosen += s =>
            {
                if (s.WantsTray)
                    InstallTray();
            };
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>Under the tray reach, closing the window hides it instead — that IS the feature, and
    /// the tray menu carries the real Quit. Under every other reach the close is a close, and the
    /// app shuts down with it.</summary>
    private void OnWindowClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_exiting || _tray is null || _window is null)
        {
            if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop && !_exiting)
            {
                _exiting = true;
                desktop.Shutdown();
            }
            return;
        }

        e.Cancel = true;
        _window.Hide();
    }

    private void InstallTray()
    {
        if (_tray is not null || _vm is null)
            return;

        _tray = new TrayIcon
        {
            ToolTipText = "Vortex Arena Launcher",
            Icon = LoadTrayIcon(),
            Menu =
            [
                new NativeMenuItem("Open launcher") { Command = _vm.ActivateWindowCommand },
                new NativeMenuItem("Check for updates now") { Command = _vm.CheckNowCommand },
                new NativeMenuItemSeparator(),
                new NativeMenuItem("Quit") { Command = _vm.ExitApplicationCommand },
            ],
        };
        // Left-clicking the icon does the obvious thing; the menu is for everything else.
        _tray.Clicked += (_, _) => ShowWindow();
        _tray.IsVisible = true;
    }

    private static WindowIcon? LoadTrayIcon()
    {
        try
        {
            return new WindowIcon(AssetLoader.Open(
                new Uri("avares://VortexLauncher/Assets/tray-icon.png")));
        }
        catch (Exception)
        {
            // A tray icon that will not load is not a reason to fail startup; the platform draws a
            // placeholder and the menu still works.
            return null;
        }
    }

    private void ShowWindow()
    {
        if (_window is null)
            return;
        _window.Show();
        _window.WindowState = WindowState.Normal;
        _window.Activate();
    }
}
