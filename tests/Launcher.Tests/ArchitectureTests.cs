using System.Xml.Linq;
using Xunit;

namespace Launcher.Tests;

/// <summary>Enforces the dependency graph from planning/launcher-host-agent-plan.md §2:
/// <code>
///   Launcher.Protocol  → BCL only
///   Launcher.Core      → Protocol
///   Launcher.Cli       → Core, Protocol
///   Launcher.WebServer → Protocol          (NOT Core)
///   Launcher.Desktop   → Core
/// </code>
/// The load-bearing rule is WebServer ↛ Core. The WebServer must not be able to read the build store,
/// write server.cfg or spawn a process; every box-touching operation is a Launcher.Protocol message to
/// a runner. Keeping the reference out makes that a compile error rather than a convention, and keeps
/// the same-box and cross-box paths identical because no shortcut exists for the same-box case.
///
/// This reads the .csproj files off disk instead of reflecting over loaded assemblies, for two
/// reasons: the test project cannot reference WebServer without itself dragging Core into the same
/// graph it is policing, and a declared-but-unused reference is a violation the compiler would have
/// trimmed out of Assembly.GetReferencedAssemblies().
///
/// Projects that do not exist yet are skipped, so the rules can be written once and hold as each
/// milestone lands.</summary>
public class ArchitectureTests
{
    private static readonly string[] NoProjectReferences = [];

    public static TheoryData<string, string[]> Rules() => new()
    {
        { "Launcher.Core", ["Launcher.Protocol"] },
        { "Launcher.Protocol", NoProjectReferences },
        { "Launcher.Cli", ["Launcher.Core", "Launcher.Protocol"] },
        { "Launcher.WebServer", ["Launcher.Protocol"] },
        { "Launcher.Desktop", ["Launcher.Core"] },
    };

    [Theory]
    [MemberData(nameof(Rules))]
    public void Project_references_stay_inside_the_allowed_set(string project, string[] allowed)
    {
        var csproj = ProjectFile(project);
        if (csproj is null)
            return; // milestone not landed yet

        var referenced = ProjectReferenceNames(csproj);
        var violations = referenced.Except(allowed, StringComparer.Ordinal).Order().ToArray();

        Assert.True(violations.Length == 0,
            $"{project} references {string.Join(", ", violations)}; allowed: " +
            (allowed.Length == 0 ? "(none)" : string.Join(", ", allowed)));
    }

    /// <summary>Core and Protocol are consumed by every other project, and Protocol by Conductor
    /// through a NuGet package. A package reference here lands in all of them, so the bar is: none at
    /// all. Core's srcon and getinfo clients are written against BCL sockets for this reason.</summary>
    [Theory]
    [InlineData("Launcher.Core")]
    [InlineData("Launcher.Protocol")]
    public void Bcl_only_projects_take_no_package_references(string project)
    {
        var csproj = ProjectFile(project);
        if (csproj is null)
            return;

        var packages = XDocument.Load(csproj)
            .Descendants("PackageReference")
            .Select(e => e.Attribute("Include")?.Value ?? "?")
            .Order()
            .ToArray();

        Assert.True(packages.Length == 0,
            $"{project} is BCL-only but references {string.Join(", ", packages)}");
    }

    /// <summary>The rule with a reason worth restating at the point of failure.</summary>
    [Fact]
    public void WebServer_does_not_reference_Core()
    {
        var csproj = ProjectFile("Launcher.WebServer");
        if (csproj is null)
            return;

        Assert.DoesNotContain("Launcher.Core", ProjectReferenceNames(csproj));
    }

    private static string[] ProjectReferenceNames(string csprojPath) =>
        XDocument.Load(csprojPath)
            .Descendants("ProjectReference")
            .Select(e => e.Attribute("Include")?.Value ?? "")
            .Select(p => Path.GetFileNameWithoutExtension(p.Replace('\\', Path.DirectorySeparatorChar)))
            .Where(n => !string.IsNullOrEmpty(n))
            .ToArray()!;

    private static string? ProjectFile(string project)
    {
        var path = Path.Combine(RepoRoot(), "src", project, project + ".csproj");
        return File.Exists(path) ? path : null;
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
