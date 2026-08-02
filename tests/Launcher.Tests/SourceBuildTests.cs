using Launcher.Core;
using Launcher.Core.Instances;
using Launcher.Desktop.ViewModels;
using Launcher.Protocol;
using Xunit;

namespace Launcher.Tests;

/// <summary>The vx side of a source build: finding it, reading what it says, and staying correct on a
/// ref that predates it.
///
/// None of these run a build. A build needs a clone, a Godot editor and twenty minutes, so what is
/// pinned here is the reasoning around it — which is also where the mistakes are.</summary>
public class VxTests : ScratchTest
{
    /// <summary>vx landed in the game repo in 2026-08; the launcher builds arbitrary refs, including
    /// older ones. Find returning null rather than throwing is what lets every call site keep the
    /// script it used before as its fallback.</summary>
    [Fact]
    public void A_checkout_without_vx_is_not_an_error()
    {
        Directory.CreateDirectory(Tmp);
        Assert.Null(Vx.Find(Tmp));
    }

    [Fact]
    public void A_checkout_with_vx_is_found()
    {
        Directory.CreateDirectory(Tmp);
        File.WriteAllText(Path.Combine(Tmp, OperatingSystem.IsWindows() ? "vx.cmd" : "vx"), "");

        var vx = Vx.Find(Tmp);

        Assert.NotNull(vx);
        Assert.Equal(Tmp, Path.GetDirectoryName(vx.ShimPath));
    }

    /// <summary>The shim is named by its full path, including when the path has a space in it.
    ///
    /// A bare `vx.cmd` relying on the working directory looked tidier and was wrong: Windows'
    /// NoDefaultCurrentDirectoryInExePath stops cmd.exe resolving against the current directory, and it
    /// is set on this project's dev box, so the relative form failed there while working elsewhere.</summary>
    [Fact]
    public void The_command_names_the_shim_by_full_path()
    {
        var checkout = Path.Combine(Tmp, "a directory with spaces");
        Directory.CreateDirectory(checkout);
        File.WriteAllText(Path.Combine(checkout, OperatingSystem.IsWindows() ? "vx.cmd" : "vx"), "");

        var (exe, args) = Vx.Find(checkout)!.Command("engine", "--only", "windows");

        Assert.Contains(args, a => a.StartsWith(checkout, StringComparison.Ordinal));
        Assert.EndsWith(OperatingSystem.IsWindows() ? "cmd.exe" : "sh", exe, StringComparison.Ordinal);
        Assert.Equal(["engine", "--only", "windows"], args.TakeLast(3));

        // The shim path is the only thing on the line that can need quoting, which is what keeps
        // cmd.exe's /c rule on the side where it preserves the quotes.
        Assert.Single(args, a => a.Contains(' '));
    }

    /// <summary>The constraint that makes the quoting above safe, enforced rather than hoped for.</summary>
    [Fact]
    public void An_argument_with_a_space_is_refused_rather_than_misquoted()
    {
        Directory.CreateDirectory(Tmp);
        File.WriteAllText(Path.Combine(Tmp, OperatingSystem.IsWindows() ? "vx.cmd" : "vx"), "");

        var failure = Assert.Throws<SourceBuildException>(
            () => Vx.Find(Tmp)!.Command("engine", "--only", "two words"));

        Assert.Contains("space or a quote", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_doctor_envelope_is_read()
    {
        var report = Vx.Parse("""
            {
              "schema": 1,
              "command": "doctor",
              "ok": false,
              "checks": [
                { "name": ".NET SDK", "status": "ok", "detail": "8.0.100", "required": true, "fix": null },
                { "name": "Godot 4.6.3 (mono)", "status": "warn", "detail": "not found",
                  "required": false, "fix": "./vx setup" }
              ]
            }
            """);

        Assert.NotNull(report);
        Assert.False(report.Ok);
        Assert.Null(report.UnsupportedSchema);
        Assert.Equal(2, report.Checks.Count);
        Assert.Equal("warn", report.Checks[1].Status);
        Assert.Equal("./vx setup", report.Checks[1].Fix);
        Assert.True(report.Checks[0].Required);
    }

    /// <summary>A schema the launcher does not read must not come back looking like a clean bill of
    /// health. vx treats a breaking change to this envelope as a breaking change precisely because
    /// something across a repo boundary reads it; silently parsing an unknown version would waste that.</summary>
    [Fact]
    public void A_schema_the_launcher_does_not_read_is_reported_rather_than_guessed()
    {
        var report = Vx.Parse("""
            {"schema": 99, "ok": true, "checks": [{"name": "something new", "status": "ok"}]}
            """);

        Assert.NotNull(report);
        Assert.Equal(99, report.UnsupportedSchema);
        Assert.Empty(report.Checks);
        Assert.False(report.Ok);
    }

    /// <summary>Output that is not an envelope means the launcher's own checks stand alone, not that a
    /// preflight fails. The doctor pass is additive.</summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("vx: building the task runner…")]
    [InlineData("[1, 2, 3]")]
    [InlineData("{\"schema\": 1, ")]
    public void Anything_that_is_not_an_envelope_reads_as_no_report(string stdout) =>
        Assert.Null(Vx.Parse(stdout));

    /// <summary>Missing checks, or a checks entry with no name, is not a parse failure: the envelope is
    /// still an envelope and its verdict is still worth having.</summary>
    [Fact]
    public void An_envelope_with_no_usable_checks_still_reports_its_verdict()
    {
        var report = Vx.Parse("""{"schema": 1, "ok": true}""");

        Assert.NotNull(report);
        Assert.True(report.Ok);
        Assert.Empty(report.Checks);
    }
}

public class GodotResolutionTests : ScratchTest
{
    /// <summary>The checkout's own .godot-bin/ is probed, and ahead of PATH.
    ///
    /// That order is the point rather than an accident: two checkouts at two refs can pin two engine
    /// versions, and only a per-checkout install can express that. It also matches where `vx setup`
    /// puts an engine and where the game repo's own find-godot.sh looks.</summary>
    [Fact]
    public void The_checkouts_own_engine_is_found()
    {
        var checkout = Path.Combine(Tmp, "checkout");
        var bin = Path.Combine(checkout, ".godot-bin");
        Directory.CreateDirectory(bin);

        var name = OperatingSystem.IsWindows() ? "godot_console.exe" : "godot";
        File.WriteAllText(Path.Combine(bin, name), "");

        // Resolve probes --version and refuses what cannot answer, so an empty file cannot come back
        // as a usable editor. What is asserted is which path it CHOSE, which the refusal names.
        var failure = Assert.Throws<SourceBuildException>(
            () => GodotEditor.Resolve(null, checkout));

        Assert.Equal(SourceFailure.EditorUnusable, failure.Code);
        Assert.Contains(".godot-bin", failure.Message, StringComparison.Ordinal);
    }

    /// <summary>With no checkout-local engine the resolver carries on to PATH and the platform
    /// locations, so passing a checkout never makes discovery worse than not passing one.</summary>
    [Fact]
    public void An_empty_godot_bin_does_not_stop_the_search()
    {
        var checkout = Path.Combine(Tmp, "checkout");
        Directory.CreateDirectory(Path.Combine(checkout, ".godot-bin"));

        // Either it found a real editor on this box or it reported none. Both are correct; what would
        // be wrong is the empty directory being treated as an answer.
        try
        {
            var editor = GodotEditor.Resolve(null, checkout);
            Assert.DoesNotContain(".godot-bin", editor.Path, StringComparison.Ordinal);
        }
        catch (SourceBuildException ex)
        {
            Assert.Equal(SourceFailure.EditorMissing, ex.Code);
        }
    }
}

public class SourcePresetTests
{
    /// <summary>Every preset a picker offers can also be staged. A preset with no manifest platform key
    /// is refused by the build, and finding that out after an export is an expensive way to learn it.</summary>
    [Fact]
    public void Every_known_preset_maps_to_a_platform_key()
    {
        Assert.NotEmpty(SourceProvider.KnownPresets);

        foreach (var preset in SourceProvider.KnownPresets)
            Assert.NotNull(SourceProvider.PlatformKeyForPreset(preset));
    }

    [Fact]
    public void The_default_preset_is_one_of_the_known_ones() =>
        Assert.Contains(SourceProvider.DefaultPreset(), SourceProvider.KnownPresets);
}

/// <summary>The desktop sheet's view model, which the window builds in its own constructor — so
/// anything that throws in here does not break a screen, it stops the launcher starting.</summary>
public class SourceBuildViewModelTests : ScratchTest
{
    [Fact]
    public void It_constructs_and_opens_against_a_root_that_does_not_exist_yet()
    {
        var vm = new SourceBuildViewModel(Paths);

        vm.Open();

        Assert.True(vm.IsOpen);
        Assert.Empty(vm.KnownSources);
        Assert.Contains(vm.SelectedPreset, SourceProvider.KnownPresets);
        Assert.False(string.IsNullOrWhiteSpace(vm.Repo));
    }

    /// <summary>Opening lists what is configured, and picking one fills the form from it.</summary>
    [Fact]
    public void Picking_a_configured_source_loads_its_settings()
    {
        new SourceStore(Paths).Save(new SourceSpec
        {
            Name = "fork",
            Repo = "https://example.invalid/fork.git",
            Ref = "experiment",
            Target = "linux-dedicated",
        });

        var vm = new SourceBuildViewModel(Paths);
        vm.Open();

        Assert.Equal(["fork"], vm.KnownSources);

        vm.PickedSource = "fork";

        Assert.Equal("fork", vm.SourceName);
        Assert.Equal("https://example.invalid/fork.git", vm.Repo);
        Assert.Equal("experiment", vm.Reference);
        Assert.Equal("linux-dedicated", vm.SelectedPreset);
    }

    /// <summary>A name that is not yet a source is how one gets created, so typing it must not be
    /// undone. The picker and the name box bind to different properties for exactly this reason: a
    /// ComboBox writes null back when its bound value is not in its list.</summary>
    [Fact]
    public void Typing_a_name_that_does_not_exist_keeps_what_was_typed()
    {
        var vm = new SourceBuildViewModel(Paths);
        vm.Open();

        vm.Repo = "https://example.invalid/new.git";
        vm.SourceName = "brand-new";

        Assert.Equal("brand-new", vm.SourceName);
        Assert.Equal("https://example.invalid/new.git", vm.Repo);
    }
}

public class SourceBuildJobTests : ScratchTest
{
    /// <summary>Nothing has run, so there is nothing to report — as distinct from reporting an idle
    /// job, which a poller would read as a build that finished.</summary>
    [Fact]
    public void A_runner_that_has_built_nothing_reports_no_job() =>
        Assert.Null(new SourceBuildJobs(Paths).Current);

    [Fact]
    public void Cancelling_nothing_is_false() =>
        Assert.False(new SourceBuildJobs(Paths).Cancel());
}

/// <summary>The source routes on the runner API, and the refusal that guards them.</summary>
public class SourceRouteTests : ScratchTest
{
    /// <summary>The security property this whole route family turns on.
    ///
    /// A source build clones a repository named in the request and compiles it here. Exposed to an
    /// orchestrator that is arbitrary code execution addressed by URL, on a box a community operator
    /// lent to a network — categorically unlike the instance routes, which only ever start a binary
    /// already in the build store.</summary>
    [Theory]
    [InlineData(ProtocolMethods.Post, "/api/v1/sources")]
    [InlineData(ProtocolMethods.Post, "/api/v1/sources/game/build")]
    [InlineData(ProtocolMethods.Post, "/api/v1/sources/game/build/cancel")]
    [InlineData(ProtocolMethods.Delete, "/api/v1/sources/game")]
    public async Task An_orchestrator_cannot_start_or_change_a_source_build(string method, string path)
    {
        var (supervisor, dispatcher) = Runner();
        using var _ = supervisor;

        var result = await dispatcher.ExecuteAsync(Command(method, path),
            ControlOrigin.Orchestrator, default);

        Assert.Equal(ProtocolStatus.Forbidden, result.Status);
        Assert.Equal(ApiErrorCodes.ScopeDenied, Error(result).Code);
    }

    /// <summary>Reads stay open to both planes. An orchestrator that could not tell a busy box from an
    /// idle one would schedule onto a machine already spending every core on an export.</summary>
    [Fact]
    public async Task An_orchestrator_may_still_read()
    {
        var (supervisor, dispatcher) = Runner();
        using var _ = supervisor;

        var result = await dispatcher.ExecuteAsync(
            Command(ProtocolMethods.Get, "/api/v1/sources"), ControlOrigin.Orchestrator, default);

        Assert.Equal(ProtocolStatus.Ok, result.Status);
    }

    [Fact]
    public async Task A_source_is_created_then_listed_and_updated_in_place()
    {
        var (supervisor, dispatcher) = Runner();
        using var _ = supervisor;

        var created = await dispatcher.ExecuteAsync(
            Command(ProtocolMethods.Post, "/api/v1/sources",
                """{"name": "game", "repo": "https://example.invalid/game.git", "ref": "main"}"""),
            ControlOrigin.Local, default);

        Assert.Equal(ProtocolStatus.Created, created.Status);

        // Only the ref. The repo must survive: a verb that reset the fields it was not given would
        // make changing a ref a two-field operation nobody would remember to complete.
        var updated = await dispatcher.ExecuteAsync(
            Command(ProtocolMethods.Post, "/api/v1/sources", """{"name": "game", "ref": "v1.2"}"""),
            ControlOrigin.Local, default);

        Assert.Equal(ProtocolStatus.Ok, updated.Status);

        var spec = new SourceStore(Paths).Get("game");
        Assert.NotNull(spec);
        Assert.Equal("v1.2", spec.Ref);
        Assert.Equal("https://example.invalid/game.git", spec.Repo);
    }

    /// <summary>A name that cannot become a directory is refused rather than rewritten, on the same
    /// rule instance names follow.</summary>
    [Fact]
    public async Task A_name_that_is_not_a_safe_directory_is_refused()
    {
        var (supervisor, dispatcher) = Runner();
        using var _ = supervisor;

        var result = await dispatcher.ExecuteAsync(
            Command(ProtocolMethods.Post, "/api/v1/sources", """{"name": "../escape"}"""),
            ControlOrigin.Local, default);

        Assert.Equal(ProtocolStatus.BadRequest, result.Status);
    }

    [Fact]
    public async Task Building_a_source_that_does_not_exist_is_a_404()
    {
        var (supervisor, dispatcher) = Runner();
        using var _ = supervisor;

        var result = await dispatcher.ExecuteAsync(
            Command(ProtocolMethods.Post, "/api/v1/sources/nope/build"), ControlOrigin.Local, default);

        Assert.Equal(ProtocolStatus.NotFound, result.Status);
    }

    /// <summary>Deleting a source deliberately leaves the checkout: the spec is four fields and the
    /// checkout is gigabytes.</summary>
    [Fact]
    public async Task Removing_a_source_keeps_its_checkout()
    {
        var (supervisor, dispatcher) = Runner();
        using var _ = supervisor;

        await dispatcher.ExecuteAsync(
            Command(ProtocolMethods.Post, "/api/v1/sources", """{"name": "game"}"""),
            ControlOrigin.Local, default);

        var checkout = new SourceProvider(Paths, new BuildStore(Paths)).CheckoutFor("game");
        Directory.CreateDirectory(checkout);

        var removed = await dispatcher.ExecuteAsync(
            Command(ProtocolMethods.Delete, "/api/v1/sources/game"), ControlOrigin.Local, default);

        Assert.Equal(ProtocolStatus.NoContent, removed.Status);
        Assert.Null(new SourceStore(Paths).Get("game"));
        Assert.True(Directory.Exists(checkout));
    }

    /// <summary>A dispatcher built without an install root serves every other verb and answers these
    /// 404 — rather than failing to start, which would make an optional feature a startup dependency.</summary>
    [Fact]
    public async Task Without_an_install_root_the_source_routes_are_absent_but_the_runner_lives()
    {
        var paths = Paths;
        var store = new InstanceStore(paths);
        var builds = new BuildStore(paths);
        using var supervisor = new InstanceSupervisor(store, builds);
        var dispatcher = new CommandDispatcher(supervisor, builds);

        var sources = await dispatcher.ExecuteAsync(
            Command(ProtocolMethods.Get, "/api/v1/sources"), ControlOrigin.Local, default);
        Assert.Equal(ProtocolStatus.NotFound, sources.Status);

        var runner = await dispatcher.ExecuteAsync(
            Command(ProtocolMethods.Get, "/api/v1/runner/status"), ControlOrigin.Local, default);
        Assert.Equal(ProtocolStatus.Ok, runner.Status);
    }

    private (InstanceSupervisor Supervisor, CommandDispatcher Dispatcher) Runner()
    {
        var paths = Paths;
        var builds = new BuildStore(paths);
        var supervisor = new InstanceSupervisor(new InstanceStore(paths), builds);
        return (supervisor, new CommandDispatcher(supervisor, builds, paths: paths));
    }

    private static ApiError Error(CommandResult result) =>
        ManagementProtocol.Deserialize<ApiError>(result.Body!)!;

    private static CommandEnvelope Command(string method, string path, string? body = null) => new()
    {
        CommandId = Guid.NewGuid().ToString("n"),
        Method = method,
        Path = path,
        Body = body,
        ActorId = "source-test",
    };
}
