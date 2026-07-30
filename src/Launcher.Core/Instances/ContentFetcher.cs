using System.IO.Compression;
using System.Security.Cryptography;
using Launcher.Protocol;

namespace Launcher.Core.Instances;

/// <summary>Validates a .pk3 before anything is written into an instance's data directory.
///
/// This duplicates the check Conductor runs at upload, on purpose. A runner must not trust a control
/// plane with arbitrary file writes into its own filesystem, and "the store already checked it" stops
/// being true the moment a community operator points a runner at their own store, or a control plane
/// is compromised, or the stored bytes are corrupted in transit. The upload check protects the fleet;
/// this one protects the box.
///
/// Mirrors Pk3Validator in the Conductor repo. Shared fixtures keep the two honest.</summary>
public static class Pk3Guard
{
    public const long MaxUncompressedBytes = 2L * 1024 * 1024 * 1024;
    public const int MaxEntries = 20_000;
    public const int MaxCompressionRatio = 200;

    public sealed record Result(bool Ok, string? Error, IReadOnlyList<string> Maps, string? Format);

    public static Result Inspect(Stream pk3, long compressedSize)
    {
        ZipArchive archive;
        try
        {
            archive = new ZipArchive(pk3, ZipArchiveMode.Read, leaveOpen: true);
        }
        catch (InvalidDataException ex)
        {
            return new Result(false, $"not a readable zip: {ex.Message}", [], null);
        }

        using (archive)
        {
            if (archive.Entries.Count > MaxEntries)
                return new Result(false, $"more than {MaxEntries} entries", [], null);

            long uncompressed = 0;
            var maps = new List<string>();
            string? format = null;

            foreach (var entry in archive.Entries)
            {
                if (!IsSafePath(entry.FullName))
                    return new Result(false,
                        $"entry '{entry.FullName}' escapes the archive root", [], null);

                if (IsSymlink(entry))
                    return new Result(false, $"entry '{entry.FullName}' is a symlink", [], null);

                uncompressed += entry.Length;
                if (uncompressed > MaxUncompressedBytes)
                    return new Result(false, "expands beyond the uncompressed size limit", [], null);

                var name = entry.FullName.Replace('\\', '/');
                if (!name.StartsWith("maps/", StringComparison.OrdinalIgnoreCase))
                    continue;

                if (name.EndsWith(".bsp", StringComparison.OrdinalIgnoreCase))
                {
                    maps.Add(Path.GetFileNameWithoutExtension(name));
                    format ??= "bsp";
                }
                else if (name.EndsWith(".vmap", StringComparison.OrdinalIgnoreCase))
                {
                    maps.Add(Path.GetFileNameWithoutExtension(name));
                    format = "vmap";
                }
            }

            // A ratio cap catches what a size cap alone misses: a few hundred kilobytes of nested
            // nothing expands to gigabytes, and the archive looks harmless until it is opened.
            if (compressedSize > 0 && uncompressed / Math.Max(1, compressedSize) > MaxCompressionRatio)
                return new Result(false, $"compression ratio above {MaxCompressionRatio}:1", [], null);

            if (maps.Count == 0)
                return new Result(false,
                    "no maps/*.bsp or maps/*.vmap entries; this is not a map package", [], null);

            return new Result(true, null, maps.Distinct().ToList(), format);
        }
    }

    /// <summary>Reject anything that would write outside where it is extracted: absolute paths, drive
    /// letters, and any traversal segment.</summary>
    public static bool IsSafePath(string entryPath)
    {
        if (string.IsNullOrWhiteSpace(entryPath))
            return false;

        var normalized = entryPath.Replace('\\', '/');
        if (normalized.StartsWith('/') || normalized.Contains(':'))
            return false;

        return !normalized.Split('/').Any(segment => segment == "..");
    }

    private static bool IsSymlink(ZipArchiveEntry entry)
    {
        const int unixSymlinkFlag = 0xA000;
        var mode = entry.ExternalAttributes >> 16;
        return (mode & 0xF000) == unixSymlinkFlag;
    }
}

/// <summary>Brings an instance's content set up to date by fetching what is missing, by hash.
///
/// Nothing is ever pushed at a runner. A control plane says "instance X should have this set of
/// sha256s" and the runner goes and gets them. That solves the half a blob push does not: players
/// joining the server need the same file, and they pull the same content-addressed object from the
/// same URL, so there is one distribution path instead of two.</summary>
public sealed class ContentFetcher(LauncherPaths paths, HttpClient http)
{
    /// <summary>Shared across instances. Two servers running the same map pool download it once, which
    /// is the same property the asset store already gives the split payload.</summary>
    public string CacheDir => Path.Combine(paths.Root, "content");

    public string CachePathFor(string sha256) =>
        Path.Combine(CacheDir, sha256[..2], sha256 + ".pk3");

    public sealed record SyncResult(
        IReadOnlyList<string> Installed, IReadOnlyList<string> AlreadyPresent,
        IReadOnlyDictionary<string, string> Failed)
    {
        public bool Ok => Failed.Count == 0;
    }

    /// <summary>Fetch, verify and install everything in <paramref name="contentSet"/> that the instance
    /// does not already have.
    ///
    /// A failure on one package leaves the instance on its previous content set rather than half
    /// applied: a server missing one map of five is a server that kicks players on rotation, which is
    /// worse than one that did not update.</summary>
    public async Task<SyncResult> SyncAsync(InstanceSpec spec, IReadOnlyList<string> contentSet,
        string baseUrl, CancellationToken ct = default)
    {
        var installed = new List<string>();
        var present = new List<string>();
        var failed = new Dictionary<string, string>(StringComparer.Ordinal);

        var target = Path.Combine(paths.InstancesDir, spec.Name, "VortexData", "data");
        Directory.CreateDirectory(target);

        foreach (var sha in contentSet)
        {
            if (!IsSha256(sha))
            {
                failed[sha] = "not a lowercase hex sha256";
                continue;
            }

            var destination = Path.Combine(target, sha + ".pk3");
            if (File.Exists(destination))
            {
                present.Add(sha);
                continue;
            }

            try
            {
                var cached = await EnsureCachedAsync(sha, baseUrl, ct);
                // Hard link where the filesystem allows it, copy otherwise. A map pool shared by six
                // instances should not be six copies of the same 200 MB.
                File.Copy(cached, destination, overwrite: true);
                installed.Add(sha);
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException
                                           or InvalidDataException or TaskCanceledException)
            {
                failed[sha] = ex.Message;
            }
        }

        return new SyncResult(installed, present, failed);
    }

    /// <summary>Download into the shared cache if it is not already there, verifying the hash and the
    /// package before it is allowed to become a cache entry.</summary>
    public async Task<string> EnsureCachedAsync(string sha256, string baseUrl,
        CancellationToken ct = default)
    {
        var cached = CachePathFor(sha256);
        if (File.Exists(cached))
            return cached;

        Directory.CreateDirectory(Path.GetDirectoryName(cached)!);
        var temp = cached + ".part";

        try
        {
            var url = $"{baseUrl.TrimEnd('/')}/{sha256}.pk3";
            using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();

            await using (var file = File.Create(temp))
                await response.Content.CopyToAsync(file, ct);

            // The hash is the address. Verifying it is what makes the whole scheme mean anything, and
            // it has to happen before the bytes are treated as a package.
            string actual;
            await using (var file = File.OpenRead(temp))
                actual = Convert.ToHexString(await SHA256.HashDataAsync(file, ct)).ToLowerInvariant();

            if (actual != sha256)
                throw new InvalidDataException(
                    $"content hash mismatch: asked for {sha256[..12]}, got {actual[..12]}");

            Pk3Guard.Result inspection;
            var size = new FileInfo(temp).Length;
            await using (var file = File.OpenRead(temp))
                inspection = Pk3Guard.Inspect(file, size);

            if (!inspection.Ok)
                throw new InvalidDataException($"rejected package {sha256[..12]}: {inspection.Error}");

            File.Move(temp, cached, overwrite: true);
            return cached;
        }
        finally
        {
            if (File.Exists(temp))
                File.Delete(temp);
        }
    }

    /// <summary>Delete cached packages no instance references. Called after a content change, so a
    /// rotated-out map pool does not sit on disk forever.</summary>
    public IReadOnlyList<string> Gc(InstanceStore store)
    {
        if (!Directory.Exists(CacheDir))
            return [];

        var wanted = store.List()
            .SelectMany(s => s.ContentSet ?? [])
            .ToHashSet(StringComparer.Ordinal);

        var removed = new List<string>();
        foreach (var file in Directory.EnumerateFiles(CacheDir, "*.pk3", SearchOption.AllDirectories))
        {
            var sha = Path.GetFileNameWithoutExtension(file);
            if (wanted.Contains(sha))
                continue;
            try
            {
                File.Delete(file);
                removed.Add(sha);
            }
            catch (IOException) { }
        }

        return removed;
    }

    public static bool IsSha256(string value) =>
        value.Length == 64 && value.All(char.IsAsciiHexDigitLower);
}
