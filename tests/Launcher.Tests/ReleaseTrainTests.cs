using Launcher.Core;
using Xunit;

namespace Launcher.Tests;

/// <summary>Two release trains run in this codebase and they must not be allowed to merge.
///
/// The game repo publishes game zips and <c>latest.json</c>; this repo publishes Velopack launcher
/// packages. <see cref="LauncherConfig"/> holds a constant for each, and for a while it held only
/// one — <c>SelfUpdateService</c> pointed Velopack at <see cref="LauncherConfig.RepoUrl"/>, which is
/// the game. That was correct under ADR-0015 §7, where launcher packages rode the game's release
/// train, and became wrong the moment the launcher was extracted into its own repo with its own
/// cadence.
///
/// It is worth a test rather than a comment because both ways of collapsing the two are quiet:
///
///   - Self-update on the game repo finds no packages and reports "up to date" forever. A launcher
///     does not show its own version prominently, so nobody notices.
///   - Launcher packages published to the game repo are worse. GitHub resolves
///     <c>releases/latest</c> to the newest non-draft, non-prerelease release of that repo, so a
///     launcher release there hijacks it: <see cref="LauncherConfig.LatestManifestUrl"/> 404s and
///     every launcher in the field silently falls back to <c>GitHubApiFeed</c>, which is
///     unauthenticated GitHub API at 60 requests/hour. Nothing breaks; it just degrades.</summary>
public class ReleaseTrainTests
{
    [Fact]
    public void The_game_and_launcher_repos_are_different_repos()
    {
        Assert.NotEqual(LauncherConfig.Repo, LauncherConfig.LauncherRepo);
        Assert.Equal("VortexFPS/VortexArena", LauncherConfig.Repo);
        Assert.Equal("VortexFPS/VortexLauncher", LauncherConfig.LauncherRepo);
    }

    /// <summary>Every feed URL is the game's. These are the launcher reading the game's releases,
    /// which is the direction that stays as it was.</summary>
    [Fact]
    public void The_game_feeds_are_built_from_the_game_repo()
    {
        Assert.StartsWith(LauncherConfig.RepoUrl, LauncherConfig.LatestManifestUrl, StringComparison.Ordinal);
        Assert.StartsWith(LauncherConfig.RepoUrl, LauncherConfig.LatestManifestSignatureUrl, StringComparison.Ordinal);
        Assert.Contains(LauncherConfig.Repo, LauncherConfig.ReleasesApiUrl, StringComparison.Ordinal);

        Assert.DoesNotContain(LauncherConfig.LauncherRepo, LauncherConfig.LatestManifestUrl, StringComparison.Ordinal);
        Assert.DoesNotContain(LauncherConfig.LauncherRepo, LauncherConfig.ReleasesApiUrl, StringComparison.Ordinal);
    }

    /// <summary>The constant existing is not the property that matters — <c>SelfUpdateService</c>
    /// using it is. Read as text because this project cannot reference Launcher.Desktop: it is an
    /// Avalonia WinExe, and ArchitectureTests already establishes reading the repo off disk as how
    /// this suite polices projects it does not link.</summary>
    [Fact]
    public void SelfUpdateService_points_velopack_at_the_launcher_repo()
    {
        var path = Path.Combine(
            ArchitectureTests.RepoRoot(), "src", "Launcher.Desktop", "SelfUpdateService.cs");
        Assert.True(File.Exists(path), $"expected the self-update service at {path}");

        // Comments in that file discuss the old constant by name, so the check is on the call and
        // not on the bare identifier appearing anywhere in the source.
        var source = File.ReadAllText(path);
        Assert.DoesNotContain("GithubSource(LauncherConfig.RepoUrl", source, StringComparison.Ordinal);
        Assert.Contains("GithubSource(LauncherConfig.LauncherRepoUrl", source, StringComparison.Ordinal);
    }
}
