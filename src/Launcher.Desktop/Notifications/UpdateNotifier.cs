using System.Diagnostics;
using Launcher.Core;

namespace Launcher.Desktop.Notifications;

/// <summary>Somewhere to send "there is a new version" that is not the status line.
///
/// The in-app banner is not behind this interface: it is view-model state the window binds to, it
/// works everywhere, and it is the fallback whenever an OS notification does not land. This models
/// only the part that leaves the window.</summary>
public interface IUpdateNotifier
{
    /// <summary>Fire-and-forget by contract. A notification that cannot be delivered is not an
    /// error the player needs to see — the banner already carries the same message — so this must
    /// never throw and never block the caller.</summary>
    void Notify(string title, string body);
}

/// <summary>The in-app reach: nothing leaves the window.</summary>
public sealed class NoSystemNotifier : IUpdateNotifier
{
    public void Notify(string title, string body) { }
}

/// <summary>Native OS notifications, one small backend per platform.
///
/// Every backend shells out rather than binding an OS API, which is the trade this makes
/// deliberately. Binding the Windows one properly means <c>CommunityToolkit.WinUI.Notifications</c>,
/// which is published only for a <c>net8.0-windows10.0.x</c> target framework; taking it would make
/// this project multi-target and would put a Windows-only TFM into a launcher that builds and runs
/// on Linux CI. The cost of shelling out is latency nobody perceives on a notification and the
/// caveats listed on each backend below.
///
/// Nothing is interpolated into a shell string. Every payload crosses as an environment variable on
/// the child process, because the text includes a release version and a release is exactly as
/// trustworthy as the release process — the same reasoning that put release-note links behind
/// <see cref="Controls.SafeLinkPolicy"/>. An argument list or an env var cannot be escaped out of;
/// a quoted command line can.</summary>
public sealed class SystemNotifier : IUpdateNotifier
{
    private const string TitleVar = "VORTEX_NOTIFY_TITLE";
    private const string BodyVar = "VORTEX_NOTIFY_BODY";

    /// <summary>Set once a backend has failed, so a box without <c>notify-send</c> installed does
    /// not pay a process spawn every four hours forever.</summary>
    private bool _broken;

    public void Notify(string title, string body)
    {
        if (_broken)
            return;

        try
        {
            var psi = Backend(title, body);
            if (psi is null)
            {
                _broken = true;
                return;
            }

            // Set for every backend even though only the two script-driven ones read them: an
            // argument list is already unforgeable, and a backend that grows a script later should
            // find the payload where the other two keep it.
            psi.Environment[TitleVar] = title;
            psi.Environment[BodyVar] = body;
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;

            var process = Process.Start(psi);
            // Not awaited: the caller is a UI thread and the point is the notification, not its exit
            // code. Disposing on exit keeps the handle from leaking on a launcher left open for days.
            if (process is not null)
            {
                process.EnableRaisingEvents = true;
                process.Exited += (_, _) => process.Dispose();
            }
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException
                                       or PlatformNotSupportedException or IOException)
        {
            // No notification daemon, no osascript, no PowerShell — all the same outcome, and the
            // banner in the window has already said the same thing.
            _broken = true;
        }
    }

    private static ProcessStartInfo? Backend(string title, string body)
    {
        if (OperatingSystem.IsWindows())
            return Windows();
        if (OperatingSystem.IsMacOS())
            return MacOs();
        if (OperatingSystem.IsLinux())
            return Linux(title, body);
        return null;
    }

    /// <summary>freedesktop.org notifications. Present on every desktop Linux that has a
    /// notification daemon at all; absent on a headless box, which is the <c>_broken</c> case.
    ///
    /// <c>--</c> before the payload matters: a title that happened to begin with a dash would
    /// otherwise be parsed as an option by GOption.</summary>
    private static ProcessStartInfo Linux(string title, string body)
    {
        var psi = new ProcessStartInfo("notify-send");
        psi.ArgumentList.Add("--app-name=Vortex Arena Launcher");
        psi.ArgumentList.Add("--");
        psi.ArgumentList.Add(title);
        psi.ArgumentList.Add(body);
        return psi;
    }

    /// <summary>AppleScript's own notification. <c>system attribute</c> reads the environment of
    /// the osascript process, which is how the text gets in without being quoted into the script.
    ///
    /// Caveat worth knowing: macOS attributes this to whatever app owns the running osascript —
    /// Script Editor, in practice — rather than to the launcher, and a user who has denied that app
    /// notification permission gets nothing. Attributing it correctly needs a signed bundle with its
    /// own notification entitlement, which is a packaging change, not a code one.</summary>
    private static ProcessStartInfo MacOs()
    {
        var psi = new ProcessStartInfo("osascript");
        psi.ArgumentList.Add("-e");
        psi.ArgumentList.Add(
            $"display notification (system attribute \"{BodyVar}\") "
            + $"with title (system attribute \"{TitleVar}\")");
        return psi;
    }

    /// <summary>A Windows toast through the WinRT notification API, driven from PowerShell.
    ///
    /// The text is escaped with SecurityElement::Escape and injected as a text node rather than
    /// concatenated into the template, because the toast payload is XML and a release name
    /// containing a &lt; would otherwise either break the toast or shape it.
    ///
    /// Caveat: Windows attributes a toast to an AppUserModelID, and one that matches no installed
    /// Start Menu shortcut can be dropped without a visible error. A Velopack-installed launcher has
    /// such a shortcut; a <c>dotnet run</c> dev build does not, so this is one more thing that is
    /// inert outside an installed build — the same shape as <see cref="SelfUpdateService"/>. The
    /// PowerShell fallback AppID is the shell's own, which always resolves.</summary>
    private static ProcessStartInfo Windows()
    {
        const string script = """
            $ErrorActionPreference = 'Stop'
            [Windows.UI.Notifications.ToastNotificationManager, Windows.UI.Notifications, ContentType=WindowsRuntime] > $null
            [Windows.Data.Xml.Dom.XmlDocument, Windows.Data.Xml.Dom, ContentType=WindowsRuntime] > $null
            $title = [System.Security.SecurityElement]::Escape($env:VORTEX_NOTIFY_TITLE)
            $body  = [System.Security.SecurityElement]::Escape($env:VORTEX_NOTIFY_BODY)
            $xml = New-Object Windows.Data.Xml.Dom.XmlDocument
            $xml.LoadXml("<toast><visual><binding template='ToastText02'><text id='1'>$title</text><text id='2'>$body</text></binding></visual></toast>")
            $appId = $env:VORTEX_NOTIFY_APPID
            if (-not $appId) { $appId = '{1AC14E77-02E7-4E5D-B744-2EB1AE5198B7}\WindowsPowerShell\v1.0\powershell.exe' }
            [Windows.UI.Notifications.ToastNotificationManager]::CreateToastNotifier($appId).Show(
                [Windows.UI.Notifications.ToastNotification]::new($xml))
            """;

        var psi = new ProcessStartInfo("powershell.exe");
        psi.ArgumentList.Add("-NoProfile");
        psi.ArgumentList.Add("-NonInteractive");
        psi.ArgumentList.Add("-WindowStyle");
        psi.ArgumentList.Add("Hidden");
        psi.ArgumentList.Add("-ExecutionPolicy");
        psi.ArgumentList.Add("Bypass");
        psi.ArgumentList.Add("-Command");
        psi.ArgumentList.Add(script);
        return psi;
    }
}

/// <summary>Picks the notifier for a reach. The in-app reach and an unset one both get the silent
/// notifier — an unset reach means first run has not asked yet, and asking is not a licence to
/// start notifying in the meantime.</summary>
public static class Notifiers
{
    public static IUpdateNotifier For(LauncherSettings settings) =>
        settings.WantsSystemNotifications ? new SystemNotifier() : new NoSystemNotifier();
}
