# Vortex Launcher

The install/update/play shell for [Vortex Arena](https://github.com/VortexFPS/VortexArena) — design and
rationale in [ADR-0015](ADR-0015-launcher-updater.md). Avalonia UI, .NET 8, Velopack for the launcher's
*own* updates; game installs are launcher-managed plain zips pulled from
[the game's GitHub Releases](https://github.com/VortexFPS/VortexArena/releases).

Extracted from the game repo (`VortexArena:launcher/` on `feature/launcher-updater`) with
`git subtree split`, so the original commit history is preserved. Own release cadence, no dependency on
the game's build.

## Run (dev)

```bash
dotnet build VortexLauncher.sln                            # everything
dotnet run --project src/Launcher.Desktop                  # the UI
dotnet run --project src/Launcher.Desktop -- --smoke       # headless feed/paths check
dotnet test VortexLauncher.sln                             # unit tests
```

Dev builds are NOT Velopack-installed, so self-update is inert (`UpdateManager.IsInstalled`
guard) — everything else works, including real game installs into
`%LOCALAPPDATA%/VortexArena/Launcher` (`~/.local/share/…` on Linux).

## Nightly end-to-end

`.github/workflows/nightly-e2e.yml` (03:17 UTC, plus `workflow_dispatch`) runs the published `vortex`
binary through the sequence a new operator performs on day one, on a Linux runner against a scratch
`--data-root`: put a build in the store, `server create`, `server start`, poll `server list --json`
until the instance reports `running` on the map it was created with, `server exec` a `map` command and
watch the new map come back out of the same getinfo probe, then `server stop` and check that the pid is
gone, that nothing still holds the UDP port, and that the pidfile went with it. Every assertion reads
`--json` through jq, which is what the JSON envelope and the exit codes are for. The unit suite covers
each piece; this covers the seams, and the seams are where a fresh box fails.

**It is red on the current code, and that is the finding.** The first run fails at `server start`, and
the exec step after it fails for a second, unrelated reason. Both are in the runner, not in the
workflow, and neither was worked around:

- `SupervisedInstance.Cleanup()` deletes `instance.pid` unconditionally and `Dispose()` calls it, so
  `vortex server start` deletes the pidfile of the child it just spawned as soon as the verb returns.
  The server keeps running and keeps answering `getinfo`, but nothing can re-attach to it: the next
  `vortex` invocation reports it `stopped` with no map, and `server stop` then returns `ok` while
  leaking a process that still holds the port. The same deletion fires for an *adopted* instance, so on
  a box running `vortex runner run`, one `vortex server list` destroys the daemon's ability to re-adopt
  its servers after a restart — the thing `KillMode=process` in the generated unit file exists to make
  possible.
- `TryAdopt()` sets `_stdin = null` with the comment that commands go over rcon, and nothing goes over
  rcon: `SendViaRconAsync` has no callers anywhere in the repo. So `server exec` against an instance
  this process did not start fails `no_stdin` (exit 6), and the remediation the CLI prints — set
  `rcon_password` in `server.cfg` — cannot help, because no code reads that password.

Fixing either one alone does not turn the job green. The `if: failure()` step prints both, so a red run
is read against them and a *new* failure is recognisable as new. Booting the server by some route no
operator would take would have made this green and worth nothing.

The server it boots is `tests/Launcher.FakeGameServer`, laid out in the scratch root the way an
extracted release lands: `game/versions/<id>/<root>/VortexArena.x86_64`. No CLI verb registers a build,
so the workflow leans on `BuildStore.List()` adopting a directory with no entry in `builds.json`, the
same path that keeps an install made before that file existed from being orphaned; `vortex builds list`
and `vortex builds pin` then confirm the store took it. There is no test-only hook in production code.

The fixture binds **loopback**, not `0.0.0.0`. It used to do the opposite, on the grounds that a
stand-in should be shaped like the real server, and the cost of that landed on every developer who
ran `dotnet test`: Windows raises a firewall prompt for each new binary that listens on a public
interface, and this suite starts a server process per test. The fidelity was thin — the fixture
implements a contract, not a deployment, and everything that probes it (the supervisor's `getinfo`,
the nightly e2e) does so from the same machine. `FAKE_BIND=0.0.0.0` puts the wider bind back.
`PortPool.IsFree` moved off a wildcard bind for the same reason and gained something better than
quiet: it reads the OS listener tables instead, so it no longer races the server it is checking for
by holding the port it just declared free.

Three things it deliberately leaves uncovered:

- **The real game binary.** No Godot on a runner. The fixture implements the whole of the supervisor's
  contract with a server (`--dedicated --port --userdir +map`, a UDP port answering `getinfo`, eventlog
  lines on stdout, stdin for commands, an exit code) and nothing beyond it, so a green nightly says the
  runner's lifecycle works, not that the game boots.
- **Real downloads.** The nightly starts from a build already on disk. `vortex install`, the release
  feed, checksum verification and signature checks are exercised in `tests/Launcher.Tests` with
  `IDownloader` stubbed, so a broken `latest.json` or a hijacked `releases/latest` will not surface
  here.
- **Windows and macOS install paths.** `runs-on: ubuntu-latest`, and the Linux binary name is the only
  one probed for. The macOS `ditto` extract path still has no machine in CI at all.

## Layout

```
src/Launcher.Core/       the shared framework, BCL only
src/Launcher.Desktop/    the Avalonia player launcher
tests/Launcher.Tests/    unit tests + the architecture test
planning/                design docs for the launcher, Conductor, and the roadmap
```

`Launcher.Cli` (the `vortex` binary, which is also the runner daemon) and `Launcher.WebServer` land in
A1 and A4. The dependency graph they all have to satisfy is in
[planning/launcher-host-agent-plan.md](planning/launcher-host-agent-plan.md) §2 and is enforced by
`tests/Launcher.Tests/ArchitectureTests.cs`, which reads the `.csproj` files directly. The rule that
matters: `Launcher.WebServer` must never reference `Launcher.Core`, so the control plane physically
cannot touch the box and every operation goes to a runner over the protocol.

## Map

| Piece | File | Job |
|---|---|---|
| Feeds | `src/Launcher.Core/ReleaseFeeds.cs` | `latest.json` via `/releases/latest/download` (no API quota) → GitHub API fallback (sees prereleases) |
| Manifest | `src/Launcher.Core/Manifest.cs` | `latest.json` model (emitted by `tools/make-manifest.py` **in the game repo's** release job) |
| Download | `src/Launcher.Core/DownloadService.cs` | resumable (Range), sha256-verified — refuses checksum-less files |
| Install | `src/Launcher.Core/InstallService.cs` | staging extract → atomic move → `current.json` flip; keeps N-1 for rollback; shared content-addressed asset store for `-core` installs |
| Extract | `src/Launcher.Core/ArchiveExtractor.cs` | `System.IO.Compression` on Windows/Linux; `ditto` on macOS, where the managed extractor drops the `.app`'s symlinks |
| Launch | `src/Launcher.Core/GameLauncher.cs` | spawns the game; `--data <store>` for core installs (complete installs self-resolve) |
| Source build | `src/Launcher.Core/SourceProvider.cs` | clone, export, verify, package, stage; `vortex source *`, the desktop sheet, and `/api/v1/sources` all drive this one class |
| Source build jobs | `src/Launcher.Core/SourceBuildJobs.cs` | the runner's one build slot, so a tens-of-minutes build survives a 30-second command envelope |
| The game's task runner | `src/Launcher.Core/Vx.cs` | finds and drives the checkout's own `./vx`, and reads `vx doctor --json` (schema 1) |
| Engine pin | `src/Launcher.Core/GameCheckout.cs` | reads `engine.lock.json` and `export_presets.cfg` out of a checkout, and names every game-repo script the build shells out to |
| Toolchain | `src/Launcher.Core/GodotToolchain.cs` | finds git/dotnet/python/bash and the Godot editor — `$VORTEX_GODOT`, `$GODOT`, the checkout's `.godot-bin/`, PATH, then the platform install location — and refuses an editor that is not the pinned engine |
| Artifact names | `src/Launcher.Core/LauncherConfig.cs` | the accepted release-artifact prefixes; see the rename note below |
| Update policy | `src/Launcher.Core/UpdatePolicy.cs` | the setting vocabularies and what an unrecognised value is allowed to mean |
| Update check | `src/Launcher.Core/UpdateCheck.cs` | one verdict type, the polling loop, and once-per-version announcement |
| Self-update | `src/Launcher.Desktop/SelfUpdateService.cs` | Velopack against this repo's releases; check and restart are separate |
| Notifications | `src/Launcher.Desktop/Notifications/` | OS notification per platform, or silence under the in-app reach |

Invariants (ADR-0015 §6): never gate Play on the network; verify before swap; resume
interrupted downloads; keep the previous version.

## Updating: the game, the launcher, and being told

Three separate questions, three separate settings, all in `settings.json` (schema 2) and all on the
Settings sheet. A schema-1 file needs no migration: every new field defaults to what its absence
implied.

**The channel** (`channel`) is a fourth, and it defaults to **`beta`** while the game is pre-1.0 —
everything published is a prerelease or near enough that a player defaulted to stable would be
looking at an empty feed. Two consequences, both real:

- It puts the *default* path on `GitHubApiFeed`, because the `releases/latest` redirect structurally
  cannot see a prerelease (`ChannelFeeds`). That is unauthenticated GitHub API at 60 requests/hour
  per source IP, where stable's is an unmetered redirect. The `updateCheckMinutes` floor below now
  applies to everyone rather than to the few who opted in.
- It does **not** affect the launcher's own updates. Velopack's `GithubSource` reads `prerelease` as
  "consider these too", not "only these", so a beta launcher still finds the full releases `stable`
  publishes.

An unrecognised value still reads as stable, which is deliberately not the same as the default: a
file with no channel gets the product decision, a file with a typo gets the reading that cannot
hurt. Flipping the default back is one line in `LauncherSettings.Channel`, and leaves anyone who
picked a channel explicitly where they put themselves.

**The game** (`gameUpdates`) defaults to `download` — fetch a new release in the background, then
ask before switching to it. That split is the reason `InstallService` grew `StageAsync`/`Apply`: the
install already did all its expensive, failure-prone work out-of-tree with a single
`Directory.Move` between it and live (verify-before-swap, ADR-0015 §6), so stopping short of the
`current.json` flip costs nothing and buys a build that is downloaded and *not yet* the one Play
launches. `install` skips the asking; `notify` touches the network only when the player presses
Update. With nothing installed, all three install immediately — there is no session to protect and
prompting would just be a click between the player and a game they have none of.

**The launcher** (`launcherUpdates`) defaults to `automatic` and can be turned off, which is a
player's call to make and a real hazard worth stating: `latest.json` is a cross-repo contract, so a
launcher left far enough behind can lose the ability to read the game's feed at all. `off` therefore
still *checks* and still reports the gap; it just does not act. Two bugs in the original
`SelfUpdateService` are fixed here — it called `ApplyUpdatesAndRestart` from a fire-and-forget
startup check, which terminates the process with no regard for an install in flight, and it passed
`prerelease: true` unconditionally, serving prerelease launchers to players on the stable channel.
Checking and restarting are now separate calls and the restart is gated on nothing being in flight.

**Being told** (`notificationReach`) is the one preference with no defensible default, because the
honest answer turns on whether the player wants a resident process — which nothing on disk can say.
So it is the single question first run asks, and until it is answered nothing notifies:

| Reach | What it costs | What it gets |
|---|---|---|
| `in-app` | nothing | a banner, next time the launcher is opened |
| `system` | a notification per new version | a native OS notification while the launcher runs |
| `background` | a tray-resident process, optionally started at login | notice without the launcher being open |

`SystemNotifier` shells out per platform — `notify-send`, `osascript`, and a PowerShell/WinRT toast
on Windows — rather than binding an OS API, because the maintained Windows toast package
(`CommunityToolkit.WinUI.Notifications`) ships only for a `net8.0-windows10.0.x` target framework,
and taking it would force this project to multi-target and put a Windows-only TFM in a launcher that
builds on Linux CI. Payloads cross as environment variables, never interpolated into a command line:
the text carries a release version, and a release is exactly as trustworthy as the release process —
the same reasoning that put release-note links behind `SafeLinkPolicy`. Two caveats, both inherent
to shelling out: on macOS the notification is attributed to whatever owns `osascript` rather than to
the launcher, and on Windows a toast is attributed to an AppUserModelID, so it is inert for a
`dotnet run` dev build with no Start Menu shortcut — the same shape as self-update. Anything that
fails degrades to the banner, which has already said the same thing.

Background checks run on a loop, not a timer, so a slow connection cannot stack a second check on
top of the first. The interval (`updateCheckMinutes`, default 240) has a 15-minute floor for a
reason: the beta channel asks `GitHubApiFeed` *first* (`ChannelFeeds`), and that is unauthenticated
GitHub API at 60 requests/hour. Now that beta is the default channel, that floor is load-bearing for
every install rather than for the ones that opted in.

**Not covered:** the CLI. `vortex install`/`update` still read `LauncherHttp.DefaultFeed` directly
and honour neither `channel` nor any of these settings — `vortex update --check` is the only
scheduling primitive it offers, and nothing in this repo schedules it. A box running `vortex runner
run` does not auto-update its game builds.

## Runner metrics

`vortex runner run` serves the Prometheus text format on `http://127.0.0.1:9877/metrics`:
per-instance player and bot counts, CPU, memory, supervisor state, restart count and match state,
plus runner-level counts, the link state and free disk. `--metrics-port 0` turns it off,
`--metrics-bind` moves it off loopback, and both have `runner.json` equivalents.

It sits on the runner and not on `Launcher.WebServer` because the runner is what has the numbers and
the WebServer may not be running at all: a box under Conductor control never starts one, and a box
under local control can have it stopped for an upgrade with forty players still connected. Metrics
that vanish whenever the dashboard does are metrics nobody can alert on.

The exporter is hand-written (`src/Launcher.Core/Metrics/`) rather than prometheus-net in
`Launcher.Cli`, which was the other option, because `Launcher.Core` is BCL-only and
`ArchitectureTests` fails the build on a `PackageReference` there. Three reasons it went that way and
not the other:

1. The numbers already live in Core. `SupervisedInstance.Status()` computes every series; a registry
   in `Launcher.Cli` would be a second copy kept in step by a pump on a timer, and a scrape would then
   report what the pump last saw rather than what the supervisor knows.
2. The package brings no server the runner can use. `vortex` is a console app, so exposition would
   arrive as either `KestrelMetricServer`, which drags the ASP.NET hosting stack into the binary a
   *player* runs to launch the game, or `MetricServer` on `HttpListener`, which on Windows wants a URL
   ACL. There is a listener to bind and harden either way.
3. Nothing exported accumulates. Every series is a level read at scrape time, which is the one case
   where a registry buys nothing.

What that costs is the exposition format, which is one frozen spec, three escaping rules and a number
format, and a single-route HTTP listener bound to loopback.

## Building the game from source

```bash
vortex source set game --repo https://github.com/VortexFPS/VortexArena.git --ref main
vortex source status game        # can this box build it, and against which engine
vortex source build game         # clone, export, verify, stage
vortex builds pin source:linux-dedicated:main@a1b2c3d
```

`--repo` defaults to the game repo, so a plain `vortex source set game --ref main` works; the flag is
there for forks. `--target` picks the export preset and defaults to `windows-client`, `macos-client`
or `linux-dedicated` by OS, the last because a Linux box running `vortex` is usually a server. The
result is an ordinary entry in the build store, so `builds list`, `builds pin`, `builds gc` and
`server create --build` treat a compiled build exactly like a downloaded one.

**Three ways in, one implementation.** `SourceProvider` is driven by the CLI above, by the desktop
app's *Build from source* sheet, and by `/api/v1/sources` on the runner API — so a source configured
in one is the source the others see, over the same `sources.json` and the same build store.

- **Desktop.** The sheet takes a name, repo, ref and preset, streams the build log, and stops there.
  A finished build lands in the build store and becomes the installed game only when *Use this build*
  is pressed — the same rule the release path follows, and it matters more here: someone building a
  branch to test it is often not someone who wants to be left on it. The press is refused while the
  game is running, because the swap rewrites `current.json` underneath it.
- **Runner API.** `POST /api/v1/sources/{name}/build` answers **202 with a job that has started**, not
  a build that has finished, and the plane polls `GET` on the same path. Every other runner verb
  answers inside the 30-second command envelope; a build runs for tens of minutes. One at a time per
  box — two builds contend for the same cores, disk and, if they name one source, the same checkout.
  **Reads are open to both planes; everything that mutates is refused unless it came from the host
  owner's own plane**, because these routes compile a repository named in the request, which exposed
  to an orchestrator is arbitrary code execution addressed by URL. That is a different thing from the
  instance routes, which only ever start a binary already in the build store.

What the box has to have, all of it named in the refusal when it is absent: **git**, the **.NET SDK**,
**Python 3**, **bash**, and a **Godot editor** of the version the checkout pins, mono/.NET build. On
Windows the Git Bash that ships with git is used and the `bash` in `System32` is skipped, because that
one is the WSL launcher and would run `package.sh` against `/mnt/c` inside a different filesystem.

The editor is looked for at `--godot`, then `$VORTEX_GODOT`/`$GODOT`, then the checkout's own
`.godot-bin/`, then PATH, then the platform install location (`C:\Program Files\Godot`,
`/Applications/Godot*.app`, `/usr/{local/,}bin/godot`). Two of those are new and each fixes a real
answer this repo was getting wrong:

- **`.godot-bin/` ahead of PATH.** A checkout carrying `./vx` installs its own pinned editor there
  with `./vx setup --profile dev`, and that is the one option which gets the version right by
  construction instead of by the skew check catching it afterwards. It also lets two sources at two
  refs pin two engine versions, which a single PATH entry cannot express.
- **The platform install location at all.** vx and the game repo's `find-godot.sh` both look there and
  this resolver did not, so on a box with a normal Godot install that never touched PATH — the usual
  case on Windows and macOS — `vx doctor` reported a usable pinned editor while a source build on the
  same machine refused for having none. Windows is enumerated rather than hardcoded to the pinned
  filename the way vx does it, because the launcher builds arbitrary refs pinning arbitrary versions;
  it takes what is installed, newest first, and lets the skew check be the thing with an opinion.

**Where the engine comes from is the part worth reading.** The Godot *editor* drives the export and can
be a stock download; the export *template* is what gets embedded in the shipped game and therefore
decides what engine players run. They are resolved differently on purpose:

- The **template** comes from the checkout's own `tools/engine-patches/engine.lock.json` (the
  authoritative pin, the file CI already trusts), fetched by the checkout's own
  `tools/data/fetch-engine-template.py` and verified against the sha256 in that lockfile. The launcher
  does not download it itself. A second downloader reading the same lockfile is how a project ends up
  patched in CI and stock on a dev box, and nothing downstream notices.
- The **editor** comes from `--godot`, then `$VORTEX_GODOT` or `$GODOT`, then PATH. There is no
  download path: the game's release publishes three `template_release` binaries and no editor, so
  there is nothing pinned to fetch, and guessing a godotengine.org URL would add an unpinned
  acquisition path for the one input this whole mechanism exists to control. A missing editor fails
  naming the version to install and the three ways to point at it. On Windows a `_console` twin beside
  the binary is preferred, including when the operator names the GUI one, because the plain build
  detaches from the terminal and `--version` comes back empty.

Version skew refuses and names both versions. So does a stable-versus-prerelease channel mismatch, and
so does a non-mono editor against a lockfile that sets `engine.dotnet`. There is no "try anyway"
branch: a build against a mismatched engine compiles, exports, and then misbehaves at runtime on
somebody else's machine.

Two verification passes run, both through the game repo's own `tools/verify-engine-template.py`:
`--preset-config` before the export, because it catches an emptied `custom_template/release` in
seconds, and `--patches --binary` after it, because that is the only check that speaks to what
shipped. Measured in the game repo (G10): an empty `custom_template/release` makes Godot export a
complete, launchable binary from the *stock* template without failing. CI closed that trap; a source
build that skipped these two would reopen it on every operator's box.

Cross-OS exports are refused rather than attempted (ADR-0014): the lockfile already says which platform
a preset builds for, so `--target linux-dedicated` on Windows costs one message instead of a
twenty-minute export that could not have worked.

The order of steps is the release workflow's order, deliberately: import, fetch template, verify the
preset config, `dotnet build`, export, verify the binary, `tools/data/fetch-maps.py`, then
`tools/package.sh --no-zip` to lay content beside the binary. `--skip-maps` drops the maps fetch, which
leaves whatever the checkout already has; the fetch is otherwise cheap after the first run because it
skips packs whose hash already matches. One step is not in release.yml: any NuGet package source in the
checkout's `nuget.config` that points at a directory this box does not have is dropped before the
restore, because the game's config adds the Godot editor's bundled `nupkgs` folder as an absolute path
to one dev machine and NuGet hard-fails on a missing local source. CI removes that source by name; this
generalises it. The edit is undone by the next build's `git checkout --force`.

Verified end to end on Windows against the real `engine.lock.json`, `export_presets.cfg`, patched
template and verify script: the exported binary came back with `GetRawInputBuffer present (1x)`, so it
carried the patched engine, and the staged build showed up in `builds list` and took a `builds pin`.

Exit codes: `4` something is not installed, `2` bad preset or wrong platform, `7` engine skew or a
failed verification, `1` the build itself failed, `5` no such source. `--json` puts the same failure
code in the envelope.

Three things to know before relying on it:

- **A macOS source build has never run.** The `.app` bundle path is written (`CopyTree` recreates
  symlinks rather than dereferencing them, for the same reason `ArchiveExtractor` shells out to
  `ditto`) and no Mac has executed it.
- **`tools/package.sh --no-zip` exits 1 having done everything right**, because its last statement is
  `$do_zip && info ...`. CI always zips, so nothing noticed. The launcher therefore asserts on the
  output the way the release workflow asserts on the export's, and treats the exit code as advisory.
  Worth fixing in the game repo.
- **The desktop sheet and the runner API have not been exercised end to end.** Both drive the same
  `SourceProvider` the CLI does, and the routes, the refusal and the job lifecycle are covered by
  tests — but no full build has been run through either surface, so what is unproven is the long tail
  of a twenty-minute job: the log staying readable, cancellation landing, the pin at the end.
- **The operator panel has no screen for it yet.** The API is there and documented, so Conductor and
  the panel *can* read what a box has built and watch a running build; nothing in `wwwroot` calls it.

## The contract with the game repo

This repo builds and ships the launcher, and since `vortex source build` landed it can also build the
game from a checkout. The game still does not reference it. Two interfaces run the other way:

**`latest.json`**, emitted by the game repo's release job (`tools/make-manifest.py`) and modelled here
by `Core/Manifest.cs`. Changing the manifest shape is a cross-repo change and both sides have to land
together.

**The game repo's build tooling**, consumed by `SourceProvider`: `tools/engine-patches/engine.lock.json`,
`export_presets.cfg`, `tools/data/fetch-engine-template.py`, `tools/verify-engine-template.py`,
`tools/data/fetch-maps.py` and `tools/package.sh`. Those are called rather than reimplemented, which
makes them a contract: renaming one, or changing `engine.lock.json`'s shape, breaks source builds in
this repo. `GameCheckout` is the single place that names them, and a checkout missing one fails saying
which file and that the ref predates the tooling.

**`./vx`, the game repo's own task runner**, where it already owns a step. Its plan names this
launcher as a consumer and versions `--json` as a shipping interface across the boundary, so
`Core/Vx.cs` reads `vx doctor --json` and pins the schema number it understands — an envelope
declaring a version this launcher does not read is reported as exactly that, never parsed hopefully.
`Vx.Find` returns null on a ref that predates vx, so every use site keeps its old road.

Which steps, and the three that deliberately stay put, is the part worth reading before adding a
fourth:

| step | driven by | why |
|---|---|---|
| export template | `vx engine --only <plat>` | Same lockfile, same sha256, same destination — but HttpClient instead of urllib. A python.org macOS install has no usable CA bundle, so the script dies `CERTIFICATE_VERIFY_FAILED` four retries deep on a box that is otherwise ready. The launcher inherited that by calling it. |
| compiled maps | `vx maps` | Same trade, bigger download. |
| preflight | `vx doctor --json` | Additive, never merged into `problems`, so a vx finding cannot decide `ready`. Earns its place on one check this side cannot make: whether the editor's stock export templates are installed, which only `macos-client` needs. |
| the export | **not** `vx export` | It resolves its own Godot. The launcher resolves an editor and then checks it against the checkout's pin; an export that went and found a different one would walk straight past the single check standing between an operator and a build that compiles and then misbehaves at runtime. |
| packaging | **not** `vx package` | It takes the first `bash` on PATH, which on a default Windows install is the WSL launcher — the trap `BuildTools.ResolveBash` exists to avoid. |
| the pre-build | **not** `vx build` | It names the game's `.csproj` literally, where `GameCheckout.GameProject` discovers the single root csproj and keeps the game's filename out of this repo. |

In each of the three, the launcher's existing path is the more careful one, so "use the repo's own
door" would be a regression rather than a tidy-up. `Python 3` and `bash` therefore stay hard
requirements: `verify-engine-template.py` runs twice per build and `package.sh` lays out the content.
What vx removed is the TLS failure class on the two large downloads, not the dependency.

Two more files are read and are deliberately *not* in that list, because neither breaks on a rename:
`nuget.config`, whose dev-local package sources are dropped when the directory they point at is not on
this box, and the single `.csproj` at the checkout root, which the pre-build compiles by name because a
bare `dotnet build` in that directory picks the *solution* over it and drags in the game's test suite.

Two consequences worth knowing before touching either side:

- **`ManifestFeed` reads `/releases/latest/download/latest.json`**, and GitHub resolves
  `releases/latest` to the newest *non-draft, non-prerelease* release of the game repo. Any
  non-game-build release published there hijacks the feed: `latest.json` 404s and every launcher falls
  back to `GitHubApiFeed`, which is unauthenticated GitHub API at 60 req/hr. That degrades quietly —
  the launcher keeps working, just on a rate-limited path. Anything else published to the game repo's
  releases (an engine template, for instance) must be marked `prerelease`.
- **The artifact rename is handled, but only because both names are accepted.** The game's artifacts go
  `XonoticGodot-*` → `VortexArena-*` when the rebrand reaches `tools/package.sh`. Rather than pick a
  side, `LauncherConfig.ArtifactPrefixes` lists both (newest first) and every consumer tries each:
  zip-name parsing, the assets-pack regex, and the binary probe in `GameLauncher`. A launcher built
  before the cutover can install a release published after it, and an install made under the old name
  keeps launching. Drop `XonoticGodot` from that list only when no supported install can still carry
  it. The cutover release is still worth a line in the game's `docs/RELEASING.md`.

## Why `Directory.Build.props` exists here

Load-bearing, not stylistic. This code was written inside the game repo and inherited VortexArena's
root `Directory.Build.props`. Extraction left nothing above it, and without a local copy the build
fails outright: `ImplicitUsings` alone accounts for ~20 `CS0246` errors (`Task`, `HttpClient`,
`Dictionary<,>`, `IProgress<>`, `CancellationToken`, `STAThreadAttribute` are all used with no explicit
`using`), which then cascades into `MVVMTK0007`/`MVVMTK0016` from the CommunityToolkit generator —
the generator errors are the loud symptom, the missing usings the cause. Dropping `Nullable` is the
quieter half: the code still compiles and the null-safety the `?` annotations claim just stops being
checked. See the comments in that file.

## Naming

Projects, namespaces and assemblies are renamed: `Launcher.Core`, `Launcher.Desktop`, `Launcher.Tests`,
and `VortexLauncher.exe`. That was safe to do independently because nothing consumes them yet.

The game's *artifact* names are a separate question with a separate answer, because the launcher does
not own them and has to read whatever the release job uploaded. See the rename bullet above.

The launcher's data root moved with the rename, from `%LOCALAPPDATA%/XonoticGodot/Launcher` to
`%LOCALAPPDATA%/VortexArena/Launcher`. No migration exists and none is needed: the only published
release carries a `SHA256SUMS` file and no platform zips, so no machine can be holding an install under
the old root.

## Releasing the launcher

**`stable` is the release branch.** Any commit that lands on it publishes a GitHub release
(`.github/workflows/release.yml`); work lands on `main` and is promoted by fast-forwarding `stable`
onto the commit players should have. There is no tag to remember, which is the point — the manual
alternative is how a repo ends up with code that has been "released" for weeks and no artifact
anybody can install.

```bash
git checkout stable && git merge --ff-only main && git push
```

Versions are `<major>.<minor>.<run-number>`: major/minor from `<VersionPrefix>` in
`Directory.Build.props`, patch from the workflow run number, so every stable commit is a strictly
increasing semver without anybody maintaining a counter. Velopack only offers an update when the
candidate sorts above what is installed, so that property is the requirement, not a convenience.
Bump the minor by editing `Directory.Build.props`. A run whose tag already exists fails in the gate
job rather than after three platforms have finished packaging.

Nothing is packaged until `dotnet build` and `dotnet test` pass **in Release**, which is a stronger
gate than `ci.yml`'s Debug run: `Launcher.Desktop` flips `OutputType` to `WinExe` under Release, so
Debug-only CI was not compiling the configuration the artifacts are cut from.

| Asset | Platform | Self-updates |
|---|---|---|
| `VortexLauncher-win-Setup.exe`, `-win-Portable.zip`, `-<ver>-full.nupkg`, `releases.win.json` | Windows | yes, Velopack |
| `VortexLauncher-<ver>-linux-x64.tar.gz`, `-osx-arm64.tar.gz` | Linux, macOS | no |
| `vortex-<ver>-{win-x64.zip,linux-x64.tar.gz,osx-arm64.tar.gz}` | all three | no |
| `SHA256SUMS` | — | — |

Three things about that table are worth the words:

**Only Windows gets a Velopack package,** and the reason is icon assets, not effort. `vpk pack`
wants an AppImage icon on Linux and an `.icns` on macOS, and the only image in this repo is
`src/Launcher.Desktop/Assets/tray-icon.png`, sized for a tray. Self-update is already inert off
Windows — `UpdateManager.IsInstalled` is false for anything not Velopack-installed — so a tarball
is honest about what it is, where a package would advertise an update path it does not have.
Producing real icon assets is the prerequisite for changing this; macOS additionally has no signing
identity and no Mac in CI.

**Nothing renames vpk's output.** `releases.win.json` is the index `GithubSource` reads and it
names the `.nupkg` by filename; `assets.win.json` names the installer and the portable zip. Renaming
any of them breaks the lookup that makes an installed launcher updatable.

**Releases are published full, not prerelease.** `SelfUpdateService` passes
`prerelease: settings.IsBeta`, and Velopack reads that as "consider prereleases too" rather than
"only prereleases", so a full release reaches both channels and a prerelease would reach only beta.
Since nothing here publishes a prerelease, the launcher's beta channel currently sees exactly what
stable sees — which is why defaulting players to beta costs nothing on the self-update path even
though it changes which feed the *game* is looked up on. Wiring `main` to prereleases is the
follow-up when there is something to beta-test.

The launcher's own release train is **this repo**, not the game's. `LauncherConfig.LauncherRepo`
exists next to `LauncherConfig.Repo` for that reason and `ReleaseTrainTests` holds them apart:
publishing launcher packages to the game repo would resolve `releases/latest` there to a non-game
release, 404 `latest.json` and silently drop every launcher onto the rate-limited API feed. This
supersedes ADR-0015 §7, which predates the extraction.

Not covered: **no Velopack round trip has been run.** `vpk pack` output was verified against a real
1.2.0 run — the tool confirms `VelopackApp.Run()` is wired, and the assets are what the table says
— but installing a `Setup.exe` and having it update itself to the next release needs two releases
and a Windows box. The second stable commit is the first chance to see it work.

Also not covered: **the SDK is not pinned.** `setup-dotnet`'s `dotnet-version: 8.0.x` only
guarantees 8.0.x is *present*; `dotnet` picks the highest SDK on the image, which is how a C# 14
overload-resolution change broke three commits on `main` that compiled locally. `<LangVersion>` is
pinned in `Directory.Build.props`, which closes the language half. A `global.json` would close the
rest, and is not here only because it would require every dev box to install the 8.0 SDK
specifically.

Packing by hand, which is what the workflow does per platform:

```bash
dotnet publish src/Launcher.Desktop -c Release -r win-x64 --self-contained -p:Version=<ver> -o pub
vpk pack --packId VortexLauncher --packVersion <ver> --packDir pub --mainExe VortexLauncher.exe
```

## Known prototype gaps

- macOS install — **fixed**. Extraction runs through `IArchiveExtractor`
  (`src/Launcher.Core/ArchiveExtractor.cs`), injected into `InstallService` the way `IDownloader` is:
  `System.IO.Compression` still on Windows/Linux, `ditto -x -k` on macOS. `ditto` restores the symlinks
  in the `.app`'s Frameworks dir — and the bundle's extended attributes — that the managed extractor
  silently dropped. If `ditto` is missing or exits nonzero the install fails with a message naming it;
  there is no fallback to the managed path, because that fallback is what produced an install that
  looked complete and refused to launch. Residual gap: no Mac in CI, so that path has never run against
  a real bundle.
- No settings UI — **fixed**. Five tabs (channel, game updates, launcher updates, notifications,
  folders) with a pinned Cancel/Save footer, and a search box over the top that filters to matching
  rows *across* tabs — which is why the sections are a flat self-hiding list rather than a
  `TabControl`: one query has to be able to put the check interval and the install folder on screen
  together, and no tab holds both. Keywords live in
  `src/Launcher.Desktop/ViewModels/SettingsSearch.cs`, and they are the words a player would reach
  for ("how often", "disk", "saves"), not the labels. Clicking the backdrop dismisses the sheet
  through `CancelCommand`, so it discards edits and refuses while a relocation is in flight.
- **The game's user directory can be opened, not moved.** The Folders tab shows it beside the
  install location with an Open button for each. Only the install location is a picker: Godot
  resolves `user://` itself, its `--userdir` flag takes a directory *name* rather than a path, and
  the game's `Main.cs` parses no user-directory argument at all — so a picker there would be a
  setting nothing could honour. `GameUserData` in `Launcher.Core` derives the path per platform;
  moving it for real is a game-side change. Related and unfixed: `SupervisedInstance` passes an
  absolute path to `--userdir` when it spawns a server, which Godot reads as a name.
- **Nothing here has run against a real Velopack-installed launcher.** A commit to `stable` now
  packages one (see *Releasing the launcher*), which closes the half of this gap that was "nothing
  produces a package". What remains is that nobody has installed the `Setup.exe` and watched it
  update itself: that needs two releases and a Windows box. The self-update paths are guarded by
  `UpdateManager.IsInstalled` and are inert without it, so what is exercised today is the check, the
  mode branching and the restart gate — not an actual restart into a new build. The Windows toast
  has the same gap for the same reason: it wants the Start Menu shortcut a Velopack install creates.
- **The tray reach is the least exercised of the three.** Close-to-tray, the tray menu and autostart
  registration are written and build, but autostart has only been reasoned about per platform, not
  run: `reg.exe` on Windows, `~/.config/autostart` on Linux, a LaunchAgent plist on macOS.
- Release notes markdown — **fixed**. `MarkdownView` (`src/Launcher.Desktop/Controls/MarkdownView.cs`)
  renders headings, emphasis, inline and fenced code, bullet and numbered lists, rules and links;
  anything outside that list falls through as literal text, so a construct it does not know shows up
  verbatim instead of disappearing. Hand-rolled rather than pulling in Markdown.Avalonia, because the
  one behaviour that had to be constrained is the one that package gets wrong by default — its
  hyperlink command shell-executes whatever URL the document carries. A release body is
  attacker-influenced the moment the release process is, so every link goes through
  `src/Launcher.Desktop/Controls/SafeLinkPolicy.cs`: `http`/`https` only, opened in the system browser,
  `HyperlinkButton.NavigateUri` deliberately never set. Images render as alt text instead of being
  fetched, which keeps a release body from beaconing the machines that display it. Residual gap: no
  tables, block quotes or inline HTML.
- Manifest signing (minisign) is the gate before this becomes the default install path — ADR-0015 cut
  list. Half done: the launcher verifies a signature over `latest.json` when a release carries one
  (`src/Launcher.Core/Signing/`), and refuses the release if that check fails. Nothing signs yet, so the
  policy is verify-if-present and no release key is provisioned. `release-signing.md` has the
  requirements for the game repo's release job and the order the two sides have to change in.
