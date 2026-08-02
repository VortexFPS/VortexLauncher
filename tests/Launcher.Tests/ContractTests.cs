using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using Launcher.Core;
using Launcher.Core.Instances;
using Launcher.Protocol;
using Xunit;
using YamlDotNet.RepresentationModel;

namespace Launcher.Tests;

/// <summary>protocol/runner-api-v1.yaml, read as data.
///
/// The spec is the contract two separate control planes code against — the host owner's WebServer and
/// Conductor, which operates runners on boxes it does not own — and neither of them can see this
/// source tree. Nothing else in the build compares the two, so they drift silently until a call fails
/// on somebody's machine.
///
/// The parser is a package reference in the test project and nowhere else: Launcher.Core and
/// Launcher.Protocol are BCL-only by rule, which ArchitectureTests enforces, and reading the contract
/// as data is a test's job rather than a runner's.</summary>
internal static class Contract
{
    private static readonly Lazy<YamlMappingNode> Document = new(Load);

    public static YamlMappingNode Root => Document.Value;

    public static string SpecPath => Path.Combine(RepoRoot(), "protocol", "runner-api-v1.yaml");

    /// <summary>The dispatcher's routes are read out of its source. There is no route table to
    /// enumerate at runtime: the routing is a pattern match, so the file is the table.</summary>
    public static string DispatcherPath =>
        Path.Combine(RepoRoot(), "src", "Launcher.Core", "Instances", "RunnerLink.cs");

    private static YamlMappingNode Load()
    {
        if (!File.Exists(SpecPath))
            throw new FileNotFoundException($"the runner API contract is missing: {SpecPath}");

        var yaml = new YamlStream();
        yaml.Load(new StringReader(File.ReadAllText(SpecPath)));
        return (YamlMappingNode)yaml.Documents[0].RootNode;
    }

    public static YamlNode? Child(YamlNode? node, string key) =>
        node is YamlMappingNode map
            ? map.Children.FirstOrDefault(pair => (pair.Key as YamlScalarNode)?.Value == key).Value
            : null;

    public static YamlNode Require(YamlNode? node, params string[] keys)
    {
        var current = node;
        foreach (var key in keys)
            current = Child(current, key)
                ?? throw new InvalidOperationException(
                    $"runner-api-v1.yaml has no {string.Join(" -> ", keys)}");
        return current!;
    }

    public static string? Scalar(YamlNode? node, string key) => (Child(node, key) as YamlScalarNode)?.Value;

    public static IEnumerable<string> Keys(YamlNode? node) =>
        node is YamlMappingNode map
            ? map.Children.Select(pair => ((YamlScalarNode)pair.Key).Value!)
            : [];

    public static YamlNode Schema(string name) => Require(Root, "components", "schemas", name);

    public static YamlNode Property(string schema, string property) =>
        Require(Schema(schema), "properties", property);

    public static YamlNode RequestBody(string path, string method) =>
        Require(Root, "paths", path, method, "requestBody", "content", "application/json", "schema");

    public static IEnumerable<string> Properties(YamlNode schema) => Keys(Child(schema, "properties"));

    public static IEnumerable<string> EnumValues(YamlNode schema) =>
        Child(schema, "enum") is YamlSequenceNode values
            ? values.Children.OfType<YamlScalarNode>().Select(value => value.Value!)
            : [];

    public static IEnumerable<string> Paths() => Keys(Require(Root, "paths"));

    private static readonly string[] Methods = ["get", "post", "patch", "put", "delete"];

    /// <summary>Every operation the contract documents, as the dispatcher would receive it.</summary>
    public static IEnumerable<(string Method, string Path)> Operations()
    {
        foreach (var (key, operations) in ((YamlMappingNode)Require(Root, "paths")).Children)
        {
            var path = ((YamlScalarNode)key).Value!;
            foreach (var method in Keys(operations).Where(name => Methods.Contains(name)))
                yield return (method.ToUpperInvariant(), path);
        }
    }

    /// <summary>Both sides of a closed set, named and diffed, because "expected 11, got 10" from a
    /// bare collection compare is not enough to act on.</summary>
    public static void AssertSameSet(string what, IEnumerable<string> documented,
        IEnumerable<string> implemented)
    {
        var spec = documented.ToArray();
        var code = implemented.ToArray();

        var undocumented = code.Except(spec, StringComparer.Ordinal).Order(StringComparer.Ordinal);
        var unimplemented = spec.Except(code, StringComparer.Ordinal).Order(StringComparer.Ordinal);

        var problems = new List<string>();
        if (undocumented.Any())
            problems.Add($"in the code, absent from the spec: {string.Join(", ", undocumented)}");
        if (unimplemented.Any())
            problems.Add($"in the spec, absent from the code: {string.Join(", ", unimplemented)}");

        Assert.True(problems.Count == 0, $"{what} disagrees: {string.Join("; ", problems)}");
    }

    private static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "VortexLauncher.sln")))
                return dir.FullName;
        throw new InvalidOperationException(
            $"VortexLauncher.sln not found above {AppContext.BaseDirectory}");
    }
}

/// <summary>Every documented endpoint, run against a real dispatcher.</summary>
public class RouteContractTests : ScratchTest
{
    private const string Instance = "eu-1";

    /// <summary>Only the runner API. /healthz is the control plane's own liveness — unauthenticated,
    /// served by the plane to a browser, and never tunneled to a runner — so a dispatcher that does
    /// not route it is correct rather than incomplete.</summary>
    public static TheoryData<string, string> DocumentedRoutes()
    {
        var routes = new TheoryData<string, string>();
        foreach (var (method, path) in Contract.Operations())
            if (path.StartsWith(ManagementProtocol.ApiPrefix, StringComparison.Ordinal))
                routes.Add(method, path);
        return routes;
    }

    /// <summary>A documented endpoint that no runner routes is a lie to whoever implements against
    /// the spec. Their call compiles, ships, and comes back 404 from a box they cannot debug.</summary>
    [Theory]
    [MemberData(nameof(DocumentedRoutes))]
    public async Task Every_documented_endpoint_is_routed_by_the_dispatcher(string method, string path)
    {
        var (supervisor, dispatcher) = Runner();
        using var _ = supervisor;

        var result = await dispatcher.ExecuteAsync(
            Command(method, path.Replace("{name}", Instance), BodyFor(method, path)),
            ControlOrigin.Local, default);

        Assert.False(Unrouted(result),
            $"{method} {path} is documented but CommandDispatcher has no route for it");
    }

    /// <summary>The check above is only worth something if a missing route is detectable at all. If
    /// the dispatcher ever answers an unknown path some other way, every documented endpoint would
    /// pass whether or not it existed.</summary>
    [Fact]
    public async Task A_path_the_dispatcher_does_not_know_is_reported_as_unrouted()
    {
        var (supervisor, dispatcher) = Runner();
        using var _ = supervisor;

        var result = await dispatcher.ExecuteAsync(
            Command(ProtocolMethods.Post,
                $"{ManagementProtocol.ApiPrefix}/instances/{Instance}/teleport"),
            ControlOrigin.Local, default);

        Assert.True(Unrouted(result));
    }

    /// <summary>An unknown route is a 404 whose body says so. Anything else — a refusal, or a failure
    /// deeper in the supervisor because there is no build installed and no process running — means
    /// the route exists, which is all this asks.</summary>
    private static bool Unrouted(CommandResult result)
    {
        if (result.Status != ProtocolStatus.NotFound || result.Body is null)
            return false;

        var error = ManagementProtocol.Deserialize<ApiError>(result.Body);
        return error?.Message.StartsWith("no route for", StringComparison.Ordinal) == true;
    }

    /// <summary>Bodies for the operations the spec marks required, so a route is not mistaken for
    /// missing when it was only sent nothing to work with.</summary>
    private static string? BodyFor(string method, string path)
    {
        if (method == ProtocolMethods.Post && path.EndsWith("/instances", StringComparison.Ordinal))
            return ManagementProtocol.Serialize(
                new InstanceSpec { Name = "contract-probe", Map = "stormkeep", Port = 26099 });

        // Ahead of the spec case below: server.cfg is the other PATCH on an instance, and it takes a
        // document rather than a spec. Sending the wrong one still proves the route exists, which is
        // all this asks, but it would prove it by being refused.
        if (path.EndsWith("/config", StringComparison.Ordinal))
            return ManagementProtocol.Serialize(new { text = "hostname \"contract\"\n" });

        if (method == ProtocolMethods.Patch)
            return ManagementProtocol.Serialize(
                new InstanceSpec { Name = Instance, Map = "stormkeep", Port = 26010 });

        if (path.EndsWith("/exec", StringComparison.Ordinal))
            return ManagementProtocol.Serialize(new { command = "status" });

        // Bounded: the instance is not running, so drain returns at once, but a default 300s timeout
        // sitting in a test is a five minute hang waiting for a mistake elsewhere.
        if (path.EndsWith("/drain", StringComparison.Ordinal))
            return ManagementProtocol.Serialize(new DrainRequest { TimeoutSeconds = 1 });

        if (path.EndsWith("/release", StringComparison.Ordinal))
            return ManagementProtocol.Serialize(new ReleaseRequest { When = ReleaseWhen.Now });

        return null;
    }

    private (InstanceSupervisor Supervisor, CommandDispatcher Dispatcher) Runner()
    {
        var paths = Paths;
        var store = new InstanceStore(paths);
        var builds = new BuildStore(paths);
        var supervisor = new InstanceSupervisor(store, builds);

        store.Save(new InstanceSpec { Name = Instance, Map = "stormkeep", Port = 26010 });
        supervisor.LoadAndAdopt();

        // paths is passed so the /sources routes are actually reached. Without it they answer 404 for
        // having no install root, which is not the "no route for" 404 this suite looks for — so every
        // source endpoint would pass whether or not it was ever routed.
        return (supervisor,
            new CommandDispatcher(supervisor, builds, new ContentFetcher(paths, new HttpClient()),
                paths: paths));
    }

    private static CommandEnvelope Command(string method, string path, string? body = null) => new()
    {
        CommandId = Guid.NewGuid().ToString("n"),
        Method = method,
        Path = path,
        Body = body,
        ActorId = "contract-test",
    };
}

/// <summary>The other direction: what the dispatcher routes, checked against what is written down.
///
/// An undocumented endpoint is worse than a missing one. A missing one fails loudly for whoever calls
/// it; an undocumented one simply never gets called, because the other control plane has no way to
/// know it is there, and the feature quietly exists on one plane only.</summary>
public class DispatcherCoverageTests
{
    private static readonly string Source = File.ReadAllText(Contract.DispatcherPath);

    /// <summary>`segments is ["instances", var name, ..]` and its siblings, turned back into paths.
    /// A literal is a segment, an `or` of literals is an alias, `var name` is the parameter, and a
    /// `..` slice delegates to the per-instance actions below.</summary>
    private static IEnumerable<string> TopLevelRoutes()
    {
        foreach (Match match in Regex.Matches(Source, @"segments is \[(?<list>[^\]]*)\]"))
        {
            IEnumerable<string> paths = [ManagementProtocol.ApiPrefix];

            foreach (var element in match.Groups["list"].Value.Split(','))
            {
                var literals = Regex.Matches(element, "\"(?<segment>[^\"]+)\"")
                    .Select(literal => literal.Groups["segment"].Value)
                    .ToArray();

                if (literals.Length > 0)
                    paths = paths.SelectMany(p => literals.Select(l => $"{p}/{l}")).ToList();
                else if (element.Contains("var ", StringComparison.Ordinal))
                    paths = paths.Select(p => p + "/{name}").ToList();
            }

            foreach (var path in paths)
                yield return path;
        }
    }

    /// <summary>`case ("logs", _):` — the actions on one instance. Deliberately wider than the
    /// segments in use today: an action named `map_pool` or `contentV2` that the scan skipped would be
    /// an undocumented route this test reported as absent rather than found.</summary>
    private static IEnumerable<string> InstanceActions() =>
        Regex.Matches(Source, @"case \(""(?<action>[A-Za-z0-9_-]+)"",")
            .Select(match => match.Groups["action"].Value)
            .Distinct(StringComparer.Ordinal);

    /// <summary>`case (null, "PATCH"):` — the methods on the instance itself.</summary>
    private static IEnumerable<string> BareInstanceMethods() =>
        Regex.Matches(Source, @"case \(null, ""(?<method>[A-Z]+)""\)")
            .Select(match => match.Groups["method"].Value)
            .Distinct(StringComparer.Ordinal);

    [Fact]
    public void Every_top_level_route_the_dispatcher_handles_is_documented()
    {
        var routes = TopLevelRoutes().ToArray();

        // Read out of source, so a refactor that defeats the scan would otherwise pass by finding
        // nothing at all to check.
        Assert.Contains($"{ManagementProtocol.ApiPrefix}/instances", routes);
        Assert.Contains($"{ManagementProtocol.ApiPrefix}/builds", routes);

        var undocumented = routes.Except(Contract.Paths(), StringComparer.Ordinal).Order();
        Assert.True(!undocumented.Any(),
            $"CommandDispatcher routes {string.Join(", ", undocumented)}, " +
            "which runner-api-v1.yaml does not document");
    }

    [Fact]
    public void Every_instance_action_the_dispatcher_handles_is_documented()
    {
        var actions = InstanceActions().ToArray();

        Assert.Contains("start", actions);
        Assert.Contains("release", actions);

        var undocumented = actions
            .Select(action => $"{ManagementProtocol.ApiPrefix}/instances/{{name}}/{action}")
            .Except(Contract.Paths(), StringComparer.Ordinal)
            .Order();

        Assert.True(!undocumented.Any(),
            $"CommandDispatcher routes {string.Join(", ", undocumented)}, " +
            "which runner-api-v1.yaml does not document");
    }

    /// <summary>The dispatcher ignores the method on an action route — a GET and a POST to /start do
    /// the same thing — so paths are what the two sides have to agree on there. The instance itself
    /// is the exception: read, edit and delete are three different operations on one path, and each
    /// has to be written down separately.</summary>
    [Fact]
    public void The_instance_route_documents_every_method_it_handles()
    {
        var handled = BareInstanceMethods().ToArray();
        Assert.Contains(ProtocolMethods.Delete, handled);

        var documented = Contract.Operations()
            .Where(op => op.Path == $"{ManagementProtocol.ApiPrefix}/instances/{{name}}")
            .Select(op => op.Method)
            .ToArray();

        foreach (var method in handled)
            Assert.True(documented.Contains(method, StringComparer.Ordinal),
                $"CommandDispatcher handles {method} on an instance, which the spec does not document");
    }
}

/// <summary>Schema property names against the JSON the DTOs actually produce.
///
/// Serialized rather than reflected over, because the names on the wire come from a naming policy
/// rather than from the property names, and the drift worth catching is a field renamed in code while
/// the spec keeps the old name — which a plane experiences as a value that is silently always null.
///
/// The samples are fully populated on purpose: nulls are dropped on the wire, so a half-filled DTO
/// would hide exactly the fields most likely to have moved. A nullable field added to a DTO has to be
/// added to the sample here too, or it reads as documented-but-never-sent.</summary>
public class SchemaContractTests
{
    [Theory]
    [InlineData("InstanceSpec")]
    [InlineData("InstanceStatus")]
    [InlineData("RunnerStatus")]
    [InlineData("LogLine")]
    [InlineData("BuildSummary")]
    [InlineData("ContentPackage")]
    [InlineData("ApiError")]
    [InlineData("ApiError.orchestrated")]
    [InlineData("drain request")]
    [InlineData("release request")]
    public void Documented_properties_are_the_ones_that_go_on_the_wire(string name)
    {
        var (dto, schema) = Case(name);
        var wire = WireKeys(dto).ToArray();

        // The sample has to be complete before the comparison below means anything. A property left
        // null is dropped on the wire, so a field added to a DTO and to neither the sample nor the
        // spec would agree with the spec by being invisible to both — this test reporting safety it
        // is not providing, which is the one outcome worse than not having it.
        var unset = AllWireNames(dto.GetType()).Except(wire, StringComparer.Ordinal)
            .Order(StringComparer.Ordinal);
        Assert.True(!unset.Any(),
            $"the {name} sample leaves {string.Join(", ", unset)} null, so this test cannot see " +
            "whether the spec documents them; populate the sample");

        Contract.AssertSameSet(name, Contract.Properties(schema), wire);
    }

    /// <summary>The exec body is the one request shape whose type is private to the dispatcher, so it
    /// is checked through the naming policy instead of by serializing an instance. It is also the
    /// most-used endpoint in the panel, and the failure is quiet: a property the runner cannot find
    /// deserializes to null rather than erroring, so a renamed field becomes a console that accepts
    /// every command and runs none of them.</summary>
    [Fact]
    public void The_exec_body_matches_the_record_the_dispatcher_reads()
    {
        var request = typeof(CommandDispatcher)
            .GetNestedType("ExecRequest", BindingFlags.NonPublic | BindingFlags.Public);
        Assert.True(request is not null,
            "CommandDispatcher no longer has an ExecRequest; point this test at whatever replaced it");

        var policy = ManagementProtocol.Json.PropertyNamingPolicy!;
        var wire = request!.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(property => policy.ConvertName(property.Name));

        Contract.AssertSameSet("exec request",
            Contract.Properties(Contract.RequestBody(
                $"{ManagementProtocol.ApiPrefix}/instances/{{name}}/exec", "post")),
            wire);
    }

    private static IEnumerable<string> WireKeys(object dto)
    {
        using var json = JsonDocument.Parse(
            JsonSerializer.Serialize(dto, dto.GetType(), ManagementProtocol.Json));
        return json.RootElement.EnumerateObject().Select(property => property.Name).ToList();
    }

    /// <summary>Every property a DTO has, spelled the way the wire would spell it, whether or not a
    /// sample happened to set it.</summary>
    private static IEnumerable<string> AllWireNames(Type type)
    {
        var policy = ManagementProtocol.Json.PropertyNamingPolicy!;
        return type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(property => policy.ConvertName(property.Name));
    }

    private static (object Dto, YamlNode Schema) Case(string name) => name switch
    {
        "InstanceSpec" => (new InstanceSpec
        {
            Name = "eu-1", Map = "stormkeep", Gametype = "ctf", Port = 26010, MaxPlayers = 16,
            Hostname = "Vortex EU #1", BuildId = "0.3.0", RestartPolicy = RestartPolicy.OnFailure,
            RestartAt = "05:00", RestartOnlyWhenEmpty = true, ExtraArgs = ["+exec", "extra.cfg"],
            Environment = new Dictionary<string, string> { ["VORTEX_REGION"] = "eu" },
            ContentSet = [new string('a', 64)],
            ControlMode = ControlMode.Orchestrated,
            ControllerUrl = "https://conductor.vortexfps.org",
            GrantedScopes = Scopes.DefaultForAdoption,
            ControlledSince = DateTimeOffset.UtcNow,
        }, Contract.Schema("InstanceSpec")),

        "InstanceStatus" => (Status(), Contract.Schema("InstanceStatus")),

        "RunnerStatus" => (new RunnerStatus
        {
            RunnerId = "runner-1", Version = "0.3.0", Platform = "linux-server",
            Hostname = "box-eu-1", StartedAt = DateTimeOffset.UtcNow,
            DiskFreeBytes = 40L * 1024 * 1024 * 1024, CpuPercent = 12.5,
            MemoryTotalBytes = 8L * 1024 * 1024 * 1024, MemoryUsedBytes = 2L * 1024 * 1024 * 1024,
            ConductorUrl = "https://conductor.vortexfps.org", Instances = [Status()],
        }, Contract.Schema("RunnerStatus")),

        "LogLine" => (new LogLine
        {
            InstanceName = "eu-1", Stream = LogStream.Event, Text = ":chat:3:hello",
            Timestamp = DateTimeOffset.UtcNow, EventType = "chat", IsChat = true,
        }, Contract.Schema("LogLine")),

        "BuildSummary" => (new BuildSummary
        {
            Id = "0.3.0", Version = "0.3.0", Provider = BuildProviders.Release,
            PlatformKey = "linux-server", Layout = InstalledState.LayoutCore,
            SizeBytes = 512 * 1024 * 1024, InstalledAt = DateTimeOffset.UtcNow, InUse = true,
        }, Contract.Schema("BuildSummary")),

        "ContentPackage" => (new ContentPackage
        {
            Sha256 = new string('a', 64), Name = "stormkeep.pk3", SizeBytes = 4 * 1024 * 1024,
            Maps = ["stormkeep"], MapFormat = "vmap", AddedAt = DateTimeOffset.UtcNow,
            Url = "https://content.vortexfps.org/" + new string('a', 64),
        }, Contract.Schema("ContentPackage")),

        "ApiError" => (new ApiError
        {
            Code = ApiErrorCodes.InstanceOrchestrated,
            Message = "instance 'eu-1' is controlled by https://conductor.vortexfps.org",
            Orchestrated = Orchestrated(),
        }, Contract.Schema("ApiError")),

        "ApiError.orchestrated" => (Orchestrated(),
            Contract.Require(Contract.Schema("ApiError"), "properties", "orchestrated")),

        "drain request" => (new DrainRequest { Message = "restarting in 5", TimeoutSeconds = 300 },
            Contract.RequestBody(
                $"{ManagementProtocol.ApiPrefix}/instances/{{name}}/drain", "post")),

        "release request" => (new ReleaseRequest
        {
            When = ReleaseWhen.EndOfMatch, Reason = "taking the box back for a LAN",
        }, Contract.RequestBody(
            $"{ManagementProtocol.ApiPrefix}/instances/{{name}}/release", "post")),

        _ => throw new ArgumentOutOfRangeException(nameof(name), name, "no such schema case"),
    };

    private static InstanceStatus Status() => new()
    {
        Name = "eu-1", State = InstanceState.Running, ControlMode = ControlMode.Orchestrated,
        BuildId = "0.3.0", Pid = 4242, StartedAt = DateTimeOffset.UtcNow, Map = "stormkeep",
        Gametype = "ctf", Players = 12, Bots = 2, MaxPlayers = 16, MatchLive = true,
        MatchElapsedSeconds = 340, CpuPercent = 18.5, MemoryBytes = 512 * 1024 * 1024,
        RestartCount = 1, LastExitReason = "signal 15",
    };

    private static OrchestratedDetail Orchestrated() => new()
    {
        ControllerUrl = "https://conductor.vortexfps.org",
        ControlledSince = DateTimeOffset.UtcNow,
        GrantedScopes = Scopes.DefaultForAdoption,
    };
}

/// <summary>The closed sets: error codes, enums, and the name pattern. Every one of them is something
/// a control plane branches on, and every one of them is a string on the wire rather than a type the
/// compiler checks across the two repositories.</summary>
public class VocabularyContractTests
{
    /// <summary>A code the runner can return and the spec does not list is unhandleable by anyone
    /// reading the spec. A code the spec lists and the runner never returns is worse: it looks
    /// handled, and the branch written for it can never be taken.</summary>
    [Fact]
    public void The_documented_error_codes_are_exactly_the_ones_the_runner_returns()
    {
        Contract.AssertSameSet("ApiError.code",
            Contract.EnumValues(Contract.Property("ApiError", "code")), Constants(typeof(ApiErrorCodes)));
    }

    /// <summary>Enums travel as text, so the spelling is the contract. An enum that matched by name
    /// but serialized differently would be a different protocol with the same documentation.</summary>
    [Theory]
    [InlineData("InstanceSpec.restart_policy")]
    [InlineData("InstanceSpec.control_mode")]
    [InlineData("InstanceStatus.state")]
    [InlineData("InstanceStatus.control_mode")]
    [InlineData("LogLine.stream")]
    [InlineData("release request.when")]
    public void Documented_enums_match_the_C_sharp_enum_and_its_wire_spelling(string locator)
    {
        var (schema, type) = EnumCase(locator);
        Contract.AssertSameSet(locator, Contract.EnumValues(schema), WireNames(type));
    }

    /// <summary>Two of the spec's enums have no C# enum behind them: a build's provider and its
    /// layout are strings, because they come from a build store older than this protocol. They still
    /// have to name the same things the store writes.
    ///
    /// Read off the constant classes rather than listed here. Naming the two values in the test would
    /// only ever catch a rename, and the change that actually happens to a closed set of strings is a
    /// third one being added — which a hand-written list agrees with by never having heard of it.</summary>
    [Fact]
    public void The_build_enums_match_the_constants_the_store_writes()
    {
        Contract.AssertSameSet("BuildSummary.provider",
            Contract.EnumValues(Contract.Property("BuildSummary", "provider")),
            Constants(typeof(BuildProviders)));

        Contract.AssertSameSet("BuildSummary.layout",
            Contract.EnumValues(Contract.Property("BuildSummary", "layout")),
            Constants(typeof(InstalledState)));
    }

    /// <summary>A plane validates a name against the documented pattern before it sends anything. If
    /// the runner is stricter than the pattern, an operator gets a 400 for a name the panel just told
    /// them was fine; if it is looser, the panel rejects names the runner would have taken.</summary>
    [Theory]
    [InlineData("eu-1", true)]
    [InlineData("test_server.2", true)]
    [InlineData("", false)]
    [InlineData(".", false)]
    [InlineData("..", false)]
    [InlineData("has space", false)]
    [InlineData("../escape", false)]
    [InlineData("semi;colon", false)]
    public void The_documented_name_pattern_accepts_exactly_what_the_runner_accepts(
        string name, bool acceptable)
    {
        Assert.Equal(acceptable, Regex.IsMatch(name, NamePattern()));
        Assert.Equal(acceptable, RunnerAccepts(name));
    }

    /// <summary>The other end of the same disagreement, and the one an inline case cannot carry. The
    /// pattern's upper bound and the runner's length check are written independently, so an off-by-one
    /// there is a name a panel offers and the runner then refuses.</summary>
    [Fact]
    public void The_documented_name_pattern_stops_at_the_same_length_the_runner_does()
    {
        foreach (var (name, acceptable) in
                 new[] { (new string('a', 64), true), (new string('a', 65), false) })
        {
            Assert.Equal(acceptable, Regex.IsMatch(name, NamePattern()));
            Assert.Equal(acceptable, RunnerAccepts(name));
        }
    }

    private static string NamePattern() =>
        Contract.Scalar(
            Contract.Require(Contract.Root, "components", "parameters", "InstanceName", "schema"),
            "pattern")
        ?? throw new InvalidOperationException("the InstanceName parameter documents no pattern");

    private static bool RunnerAccepts(string name)
    {
        try
        {
            InstanceStore.ValidateName(name);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    /// <summary>The public string constants of a class, which is how the runner spells a closed set
    /// that has no enum behind it.</summary>
    private static IEnumerable<string> Constants(Type type) =>
        type.GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(field => field.IsLiteral && field.FieldType == typeof(string))
            .Select(field => (string)field.GetRawConstantValue()!);

    /// <summary>Serialized rather than named: the wire form is snake_case, and that conversion is the
    /// half most likely to drift.</summary>
    private static IEnumerable<string> WireNames(Type type) =>
        Enum.GetValues(type).Cast<object>()
            .Select(value => JsonSerializer.Serialize(value, type, ManagementProtocol.Json).Trim('"'));

    private static (YamlNode Schema, Type Enum) EnumCase(string locator) => locator switch
    {
        "InstanceSpec.restart_policy" =>
            (Contract.Property("InstanceSpec", "restart_policy"), typeof(RestartPolicy)),
        "InstanceSpec.control_mode" =>
            (Contract.Property("InstanceSpec", "control_mode"), typeof(ControlMode)),
        "InstanceStatus.state" =>
            (Contract.Property("InstanceStatus", "state"), typeof(InstanceState)),
        "InstanceStatus.control_mode" =>
            (Contract.Property("InstanceStatus", "control_mode"), typeof(ControlMode)),
        "LogLine.stream" => (Contract.Property("LogLine", "stream"), typeof(LogStream)),
        "release request.when" => (Contract.Require(
            Contract.RequestBody($"{ManagementProtocol.ApiPrefix}/instances/{{name}}/release", "post"),
            "properties", "when"), typeof(ReleaseWhen)),
        _ => throw new ArgumentOutOfRangeException(nameof(locator), locator, "no such enum case"),
    };
}
