using System.ComponentModel;
using System.Diagnostics;
using System.IO.Compression;

namespace Launcher.Core;

/// <summary>Seam between the installer and zip extraction, for the same reason
/// <see cref="IDownloader"/> exists: macOS cannot use the managed extractor at all (see
/// <see cref="DittoArchiveExtractor"/>), and the platform that can't run the managed path is also the
/// platform CI doesn't have, so the choice has to be substitutable from a test.</summary>
public interface IArchiveExtractor
{
    /// <summary>Extract <paramref name="zipPath"/> into <paramref name="destDir"/>, which the caller
    /// has already cleared. Either the tree lands complete or this throws — an implementation must
    /// never quietly produce a partial one, because the installer's next step moves whatever is there
    /// into versions/ and writes current.json over it.</summary>
    Task ExtractAsync(string zipPath, string destDir, CancellationToken ct);
}

/// <summary>Chooses the extractor for the machine doing the install. Keyed off the running OS rather
/// than the package's platform key: what decides this is whose filesystem the files land on.</summary>
public static class ArchiveExtractor
{
    public static IArchiveExtractor ForCurrentPlatform() => OperatingSystem.IsMacOS()
        ? new DittoArchiveExtractor()
        : new ManagedArchiveExtractor();
}

/// <summary>Windows and Linux: System.IO.Compression, no external process. Neither platform's package
/// contains symlinks, so the managed extractor's blind spot doesn't cost anything there.</summary>
public sealed class ManagedArchiveExtractor : IArchiveExtractor
{
    public Task ExtractAsync(string zipPath, string destDir, CancellationToken ct) =>
        Task.Run(() => ZipFile.ExtractToDirectory(zipPath, destDir), ct);
}

/// <summary>macOS: shells out to <c>ditto</c>, because the macOS package is a <c>.app</c> bundle whose
/// Contents/Frameworks holds symlinks and <see cref="ZipFile"/> drops symlink entries without a word —
/// the install looks finished and the bundle will not launch.
///
/// <c>ditto</c> over <c>/usr/bin/unzip</c>: both restore symlinks, but ditto is Apple's own
/// bundle-aware copier and also carries across the extended attributes an <c>.app</c> depends on
/// (quarantine flags, the xattrs a signed bundle needs to satisfy Gatekeeper), which unzip discards.
/// It is also the inverse of a <c>ditto -c -k --keepParent</c> pack, so packaging and installing
/// agree — though that packaging job lives in the game repo, so nothing here can enforce that it
/// stays one. It ships with macOS, so there is nothing for a player to install.</summary>
public sealed class DittoArchiveExtractor : IArchiveExtractor
{
    /// <summary>Absolute by design — which binary extracts a game build must not depend on whatever
    /// PATH the launcher inherited.</summary>
    public const string ToolPath = "/usr/bin/ditto";

    public async Task ExtractAsync(string zipPath, string destDir, CancellationToken ct)
    {
        // -x extract, -k source is a PKZip archive. ditto creates destDir, intermediates included.
        var psi = new ProcessStartInfo(ToolPath) { UseShellExecute = false, RedirectStandardError = true };
        foreach (var arg in new[] { "-x", "-k", zipPath, destDir })
            psi.ArgumentList.Add(arg);

        using var process = Start(psi);
        string stderr;
        try
        {
            // Silent on success; on failure stderr carries the only useful detail (disk full, bad zip).
            stderr = await process.StandardError.ReadToEndAsync(ct);
            await process.WaitForExitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            // Unlike the managed extractor, this work runs in a process the runtime does not own:
            // abandoning it leaves ditto unpacking a multi-GB build into staging after the player
            // cancelled, outliving the launcher itself.
            TryKill(process);
            throw;
        }

        // No fallback to ManagedArchiveExtractor here, deliberately: falling back is what produced a
        // broken-but-installed-looking .app in the first place. Failing loudly is the fix.
        if (process.ExitCode != 0)
            throw new InvalidOperationException(
                $"{ToolPath} -x -k exited with {process.ExitCode} while extracting " +
                $"{Path.GetFileName(zipPath)}{Detail(stderr)} — nothing was installed. Check free disk " +
                $"space and retry the install; to see the raw error, run " +
                $"'{ToolPath} -x -k \"{zipPath}\" \"{destDir}\"' in Terminal.");
    }

    private static Process Start(ProcessStartInfo psi)
    {
        try
        {
            return Process.Start(psi) ?? throw new Win32Exception();
        }
        catch (Exception ex) when (ex is Win32Exception or FileNotFoundException)
        {
            throw new InvalidOperationException(
                $"{ToolPath} could not be run, so this macOS build cannot be installed: only ditto " +
                "restores the symlinks inside the game's .app bundle. ditto ships with macOS — if it " +
                "is missing or unrunnable, that system is damaged; repair or reinstall macOS, or " +
                "install the Windows/Linux build instead.", ex);
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException or Win32Exception)
        {
            // It already exited, or the OS refused. Nothing useful to do while unwinding a cancel.
        }
    }

    private static string Detail(string stderr)
    {
        var text = stderr.Trim();
        return text.Length == 0 ? "" : $" ({text.ReplaceLineEndings(" ")})";
    }
}
