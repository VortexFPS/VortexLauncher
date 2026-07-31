using System.Diagnostics;

namespace Launcher.Desktop;

/// <summary>Start the launcher at login, for the one reach that needs it.
///
/// Only meaningful under <see cref="Launcher.Core.NotificationReaches.Background"/>: autostarting a
/// launcher that then shows a window nobody asked for is a nuisance, so the entry it writes always
/// carries <see cref="TrayFlag"/> and the setting is cleared alongside the reach.
///
/// Every backend is best-effort and returns a message rather than throwing. Failing to register for
/// autostart is not a reason to fail a settings save — the player's other preferences on that sheet
/// still have to land, and the checkbox reports what happened.
///
/// Windows goes through <c>reg.exe</c> rather than the Registry API because the Registry types are
/// not in a plain <c>net8.0</c> reference set; reaching them means either a NuGet package or
/// multi-targeting <c>net8.0-windows</c>, and neither is worth it for two key writes in a launcher
/// that also builds on Linux. Same trade, and the same reasoning, as
/// <see cref="Notifications.SystemNotifier"/>.</summary>
public static class Autostart
{
    /// <summary>Told to the launcher by its own autostart entry, so a login start comes up in the
    /// tray instead of opening a window.</summary>
    public const string TrayFlag = "--tray";

    private const string WindowsRunKey = @"HKCU\Software\Microsoft\Windows\CurrentVersion\Run";
    private const string EntryName = "VortexLauncher";

    /// <summary>The launcher's own executable, or null when there is not one to point at — a
    /// <c>dotnet run</c> dev build's ProcessPath is the dotnet host, and registering THAT to run at
    /// login would start something that is not this launcher.</summary>
    private static string? ExecutablePath
    {
        get
        {
            var path = Environment.ProcessPath;
            if (path is null)
                return null;
            var name = Path.GetFileNameWithoutExtension(path);
            return name.Equals("dotnet", StringComparison.OrdinalIgnoreCase) ? null : path;
        }
    }

    /// <summary>Apply the setting. Returns null on success, or a sentence explaining why not.</summary>
    public static string? Set(bool enabled)
    {
        var exe = ExecutablePath;
        if (exe is null)
            return enabled
                ? "Start at login needs an installed launcher — it does nothing for a dev build."
                : null; // nothing was ever registered

        try
        {
            if (OperatingSystem.IsWindows())
                return Windows(exe, enabled);
            if (OperatingSystem.IsMacOS())
                return MacOs(exe, enabled);
            if (OperatingSystem.IsLinux())
                return Linux(exe, enabled);
            return "Start at login isn't supported on this platform.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                       or System.ComponentModel.Win32Exception)
        {
            return $"Couldn't change the start-at-login setting: {ex.Message}";
        }
    }

    private static string? Windows(string exe, bool enabled)
    {
        var psi = new ProcessStartInfo("reg.exe");
        if (enabled)
        {
            psi.ArgumentList.Add("add");
            psi.ArgumentList.Add(WindowsRunKey);
            psi.ArgumentList.Add("/v");
            psi.ArgumentList.Add(EntryName);
            psi.ArgumentList.Add("/t");
            psi.ArgumentList.Add("REG_SZ");
            psi.ArgumentList.Add("/d");
            psi.ArgumentList.Add($"\"{exe}\" {TrayFlag}");
            psi.ArgumentList.Add("/f");
        }
        else
        {
            psi.ArgumentList.Add("delete");
            psi.ArgumentList.Add(WindowsRunKey);
            psi.ArgumentList.Add("/v");
            psi.ArgumentList.Add(EntryName);
            psi.ArgumentList.Add("/f");
        }

        psi.UseShellExecute = false;
        psi.CreateNoWindow = true;
        psi.RedirectStandardOutput = true;
        psi.RedirectStandardError = true;

        using var process = Process.Start(psi);
        if (process is null)
            return "Couldn't run reg.exe to change the start-at-login setting.";
        process.WaitForExit(5000);

        // Deleting a value that was never written exits nonzero, which is the expected outcome of
        // turning off something that was never on — not a failure worth reporting.
        return process.ExitCode == 0 || !enabled
            ? null
            : "Couldn't write the start-at-login entry.";
    }

    private static string? Linux(string exe, bool enabled)
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config", "autostart");
        var file = Path.Combine(dir, "vortex-launcher.desktop");

        if (!enabled)
        {
            if (File.Exists(file))
                File.Delete(file);
            return null;
        }

        Directory.CreateDirectory(dir);
        File.WriteAllText(file, $"""
            [Desktop Entry]
            Type=Application
            Name=Vortex Arena Launcher
            Comment=Checks for Vortex Arena updates in the background
            Exec="{exe}" {TrayFlag}
            Terminal=false
            X-GNOME-Autostart-enabled=true

            """);
        return null;
    }

    private static string? MacOs(string exe, bool enabled)
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "LaunchAgents");
        var file = Path.Combine(dir, "com.vortexfps.vortexlauncher.plist");

        if (!enabled)
        {
            if (File.Exists(file))
                File.Delete(file);
            return null;
        }

        Directory.CreateDirectory(dir);
        // The exe path goes through XML escaping: it is a filesystem path the player chose, and a
        // '&' in a folder name would otherwise produce a plist launchd refuses to load.
        File.WriteAllText(file, $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
            <plist version="1.0">
            <dict>
              <key>Label</key><string>com.vortexfps.vortexlauncher</string>
              <key>ProgramArguments</key>
              <array>
                <string>{System.Security.SecurityElement.Escape(exe)}</string>
                <string>{TrayFlag}</string>
              </array>
              <key>RunAtLoad</key><true/>
            </dict>
            </plist>

            """);
        return null;
    }
}
