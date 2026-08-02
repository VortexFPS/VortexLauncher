namespace Launcher.Core;

/// <summary>States a <see cref="SourceBuildJob"/> passes through. Strings rather than an enum because
/// they cross the wire to a panel and to Conductor, and a number that has to be looked up in a table on
/// the other side of a repo boundary is the kind of thing that gets looked up wrongly.</summary>
public static class SourceBuildStates
{
    public const string Running = "running";
    public const string Succeeded = "succeeded";
    public const string Failed = "failed";
    public const string Cancelled = "cancelled";
}

/// <summary>One source build, as a plane sees it.</summary>
public sealed record SourceBuildJob
{
    public required string SourceName { get; init; }
    public required string Ref { get; init; }
    public required string State { get; init; }
    public required DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset? FinishedAt { get; init; }

    /// <summary>The build store id, once there is one. This is the handle `builds pin` and
    /// `server create --build` take, and it is the whole reason a plane waits for this job.</summary>
    public string? BuildId { get; init; }

    public string? Sha { get; init; }
    public string? Preset { get; init; }

    /// <summary>The <see cref="SourceFailure"/> code, so a caller can tell "install Godot" from "this
    /// ref does not build" without reading prose.</summary>
    public string? Code { get; init; }

    public string? Error { get; init; }

    /// <summary>The tail of the toolchain output. Bounded — see <see cref="SourceBuildJobs"/>.</summary>
    public IReadOnlyList<string> Log { get; init; } = [];
}

/// <summary>The runner's one source-build slot.
///
/// <para><b>Why a job and not a call.</b> Every other runner verb answers inside the 30-second command
/// envelope; a source build clones a repo, compiles, exports and packages, and takes tens of minutes. So
/// the route starts this and returns, and the plane polls. That keeps the protocol unchanged — no new
/// frame kind, no streaming path — which is the same reason the runner API is a single dispatcher in the
/// first place.</para>
///
/// <para><b>Why exactly one at a time.</b> Two builds on one box are not twice as fast: they contend for
/// the same cores, the same disk and, if they name the same source, literally the same checkout
/// directory, where the second one's `git checkout --force` would pull the tree out from under the
/// first. Refusing the second is the honest answer and costs a caller one retry.</para></summary>
public sealed class SourceBuildJobs(LauncherPaths paths)
{
    /// <summary>Log lines retained. The same order as the supervisor's ring and for the same reason: a
    /// plane polling every few seconds wants the recent past, and an unbounded buffer on a
    /// twenty-minute export is a slow leak in a process that is meant to stay up for weeks.</summary>
    public const int LogRingLines = 500;

    private readonly object _gate = new();
    private readonly Queue<string> _log = new();

    private SourceBuildJob? _job;
    private CancellationTokenSource? _cts;

    /// <summary>True while a build is in flight.</summary>
    public bool Busy
    {
        get { lock (_gate) return _job?.State == SourceBuildStates.Running; }
    }

    /// <summary>The current or most recent job, with its log attached, or null if none has run.
    ///
    /// The last job is kept after it ends rather than cleared, because the poll that discovers a build
    /// finished is a separate call from the one that started it — dropping the record on completion
    /// would make success and "never heard of it" the same answer.</summary>
    public SourceBuildJob? Current
    {
        get
        {
            lock (_gate)
                return _job is null ? null : _job with { Log = _log.ToArray() };
        }
    }

    /// <summary>Begin a build. Throws <see cref="InvalidOperationException"/> when one is already
    /// running, which the dispatcher turns into a 409.</summary>
    public SourceBuildJob Start(SourceSpec spec, bool fetchMaps, SourceStore? store = null)
    {
        lock (_gate)
        {
            if (_job?.State == SourceBuildStates.Running)
                throw new InvalidOperationException(
                    $"a source build of '{_job.SourceName}' is already running on this box; " +
                    "builds contend for the same cores, disk and checkout, so they are taken one at " +
                    "a time. Poll it, or cancel it first.");

            _log.Clear();
            _job = new SourceBuildJob
            {
                SourceName = spec.Name,
                Ref = spec.Ref,
                State = SourceBuildStates.Running,
                StartedAt = DateTimeOffset.UtcNow,
                Preset = spec.Target ?? SourceProvider.DefaultPreset(),
            };

            _cts = new CancellationTokenSource();

            // Not awaited, and that is the point. The envelope this was called from is answered
            // immediately with the record above; everything after this line outlives the request.
            _ = RunAsync(spec, fetchMaps, store, _cts.Token);

            return _job with { Log = [] };
        }
    }

    private async Task RunAsync(SourceSpec spec, bool fetchMaps, SourceStore? store,
        CancellationToken ct)
    {
        var provider = new SourceProvider(paths, new BuildStore(paths));
        var log = new Progress<string>(Append);

        try
        {
            var result = await provider.BuildAsync(spec, fetchMaps, log, ct);

            lock (_gate)
                _job = _job! with
                {
                    State = result.Ok ? SourceBuildStates.Succeeded : SourceBuildStates.Failed,
                    FinishedAt = DateTimeOffset.UtcNow,
                    BuildId = result.BuildId,
                    Sha = result.Sha,
                    Preset = result.Preset ?? _job!.Preset,
                    Code = result.Code,
                    Error = result.Error,
                };

            // Recorded on the spec too, so `sources` answers "what did this last build" without the
            // caller having held on to a job it may not have been running when it started.
            if (result.Ok && store is not null)
                store.Save(spec with
                {
                    LastBuildId = result.BuildId,
                    LastBuiltSha = result.Sha,
                    LastBuiltAt = DateTimeOffset.UtcNow,
                });
        }
        catch (OperationCanceledException)
        {
            lock (_gate)
                _job = _job! with
                {
                    State = SourceBuildStates.Cancelled,
                    FinishedAt = DateTimeOffset.UtcNow,
                };
        }
        catch (Exception ex)
        {
            // BuildAsync converts the failures it knows about into a Result, so anything arriving here
            // is unexpected. It still has to land on the job: a background task that throws into
            // nowhere would leave the state stuck on "running" for as long as this process lives.
            lock (_gate)
                _job = _job! with
                {
                    State = SourceBuildStates.Failed,
                    FinishedAt = DateTimeOffset.UtcNow,
                    Code = SourceFailure.StepFailed,
                    Error = ex.Message,
                };
        }
    }

    /// <summary>Ask the running build to stop. False when there is nothing running.</summary>
    public bool Cancel()
    {
        lock (_gate)
        {
            if (_job?.State != SourceBuildStates.Running || _cts is null)
                return false;

            _cts.Cancel();
            return true;
        }
    }

    private void Append(string line)
    {
        lock (_gate)
        {
            _log.Enqueue(line);
            while (_log.Count > LogRingLines)
                _log.Dequeue();
        }
    }
}
