using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Launcher.Core;

namespace Launcher.Desktop.ViewModels;

/// <summary>Building the game from a git checkout, from the launcher window.
///
/// This is the same <see cref="SourceProvider"/> the CLI's `vortex source build` drives, over the same
/// <see cref="SourceStore"/>, so a source configured here is the one the CLI sees and the reverse. That
/// was the point of putting it here rather than giving the window its own idea of what a source is: two
/// stores would disagree the first time anyone used both.
///
/// <para><b>A build never becomes the installed game on its own.</b> It lands in the build store and
/// stops there, and "Use this build" is a second, deliberate press. That is the rule the whole update
/// flow is written around (ADR-0015 §6) and a locally compiled build has no claim to an exception —
/// especially not this one, which an operator may well be running to test a branch they do not want to
/// be left on.</para></summary>
public partial class SourceBuildViewModel : ObservableObject
{
    /// <summary>Log lines retained. Bounded because a twenty-minute export writes more than a window
    /// needs to hold; the tail is the useful part, and a failure's last lines are also lifted into
    /// <see cref="StatusDetail"/> because SourceProvider puts them in the exception.</summary>
    private const int MaxLogLines = 500;

    /// <summary>How often the log view is refreshed while lines are pouring in. Rebuilding a 500-line
    /// string on every line during an MSBuild burst is work the UI thread does instead of drawing;
    /// coalescing to this makes it one rebuild per frame-ish, and nothing scrolls faster than a reader.</summary>
    private const long LogPublishIntervalMs = 120;

    /// <summary>Only here to satisfy InstallService, which takes a downloader because its other job is
    /// installing releases. Nothing on this screen downloads: the one call made of it is Pin, which
    /// rewrites current.json and touches the network never.</summary>
    private readonly HttpClient _http = LauncherHttp.Create();

    private readonly Queue<string> _lines = new();
    private readonly DispatcherTimer _ticker;

    private SourceStore _sources;
    private SourceProvider _provider;
    private InstallService _installs;
    private BuildStore _builds;

    private CancellationTokenSource? _cts;
    private BuildRecord? _built;
    private DateTimeOffset _startedAt;
    private long _lastPublishedAt;

    [ObservableProperty] private bool _isOpen;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(BuildCommand))]
    [NotifyCanExecuteChangedFor(nameof(CheckCommand))]
    private string _sourceName = "game";

    /// <summary>The picker's selection, deliberately a different property from <see cref="SourceName"/>.
    ///
    /// A ComboBox with SelectedItem bound two-way writes null back the moment the bound value is not in
    /// its list — which is every keystroke of a name that does not exist yet, and that is precisely the
    /// case that creates a source. Two controls writing one property would make typing a new name clear
    /// the box it was being typed into.</summary>
    [ObservableProperty] private string? _pickedSource;

    [ObservableProperty] private string _repo = $"{LauncherConfig.RepoUrl}.git";
    [ObservableProperty] private string _reference = "main";
    [ObservableProperty] private string _godotPath = "";

    /// <summary>On by default: a build with no maps starts and then finds nothing to load, which reads
    /// as a broken game rather than an incomplete build. Off is for a checkout that already has them.</summary>
    [ObservableProperty] private bool _fetchMaps = true;

    [ObservableProperty] private string _selectedPreset = SourceProvider.DefaultPreset();

    // ---- status ------------------------------------------------------------------------------------
    //
    // Four properties rather than one status string, because a build runs for tens of minutes and the
    // three questions a person asks of it are different: is it alive, how long has it been, and what is
    // it doing. One line that answers all three by being overwritten answers none of them well — and the
    // outcome, which is the only part worth reading twice, gets scrolled away by the next progress tick.

    /// <summary>Running / Succeeded / Failed / Cancelled, or empty before the first run.</summary>
    [ObservableProperty] private string _statusHeadline = "";

    /// <summary>The outcome, or the reason for it. Survives until the next run: this is the part someone
    /// reads after looking away for twenty minutes.</summary>
    [ObservableProperty] private string _statusDetail = "";

    /// <summary>The most recent line the toolchain printed, so a quiet export still looks alive.</summary>
    [ObservableProperty] private string _currentActivity = "";

    /// <summary>mm:ss since the build started, ticking.</summary>
    [ObservableProperty] private string _elapsed = "";

    [ObservableProperty] private bool _succeeded;
    [ObservableProperty] private bool _failed;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(BuildCommand))]
    [NotifyCanExecuteChangedFor(nameof(CheckCommand))]
    [NotifyCanExecuteChangedFor(nameof(CancelCommand))]
    [NotifyCanExecuteChangedFor(nameof(CloseCommand))]
    [NotifyCanExecuteChangedFor(nameof(UseBuildCommand))]
    private bool _busy;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(UseBuildCommand))]
    private bool _builtReady;

    /// <summary>The log as one string rather than a list of lines, so it can be selected across lines
    /// and copied. A per-line ItemsControl looked the same and could not be selected at all, which made
    /// the one thing anybody wants from a failed build — the error text — impossible to get out.</summary>
    [ObservableProperty] private string _logText = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CopyLogCommand))]
    private bool _hasLog;

    public IReadOnlyList<string> Presets { get; } = SourceProvider.KnownPresets;

    /// <summary>Names already configured, so the window offers what exists instead of asking the
    /// operator to remember it.</summary>
    public ObservableCollection<string> KnownSources { get; } = [];

    /// <summary>Set by the view: only it can reach the TopLevel that owns the clipboard.</summary>
    public Func<string, Task>? CopyToClipboard { get; set; }

    /// <summary>Set by the window: whether the game this launcher started is still running. Pinning a
    /// build rewrites current.json and the running game is reading files out of the build it points
    /// at, which is the same refusal <c>ApplyStaged</c> makes for a downloaded release.</summary>
    public Func<bool>? IsGameRunning { get; set; }

    /// <summary>Raised after "Use this build" has repointed current.json, so the window can re-read
    /// what is installed rather than being told.</summary>
    public event Action? Applied;

    public SourceBuildViewModel(LauncherPaths paths)
    {
        Bind(paths);

        // A second, not a frame: this only moves a clock. It exists because the log can be silent for
        // minutes during an export, and a status area frozen at "02:14" is indistinguishable from a
        // launcher that has stopped responding.
        _ticker = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _ticker.Tick += (_, _) => Elapsed = Format(DateTimeOffset.UtcNow - _startedAt);
    }

    /// <summary>(Re)bind to an install root. Called when the settings change the root under us: the
    /// checkouts, the build store and current.json all live under it, so a stale binding here would
    /// build into one root and pin in another.</summary>
    [System.Diagnostics.CodeAnalysis.MemberNotNull(nameof(_sources), nameof(_provider),
        nameof(_installs), nameof(_builds))]
    public void Bind(LauncherPaths paths)
    {
        _sources = new SourceStore(paths);
        _builds = new BuildStore(paths);
        _provider = new SourceProvider(paths, _builds);
        // The same BuildStore instance the provider stages into, rather than the one InstallService
        // would construct for itself: Pin looks the build up through it, and a second reader of the
        // same file is one more thing that can be looking at a stale copy.
        _installs = new InstallService(paths, new DownloadService(_http), _builds);
    }

    public void Open()
    {
        RefreshKnownSources();
        LoadSpec(SourceName);
        IsOpen = true;
    }

    private void RefreshKnownSources()
    {
        KnownSources.Clear();
        foreach (var spec in _sources.List())
            KnownSources.Add(spec.Name);
    }

    /// <summary>Fill the form from a stored source, when the typed name names one. Silent when it does
    /// not: a name that does not exist yet is how a new source gets created, not an error.</summary>
    private void LoadSpec(string name)
    {
        if (_sources.Get(name) is not { } spec)
            return;

        Repo = spec.Repo;
        Reference = spec.Ref;
        SelectedPreset = spec.Target ?? SourceProvider.DefaultPreset();
        GodotPath = spec.GodotPath ?? "";
    }

    partial void OnSourceNameChanged(string value) => LoadSpec(value);

    /// <summary>Picking an existing source fills the name, and the line above then fills the rest.
    /// One-way on purpose: the picker is a shortcut into the form, not a mirror of it.</summary>
    partial void OnPickedSourceChanged(string? value)
    {
        if (value is { Length: > 0 })
            SourceName = value;
    }

    /// <summary>The form as a spec. Null, having reported why, when the name is not one that can become
    /// a directory.</summary>
    private SourceSpec? CurrentSpec()
    {
        try
        {
            SourceStore.ValidateName(SourceName);
        }
        catch (ArgumentException ex)
        {
            Fail(ex.Message);
            return null;
        }

        if (string.IsNullOrWhiteSpace(Repo))
        {
            Fail("A repository URL is needed before anything can be cloned.");
            return null;
        }

        var existing = _sources.Get(SourceName);
        return new SourceSpec
        {
            Name = SourceName,
            Repo = Repo.Trim(),
            Ref = string.IsNullOrWhiteSpace(Reference) ? "main" : Reference.Trim(),
            Target = SelectedPreset,
            GodotPath = string.IsNullOrWhiteSpace(GodotPath) ? null : GodotPath.Trim(),
            LastBuildId = existing?.LastBuildId,
            LastBuiltSha = existing?.LastBuiltSha,
            LastBuiltAt = existing?.LastBuiltAt,
        };
    }

    // ---- the log -----------------------------------------------------------------------------------

    private void Append(string line)
    {
        _lines.Enqueue(line);
        while (_lines.Count > MaxLogLines)
            _lines.Dequeue();

        CurrentActivity = line.Trim();

        // Coalesced. Publishing every line would rebuild the whole string per line and spend the burst
        // of an MSBuild run laying out text nobody can read at that speed.
        var now = Environment.TickCount64;
        if (now - _lastPublishedAt < LogPublishIntervalMs)
            return;

        _lastPublishedAt = now;
        PublishLog();
    }

    private void PublishLog()
    {
        LogText = string.Join(Environment.NewLine, _lines);
        HasLog = _lines.Count > 0;
    }

    private void ResetLog()
    {
        _lines.Clear();
        _lastPublishedAt = 0;
        PublishLog();
    }

    [RelayCommand(CanExecute = nameof(CanCopyLog))]
    private async Task CopyLogAsync()
    {
        if (CopyToClipboard is { } copy)
            await copy(LogText);
    }

    private bool CanCopyLog() => HasLog;

    // ---- status ------------------------------------------------------------------------------------

    private void Start(string headline)
    {
        Busy = true;
        Succeeded = false;
        Failed = false;
        StatusHeadline = headline;
        StatusDetail = "";
        CurrentActivity = "";
        _startedAt = DateTimeOffset.UtcNow;
        Elapsed = Format(TimeSpan.Zero);
        _ticker.Start();
    }

    private void Finish(bool ok, string headline, string detail)
    {
        _ticker.Stop();
        PublishLog();
        Busy = false;
        Succeeded = ok;
        Failed = !ok;
        StatusHeadline = headline;
        StatusDetail = detail;
        CurrentActivity = "";
        Elapsed = Format(DateTimeOffset.UtcNow - _startedAt);
    }

    /// <summary>A refusal that never started anything — a bad name, an empty repo.</summary>
    private void Fail(string detail)
    {
        StatusHeadline = "Can't start";
        StatusDetail = detail;
        Failed = true;
        Succeeded = false;
    }

    private static string Format(TimeSpan elapsed) =>
        elapsed < TimeSpan.Zero ? "" : $"{(int)elapsed.TotalMinutes:00}:{elapsed.Seconds:00}";

    // ---- commands ----------------------------------------------------------------------------------

    /// <summary>Preflight. Worth its own button because the alternative is learning that this box has
    /// no Godot twenty minutes into a build that was never going to finish.</summary>
    [RelayCommand(CanExecute = nameof(CanStart))]
    private async Task CheckAsync()
    {
        if (CurrentSpec() is not { } spec)
            return;

        ResetLog();
        Start("Checking");

        try
        {
            // Constructed on the UI thread so its callbacks come back to it; Inspect itself runs off
            // the UI thread because its vx doctor pass can spend a minute building vx's task runner.
            var log = new Progress<string>(Append);
            var report = await Task.Run(() => _provider.Inspect(spec, log));

            Append($"checkout   {report.Checkout}");
            Append($"target     {report.Preset} ({report.PlatformKey ?? "unmapped"})");
            Append($"engine     {report.EngineVersion ?? "?"} pinned by {report.EngineTag ?? "(no tag)"}");

            foreach (var tool in report.Tools)
                Append($"{tool.Name,-10} {(tool.Ok ? tool.Path : "unusable — see below")}");

            if (report.VxDoctor is { } doctor)
            {
                if (doctor.UnsupportedSchema is { } schema)
                    Append($"vx doctor  speaks schema {schema}, this launcher reads {Vx.SupportedSchema}");
                else
                    foreach (var check in doctor.Checks.Where(c => c.Status != "ok"))
                        Append($"vx {(check.Required ? "!" : "-")} {check.Name}: {check.Detail}");
            }

            foreach (var problem in report.Problems)
                Append(problem);

            Finish(report.Ready,
                report.Ready ? "Ready to build" : "Not ready",
                report.Ready
                    ? "Everything this build needs is present."
                    : $"{report.Problems.Count} thing(s) to fix — listed at the end of the log.");
        }
        catch (Exception ex)
        {
            Finish(false, "Check failed", ex.Message);
        }
    }

    [RelayCommand(CanExecute = nameof(CanStart))]
    private async Task BuildAsync()
    {
        if (CurrentSpec() is not { } spec)
            return;

        // Saved before the build, not after: the checkout is keyed on the name, so a build that dies
        // half way still leaves a source the operator can inspect and retry without retyping it.
        _sources.Save(spec);
        RefreshKnownSources();

        ResetLog();
        Start("Building");
        BuiltReady = false;
        _built = null;
        _cts = new CancellationTokenSource();
        var token = _cts.Token;

        try
        {
            var log = new Progress<string>(Append);

            // Task.Run around the whole thing, and this is not ceremony. BuildAsync is async but it is
            // not async all the way down: the editor probe, the engine-pin read, vx's warm-up and above
            // all Stage — which copies the finished build, some gigabytes of it — are synchronous, and
            // each await inside resumes on the UI thread. Left alone, the window locks solid for the
            // length of the copy right at the point the operator is watching for it to finish. The
            // Progress above still marshals every line back here, so the log keeps flowing.
            var result = await Task.Run(() => _provider.BuildAsync(spec, FetchMaps, log, token), token);

            if (!result.Ok)
            {
                Finish(false, "Build failed", result.Error ?? "The build failed.");
                return;
            }

            _sources.Save(spec with
            {
                LastBuildId = result.BuildId,
                LastBuiltSha = result.Sha,
                LastBuiltAt = DateTimeOffset.UtcNow,
            });

            _built = result.BuildId is null ? null : _builds.Get(result.BuildId);
            BuiltReady = _built is not null;

            Finish(true, "Built",
                BuiltReady
                    ? $"{result.BuildId} is in the build store. Press Use this build to play it."
                    : $"Built {result.BuildId}, but it is not readable back out of the build store.");
        }
        catch (OperationCanceledException)
        {
            Finish(false, "Cancelled", "The build was stopped. Its checkout and anything already " +
                                       "downloaded are kept, so starting again resumes from there.");
        }
        catch (Exception ex)
        {
            Finish(false, "Build failed", ex.Message);
        }
        finally
        {
            _cts = null;
        }
    }

    /// <summary>A name and a repo are the whole precondition; everything else the build reports on
    /// itself, and a disabled button with no explanation is worse than a build that refuses out loud.</summary>
    private bool CanStart() => !Busy && !string.IsNullOrWhiteSpace(SourceName);

    [RelayCommand(CanExecute = nameof(CanCancel))]
    private void Cancel()
    {
        StatusHeadline = "Cancelling";
        _cts?.Cancel();
    }

    private bool CanCancel() => Busy;

    /// <summary>Point the launcher at the build that was just compiled. Same refusal as the release
    /// path: the swap rewrites current.json and the running game is reading out of what it names.</summary>
    [RelayCommand(CanExecute = nameof(CanUseBuild))]
    private void UseBuild()
    {
        if (_built is null)
            return;

        if (IsGameRunning?.Invoke() == true)
        {
            StatusDetail = "Close Vortex Arena first — this replaces the build it is running from.";
            return;
        }

        try
        {
            var state = _installs.Pin(_built);
            StatusHeadline = "Installed";
            StatusDetail = $"Now on {state.Version} ({_built.Id}).";
            BuiltReady = false;
            Applied?.Invoke();
        }
        catch (Exception ex)
        {
            Finish(false, "Couldn't switch to it", ex.Message);
        }
    }

    private bool CanUseBuild() => BuiltReady && !Busy;

    /// <summary>Closed only while nothing is running: the sheet owns the cancellation token, so
    /// dismissing it mid-build would leave a Godot export with nothing able to stop it.</summary>
    [RelayCommand(CanExecute = nameof(CanClose))]
    private void Close() => IsOpen = false;

    private bool CanClose() => !Busy;
}
