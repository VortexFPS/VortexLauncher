using Avalonia.Controls;
using Avalonia.Platform.Storage;

namespace Launcher.Desktop.Controls;

/// <summary>The single gate between release-notes text and the operating system.
///
/// Release bodies are typed into GitHub and fetched over the wire, so they are attacker-influenced
/// the moment the release process is: one compromised publishing token puts arbitrary text in front
/// of every player, on every machine, the next time the launcher checks for updates. That makes the
/// notes pane the one place in this app where hostile input reaches the UI, so link handling is a
/// whitelist and nothing here trusts what it was handed.</summary>
public static class SafeLinkPolicy
{
    /// <summary>True when <paramref name="href"/> is something we are willing to make clickable.</summary>
    public static bool TryParse(string? href, out Uri uri)
    {
        uri = null!;

        // Absolute only. A relative href has no base document to resolve against, and a
        // protocol-relative "//host/path" would silently inherit whichever scheme we guessed for it.
        if (!Uri.TryCreate(href, UriKind.Absolute, out var parsed))
            return false;

        // Allowlist, deliberately not a denylist. A denylist would have to enumerate every handler
        // registered on the player's box to be correct — javascript:, file:, ms-msdt:, and every
        // custom scheme any installed game or tool claimed — and each one is a way to make the
        // launcher start something on the player's behalf. Release notes need exactly two schemes.
        if (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps)
            return false;

        uri = parsed;
        return true;
    }

    /// <summary>Hands an already-validated URI to the system default browser.</summary>
    public static void Open(Control origin, Uri uri)
    {
        // Re-checked here because this is the call that actually reaches the shell, and it sits far
        // enough from TryParse that a future edit could route an unvalidated URI into it.
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            return;

        // Out-of-process by construction: the page renders in the player's browser, never inside
        // the launcher. There is no embedded webview in this app and this is the reason to keep it
        // that way — an in-app view would put attacker-authored HTML in our own process.
        if (TopLevel.GetTopLevel(origin)?.Launcher is { } launcher)
            _ = LaunchAsync(launcher, uri);
    }

    private static async Task LaunchAsync(ILauncher launcher, Uri uri)
    {
        try
        {
            await launcher.LaunchUriAsync(uri);
        }
        catch
        {
            // A box with no browser association must not take the launcher down over a release note.
        }
    }
}
