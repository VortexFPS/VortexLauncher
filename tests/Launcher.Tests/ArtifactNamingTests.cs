using Launcher.Core;
using Xunit;

namespace Launcher.Tests;

/// <summary>The game's artifacts are mid-rename: tools/package.sh still emits XonoticGodot-*.zip while
/// the rebrand is landing, so releases on both sides of the cutover have to stay installable and
/// launchable. These cover the seam the README calls "the artifact rename breaks update continuity".</summary>
public class ArtifactNamingTests
{
    /// <summary>The regex in GitHubApiFeed needs a compile-time constant, so the prefix list exists
    /// twice: once as data, once as a pattern. They have to say the same thing.</summary>
    [Fact]
    public void Prefix_list_and_regex_pattern_agree()
    {
        Assert.Equal(
            LauncherConfig.ArtifactPrefixes,
            LauncherConfig.ArtifactPrefixPattern.Split('|'));
    }

    [Fact]
    public void Canonical_prefix_is_the_first_accepted_one()
    {
        Assert.Equal(LauncherConfig.CanonicalArtifactPrefix, LauncherConfig.ArtifactPrefixes[0]);
    }

    [Theory]
    [InlineData("VortexArena-0.2.0-windows-x86_64.zip", "0.2.0", PlatformKey.Windows, "windows-client", false)]
    [InlineData("VortexArena-0.2.0-linux-x86_64-core.zip", "0.2.0", PlatformKey.Linux, "linux-client", true)]
    [InlineData("XonoticGodot-0.2.0-windows-x86_64.zip", "0.2.0", PlatformKey.Windows, "windows-client", false)]
    [InlineData("XonoticGodot-0.2.0-linux-x86_64-core.zip", "0.2.0", PlatformKey.Linux, "linux-client", true)]
    public void Zip_names_parse_under_either_prefix(
        string zip, string version, string expectedKey, string expectedRoot, bool expectedCore)
    {
        Assert.True(PlatformKey.TryParseZipName(zip, version, out var key, out var root, out var isCore));
        Assert.Equal(expectedKey, key);
        Assert.Equal(expectedRoot, root);
        Assert.Equal(expectedCore, isCore);
    }

    [Fact]
    public void A_third_prefix_is_still_rejected()
    {
        Assert.False(PlatformKey.TryParseZipName(
            "SomeOtherGame-0.2.0-windows-x86_64.zip", "0.2.0", out _, out _, out _));
    }

    [Theory]
    [InlineData("VortexArena-assets-abc123def456.zip")]
    [InlineData("XonoticGodot-assets-abc123def456.zip")]
    public void Assets_pack_is_recognised_under_either_prefix(string assetName)
    {
        var release = new GitHubApiFeed.ApiRelease(
            TagName: "v0.2.0", Prerelease: false, Draft: false,
            HtmlUrl: null, Body: null,
            Assets: [new GitHubApiFeed.ApiAsset(assetName, 999, "https://example/" + assetName,
                Digest: "sha256:" + new string('a', 64))]);

        var manifest = GitHubApiFeed.Synthesize(release, new Dictionary<string, string>());

        Assert.NotNull(manifest.Assets);
        Assert.Equal("abc123def456", manifest.Assets!.Version);
    }

    /// <summary>GameLauncher probes these in order, so an install made under the old name still
    /// launches after the launcher itself has moved on.</summary>
    [Fact]
    public void Executable_candidates_cover_both_names_canonical_first()
    {
        var candidates = PlatformKey.ExecutableCandidates(PlatformKey.Windows).ToArray();

        Assert.Equal(["VortexArena.exe", "XonoticGodot.exe"], candidates);
        Assert.Equal(candidates[0], PlatformKey.ExecutableRelativePath(PlatformKey.Windows));
    }

    [Fact]
    public void Dedicated_binary_name_is_lowercased_per_package_sh()
    {
        Assert.Equal("vortexarena-dedicated.x86_64",
            PlatformKey.ExecutableRelativePath(PlatformKey.LinuxDedicated, "VortexArena"));
    }
}
