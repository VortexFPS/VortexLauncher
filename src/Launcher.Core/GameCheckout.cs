using System.Text.Json;

namespace Launcher.Core;

/// <summary>Where a source build finds the game repo's own build tooling, and the facts it reads out
/// of it.
///
/// Every path here is a file the game repo owns and CI already runs. The launcher deliberately calls
/// those scripts rather than reimplementing them: a second template downloader or a second content
/// layout would drift from the first, and the way that drift shows up is a build that is patched in CI
/// and stock locally, which nothing downstream would notice.</summary>
public static class GameCheckout
{
    /// <summary>The authoritative engine pin. Prose in docs/RUNNING.md drifts; this is the file CI
    /// already trusts and the one tools/verify-engine-template.py checks against.</summary>
    public static string EngineLockPath(string checkout) =>
        Path.Combine(checkout, "tools", "engine-patches", "engine.lock.json");

    public static string ExportPresetsPath(string checkout) =>
        Path.Combine(checkout, "export_presets.cfg");

    /// <summary>Downloads the pinned export TEMPLATES into tools/engine-templates/, verified against
    /// the sha256 in the lockfile.</summary>
    public static string FetchTemplateScript(string checkout) =>
        Path.Combine(checkout, "tools", "data", "fetch-engine-template.py");

    /// <summary>Three modes: --patches (the source), --preset-config (the input), --binary (what
    /// shipped). A source build runs the second before the export and the third after it.</summary>
    public static string VerifyTemplateScript(string checkout) =>
        Path.Combine(checkout, "tools", "verify-engine-template.py");

    public static string FetchMapsScript(string checkout) =>
        Path.Combine(checkout, "tools", "data", "fetch-maps.py");

    /// <summary>Lays content, licences and launch scripts beside the exported binary. Run with
    /// --no-zip: the build store wants the directory, not an archive it would immediately unpack.</summary>
    public static string PackageScript(string checkout) =>
        Path.Combine(checkout, "tools", "package.sh");

    public static string NuGetConfigPath(string checkout) =>
        Path.Combine(checkout, "nuget.config");

    /// <summary>The Godot C# project at the checkout root, or null if it cannot be identified.
    ///
    /// Named rather than left to a bare `dotnet build` in the checkout directory, which is not the
    /// same command: with both a .sln and a .csproj at the root, dotnet picks the SOLUTION, and this
    /// repo's solution carries eight projects including the test suite. The export compiles this one
    /// file, and the game repo's own ci.sh and ci.yml build this one file, so a pre-build that
    /// compiles a wider set can fail on code the export never touches, which is the opposite of the
    /// fail-early it exists for. Godot generates exactly one csproj at a project root, so "the single
    /// root csproj" finds it without hard-coding the game's name into the launcher.</summary>
    public static string? GameProject(string checkout)
    {
        var roots = Directory.Exists(checkout) ? Directory.GetFiles(checkout, "*.csproj") : [];
        return roots.Length == 1 ? roots[0] : null;
    }

    /// <summary>Where an export preset writes, and what package.sh then fills in.</summary>
    public static string DistDir(string checkout, string preset) =>
        Path.Combine(checkout, "dist", preset);

    public static string TemplateDir(string checkout) =>
        Path.Combine(checkout, "tools", "engine-templates");

    /// <summary>Fail naming the file rather than letting the process launch fail with "no such file",
    /// which reads as a broken launcher instead of a checkout that predates the tooling.</summary>
    public static string Require(string path, string what)
    {
        if (!File.Exists(path))
            throw new SourceBuildException(SourceFailure.CheckoutIncomplete,
                $"this checkout has no {what} at {path}. A source build drives the game repo's own " +
                "build scripts, so a ref from before they existed cannot be built by the launcher.");
        return path;
    }
}

/// <summary>One published export template, as pinned by tools/engine-patches/engine.lock.json.</summary>
public sealed record TemplatePin
{
    /// <summary>windows, linux or macos: the key the lockfile uses and the value
    /// fetch-engine-template.py takes for --only.</summary>
    public required string Platform { get; init; }

    public required string FileName { get; init; }
    public string? Sha256 { get; init; }
    public long Bytes { get; init; }

    /// <summary>Export presets built from this template. One template can serve several: both Linux
    /// presets share a file.</summary>
    public required IReadOnlyList<string> Presets { get; init; }

    /// <summary>Whether this template carries the patch set. False is not "unused": the patches touch
    /// platform/windows/ only, so the other templates are pinned for provenance.</summary>
    public bool Patched { get; init; }
}

/// <summary>The engine a checkout must be built with, read from its own lockfile.</summary>
public sealed record EnginePin
{
    public required string Version { get; init; }

    /// <summary>stable, beta3 and so on: Godot's own status field, which --version also reports.</summary>
    public string? Channel { get; init; }

    /// <summary>The lockfile's engine.dotnet. When true the export needs the mono/.NET editor, and a
    /// plain editor cannot build a C# project at all.</summary>
    public bool RequiresDotnet { get; init; }

    /// <summary>The release tag the templates are published under. Named in error messages because
    /// "engine-4.6.3" is the wrong guess and the wrong guess 404s.</summary>
    public string? TemplateTag { get; init; }

    public required IReadOnlyList<TemplatePin> Templates { get; init; }

    public TemplatePin? TemplateForPreset(string preset) =>
        Templates.FirstOrDefault(t => t.Presets.Contains(preset, StringComparer.Ordinal));

    public IReadOnlyList<string> KnownPresets =>
        Templates.SelectMany(t => t.Presets).Distinct(StringComparer.Ordinal)
            .OrderBy(p => p, StringComparer.Ordinal).ToList();

    public static EnginePin Read(string checkout)
    {
        var path = GameCheckout.Require(GameCheckout.EngineLockPath(checkout),
            "tools/engine-patches/engine.lock.json");

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(File.ReadAllText(path));
        }
        catch (JsonException ex)
        {
            throw new SourceBuildException(SourceFailure.LockfileUnreadable,
                $"{path} is not valid JSON: {ex.Message}. Refusing to guess an engine version.");
        }

        using (doc)
        {
            var root = doc.RootElement;

            if (!root.TryGetProperty("engine", out var engine) ||
                !engine.TryGetProperty("version", out var version) ||
                version.GetString() is not { Length: > 0 } versionText)
                throw new SourceBuildException(SourceFailure.LockfileUnreadable,
                    $"{path} has no engine.version. That value decides which engine this build is " +
                    "made with, and guessing it produces a build that compiles and then misbehaves.");

            var template = root.TryGetProperty("template", out var t) ? t : default;
            var templates = new List<TemplatePin>();

            if (template.ValueKind == JsonValueKind.Object &&
                template.TryGetProperty("platforms", out var platforms) &&
                platforms.ValueKind == JsonValueKind.Object)
            {
                foreach (var platform in platforms.EnumerateObject())
                {
                    // $comment keys sit beside real entries at every level of this file.
                    if (platform.Name.StartsWith('$') || platform.Value.ValueKind != JsonValueKind.Object)
                        continue;

                    var entry = platform.Value;
                    if (!entry.TryGetProperty("filename", out var fileName) ||
                        fileName.GetString() is not { Length: > 0 } fileNameText)
                        continue;

                    templates.Add(new TemplatePin
                    {
                        Platform = platform.Name,
                        FileName = fileNameText,
                        Sha256 = entry.TryGetProperty("sha256", out var sha) ? sha.GetString() : null,
                        Bytes = entry.TryGetProperty("bytes", out var bytes) &&
                                bytes.ValueKind == JsonValueKind.Number ? bytes.GetInt64() : 0,
                        Presets = entry.TryGetProperty("presets", out var presets) &&
                                  presets.ValueKind == JsonValueKind.Array
                            ? presets.EnumerateArray().Select(p => p.GetString() ?? "")
                                .Where(p => p.Length > 0).ToList()
                            : [],
                        Patched = entry.TryGetProperty("patched", out var patched) &&
                                  patched.ValueKind == JsonValueKind.True,
                    });
                }
            }

            if (templates.Count == 0)
                throw new SourceBuildException(SourceFailure.LockfileUnreadable,
                    $"{path} pins no export templates (template.platforms is empty). Nothing here says " +
                    "which engine to ship, and an export with no template falls back to whatever stock " +
                    "template this box happens to have installed.");

            return new EnginePin
            {
                Version = versionText,
                Channel = engine.TryGetProperty("channel", out var channel) ? channel.GetString() : null,
                RequiresDotnet = engine.TryGetProperty("dotnet", out var dotnet) &&
                                 dotnet.ValueKind == JsonValueKind.True,
                TemplateTag = template.ValueKind == JsonValueKind.Object &&
                              template.TryGetProperty("tag", out var tag) ? tag.GetString() : null,
                Templates = templates,
            };
        }
    }
}

/// <summary>Reads export_presets.cfg well enough to answer "where does preset P write?".
///
/// Godot splits a preset across two sections, `[preset.N]` holding `name` and `export_path`, so the
/// value has to be joined on the index. The export path is taken from the file rather than invented
/// here because package.sh, the release workflow and verify-engine-template.py all key off those exact
/// names: a launcher that exported to a path of its own choosing would produce a directory none of
/// them recognise.</summary>
public static class ExportPresets
{
    public static IReadOnlyDictionary<string, string> Read(string checkout)
    {
        var path = GameCheckout.Require(GameCheckout.ExportPresetsPath(checkout), "export_presets.cfg");

        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        var exportPaths = new Dictionary<string, string>(StringComparer.Ordinal);
        var section = "";

        foreach (var raw in File.ReadLines(path))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith(';'))
                continue;

            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                section = line[1..^1].Trim();
                continue;
            }

            // Only the `[preset.N]` half carries name and export_path; `[preset.N.options]` holds
            // custom_template/release, which verify-engine-template.py is the one that checks.
            const string prefix = "preset.";
            if (!section.StartsWith(prefix, StringComparison.Ordinal) ||
                section.IndexOf('.', prefix.Length) >= 0)
                continue;

            var index = section[prefix.Length..];
            var eq = line.IndexOf('=');
            if (eq <= 0)
                continue;

            var key = line[..eq].Trim();
            var value = line[(eq + 1)..].Trim().Trim('"');

            if (key == "name")
                names[index] = value;
            else if (key == "export_path")
                exportPaths[index] = value;
        }

        return names
            .Where(n => exportPaths.ContainsKey(n.Key))
            .ToDictionary(n => n.Value, n => exportPaths[n.Key], StringComparer.Ordinal);
    }
}
