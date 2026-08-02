using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;

namespace Launcher.Core;

/// <summary>The game repo's own task runner, `./vx`, as the launcher sees it.
///
/// vx is the front door the game repo added for its build tooling, and its plan names this launcher as
/// a consumer rather than a wrapper: `--json` is versioned and documented there as a shipping interface
/// across the repo boundary. So where vx already owns a step, the launcher drives vx instead of the
/// script underneath it.
///
/// <para><b>Which steps, and why only those.</b> The launcher uses vx for the two CONTENT DOWNLOADS —
/// the export template and the maps — because vx ported both off Python onto HttpClient. That is not a
/// stylistic preference: python.org's macOS installer ships an OpenSSL that ignores the system keychain,
/// so tools/data/fetch-engine-template.py dies with CERTIFICATE_VERIFY_FAILED four retries deep on a Mac
/// that is otherwise perfectly set up. Same lockfile, same sha256, same destination directory; platform
/// TLS instead of urllib's. The launcher inherited that failure by calling the script, and stops
/// inheriting it by calling vx.</para>
///
/// <para><b>And which steps deliberately NOT.</b> Three of vx's commands look like they fit and do not:
/// <list type="bullet">
/// <item><c>vx export</c> resolves its OWN Godot through Env.FindGodot. The launcher resolves an editor
/// and then checks it against the checkout's engine pin (<see cref="GodotEditor.RequireMatches"/>); an
/// export that went and found a different editor would walk straight past that check, which is the one
/// thing standing between an operator and a build that compiles cleanly and misbehaves at runtime.</item>
/// <item><c>vx package</c> takes the first `bash` on PATH. On a default Windows install that is
/// System32\bash.exe, the WSL launcher — the exact trap <see cref="BuildTools.ResolveBash"/> exists to
/// avoid, and routing through vx would re-enter it.</item>
/// <item><c>vx build</c> names the game's csproj literally, where <see cref="GameCheckout.GameProject"/>
/// discovers the single root csproj so the launcher does not carry the game's filename.</item>
/// </list>
/// In each case the launcher's existing path is the more careful one, so "use the repo's own door"
/// would be a regression rather than a tidy-up.</para>
///
/// <para><b>Absent on older refs.</b> vx landed 2026-08-01 and the launcher builds arbitrary refs, so
/// <see cref="Find"/> returns null rather than throwing and every call site keeps the script it used
/// before as its fallback. That is the same judgement <see cref="GameCheckout.Require"/> makes, one step
/// softer: a ref that predates vx is still buildable, it just takes the older road.</para></summary>
public sealed class Vx
{
    /// <summary>The --json envelope shape this launcher knows how to read. vx bumps its own number when
    /// the shape changes incompatibly, and treats that as a breaking change precisely because something
    /// in another repo — this — is reading it. Guarding on it means a newer game checkout reports
    /// "speaks a schema I do not read" instead of being silently misparsed into a clean bill of health.</summary>
    public const int SupportedSchema = 1;

    /// <summary>Environment every vx invocation gets, on top of the launcher's own.
    ///
    /// <para>vx's shim compiles its task runner with `dotnet build`, and MSBuild's default is to leave
    /// worker nodes alive for about fifteen minutes so the next build can reuse them. For a long-lived
    /// developer session that is a win; for a short build spawned by another tool it means the workers
    /// outlive the thing that wanted them and keep a handle on <c>obj/…/vx.dll</c>. The next rebuild then
    /// dies on <c>CS2012 … being used by another process</c>, which reaches a caller as vx refusing to
    /// start for no visible reason. Observed here, exactly that way.</para>
    ///
    /// <para>The shim passes <c>-nodeReuse:false</c> for itself now, but this stays: the launcher builds
    /// whatever ref it is pointed at, and a ref from before that fix is still a ref it has to build.</para></summary>
    public static IReadOnlyDictionary<string, string> BuildEnvironment { get; } =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["MSBUILDDISABLENODEREUSE"] = "1",
        };

    private readonly string _checkout;

    private Vx(string checkout) => _checkout = checkout;

    /// <summary>The shim's filename. Two files, one per platform, both committed: the sh one carries the
    /// logic on POSIX and the .cmd one repeats it for cmd.exe.</summary>
    private static string ShimName => OperatingSystem.IsWindows() ? "vx.cmd" : "vx";

    public string ShimPath => Path.Combine(_checkout, ShimName);

    /// <summary>vx in this checkout, or null if the ref predates it.</summary>
    public static Vx? Find(string checkout) =>
        File.Exists(Path.Combine(checkout, ShimName)) ? new Vx(checkout) : null;

    /// <summary>The executable and argument vector for a vx invocation.
    ///
    /// <para><b>The shim is named by its FULL path, and a bare `vx.cmd` is wrong even with the checkout
    /// as the working directory.</b> Windows has an opt-in hardening setting,
    /// <c>NoDefaultCurrentDirectoryInExePath</c>, which stops cmd.exe resolving a command against the
    /// current directory — it is set on this project's own dev box, and under it `cmd /c vx.cmd` fails
    /// with "not recognized as an internal or external command" from inside a directory that plainly
    /// contains one. Relying on the working directory would have made this work on some machines and
    /// not others, which is the worst of the available outcomes.</para>
    ///
    /// <para><b>The quoting then holds because no argument here contains a space</b>, and that is a
    /// constraint rather than a coincidence — see the guard below. cmd's rule for what follows `/c`
    /// (from `cmd /?`) is that it keeps the quotes when there are exactly two of them, nothing special
    /// between them, whitespace between them, and the quoted text names an executable. A checkout path
    /// with a space satisfies all four, so the quotes survive. A path without one fails the whitespace
    /// clause, so cmd strips the pair — and with no space anywhere there is nothing left to misparse.
    /// Both cases work; what would break both is a second quoted argument, because "exactly two quote
    /// characters" would stop being true.</para>
    ///
    /// <para>POSIX goes through `sh` rather than exec'ing the file, so a checkout on a filesystem that
    /// dropped the exec bit still works; the shim is `#!/bin/sh` and says POSIX sh in its header, so
    /// running it under sh is what it already expects.</para></summary>
    public (string Exe, IReadOnlyList<string> Args) Command(params string[] args)
    {
        // Enforced rather than trusted. Every caller passes literals plus a platform key read out of
        // engine.lock.json, so this cannot fire today; if that stops being true it should fail here,
        // loudly, rather than as a cmd.exe parse that quietly runs something else.
        foreach (var arg in args)
            if (arg.Contains(' ') || arg.Contains('"'))
                throw new SourceBuildException(SourceFailure.StepFailed,
                    $"vx argument '{arg}' contains a space or a quote. The Windows invocation goes " +
                    "through cmd.exe, whose /c quoting only survives while the shim path is the one " +
                    "quoted thing on the line. Pass this some other way rather than loosening it.");

        var argv = new List<string>();
        string exe;

        if (OperatingSystem.IsWindows())
        {
            exe = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe");
            argv.Add("/c");
            argv.Add(ShimPath);
        }
        else
        {
            exe = "/bin/sh";
            argv.Add(ShimPath);
        }

        argv.AddRange(args);
        return (exe, argv);
    }

    /// <summary>Run `vx doctor --json` and read the envelope, or null when there is nothing usable to
    /// read.
    ///
    /// <para>Best-effort by design, and every failure here returns null rather than throwing: this
    /// informs a preflight report, it does not gate one. The launcher's own checks answer the questions
    /// that decide whether a build can start (engine skew against the pin, cross-OS presets, whether
    /// there is a checkout at all), and vx answers questions the launcher does not ask — whether the
    /// editor's stock export templates are installed, how many map packs are present. A doctor that
    /// could not run should subtract nothing.</para>
    ///
    /// <para>A non-zero exit is NOT a failure to report. doctor exits 1 when a required item is missing,
    /// which is a finding and the most interesting one it has; only the absence of a parseable envelope
    /// means there is nothing to say.</para></summary>
    /// <param name="log">Receives the shim's stderr as it arrives. Worth wiring up even though the
    /// caller wants the JSON on stdout: on a cold checkout the shim's first act is to build vx's own
    /// task runner, which takes tens of seconds and announces itself on stderr. Without this that time
    /// is a silent stall in a command an operator expects to answer quickly.</param>
    public VxDoctorReport? Doctor(TimeSpan timeout, IProgress<string>? log = null) =>
        Invoke(["doctor", "--json"], timeout, log, out var stdout) is null ? null : Parse(stdout);

    /// <summary>Run the shim once for nothing, so that its own build happens HERE.
    ///
    /// <para>The shim compiles vx's task runner on a cold checkout, and that compile can fail for reasons
    /// that have nothing to do with the caller — a restore that cannot reach nuget.org, or its output
    /// still being held by a previous build. Discovering that in the middle of the template fetch turns a
    /// recoverable "use the scripts instead" into a failed build, because by then the launcher is
    /// committed. Doing it up front makes the answer to "can this checkout's vx run" a fact the build can
    /// branch on.</para>
    ///
    /// <para>Deliberately <c>--help</c>: it touches no network, reads no lockfile and changes nothing, so
    /// the only thing it can fail on is the thing being tested.</para></summary>
    public bool Warm(TimeSpan timeout, IProgress<string>? log = null) =>
        Invoke(["--help"], timeout, log, out _) == 0;

    /// <summary>Run vx, capture stdout, stream stderr to <paramref name="log"/>. Null on "could not run
    /// it at all", which every caller treats as "vx is not available here" rather than as a failure.</summary>
    private int? Invoke(string[] args, TimeSpan timeout, IProgress<string>? log, out string stdout)
    {
        stdout = "";
        var (exe, argv) = Command(args);

        var psi = new ProcessStartInfo(exe)
        {
            WorkingDirectory = _checkout,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (var arg in argv)
            psi.ArgumentList.Add(arg);
        foreach (var (name, value) in BuildEnvironment)
            psi.Environment[name] = value;

        var captured = new System.Text.StringBuilder();

        try
        {
            using var process = Process.Start(psi);
            if (process is null)
                return null;

            // Event-driven rather than ReadToEnd-then-wait: the envelope is small enough for a pipe
            // buffer, but a cold shim also pushes MSBuild's whole output down stderr, which is not, and
            // a wait-then-read deadlocks the moment one of the two buffers fills.
            process.OutputDataReceived += (_, e) =>
            {
                if (e.Data is not null)
                    lock (captured) captured.AppendLine(e.Data);
            };
            process.ErrorDataReceived += (_, e) =>
            {
                if (e.Data is not null)
                    log?.Report(e.Data);
            };
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            if (!process.WaitForExit((int)timeout.TotalMilliseconds))
            {
                try { process.Kill(entireProcessTree: true); }
                catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException) { }
                return null;
            }

            // The overload with a timeout returns as soon as the process is gone; the parameterless one
            // is what also waits for the redirected-output handlers to run out. Skipping it is how you
            // get an envelope that is intermittently truncated on a fast machine.
            process.WaitForExit();

            lock (captured)
                stdout = captured.ToString();

            return process.ExitCode;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException)
        {
            return null;
        }
    }

    /// <summary>Read a doctor envelope. Internal rather than private so the shape can be pinned by a
    /// test without a game checkout on disk — which is the only way this parse gets exercised at all on
    /// a CI box that has no clone of the game.</summary>
    internal static VxDoctorReport? Parse(string stdout)
    {
        if (string.IsNullOrWhiteSpace(stdout))
            return null;

        try
        {
            using var document = JsonDocument.Parse(stdout);
            var root = document.RootElement;

            if (root.ValueKind != JsonValueKind.Object)
                return null;

            var schema = root.TryGetProperty("schema", out var s) && s.ValueKind == JsonValueKind.Number
                ? s.GetInt32()
                : 0;

            if (schema != SupportedSchema)
                return new VxDoctorReport { Ok = false, Checks = [], UnsupportedSchema = schema };

            var checks = new List<VxCheck>();
            if (root.TryGetProperty("checks", out var array) && array.ValueKind == JsonValueKind.Array)
            {
                foreach (var check in array.EnumerateArray())
                {
                    if (check.ValueKind != JsonValueKind.Object)
                        continue;

                    var name = Text(check, "name");
                    if (name is null)
                        continue;

                    checks.Add(new VxCheck(
                        name,
                        Text(check, "status") ?? "unknown",
                        Text(check, "detail") ?? "",
                        check.TryGetProperty("required", out var r) && r.ValueKind == JsonValueKind.True,
                        Text(check, "fix")));
                }
            }

            return new VxDoctorReport
            {
                Ok = root.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.True,
                Checks = checks,
            };
        }
        catch (JsonException)
        {
            // The shim writes its own progress to stderr and keeps stdout clean, so this should not
            // happen — but "vx printed something that is not the envelope" is a reason to fall back to
            // the launcher's own checks, not a reason to fail a preflight.
            return null;
        }
    }

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}

/// <summary>One line of `vx doctor`. <paramref name="Status"/> is vx's own vocabulary — ok, warn,
/// missing — kept as the string it sent rather than mapped onto a launcher enum, because the launcher
/// reports these rather than reasoning about them and an unrecognised value should survive the trip.</summary>
public sealed record VxCheck(string Name, string Status, string Detail, bool Required, string? Fix);

public sealed record VxDoctorReport
{
    /// <summary>vx's own verdict: false when something it calls required is missing.</summary>
    public required bool Ok { get; init; }

    public required IReadOnlyList<VxCheck> Checks { get; init; }

    /// <summary>Set, with the number vx sent, when that number is not one this launcher reads. The
    /// checks list is empty in that case: reporting a schema we do not understand as "nothing wrong"
    /// is the failure mode the version field exists to prevent.</summary>
    public int? UnsupportedSchema { get; init; }
}
