using System.Diagnostics;

namespace Launcher.Desktop;

/// <summary>Opens a folder in the desktop's own file browser.
///
/// In Launcher.Desktop rather than Core for the same reason <c>SystemNotifier</c> is: it shells out
/// to whatever the desktop session provides, which is a UI concern and not something the CLI or the
/// runner has any use for.</summary>
public static class Reveal
{
    /// <summary>Opens <paramref name="path"/>, creating it first if it does not exist yet. Returns
    /// null on success, or a message fit to put in front of a player.
    ///
    /// Creating it is deliberate. Both folders this is wired to are places the launcher or the game
    /// writes into on first use, so a fresh install has a valid path pointing at nothing — and a
    /// button that errors because the thing it names has not been needed yet is worse than one that
    /// opens an empty folder.
    ///
    /// The path goes through ArgumentList, never a command string. It comes from settings, so it is
    /// player-controlled text; concatenating it into a shell line is how a folder name with a quote
    /// in it turns into an argument-injection bug.</summary>
    public static string? Open(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return $"Couldn't open {path}: {ex.Message}";
        }

        // explorer.exe is the odd one out: it exits non-zero even when it succeeded, so nothing here
        // waits on or checks an exit code. A launched-and-ignored process is the contract on all
        // three — the failure worth reporting is "no file browser to launch", which surfaces as the
        // start itself throwing.
        var (exe, arg) = OperatingSystem.IsWindows() ? ("explorer.exe", path)
            : OperatingSystem.IsMacOS() ? ("open", path)
            : ("xdg-open", path);

        try
        {
            var psi = new ProcessStartInfo(exe) { UseShellExecute = false };
            psi.ArgumentList.Add(arg);
            using var p = Process.Start(psi);
            return null;
        }
        catch (Exception ex)
        {
            // On a headless or minimal Linux box there may be no xdg-open at all. The path is in the
            // message so it can still be pasted somewhere.
            return $"Couldn't open a file browser for {path}: {ex.Message}";
        }
    }
}
