# VortexArena/Launcher: launcher, CLI, runner, and web control plane

**Status:** REVISED 2026-07-30, amending the PLANNED 2026-07-12 version.
**Repo:** `VortexFPS/VortexLauncher` (exists; 2 commits holding the ADR-0015 Avalonia + Velopack shell).
**Related:** `conductor-master-orchestrator-plan.md` (the official fleet layer above this),
`dedicated-server-v2-plan.md` (read-only reference copy of the game repo's plan; the seams the runner drives),
`implementation-roadmap.md` (cross-repo sequencing).
**Supersedes:** the ADR-0015 launcher-updater track. That work is the seed of `Launcher.Core` and
`Launcher.Desktop`, not a separate line of development.

> **This is the design, not the status.** Most of it is built. The milestone table in §9 is kept as a
> record of the order things were meant to arrive in, and it is no longer a to-do list; read
> `implementation-roadmap.md` for what actually exists and what is left. Where this document and the
> code disagree, the code is right and this is stale, with one exception worth knowing: §2's
> dependency graph is enforced by `tests/Launcher.Tests/ArchitectureTests.cs`, so there the document
> cannot drift without failing the build.

## What changed from the 2026-07-12 version

1. `Launcher.Agent` is gone. Its two jobs are now separate deployables: the **runner** (a daemon verb inside
   `vortex`) supervises game processes, and **`Launcher.WebServer`** serves the UI and API. Restarting the
   control plane no longer risks the game servers, and the control plane can sit on a different box.
2. Project count is 5 shipping plus 2 test, down from 7 and a phantom SPA project. See §1.
3. `Launcher.WebServer` does not reference `Launcher.Core`. It cannot read the build store, write
   `server.cfg`, or spawn a process, because it does not link the code that does. Every box-touching
   operation is a `Launcher.Protocol` message to a runner.
4. New in §5: control modes (`local` and `orchestrated`), the rule that exactly one control plane operates an
   instance at a time, and the alerts the two local exits raise.
5. New in §7: instances fetch map content by hash from a content store rather than receiving pushed blobs.

---

## 1. Project structure

```
VortexLauncher.sln
├─ src/
│  ├─ Launcher.Core/         # feeds, build store, providers, instance model, process supervision,
│  │                         #   platform paths, game control (srcon, getinfo, eventlog parsing)
│  ├─ Launcher.Protocol/     # DTOs + OpenAPI/WS schema, published as a NuGet package
│  ├─ Launcher.Cli/          # `vortex`: CLI verbs and the runner daemon, one binary
│  ├─ Launcher.WebServer/    # ASP.NET Core control plane; web/ builds into wwwroot at publish
│  └─ Launcher.Desktop/      # Avalonia player launcher
├─ tests/
│  ├─ Launcher.Tests/
│  └─ Launcher.FakeGameServer/   # test fixture executable: speaks getinfo and srcon, scriptable crashes
└─ protocol/                     # the versioned spec Launcher.Protocol is generated from
```

Three merges against the 2026-07-12 layout, and why each holds:

**`Launcher.GameControl` folded into `Launcher.Core`.** It was split off to stay dependency-free with BCL
sockets only. That is already Core's constraint, so the split bought a namespace at the price of an assembly.
The srcon and getinfo codecs it holds are client-side copies of `RconProtocol.cs`, `Md4.cs`, and
`MasterServerProtocol.cs` in the game repo; they stay honest through golden vectors asserted in both CIs,
which is a test arrangement rather than a project boundary.

**`Launcher.Runner` folded into `Launcher.Cli`.** The CLI already needed `vortex server create/start/stop`
and the service-install verbs, so a separate runner project would have been a `Program.cs` and hosting glue
over code the CLI links anyway. One binary per host box: `vortex` when typed, `vortex runner run` under the
systemd unit or Windows service. Still two processes at runtime, which is the point of the split.

**`Launcher.Web` folded into `Launcher.WebServer/web/`.** It was never a .NET project. An npm build with an
MSBuild publish target covers it.

`Launcher.Protocol` stays separate because it is the one real package boundary: Conductor and any third-party
panel code against it, and it versions independently of the implementation.

`Launcher.Desktop` is the project to keep under review. It carries the only heavy UI dependency in the repo
and its own Velopack packaging path, and it serves players rather than operators. It stays because in-place
self-updating wants a native app. If that stops being true, folding the player path into `vortex` plus a thin
shell removes Avalonia from the tree.

## 2. Dependency rules

```
Launcher.Core      → BCL only
Launcher.Protocol  → BCL only
Launcher.Cli       → Core, Protocol
Launcher.WebServer → Protocol
Launcher.Desktop   → Core
```

Enforce with an architecture test in `Launcher.Tests`. The rule that earns its keep is `WebServer ↛ Core`:
it makes "the runner owns the box" a compile error rather than a convention someone breaks in month three
when writing the file directly is two lines shorter. It also keeps the same-box and cross-box cases on one
code path, because no shortcut exists for the same-box case.

The cost is real and accepted: some WebServer reads become a loopback round-trip to the runner instead of a
direct file read.

## 3. `vortex` (Launcher.Cli)

The scriptable face, and the integration-test surface for everything in Core.

```
vortex install [--channel stable|beta] [--dir <path>]      # game client install
vortex update [--check]
vortex launch [--connect host[:port]] [-- <game args>]

vortex server create <name> --map <m> [--gametype dm] [...]
vortex server list|start|stop|restart|delete <name>
vortex server console <name>                               # live log tail, stdin to the game
vortex server update <name> [--build <id>|--latest]
vortex server release <name> [--when now|end-of-match]     # return an orchestrated instance to local
vortex builds list|pin|gc
vortex source set <name> --repo <url> --ref <branch|tag|sha>
vortex source build <name>

vortex runner run|install-service|status
vortex runner link <conductor-url>|unlink                  # opt in or out of official orchestration
```

Every verb takes `--json` and returns meaningful exit codes. The WebServer and CI both script against it.

## 4. The runner

**Process model.** One runner per box, owning N instances as plain child processes. A runner restart must not
kill running servers: supervise through a pidfile and re-attach, with orphan adoption on start. On Linux that
means `KillMode=process` in the unit file.

**Instance layout** (`instances/<name>/`):

- `instance.json`: map, gametype, port, build pin, provider, restart policy, env, and `control_mode`
- `VortexData/`: the server's own user directory, holding `server.cfg` (DS-5), the banlist, and the eventlog
- `logs/`: runner-captured stdout with rotation

Port allocation is a runner-managed pool with collision checks. The rule from `docs/RUNNING.md` holds: always
pass an explicit `--port`, and read the real bind line out of stdout before reporting the instance as
running. A process that started is not the same thing as a server that bound.

**Control paths into the game**, in preference order:

1. **stdin** (DS-2, landed). The runner owns the child's stdin. Primary channel, no network surface.
2. **srcon over loopback** (DS-6, landed). For adopted orphans whose stdin was lost, and for `server console`.
3. **getinfo query** (landed). Health checks, player count, current map. No auth needed.

Chat arrives through the eventlog rather than a channel of its own. `Chat.cs` emits `:chat:`, `:chat_team:`,
`:chat_spec:`, and `:chat_minigame:` when `sv_eventlog` is set, and the log parser in Core turns those into
structured events. Sending chat back is `say` over stdin.

**Health and restart.** Liveness is process-alive plus a getinfo answer inside a timeout. A crash triggers
exponential-backoff restart per the instance policy (`always`, `on-failure`, `never`), keyed off the DS-4
exit codes. N restarts in M minutes trips flap detection, which stops the instance and raises an alert
instead of continuing to bounce it.

**Update and drain.** An optional drain broadcasts a warning over stdin `say` and waits for the server to
empty or for a timeout. Then stop, flip the build pin to the new side-by-side directory, start, health-check.
The previous build stays on disk for instant rollback. Game build and data payload are separate artifacts
(the ADR-0015 split payload), so a data-only update does not redownload the engine.

## 5. Control modes

An instance is in exactly one mode at a time. Control is a mode, not a merged permission set, and that is
what keeps two control planes from ever racing on the same instance.

```
control_mode: local | orchestrated
```

The runner holds the value in `instance.json` and is the only arbiter. It routes mutating commands by mode
and rejects the other plane outright.

**While `orchestrated`, `Launcher.WebServer` can:**

- read status: state, map, players, uptime, CPU and RAM
- read config: `server.cfg` and launch settings, rendered with no save control
- read logs and live console output, chat included
- read the orchestrator's audit trail: every action Conductor took and which Conductor user took it
- return the instance to local control
- shut the instance down

**And cannot:** edit config, start or restart, run commands, kick or ban, manage builds, or upload content.
Those endpoints return `409 instance is orchestrated` with a body naming the controlling Conductor and both
exits, so the UI does not need per-button special-casing.

The owner keeps read access to logs because the logs are files on their own disk; engineering around that
would be theater. Since those logs carry player chat, the banner has to say so in plain words. Players on an
officially orchestrated server may otherwise assume the host cannot read their messages.

**Banner** on every orchestrated instance: which Conductor controls it, since when, the scopes granted at
adoption, a short explanation of what the official orchestration layer is, a link to the audit trail, and the
two exit buttons.

### The two exits

`release` returns control without stopping the instance. It takes `--when end-of-match` (the default) or
`--when now`. The graceful default exists because most releases are an operator wanting their box back rather
than an emergency, and a one-click graceful option keeps the critical-alert queue meaningful. The immediate
option is always available and gated on nothing.

`stop` shuts the instance down.

**Getting the alert out.** Both exits sever the WS connection that would carry the notification, so the
runner sends the event first, waits up to 2 seconds for an ack, then proceeds regardless. It never waits
longer and never blocks. An owner reclaiming their own hardware cannot be gated on Conductor being reachable.

The event payload is captured at the moment of action, not reconstructed afterward:

| Field | Source |
|---|---|
| `kind` | `released` or `stopped` |
| `when` | `now` or `end-of-match` |
| `players_connected` | live getinfo snapshot |
| `match_live`, `match_elapsed_s` | eventlog `:gamestart:` / `:gameover:` pair |
| `map`, `gametype` | instance state |
| `initiator` | local OS user or WebServer session identity |
| `timestamp` | runner clock |

An acked event is a clean release. A connection that simply drops is a **separate** lost-contact event that
may resolve on reconnect. Without that split, every network blip on a community box reads as an operator
yanking a server mid-match, and the alert queue fills with noise nobody reads.

**No auto-revert.** If Conductor is unreachable the instance stays `orchestrated`. The owner is never locked
out, because both exits are local and work offline. Reverting automatically on a blip would hand an operator
control at a moment they were not watching for it.

## 6. SourceProvider

Compiling from a user-selected repo and branch, exposed as a build-store provider so that pin, update, and
rollback work identically for compiled and downloaded builds. Every step resumes and streams as a build job.

1. `git clone --filter=blob:none` or `fetch` the configured repo (default `VortexFPS/VortexArena`, forks
   supported) at the configured ref.
2. Toolchain ensure: pinned .NET SDK check, then the pinned Godot console binary and export templates,
   auto-downloaded into a sha-verified cache. Templates run about 1 GB per ADR-0014, so the cache is shared
   across builds. The version comes from the repo's `docs/RUNNING.md` pin, never a hardcoded constant.
3. `dotnet build`, then `godot --headless --export-release <target>`.
4. Stage the export into the build store tagged `source:{ref}@{sha}`.
5. Resolve the data payload from the release feed's data artifact or a user-configured local directory.

Known failure modes: cross-OS exports are unreliable, so a runner builds only for its own platform
(ADR-0014). Godot version skew between the repo pin and the cached toolchain fails hard with both versions
named. Never fall through to "try anyway".

## 7. Content fetch (maps)

Instances acquire maps by hash. Nothing pushes a file at a runner.

The orchestrator or the local operator sets a desired content set on an instance, each entry a sha256. The
runner fetches whatever it is missing from the content store, verifies the hash, validates the package, and
installs it into the instance's data directory. A failure leaves the instance on its previous content set.

This reuses the build store's existing verify, cache, and GC machinery, and it solves the half a blob push
does not: players joining that server need the same map, and they can pull the same content-addressed object
from the same CDN URL. The store itself is specified in `conductor-master-orchestrator-plan.md` §5.

Validation runs on the runner as well as at upload, because a runner must not trust a control plane with
arbitrary file writes into its own data directory. Reject path traversal in package entries, enforce a size
cap and a per-instance quota, and refuse anything whose hash does not match what was requested.

## 8. Launcher.WebServer

Serves the SPA and the control API. Talks to runners over `Launcher.Protocol` and nothing else.

**Runners dial out.** A runner opens an outbound WSS connection to its configured WebServer, including when
both sit on the same box. Making the local case the one exception that goes inbound would produce two auth
models and two reconnect paths for no gain.

**API.** Mirrors the runner's own semantics; the OpenAPI spec in `protocol/` is what Conductor codes to.

```
GET    /api/v1/runners                        GET   /api/v1/runners/{r}
GET    /api/v1/instances                      POST  /api/v1/instances
GET    /api/v1/instances/{n}                  PATCH /api/v1/instances/{n}   DELETE /api/v1/instances/{n}
POST   /api/v1/instances/{n}/start|stop|restart|update|drain
POST   /api/v1/instances/{n}/release          # when=now|end-of-match
GET    /api/v1/instances/{n}/status           # supervisor state + live getinfo snapshot
GET    /api/v1/instances/{n}/logs?tail=500
GET    /api/v1/instances/{n}/audit            # includes actions taken by an orchestrator
WS     /api/v1/instances/{n}/console          # log stream down, command lines up
GET    /api/v1/builds                         POST  /api/v1/builds/check|fetch|build
GET    /api/v1/content                        POST  /api/v1/content          # map packages by sha256
```

**Security defaults, not negotiable:**

- Binds `127.0.0.1`. Binding `0.0.0.0` requires explicit config plus either a TLS certificate or a documented
  reverse proxy.
- Bearer token auth on every request including the WS upgrade, generated at `runner install-service` and
  stored hashed. `GET /healthz` is the only unauthenticated endpoint.
- rcon passwords never leave the box. The UI sends commands; the runner attaches auth locally.
- Every mutating call is written to the audit log with who, what, and when.

**Web UI:** instance dashboard, create and edit forms over `instance.json` plus a `server.cfg` editor, live
console, build management covering channel, pin, rollback, and the source-provider repo picker, content
upload, and runner settings for bind address, TLS, token rotation, and the Conductor link. It stays a thin
client of the API: anything the UI can do, `vortex` and Conductor can do.

## 9. Milestones

| # | Deliverable | Contents |
|---|---|---|
| A0 | Repo restructure | Split the existing `XonoticGodot.Launcher` into `Launcher.Core` and `Launcher.Desktop`, rename to Vortex naming, stand up the solution layout and the architecture test |
| A1 | Core + CLI player path | Feeds, build store, `vortex install/update/launch`; Desktop reaches parity against Core |
| A2 | Server instances, CLI only | Instance model, supervisor, `vortex server *`, stdin and srcon control, health checks. Headless-box operation with no web surface at all |
| A3 | Protocol freeze | `protocol/` spec plus the published `Launcher.Protocol` package. Conductor C3 unblocks here |
| A4 | WebServer + Web MVP | Outbound runner link, auth, REST and WS, dashboard, live console, service install |
| A5 | Update, drain, rollback, metrics | Side-by-side builds in the UI, drain flow, CPU/RAM/player graphs, scheduled restarts |
| A6 | Control modes | `local` and `orchestrated` enforcement, the 409 contract, banner, both exits, control-event emission |
| A7 | Content fetch | Fetch-by-hash, package validation, quotas |
| A8 | SourceProvider | git and compile pipeline, repo and branch picker |
| A9 | Conductor link | `vortex runner link`, outbound enrollment, key handling per Conductor §3 |

A2 needs DS-2 and DS-4, both landed. A0 and A1 have no game-side dependency and can start immediately.

## 10. Testing

- Core: unit tests for feed parsing, build-store GC, srcon HMAC vectors shared with the game repo's golden
  packets, the getinfo codec, and eventlog line parsing including all four chat variants.
- Supervisor: integration tests against `Launcher.FakeGameServer`, which speaks getinfo and srcon and can be
  scripted to crash or hang. No Godot in the launcher CI.
- Control modes: a matrix test asserting that every mutating endpoint returns 409 while `orchestrated`, that
  both exits work with the hub deliberately unreachable, and that the control event is emitted before the
  action rather than after it.
- API: contract tests against the OpenAPI spec, since the spec is what Conductor codes to.
- One nightly end-to-end on a Linux runner: download the latest release, create an instance, boot it, query
  it, stop it. That path mirrors what a fresh operator experiences.
