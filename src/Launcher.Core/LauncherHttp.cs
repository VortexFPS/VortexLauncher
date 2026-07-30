using System.Net.Http.Headers;

namespace Launcher.Core;

/// <summary>One place that builds a configured HttpClient. The user agent is not decoration: GitHub's
/// API rejects requests without one, and the CLI, the Desktop launcher and the runner all talk to the
/// same endpoints, so they should identify themselves the same way.</summary>
public static class LauncherHttp
{
    public static HttpClient Create(TimeSpan? timeout = null)
    {
        var http = new HttpClient { Timeout = timeout ?? TimeSpan.FromSeconds(30) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd(LauncherConfig.UserAgent);
        http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return http;
    }

    /// <summary>Manifest first, GitHub API second. The order matters: the manifest path is a plain
    /// redirect fetch with no rate limit, and the API path is capped at 60 requests an hour for
    /// unauthenticated callers. The fallback exists because releases/latest ignores prereleases, which
    /// today makes it the only path that sees anything at all.</summary>
    public static CompositeFeed DefaultFeed(HttpClient http) =>
        new(new ManifestFeed(http), new GitHubApiFeed(http));
}
