using System.Text.Json;
using Launcher.Protocol;

namespace Launcher.WebServer;

/// <summary>The stored hash this plane authenticates callers against, read from the runner's own
/// runner.json.
///
/// Two ways to get the hash here were possible: read the runner's config, or have the install step
/// write a copy into this project's own configuration. The copy loses. It makes `vortex runner
/// new-token` a two-file update, and the failure when somebody adds a third caller and misses one is
/// that a token the operator believes they revoked keeps working. Rotation that does not revoke is
/// worse than no rotation. One file, one hash, and this end only ever reads it.
///
/// It does not weaken the rule that the control plane cannot touch the box. This is a read of one
/// field of one file; every operation on this box is still a protocol message to a runner, and this
/// project still does not link Launcher.Core.</summary>
public sealed class RunnerTokenStore
{
    private readonly string _path;
    private readonly ILogger<RunnerTokenStore> _log;
    private readonly object _gate = new();
    private DateTime _stamp = DateTime.MinValue;
    private string? _sha256;

    public RunnerTokenStore(WebServerOptions options, ILogger<RunnerTokenStore> log)
    {
        _path = options.RunnerConfigPath ?? RunnerLayout.DefaultRunnerConfigPath;
        _log = log;

        // Said once at startup, because the alternative is an operator staring at a panel that 401s
        // every request with nothing anywhere explaining why. This was the fresh-install experience.
        if (Current() is null)
            _log.LogWarning(
                "no control plane token in {Path}; every request will be rejected until " +
                "`vortex runner install-service` or `vortex runner new-token` mints one", _path);
    }

    public bool Verify(string? presented) => RunnerToken.Verify(presented, Current());

    /// <summary>Re-read when the file changes, so `vortex runner new-token` takes effect on the next
    /// request instead of at the next restart. Rotating a credential must not require an outage, or it
    /// becomes a change-control event nobody schedules.</summary>
    private string? Current()
    {
        lock (_gate)
        {
            var stamp = File.Exists(_path) ? File.GetLastWriteTimeUtc(_path) : DateTime.MinValue;
            if (stamp == _stamp)
                return _sha256;

            // No file is an answer about this runner and not a failure to get one: nobody has minted a
            // token here. So it caches like any other answer, and a box in that state costs one
            // File.Exists per request instead of a failed open and a log line.
            if (stamp == DateTime.MinValue)
                return Cache(stamp, null);

            return TryRead(out var sha256) ? Cache(stamp, sha256) : null;
        }
    }

    private string? Cache(DateTime stamp, string? sha256)
    {
        _stamp = stamp;
        _sha256 = sha256;
        return sha256;
    }

    /// <summary>False only when the file could not be opened, which is the one outcome that must not
    /// be remembered against its timestamp.
    ///
    /// `Save` on the runner side writes a temp file and renames it over this one, so a scanner or a
    /// concurrent reader holding it open across that rename is an ordinary passing condition. Caching
    /// that miss would answer every later request from a "no token" nobody ever read, and the panel
    /// would 401 until somebody restarted it: a rotation that appears to have bricked the control
    /// plane, from a blip that had cleared a millisecond later. Denying this one request and retrying
    /// on the next is both fail-closed and self-healing.
    ///
    /// Malformed content is the opposite case and returns true: the bytes were read and they are bad,
    /// which stays denied until the file changes, and says so once instead of once per request.</summary>
    private bool TryRead(out string? sha256)
    {
        sha256 = null;
        try
        {
            sha256 = ManagementProtocol.Deserialize<TokenView>(File.ReadAllText(_path))?.WebToken?.Sha256;
            return true;
        }
        catch (JsonException ex)
        {
            // Denies everything, deliberately. A control plane that fell open when it could not make
            // sense of its credential would be the worst possible failure mode for the one file that
            // decides who may restart a game server.
            _log.LogWarning(ex, "the control plane token in {Path} is malformed; every request will " +
                "be rejected until it is repaired or `vortex runner new-token` rewrites it", _path);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.LogWarning(ex, "could not read the control plane token from {Path}", _path);
            return false;
        }
    }

    /// <summary>The one field of runner.json this project has any business knowing about. The rest of
    /// that file is Launcher.Core's, and the deserializer skips unmapped members, so this stays valid
    /// as the runner's own config grows.</summary>
    private sealed record TokenView(RunnerWebToken? WebToken);
}
