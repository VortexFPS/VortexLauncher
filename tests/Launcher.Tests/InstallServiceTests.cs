using System.IO.Compression;
using Launcher.Core;
using Xunit;

namespace Launcher.Tests;

/// <summary>The install lifecycle against a real filesystem, with the network replaced by a
/// url→local-file copy stub: extract → swap → current.json flip → prune → core asset store.</summary>
public sealed class InstallServiceTests : IDisposable
{
    private readonly string _tmp = Path.Combine(
        Path.GetTempPath(), "xglauncher-tests", Path.GetRandomFileName());
    private readonly LauncherPaths _paths;
    private readonly StubDownloader _net = new();
    private readonly InstallService _installs;

    public InstallServiceTests()
    {
        _paths = new LauncherPaths(Path.Combine(_tmp, "root"));
        _installs = new InstallService(_paths, _net);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tmp, recursive: true); } catch { /* best-effort */ }
    }

    [Fact]
    public async Task Fat_install_extracts_swaps_and_records_current()
    {
        var m = MakeManifest("0.2.0", MakeFatZip("0.2.0"));

        var state = await _installs.InstallAsync(m, PlatformKey.Windows,
            preferCore: false, progress: null, CancellationToken.None);

        Assert.Equal("0.2.0", state.Version);
        Assert.Equal(InstalledState.LayoutFat, state.Layout);
        Assert.True(File.Exists(Path.Combine(_installs.GameDirOf(state), "XonoticGodot.exe")));
        Assert.Null(_installs.AssetsDataDirOf(state));

        // current.json round-trips, and staging holds no leftovers.
        Assert.Equal(state, _installs.LoadCurrent());
        Assert.Empty(Directory.GetFileSystemEntries(_paths.StagingDir));
    }

    [Fact]
    public async Task Corrupt_download_is_rejected_before_anything_is_swapped()
    {
        var zip = MakeFatZip("0.2.0");
        var m = MakeManifest("0.2.0", zip with { Sha256 = new string('0', 64) });

        await Assert.ThrowsAsync<InvalidOperationException>(() => _installs.InstallAsync(
            m, PlatformKey.Windows, preferCore: false, progress: null, CancellationToken.None));

        Assert.Null(_installs.LoadCurrent());
        Assert.False(Directory.Exists(Path.Combine(_paths.VersionsDir, "0.2.0")));
    }

    [Fact]
    public async Task Prune_keeps_current_plus_one_for_rollback()
    {
        foreach (var v in new[] { "0.1.0", "0.2.0", "0.3.0" })
        {
            await _installs.InstallAsync(MakeManifest(v, MakeFatZip(v)), PlatformKey.Windows,
                preferCore: false, progress: null, CancellationToken.None);
            await Task.Delay(20); // separate LastWriteTime ticks for the prune ordering
        }

        var kept = Directory.GetDirectories(_paths.VersionsDir).Select(Path.GetFileName).ToHashSet();
        Assert.Equal(["0.2.0", "0.3.0"], kept.Order());
        Assert.Equal("0.3.0", _installs.LoadCurrent()!.Version);
    }

    [Fact]
    public async Task Core_install_populates_the_shared_store_and_reuses_it()
    {
        var m = MakeManifest("0.2.0", core: MakeCoreZip("0.2.0"), assets: MakeAssetsPack());

        var state = await _installs.InstallAsync(m, PlatformKey.Windows,
            preferCore: true, progress: null, CancellationToken.None);

        Assert.Equal(InstalledState.LayoutCore, state.Layout);
        var dataDir = _installs.AssetsDataDirOf(state);
        Assert.NotNull(dataDir);
        Assert.True(File.Exists(Path.Combine(dataDir!, "somefile.txt")));

        // A second core install (new game version, same assets hash) downloads NO assets.
        _net.Downloads.Clear();
        var m2 = MakeManifest("0.3.0", core: MakeCoreZip("0.3.0"), assets: MakeAssetsPack());
        var state2 = await _installs.InstallAsync(m2, PlatformKey.Windows,
            preferCore: true, progress: null, CancellationToken.None);

        Assert.Equal("abc123def456", state2.AssetsVersion);
        Assert.DoesNotContain(_net.Downloads, u => u.Contains("assets"));
    }

    [Fact]
    public async Task Staging_downloads_the_new_build_without_changing_what_launches()
    {
        // The default update mode's whole premise: the bytes can be on disk long before the player
        // agrees to switch, and until they do, Play must still start what it started yesterday.
        await _installs.InstallAsync(MakeManifest("0.2.0", MakeFatZip("0.2.0")), PlatformKey.Windows,
            preferCore: false, progress: null, CancellationToken.None);

        var staged = await _installs.StageAsync(MakeManifest("0.3.0", MakeFatZip("0.3.0")),
            PlatformKey.Windows, preferCore: false, progress: null, CancellationToken.None);

        Assert.Equal("0.3.0", staged.Version);
        Assert.True(_installs.IsStaged(staged));
        Assert.True(Directory.Exists(Path.Combine(_paths.VersionsDir, "0.3.0")));
        // The marker has not moved, so nothing about launching has changed.
        Assert.Equal("0.2.0", _installs.LoadCurrent()!.Version);

        var applied = _installs.Apply(staged);

        Assert.Equal("0.3.0", applied.Version);
        Assert.Equal("0.3.0", _installs.LoadCurrent()!.Version);
        Assert.True(File.Exists(Path.Combine(_installs.GameDirOf(applied), "XonoticGodot.exe")));
    }

    [Fact]
    public async Task A_staged_build_that_has_gone_missing_is_reported_rather_than_pinned()
    {
        // The launcher offers "switch now" from a staged build that may have been collected since,
        // so the check has to be against the disk and not against the fact that we staged it.
        var staged = await _installs.StageAsync(MakeManifest("0.3.0", MakeFatZip("0.3.0")),
            PlatformKey.Windows, preferCore: false, progress: null, CancellationToken.None);
        Directory.Delete(Path.Combine(_paths.VersionsDir, "0.3.0"), recursive: true);

        Assert.False(_installs.IsStaged(staged));
    }

    [Fact]
    public void Pin_resolves_a_build_whose_id_version_and_directory_all_differ()
    {
        // A release build's id, version and directory name are one string, and current.json recorded
        // only the version because of it. A source build breaks that: the marker pointed at
        // versions/<sha7>/, which does not exist, so pinning one exited 0 and left every reader
        // downstream reporting nothing installed. This is the whole of `source build` that can be
        // tested without a Godot editor and a multi-gigabyte checkout, so it is the part that is.
        const string id = "source:windows-client:main@a1b2c3d";
        var build = new BuildRecord
        {
            Id = id,
            DirName = BuildRecord.SafeDirName(id),
            Version = "a1b2c3d",
            PlatformKey = PlatformKey.Windows,
            Layout = InstalledState.LayoutFat,
            Root = "windows-client",
            Provider = BuildProviders.Source,
            InstalledAt = DateTimeOffset.UtcNow,
        };
        Assert.NotEqual(build.Version, build.DirName);
        Directory.CreateDirectory(Path.Combine(_paths.VersionsDir, build.DirName, build.Root));
        _installs.Builds.Register(build);

        var state = _installs.Pin(build);

        Assert.Equal(id, state.Id);
        Assert.Equal(build.DirName, state.Dir);
        // LoadCurrent returns null when the directory the marker names is absent, so a round trip is
        // the assertion that would have caught this.
        Assert.Equal(state, _installs.LoadCurrent());
    }

    [Fact]
    public void A_marker_written_before_source_builds_still_resolves()
    {
        // buildId and dirName are absent from every current.json already on disk, so null has to keep
        // meaning "the same as version". If it stopped, upgrading the launcher would report the
        // player's installed game as missing.
        Directory.CreateDirectory(Path.Combine(_paths.VersionsDir, "0.4.0", "windows-client"));
        Directory.CreateDirectory(_paths.GameDir);
        File.WriteAllText(_paths.CurrentJsonPath,
            """{"version":"0.4.0","layout":"fat","platformKey":"windows-x86_64","root":"windows-client"}""");

        var state = _installs.LoadCurrent();

        Assert.NotNull(state);
        Assert.Equal("0.4.0", state!.Id);
        Assert.Equal("0.4.0", state.Dir);
    }

    // ── fixtures ──────────────────────────────────────────────────────────────

    private ManifestFile MakeFatZip(string version) =>
        MakeZip($"XonoticGodot-{version}-windows-x86_64.zip",
            ("windows-client/XonoticGodot.exe", $"fake exe {version}"),
            ("windows-client/assets/data/xonotic-data.pk3dir/somefile.txt", "fake data"));

    private ManifestFile MakeCoreZip(string version) =>
        MakeZip($"XonoticGodot-{version}-windows-x86_64-core.zip",
            ("windows-client/XonoticGodot.exe", $"fake exe {version}"));

    private ManifestAssets MakeAssetsPack()
    {
        var f = MakeZip("XonoticGodot-assets-abc123def456.zip",
            ("assets/data/somefile.txt", "fake shared data"));
        return new ManifestAssets(f.Name, "abc123def456", f.Size, f.Sha256, f.Url);
    }

    private ManifestFile MakeZip(string name, params (string Path, string Content)[] entries)
    {
        var src = Path.Combine(_tmp, "srczips", name);
        Directory.CreateDirectory(Path.GetDirectoryName(src)!);
        File.Delete(src); // same fixture zip may be built twice (e.g. one assets pack, two versions)
        using (var zip = ZipFile.Open(src, ZipArchiveMode.Create))
            foreach (var (path, content) in entries)
            {
                using var w = new StreamWriter(zip.CreateEntry(path).Open());
                w.Write(content);
            }
        var url = $"https://test.invalid/{name}";
        _net.Map[url] = src;
        var sha = ChecksumFile.Sha256OfFileAsync(src).GetAwaiter().GetResult();
        return new ManifestFile(name, "windows-client", new FileInfo(src).Length, sha, url);
    }

    private static ReleaseManifest MakeManifest(string version, ManifestFile? fat = null,
        ManifestFile? core = null, ManifestAssets? assets = null) => new()
    {
        Version = version,
        Tag = $"v{version}",
        Assets = assets,
        Platforms = { [PlatformKey.Windows] = new ManifestPlatform(fat, core) },
    };

    /// <summary>IDownloader double: copies a mapped local file, honoring the real service's
    /// verify-then-hand-over contract (mismatch deletes + throws, like DownloadService).</summary>
    private sealed class StubDownloader : IDownloader
    {
        public Dictionary<string, string> Map { get; } = new();
        public List<string> Downloads { get; } = new();

        public async Task DownloadAsync(string url, string destPath, long expectedSize,
            string? expectedSha256, IProgress<double>? progress, CancellationToken ct)
        {
            Downloads.Add(url);
            Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
            File.Copy(Map[url], destPath, overwrite: true);
            progress?.Report(1.0);
            var actual = await ChecksumFile.Sha256OfFileAsync(destPath, ct);
            if (!actual.Equals(expectedSha256, StringComparison.OrdinalIgnoreCase))
            {
                File.Delete(destPath);
                throw new InvalidOperationException("checksum mismatch (stub)");
            }
        }
    }
}
