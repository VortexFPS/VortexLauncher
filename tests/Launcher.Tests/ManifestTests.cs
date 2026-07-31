using Launcher.Core;
using Xunit;

namespace Launcher.Tests;

public class ManifestTests
{
    // Byte-shape of tools/make-manifest.py output (generated from the real script) — this test
    // pins the python-emitter ↔ C#-consumer contract.
    private const string LatestJson = """
    {
      "schema": 1,
      "version": "0.2.0",
      "tag": "v0.2.0",
      "channel": "stable",
      "notesUrl": "https://github.com/bryankruman/XonoticGodot/releases/tag/v0.2.0",
      "assets": {
        "name": "XonoticGodot-assets-abc123def456.zip",
        "size": 1500000000,
        "sha256": "b1cee46e45136263dddc6681fc13a1cd725274162754277e68474d7159fc7ffd",
        "url": "https://github.com/bryankruman/XonoticGodot/releases/download/v0.1.0/XonoticGodot-assets-abc123def456.zip",
        "version": "abc123def456"
      },
      "platforms": {
        "windows-x86_64": {
          "fat": {
            "name": "XonoticGodot-0.2.0-windows-x86_64.zip",
            "root": "windows-client",
            "size": 1600000000,
            "sha256": "7b761dfe990f9e86dde82a3ebba23f5a3b60cf9eed8373553d1f6e64ceb1f069",
            "url": "https://github.com/bryankruman/XonoticGodot/releases/download/v0.2.0/XonoticGodot-0.2.0-windows-x86_64.zip"
          },
          "core": {
            "name": "XonoticGodot-0.2.0-windows-x86_64-core.zip",
            "root": "windows-client",
            "size": 90000000,
            "sha256": "e6bdf9f77508684ae880af625c6f300ba42ae689d7abf4be33328f5371babd1d",
            "url": "https://github.com/bryankruman/XonoticGodot/releases/download/v0.2.0/XonoticGodot-0.2.0-windows-x86_64-core.zip"
          }
        },
        "macos-universal": {
          "fat": null,
          "core": null
        }
      }
    }
    """;

    [Fact]
    public void Parses_make_manifest_output()
    {
        var m = ReleaseManifest.Parse(LatestJson);

        Assert.NotNull(m);
        Assert.Equal(1, m!.Schema);
        Assert.Equal("0.2.0", m.Version);
        Assert.Equal("v0.2.0", m.Tag);
        Assert.Equal("abc123def456", m.Assets!.Version);
        // The deduped assets pack points at a PREVIOUS release's URL — must round-trip untouched.
        Assert.Contains("/v0.1.0/", m.Assets.Url);

        // This fixture carries the ORIGINAL "fat" key, which is the point of leaving it that way:
        // it is the shape every release published before the rename has, and the launcher has to go
        // on reading it. Bundle is the accessor that spans both spellings.
        var win = m.PlatformFor(PlatformKey.Windows);
        Assert.NotNull(win?.Fat);
        Assert.Null(win?.Complete);
        Assert.Same(win!.Fat, win.Bundle);
        Assert.NotNull(win?.Core);
        Assert.Equal("windows-client", win!.Core!.Root);
        Assert.Equal(90000000, win.Core.Size);

        // The best-effort macOS job failed this release: entry present, both packages null.
        var mac = m.PlatformFor(PlatformKey.MacOS);
        Assert.NotNull(mac);
        Assert.Null(mac!.Bundle);
        Assert.Null(mac.Core);

        Assert.Null(m.PlatformFor(PlatformKey.Linux)); // omitted entirely is also legal
    }

    /// <summary>latest.json is written by the game repo and read here, so the two sides rename on
    /// their own schedules and every combination has to resolve. Three cases, one answer each:
    /// the old key alone (releases already published), the new key alone (once the game repo has
    /// switched), and both at once (the transition, where make-manifest.py emits the pair).</summary>
    [Theory]
    [InlineData("""{"complete": {"name": "new.zip", "root": "r", "size": 1, "sha256": "aa", "url": "u"}}""", "new.zip")]
    [InlineData("""{"fat": {"name": "old.zip", "root": "r", "size": 1, "sha256": "aa", "url": "u"}}""", "old.zip")]
    [InlineData("""
        {"complete": {"name": "new.zip", "root": "r", "size": 1, "sha256": "aa", "url": "u"},
         "fat": {"name": "old.zip", "root": "r", "size": 1, "sha256": "aa", "url": "u"}}
        """, "new.zip")]
    public void Either_spelling_of_the_bundle_key_resolves(string platformJson, string expected)
    {
        // Concatenated rather than interpolated: the surrounding JSON is nothing but braces, and
        // escaping them through a raw interpolated literal costs more than it saves.
        var json = """{"schema": 1, "version": "0.2.0", "tag": "v0.2.0", "platforms": {"windows-x86_64": """
                   + platformJson + "}}";

        var m = ReleaseManifest.Parse(json)!;

        Assert.Equal(expected, m.PlatformFor(PlatformKey.Windows)!.Bundle!.Name);
    }
}

public class GitHubApiFeedTests
{
    // Trimmed /repos/…/releases listing: a draft first (must be skipped), then a prerelease
    // whose assets carry GitHub's per-asset sha256 digest.
    private const string ReleasesJson = """
    [
      { "tag_name": "v9.9.9", "draft": true, "prerelease": false, "html_url": "x", "body": "draft", "assets": [] },
      {
        "tag_name": "v0.1.0-alpha", "draft": false, "prerelease": true,
        "html_url": "https://github.com/bryankruman/XonoticGodot/releases/tag/v0.1.0-alpha",
        "body": "First alpha.",
        "assets": [
          { "name": "XonoticGodot-0.1.0-alpha-windows-x86_64.zip", "size": 1500,
            "browser_download_url": "https://example/win.zip", "digest": "sha256:AABB00" },
          { "name": "XonoticGodot-0.1.0-alpha-linux-x86_64.zip", "size": 1400,
            "browser_download_url": "https://example/linux.zip", "digest": null },
          { "name": "XonoticGodot-assets-abc123def456.zip", "size": 999,
            "browser_download_url": "https://example/assets.zip", "digest": "sha256:CCDD11" },
          { "name": "SHA256SUMS-v0.1.0-alpha.txt", "size": 447,
            "browser_download_url": "https://example/sums.txt", "digest": null }
        ]
      }
    ]
    """;

    [Fact]
    public void Picks_the_newest_non_draft_release()
    {
        var rel = GitHubApiFeed.PickLatest(ReleasesJson);
        Assert.NotNull(rel);
        Assert.Equal("v0.1.0-alpha", rel!.TagName);
        Assert.True(rel.Prerelease);
    }

    [Fact]
    public void Synthesizes_a_manifest_with_sums_over_digest_precedence()
    {
        var rel = GitHubApiFeed.PickLatest(ReleasesJson)!;
        var sums = new Dictionary<string, string>
        {
            // Covers the digest-less linux zip; also overrides the windows digest.
            ["XonoticGodot-0.1.0-alpha-linux-x86_64.zip"] = "beef01",
            ["XonoticGodot-0.1.0-alpha-windows-x86_64.zip"] = "feed02",
        };

        var m = GitHubApiFeed.Synthesize(rel, sums);

        Assert.Equal("0.1.0-alpha", m.Version);
        Assert.True(m.Prerelease);
        Assert.Equal("First alpha.", m.NotesBody);
        Assert.Equal("feed02", m.PlatformFor(PlatformKey.Windows)!.Bundle!.Sha256); // sums win
        Assert.Equal("beef01", m.PlatformFor(PlatformKey.Linux)!.Bundle!.Sha256);   // sums cover no-digest
        Assert.Equal("ccdd11", m.Assets!.Sha256);                                 // digest, lowercased
        Assert.Equal("abc123def456", m.Assets.Version);
    }

    [Fact]
    public void Drops_files_with_no_checksum_from_any_source()
    {
        var rel = GitHubApiFeed.PickLatest(ReleasesJson)!;
        var m = GitHubApiFeed.Synthesize(rel, new Dictionary<string, string>());

        // windows has a digest → kept; linux has neither digest nor sums entry → dropped
        // (the installer never installs unverifiable bits — ADR-0015 invariant #2).
        Assert.NotNull(m.PlatformFor(PlatformKey.Windows)?.Bundle);
        Assert.Null(m.PlatformFor(PlatformKey.Linux));
    }
}
