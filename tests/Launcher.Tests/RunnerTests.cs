using System.IO.Compression;
using Launcher.Core;
using Launcher.Core.Instances;
using Launcher.Protocol;
using Xunit;

namespace Launcher.Tests;

/// <summary>Shared scratch data root, torn down after each class.</summary>
public abstract class ScratchTest : IDisposable
{
    protected readonly string Tmp = Path.Combine(
        Path.GetTempPath(), "vortex-tests", Path.GetRandomFileName());

    protected LauncherPaths Paths => new(Path.Combine(Tmp, "root"));

    public void Dispose()
    {
        try { Directory.Delete(Tmp, recursive: true); } catch { /* best effort */ }
        GC.SuppressFinalize(this);
    }
}

public class BuildStoreTests : ScratchTest
{
    /// <summary>A directory on disk with no metadata entry is adopted rather than orphaned. That is
    /// the normal state after a crash mid-install, and it is what makes installs predating builds.json
    /// keep working.</summary>
    [Fact]
    public void Builds_on_disk_without_metadata_are_adopted()
    {
        var paths = Paths;
        Directory.CreateDirectory(Path.Combine(paths.VersionsDir, "0.2.0", "windows-client"));

        var store = new BuildStore(paths);
        var build = Assert.Single(store.List());

        Assert.Equal("0.2.0", build.Id);
        Assert.Equal("windows-client", build.Root);
    }

    [Fact]
    public void Gc_keeps_the_pinned_build_and_the_newest_other()
    {
        var paths = Paths;
        var store = new BuildStore(paths);

        foreach (var (version, age) in new[] { ("0.1.0", 30), ("0.2.0", 20), ("0.3.0", 10) })
        {
            Directory.CreateDirectory(Path.Combine(paths.VersionsDir, version, "root"));
            store.Register(new BuildRecord
            {
                Id = version, DirName = version, Version = version,
                PlatformKey = PlatformKey.Windows, Layout = InstalledState.LayoutFat, Root = "root",
                InstalledAt = DateTimeOffset.UtcNow.AddMinutes(-age),
            });
        }

        var removed = store.Gc(keep: 2, protectedId: "0.1.0");

        // The pin survives even though it is the oldest, and one more is kept for rollback.
        Assert.Equal(["0.2.0"], removed);
        Assert.Equal(["0.1.0", "0.3.0"], store.List().Select(b => b.Id).Order());
    }

    [Fact]
    public void Gc_never_deletes_the_pinned_build_even_at_keep_one()
    {
        var paths = Paths;
        var store = new BuildStore(paths);
        foreach (var version in new[] { "0.1.0", "0.2.0" })
        {
            Directory.CreateDirectory(Path.Combine(paths.VersionsDir, version, "root"));
            store.Register(new BuildRecord
            {
                Id = version, DirName = version, Version = version,
                PlatformKey = PlatformKey.Windows, Layout = InstalledState.LayoutFat, Root = "root",
                InstalledAt = DateTimeOffset.UtcNow,
            });
        }

        store.Gc(keep: 1, protectedId: "0.1.0");
        Assert.Contains(store.List(), b => b.Id == "0.1.0");
    }

    /// <summary>Source build ids carry ':' and '@', which Windows rejects outright in a path.</summary>
    [Theory]
    [InlineData("source:main@abc1234", "source-main-abc1234")]
    [InlineData("0.2.0", "0.2.0")]
    [InlineData("///", "build")]
    public void Build_ids_become_safe_directory_names(string id, string expected)
    {
        Assert.Equal(expected, BuildRecord.SafeDirName(id));
    }
}

public class InstanceStoreTests : ScratchTest
{
    [Theory]
    [InlineData("eu-1")]
    [InlineData("test_server.2")]
    public void Ordinary_names_are_accepted(string name)
    {
        Assert.Equal(name, InstanceStore.ValidateName(name));
    }

    /// <summary>Names become directory names and command-line arguments. Rejecting is deliberate:
    /// silently rewriting one would make `vortex server start x` fail to find what was just
    /// created.</summary>
    [Theory]
    [InlineData("")]
    [InlineData("../escape")]
    [InlineData("has space")]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData("semi;colon")]
    public void Dangerous_names_are_rejected(string name)
    {
        Assert.Throws<ArgumentException>(() => InstanceStore.ValidateName(name));
    }

    [Fact]
    public void Spec_round_trips_through_disk()
    {
        var store = new InstanceStore(Paths);
        var spec = new InstanceSpec
        {
            Name = "eu-1", Map = "stormkeep", Port = 26010, Gametype = "ctf",
            ContentSet = ["a".PadRight(64, 'b')],
        };

        store.Save(spec);
        var loaded = store.Load("eu-1");

        Assert.Equal(spec.Map, loaded!.Map);
        Assert.Equal(spec.Port, loaded.Port);
        Assert.Equal(ControlMode.Local, loaded.ControlMode);
        Assert.Equal(spec.ContentSet, loaded.ContentSet);
    }

    /// <summary>The default config exists because bot_join_empty is invisible until somebody wastes an
    /// afternoon on an empty server that will not fill.</summary>
    [Fact]
    public void Default_config_explains_bot_join_empty_and_eventlog()
    {
        var store = new InstanceStore(Paths);
        var spec = new InstanceSpec { Name = "eu-1", Map = "m", Port = 26010 };
        store.EnsureDefaultConfig(spec);

        var config = File.ReadAllText(store.PathsFor("eu-1").ConfigPath);
        Assert.Contains("bot_join_empty 1", config);
        Assert.Contains("sv_eventlog 1", config);
    }

    [Fact]
    public void Default_config_is_never_overwritten()
    {
        var store = new InstanceStore(Paths);
        var spec = new InstanceSpec { Name = "eu-1", Map = "m", Port = 26010 };
        store.EnsureDefaultConfig(spec);

        File.WriteAllText(store.PathsFor("eu-1").ConfigPath, "// operator edits");
        store.EnsureDefaultConfig(spec);

        Assert.Equal("// operator edits", File.ReadAllText(store.PathsFor("eu-1").ConfigPath));
    }

    [Fact]
    public void Port_pool_skips_ports_other_instances_hold()
    {
        var store = new InstanceStore(Paths);
        store.Save(new InstanceSpec { Name = "a", Map = "m", Port = 26000 });
        store.Save(new InstanceSpec { Name = "b", Map = "m", Port = 26001 });

        Assert.True(new PortPool(store).IsAssigned(26000));
        Assert.False(new PortPool(store).IsAssigned(26000, exceptInstance: "a"));
    }
}

public class Pk3GuardTests
{
    private static MemoryStream Pk3(params (string Path, string Content)[] entries)
    {
        var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
            foreach (var (path, content) in entries)
            {
                using var writer = new StreamWriter(zip.CreateEntry(path).Open());
                writer.Write(content);
            }
        buffer.Position = 0;
        return buffer;
    }

    [Fact]
    public void A_legacy_bsp_package_is_accepted()
    {
        using var pk3 = Pk3(("maps/stormkeep.bsp", "geometry"));
        var result = Pk3Guard.Inspect(pk3, pk3.Length);

        Assert.True(result.Ok);
        Assert.Equal(["stormkeep"], result.Maps);
        Assert.Equal("bsp", result.Format);
    }

    [Fact]
    public void A_vmap_package_with_caches_is_accepted()
    {
        using var pk3 = Pk3(("maps/stormkeep.vmap", "source"), ("maps/stormkeep.cache", "cache"));
        var result = Pk3Guard.Inspect(pk3, pk3.Length);

        Assert.True(result.Ok);
        Assert.Equal("vmap", result.Format);
    }

    /// <summary>vmap wins when both are present: a package shipping both is a legacy map with a
    /// converted sibling, and the newer format is what should be reported.</summary>
    [Fact]
    public void Vmap_wins_when_both_formats_are_present()
    {
        using var pk3 = Pk3(("maps/a.bsp", "x"), ("maps/a.vmap", "y"));
        Assert.Equal("vmap", Pk3Guard.Inspect(pk3, pk3.Length).Format);
    }

    [Fact]
    public void A_package_with_no_maps_is_refused()
    {
        using var pk3 = Pk3(("textures/wall.png", "x"));
        var result = Pk3Guard.Inspect(pk3, pk3.Length);

        Assert.False(result.Ok);
        Assert.Contains("not a map package", result.Error);
    }

    [Fact]
    public void Something_that_is_not_a_zip_is_refused()
    {
        using var junk = new MemoryStream("this is not a zip"u8.ToArray());
        Assert.False(Pk3Guard.Inspect(junk, junk.Length).Ok);
    }

    /// <summary>Path traversal is the standard way a zip writes outside where it was extracted, and a
    /// runner extracting into its own data directory is exactly the target.</summary>
    [Theory]
    [InlineData("../../etc/passwd")]
    [InlineData("maps/../../../escape.bsp")]
    [InlineData("/absolute/path.bsp")]
    [InlineData(@"C:\windows\system32\evil.bsp")]
    [InlineData("")]
    public void Escaping_paths_are_rejected(string path)
    {
        Assert.False(Pk3Guard.IsSafePath(path));
    }

    [Theory]
    [InlineData("maps/stormkeep.bsp")]
    [InlineData("sound/weapons/fire.ogg")]
    [InlineData("maps/sub/dir/a.bsp")]
    public void Ordinary_paths_are_allowed(string path)
    {
        Assert.True(Pk3Guard.IsSafePath(path));
    }

    [Fact]
    public void A_traversal_entry_fails_the_whole_package()
    {
        using var pk3 = Pk3(("maps/a.bsp", "x"), ("../../escape.txt", "y"));
        var result = Pk3Guard.Inspect(pk3, pk3.Length);

        Assert.False(result.Ok);
        Assert.Contains("escapes the archive root", result.Error);
    }

    /// <summary>A size cap alone does not catch this: highly compressible filler is small on disk and
    /// enormous once opened.</summary>
    [Fact]
    public void An_extreme_compression_ratio_is_refused()
    {
        using var pk3 = Pk3(("maps/a.bsp", new string('\0', 5_000_000)));
        var result = Pk3Guard.Inspect(pk3, compressedSize: 1024);

        Assert.False(result.Ok);
        Assert.Contains("compression ratio", result.Error);
    }
}

public class ContentFetcherTests : ScratchTest
{
    [Theory]
    [InlineData("abc")]
    [InlineData("ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789")]
    public void Only_lowercase_hex_sha256_is_accepted(string value)
    {
        Assert.False(ContentFetcher.IsSha256(value));
    }

    [Fact]
    public void A_well_formed_hash_is_accepted()
    {
        Assert.True(ContentFetcher.IsSha256(new string('a', 64)));
    }

    [Fact]
    public void Cache_path_shards_by_the_first_byte()
    {
        var fetcher = new ContentFetcher(Paths, new HttpClient());
        var sha = new string('a', 64);

        Assert.Contains(Path.Combine("aa", sha + ".pk3"), fetcher.CachePathFor(sha));
    }

    /// <summary>GC keeps whatever any instance still references, so two servers sharing a map pool do
    /// not fight over it.</summary>
    [Fact]
    public void Gc_keeps_packages_an_instance_still_references()
    {
        var paths = Paths;
        var store = new InstanceStore(paths);
        var wanted = new string('a', 64);
        var unwanted = new string('b', 64);

        store.Save(new InstanceSpec
        {
            Name = "eu-1", Map = "m", Port = 26010, ContentSet = [wanted],
        });

        var fetcher = new ContentFetcher(paths, new HttpClient());
        foreach (var sha in new[] { wanted, unwanted })
        {
            var path = fetcher.CachePathFor(sha);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "package");
        }

        var removed = fetcher.Gc(store);

        Assert.Equal([unwanted], removed);
        Assert.True(File.Exists(fetcher.CachePathFor(wanted)));
    }
}

/// <summary>The runner API, which both control planes reach through the same envelope. These cover
/// the control-mode arbitration, which is the part with a design behind it rather than a mapping.</summary>
public class CommandDispatcherTests : ScratchTest
{
    private (InstanceSupervisor Supervisor, CommandDispatcher Dispatcher) Build(ControlMode mode)
    {
        var paths = Paths;
        var store = new InstanceStore(paths);
        var builds = new BuildStore(paths);
        var supervisor = new InstanceSupervisor(store, builds);

        store.Save(new InstanceSpec
        {
            Name = "eu-1",
            Map = "stormkeep",
            Port = 26010,
            ControlMode = mode,
            ControllerUrl = mode == ControlMode.Orchestrated
                ? "https://conductor.vortexfps.org" : null,
            GrantedScopes = mode == ControlMode.Orchestrated ? Scopes.DefaultForAdoption : null,
            ControlledSince = mode == ControlMode.Orchestrated ? DateTimeOffset.UtcNow : null,
        });
        supervisor.LoadAndAdopt();

        return (supervisor, new CommandDispatcher(supervisor, builds));
    }

    private static CommandEnvelope Command(string method, string path, string? body = null) => new()
    {
        CommandId = Guid.NewGuid().ToString("n"),
        Method = method,
        Path = ManagementProtocol.ApiPrefix + path,
        Body = body,
        ActorId = "test",
    };

    /// <summary>The whole point of the mode: while orchestrated, the owner's plane is read-only. A 409
    /// here rather than a silent no-op is what lets a UI render the banner.</summary>
    [Theory]
    [InlineData("POST", "/instances/eu-1/start")]
    [InlineData("POST", "/instances/eu-1/restart")]
    [InlineData("POST", "/instances/eu-1/drain")]
    [InlineData("POST", "/instances/eu-1/exec")]
    [InlineData("PATCH", "/instances/eu-1")]
    [InlineData("DELETE", "/instances/eu-1")]
    public async Task Local_mutations_are_refused_while_orchestrated(string method, string path)
    {
        var (supervisor, dispatcher) = Build(ControlMode.Orchestrated);
        using var _ = supervisor;

        var body = method == "PATCH"
            ? ManagementProtocol.Serialize(new InstanceSpec { Name = "eu-1", Map = "x", Port = 26010 })
            : ManagementProtocol.Serialize(new { command = "status" });

        var result = await dispatcher.ExecuteAsync(
            Command(method, path, body), ControlOrigin.Local, default);

        Assert.Equal(ProtocolStatus.Conflict, result.Status);

        var error = ManagementProtocol.Deserialize<ApiError>(result.Body!);
        Assert.Equal(ApiErrorCodes.InstanceOrchestrated, error!.Code);

        // The 409 carries the controlling plane and both exits, so the UI does not need to
        // special-case every endpoint that can produce one.
        Assert.Equal("https://conductor.vortexfps.org", error.Orchestrated!.ControllerUrl);
        Assert.Equal("release", error.Orchestrated.ReleasePath);
        Assert.Equal("stop", error.Orchestrated.StopPath);
    }

    [Theory]
    [InlineData("/instances/eu-1/status")]
    [InlineData("/instances/eu-1/logs")]
    [InlineData("/instances/eu-1/audit")]
    [InlineData("/instances/eu-1")]
    public async Task Reads_are_allowed_while_orchestrated(string path)
    {
        var (supervisor, dispatcher) = Build(ControlMode.Orchestrated);
        using var _ = supervisor;

        var result = await dispatcher.ExecuteAsync(
            Command("GET", path), ControlOrigin.Local, default);

        Assert.Equal(ProtocolStatus.Ok, result.Status);
    }

    /// <summary>Release is the one route deliberately not gated on mode. It is the owner's exit, and
    /// gating it would be the bug.</summary>
    [Fact]
    public async Task Release_is_always_available_to_the_owner()
    {
        var (supervisor, dispatcher) = Build(ControlMode.Orchestrated);
        using var _ = supervisor;

        var result = await dispatcher.ExecuteAsync(
            Command("POST", "/instances/eu-1/release",
                ManagementProtocol.Serialize(new ReleaseRequest { When = ReleaseWhen.Now })),
            ControlOrigin.Local, default);

        Assert.Equal(ProtocolStatus.Ok, result.Status);
        Assert.Equal(ControlMode.Local, supervisor.Require("eu-1").Spec.ControlMode);
    }

    /// <summary>The mirror image: an orchestrator cannot operate an instance the owner holds.</summary>
    [Fact]
    public async Task Orchestrator_cannot_operate_a_local_instance()
    {
        var (supervisor, dispatcher) = Build(ControlMode.Local);
        using var _ = supervisor;

        var result = await dispatcher.ExecuteAsync(
            Command("POST", "/instances/eu-1/start"), ControlOrigin.Orchestrator, default);

        Assert.NotEqual(ProtocolStatus.Ok, result.Status);
    }

    /// <summary>Control fields are runner state. Accepting them from a spec edit would let a PATCH
    /// hand the box to an orchestrator, or take it back without the release path that raises the
    /// alert.</summary>
    [Fact]
    public async Task A_spec_patch_cannot_change_control_mode()
    {
        var (supervisor, dispatcher) = Build(ControlMode.Local);
        using var _ = supervisor;

        await dispatcher.ExecuteAsync(
            Command("PATCH", "/instances/eu-1", ManagementProtocol.Serialize(new InstanceSpec
            {
                Name = "eu-1", Map = "aerowalk", Port = 26010,
                ControlMode = ControlMode.Orchestrated,
                ControllerUrl = "https://attacker.example",
            })),
            ControlOrigin.Local, default);

        var spec = supervisor.Require("eu-1").Spec;
        Assert.Equal(ControlMode.Local, spec.ControlMode);
        Assert.Null(spec.ControllerUrl);
        Assert.Equal("aerowalk", spec.Map); // the legitimate part of the edit still applied
    }

    [Fact]
    public async Task An_unknown_instance_is_a_404()
    {
        var (supervisor, dispatcher) = Build(ControlMode.Local);
        using var _ = supervisor;

        var result = await dispatcher.ExecuteAsync(
            Command("GET", "/instances/nope/status"), ControlOrigin.Local, default);

        Assert.Equal(ProtocolStatus.NotFound, result.Status);
    }
}

public class ControlEventTests
{
    private static ControlEvent Event(int players, bool matchLive) => new()
    {
        EventId = "e", RunnerId = "r", InstanceName = "eu-1",
        Kind = ControlEventKind.Released, PlayersConnected = players, MatchLive = matchLive,
        Initiator = "owner", Timestamp = DateTimeOffset.UtcNow,
    };

    /// <summary>Critical is players in a live match and nothing else. Widening it would bury the case
    /// the mechanism exists for; narrowing it would miss it.</summary>
    [Fact]
    public void Players_in_a_live_match_is_critical()
    {
        Assert.Equal(AlertSeverity.Critical, ControlEventSeverity.For(Event(12, matchLive: true)));
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(12, false)]
    [InlineData(0, false)]
    public void Everything_else_is_a_warning(int players, bool matchLive)
    {
        Assert.Equal(AlertSeverity.Warning, ControlEventSeverity.For(Event(players, matchLive)));
    }
}
