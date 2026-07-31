using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Launcher.Core.Signing;

namespace Launcher.Core;

public interface IReleaseFeed
{
    string Name { get; }
    Task<ReleaseManifest?> FetchLatestAsync(CancellationToken ct);
}

/// <summary>The default path (ADR-0015 §5): latest.json off the newest FULL release via the
/// /releases/latest/download redirect. Plain HTTP, no API quota. Returns null (not an error)
/// while no stable release carries a manifest — the API fallback covers that window.
///
/// This is also where the release signature is checked, and it is checked here rather than in the
/// installer on purpose: this is the one door a manifest comes through, so verification cannot be
/// skipped by a caller that forgot to ask for it. See <see cref="ReleaseSigning"/> for why the
/// manifest is the thing signed.</summary>
public sealed class ManifestFeed(HttpClient http, ManifestSignaturePolicy? policy = null) : IReleaseFeed
{
    private readonly ManifestSignaturePolicy _policy = policy ?? ReleaseSigning.ResolvePolicy();

    public string Name => "latest.json (stable channel)";

    public async Task<ReleaseManifest?> FetchLatestAsync(CancellationToken ct)
    {
        using var resp = await http.GetAsync(LauncherConfig.LatestManifestUrl, ct);
        if (resp.StatusCode == HttpStatusCode.NotFound)
            return null; // no stable release yet, or it predates latest.json
        resp.EnsureSuccessStatusCode();

        // Bytes, not ReadAsStringAsync: the signature covers latest.json exactly as published, and a
        // trip through string and back would be verifying a re-encoding of it rather than it.
        var published = await resp.Content.ReadAsByteArrayAsync(ct);
        var signature = await FetchSignatureAsync(ct);
        var status = ReleaseSigning.Check(_policy, published, signature, "latest.json");

        // A BOM is part of the signed bytes but chokes the JSON reader, so it comes off after the check.
        var manifest = ReleaseManifest.Parse(Utf8NoBom(published));
        return manifest is null ? null : manifest with { SignatureStatus = status };
    }

    /// <summary>The .minisig published beside the manifest, or null if the release carries none.
    ///
    /// Only a 404 means "not signed". Any other status throws rather than reading as unsigned,
    /// because a signature fetch that merely fails looks exactly like one an attacker made fail, and
    /// letting a 500 mean "no signature here" hands over the downgrade for the price of breaking one
    /// asset. Note where that guarantee actually lands: this throws
    /// <see cref="HttpRequestException"/>, which <see cref="CompositeFeed"/> treats as "feed
    /// unavailable" and moves past. Under <see cref="ManifestSignaturePolicy.Required"/> that is
    /// still closed, because the only feed behind this one is unsignable and gets refused too.
    /// Under verify-if-present the chain does land on that unsigned fallback — the same place a 404
    /// would have left it, which is the policy's known cost, not a hole this method can close.</summary>
    private async Task<string?> FetchSignatureAsync(CancellationToken ct)
    {
        if (_policy == ManifestSignaturePolicy.Off)
            return null;

        using var resp = await http.GetAsync(LauncherConfig.LatestManifestSignatureUrl, ct);
        if (resp.StatusCode == HttpStatusCode.NotFound)
            return null;
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadAsStringAsync(ct);
    }

    /// <summary>UTF-8 decode minus a byte-order mark, spelled out in bytes because the BOM is part
    /// of what the signature covers and must not be trimmed before the check.</summary>
    private static string Utf8NoBom(byte[] bytes) =>
        Encoding.UTF8.GetString(
            bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF
                ? bytes.AsSpan(3)
                : bytes);
}

/// <summary>The fallback (and the only path that sees prereleases): list releases via the GitHub
/// API and synthesize a manifest from the newest non-draft release's assets, taking checksums
/// from its SHA256SUMS file, else from GitHub's own per-asset sha256 digest.
///
/// This manifest is assembled here, on the client, out of an API listing — there is no published
/// document for anyone to have signed, so this feed can never be signature-checked. That is fine
/// while the policy is verify-if-present and fatal once it is required; see
/// <see cref="ReleaseSigning.EnsureUnsignedFeedAllowed"/>.</summary>
public sealed partial class GitHubApiFeed(HttpClient http, ManifestSignaturePolicy? policy = null)
    : IReleaseFeed
{
    private readonly ManifestSignaturePolicy _policy = policy ?? ReleaseSigning.ResolvePolicy();

    public string Name => "GitHub Releases API (fallback)";

    public async Task<ReleaseManifest?> FetchLatestAsync(CancellationToken ct)
    {
        ReleaseSigning.EnsureUnsignedFeedAllowed(_policy, Name);

        var json = await http.GetStringAsync(LauncherConfig.ReleasesApiUrl, ct);
        var release = PickLatest(json);
        if (release is null)
            return null;

        // The SHA256SUMS file is small — fetch it for checksums when present.
        var sums = new Dictionary<string, string>();
        var sumsAsset = release.Assets.FirstOrDefault(a =>
            a.Name.StartsWith("SHA256SUMS-", StringComparison.Ordinal));
        if (sumsAsset is not null)
        {
            try { sums = ChecksumFile.Parse(await http.GetStringAsync(sumsAsset.BrowserDownloadUrl, ct)); }
            catch (HttpRequestException) { /* digests below still cover us */ }
        }
        return Synthesize(release, sums);
    }

    /// <summary>Newest non-draft release from a /releases listing (prereleases included).</summary>
    public static ApiRelease? PickLatest(string releasesJson) =>
        JsonSerializer.Deserialize<List<ApiRelease>>(releasesJson, ReleaseManifest.JsonOptions)?
            .FirstOrDefault(r => !r.Draft);

    /// <summary>Build a manifest from bare release assets. Checksum precedence: SHA256SUMS entry,
    /// then the GitHub asset digest. A file with NO checksum from either source is dropped —
    /// the installer never installs unverified bits (ADR-0015 invariant #2).</summary>
    public static ReleaseManifest Synthesize(ApiRelease release, IReadOnlyDictionary<string, string> sums)
    {
        var version = release.TagName.TrimStart('v');
        var fat = new Dictionary<string, ManifestFile>();
        var core = new Dictionary<string, ManifestFile>();
        ManifestAssets? assetsPack = null;

        foreach (var a in release.Assets)
        {
            var sha = sums.TryGetValue(a.Name, out var s) ? s
                : a.Digest?.StartsWith("sha256:", StringComparison.Ordinal) == true
                    ? a.Digest["sha256:".Length..].ToLowerInvariant()
                    : null;
            if (sha is null)
                continue;

            if (AssetsPackName().Match(a.Name) is { Success: true } m)
            {
                assetsPack = new ManifestAssets(a.Name, m.Groups[1].Value, a.Size, sha, a.BrowserDownloadUrl);
                continue;
            }
            if (PlatformKey.TryParseZipName(a.Name, version, out var key, out var root, out var isCore))
                (isCore ? core : fat)[key] = new ManifestFile(a.Name, root, a.Size, sha, a.BrowserDownloadUrl);
        }

        var platforms = fat.Keys.Union(core.Keys).ToDictionary(
            k => k,
            k => new ManifestPlatform(fat.GetValueOrDefault(k), core.GetValueOrDefault(k)));

        return new ReleaseManifest
        {
            Version = version,
            Tag = release.TagName,
            Channel = release.Prerelease ? "prerelease" : "stable",
            NotesUrl = release.HtmlUrl,
            Assets = assetsPack,
            Platforms = platforms,
            NotesBody = release.Body,
            Prerelease = release.Prerelease,
            SignatureStatus = "unsigned (assembled from the GitHub API, nothing to verify)",
        };
    }

    [System.Text.RegularExpressions.GeneratedRegex(
        "^(?:" + LauncherConfig.ArtifactPrefixPattern + @")-assets-([0-9a-f]{12})\.zip$")]
    private static partial System.Text.RegularExpressions.Regex AssetsPackName();

    public sealed record ApiRelease(
        [property: JsonPropertyName("tag_name")] string TagName,
        [property: JsonPropertyName("prerelease")] bool Prerelease,
        [property: JsonPropertyName("draft")] bool Draft,
        [property: JsonPropertyName("html_url")] string? HtmlUrl,
        [property: JsonPropertyName("body")] string? Body,
        [property: JsonPropertyName("assets")] List<ApiAsset> Assets);

    public sealed record ApiAsset(
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("size")] long Size,
        [property: JsonPropertyName("browser_download_url")] string BrowserDownloadUrl,
        [property: JsonPropertyName("digest")] string? Digest);
}

/// <summary>Manifest first, API fallback. Network failure → null + a reason the UI can show;
/// the caller NEVER blocks Play on this (ADR-0015 invariant #1).</summary>
public sealed class CompositeFeed(params IReleaseFeed[] feeds)
{
    /// <summary>Note what is NOT caught below: <see cref="ManifestSignatureException"/> propagates out
    /// of the whole chain. Falling through to the next feed after a signature failure would turn
    /// "this manifest was tampered with" into "try a source with weaker checks", which is the
    /// attack, not the recovery. It has to reach the caller as an error with its message intact —
    /// a soft "feed unavailable" return would be summarized away by the UI, and the one thing a
    /// failed signature check must not be is quiet. Play is unaffected either way: nothing here
    /// touches the installed build.
    ///
    /// Its <see cref="UnsignableFeedException"/> subclass IS caught, because "this feed has no
    /// signature to check" is a reason to try the next feed, not to stop.</summary>
    public async Task<(ReleaseManifest? Manifest, string Detail)> FetchLatestAsync(CancellationToken ct)
    {
        var notes = new List<string>();
        foreach (var feed in feeds)
        {
            try
            {
                var m = await feed.FetchLatestAsync(ct);
                if (m is not null)
                    return (m, $"via {feed.Name}");
                notes.Add($"{feed.Name}: no release found");
            }
            catch (UnsignableFeedException ex)
            {
                notes.Add($"{feed.Name}: {ex.Message}");
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                notes.Add($"{feed.Name}: timed out");
            }
            catch (HttpRequestException ex)
            {
                notes.Add($"{feed.Name}: {ex.Message}");
            }
        }
        return (null, string.Join("; ", notes));
    }
}
