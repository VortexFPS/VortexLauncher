using System.Text.Json;

namespace Launcher.Core;

/// <summary>A named repo/ref an operator can build. Named rather than positional so `source build` is
/// a short command an operator can run from muscle memory, and so two sources can differ only by ref
/// (a stable tag and a branch under test) without either one having to be retyped.</summary>
public sealed record SourceSpec
{
    public required string Name { get; init; }

    public required string Repo { get; init; }

    public string Ref { get; init; } = "main";

    /// <summary>Export preset to build. Null means the default for this OS, resolved at build time
    /// rather than stored, so a spec written on one box is not wrong on another.</summary>
    public string? Target { get; init; }

    /// <summary>Where this box's Godot editor lives, when it is not on PATH. Per source rather than
    /// global because two sources can pin different engine versions.</summary>
    public string? GodotPath { get; init; }

    public DateTimeOffset UpdatedAt { get; init; }

    /// <summary>What the last successful build produced, so `source status` can answer "is what I
    /// last built still what this spec describes" without a rebuild.</summary>
    public string? LastBuildId { get; init; }

    public string? LastBuiltSha { get; init; }

    public DateTimeOffset? LastBuiltAt { get; init; }
}

/// <summary>Persistence for source specs: one JSON document beside the checkouts they describe.
///
/// One file rather than a directory per source, because a spec is four fields and the checkout it
/// refers to is the thing that costs gigabytes. Deleting a source deliberately leaves the checkout
/// alone; see <see cref="SourceProvider.CheckoutFor"/>.</summary>
public sealed class SourceStore(LauncherPaths paths)
{
    private string Dir => Path.Combine(paths.Root, "source");
    private string FilePath => Path.Combine(Dir, "sources.json");

    public IReadOnlyList<SourceSpec> List() =>
        Load().Values.OrderBy(s => s.Name, StringComparer.Ordinal).ToList();

    public SourceSpec? Get(string name) => Load().GetValueOrDefault(name);

    public bool Exists(string name) => Load().ContainsKey(name);

    public void Save(SourceSpec spec)
    {
        var all = Load();
        all[spec.Name] = spec with { UpdatedAt = DateTimeOffset.UtcNow };
        Write(all);
    }

    public bool Delete(string name)
    {
        var all = Load();
        if (!all.Remove(name))
            return false;
        Write(all);
        return true;
    }

    /// <summary>Source names become directory names for the checkout, so they are restricted rather
    /// than sanitized, on the same rule <see cref="Instances.InstanceStore.ValidateName"/> follows:
    /// silently rewriting the name an operator typed makes the next command fail to find it.</summary>
    public static string ValidateName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("source name cannot be empty", nameof(name));
        if (name.Length > 64)
            throw new ArgumentException("source name is limited to 64 characters", nameof(name));
        foreach (var c in name)
            if (!char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_' or '.'))
                throw new ArgumentException(
                    $"source name '{name}' may only contain letters, digits, '-', '_' and '.'",
                    nameof(name));
        if (name is "." or "..")
            throw new ArgumentException("source name cannot be '.' or '..'", nameof(name));
        return name;
    }

    private Dictionary<string, SourceSpec> Load()
    {
        try
        {
            if (!File.Exists(FilePath))
                return new(StringComparer.Ordinal);
            return JsonSerializer.Deserialize<Dictionary<string, SourceSpec>>(
                       File.ReadAllText(FilePath), ReleaseManifest.JsonOptions)
                   ?? new(StringComparer.Ordinal);
        }
        catch (JsonException)
        {
            // Same call as BuildStore's: a torn metadata file must not brick the verb that would let
            // an operator rewrite it.
            return new(StringComparer.Ordinal);
        }
    }

    private void Write(Dictionary<string, SourceSpec> all)
    {
        Directory.CreateDirectory(Dir);
        var tmp = FilePath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(all, ReleaseManifest.JsonOptions));
        File.Move(tmp, FilePath, overwrite: true);
    }
}
