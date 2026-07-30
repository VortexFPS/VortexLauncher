# Implementation roadmap: game, Launcher, Conductor

**Status:** NEW 2026-07-30.
**Scope:** sequencing across three repos. Task detail lives in `launcher-host-agent-plan.md` (A-series),
`conductor-master-orchestrator-plan.md` (C-series), and the game repo's dedicated-server plan (DS-series).

Sizes below are S/M/L relative shape, not a schedule. They exist to show which steps are one sitting and
which are a week of somebody's attention, not to be added up into a date.

## Where the three repos actually stand

| Repo | State |
|---|---|
| `VortexFPS/VortexArena` | DS-1 through DS-6 and DS-8 landed on `feature/dedicated-server-v2`. Dedicated host, stdin console, srcon, tick-matched loop, signals and exit codes, `server.cfg`, eventlog with chat lines, ban persistence. DS-7 (modern announce) is the only open item and it is blocked, not skipped |
| `VortexFPS/VortexLauncher` | 2 commits. One Avalonia project (`XonoticGodot.Launcher`, net8.0, Avalonia 11.3.18, Velopack 1.2.0) whose `Core/` folder already holds the feed, download, checksum, install, and manifest code. One test project with 3 test files |
| `VortexFPS/Conductor` | Does not exist |

## The two chains

Work splits into two mostly independent chains that meet once, at Orchestrator MVP.

```
directory chain:   C0 announce freeze → C1 Master MVP → DS-7 game announce → C2 public list
                                                \
control chain:  A0 restructure → A1 core+CLI → A2 instances → A3 protocol freeze → A4 WebServer
                                                                          \        /
                                                                       C3 Orchestrator MVP → C4 alerts
```

Nothing in the control chain waits on the directory chain until C3, and the directory chain never waits on
the control chain at all. Two people can run these in parallel from day one.

---

## Phase 0: unblock everything (no dependencies)

**A0. Restructure the launcher repo.** (M)

The existing tree maps onto the target layout almost directly, which makes this mechanical rather than a
rewrite.

1. Add `VortexLauncher.sln`, `src/`, and `tests/`.
2. New class library `src/Launcher.Core/` (net8.0, no package references). Move in from
   `XonoticGodot.Launcher/Core/`: `ChecksumFile`, `DownloadService`, `InstallService`, `LauncherConfig`,
   `Manifest`, `PlatformKey`, `ReleaseFeeds`, `GameLauncher`.
3. Leave `SelfUpdateService` behind in the Avalonia project. It depends on Velopack, and Core is BCL-only by
   the dependency rule. Launcher self-update is a Desktop concern, not a shared one.
4. Move the Avalonia shell (`App.axaml`, `Program.cs`, `ViewModels/`, `Views/`) to `src/Launcher.Desktop/`.
5. Move the test project to `tests/Launcher.Tests/` and add the architecture test that enforces the
   dependency graph. Write it now, while there are only two projects to satisfy it.
6. Rename `XonoticGodot*` to the Vortex naming through project files, namespaces, and `AssemblyName`.
   Update `Directory.Build.props` and `.github/workflows/ci.yml` to match.

Done when: solution builds, the 3 existing tests pass, the architecture test passes, and CI is green.

**C0. Freeze announce v1.** (S, paper only)

Write `protocol/announce-v1.md` covering the request body from `conductor-master-orchestrator-plan.md` §2,
including `available_for_control` and `control_key_fingerprint`, the challenge sequence, TTL semantics, and
the browse query and response. Publish `Conductor.Protocol` from it. No service code yet.

It is small, it is paper, and it unblocks two separate teams at once. Do it first.

**Decisions, both answered 2026-07-30:**

- Map package format is `.pk3`, carrying either a legacy `.bsp` or a `.vmap` with its caches. C5 and A7
  are unblocked; the validation rules that follow from it are in
  `conductor-master-orchestrator-plan.md` §5.
- Hostnames are `master.vortexfps.org` for the announce protocol and `conductor.vortexfps.org` for the
  panel, one deployment behind both. C2 is unblocked on everything except who operates it.

## Phase 1: the player path and the directory

**A1. Core plus the CLI player path.** (M) Depends on A0.

New `src/Launcher.Cli/` with `vortex install`, `update`, and `launch` over Core. Build store lands here:
side-by-side versioned directories, sha256 verify, GC, rollback. Desktop reaches feature parity by calling
Core rather than its own copies.

Carry forward the ADR-0015 lesson rather than rediscovering it: GitHub's `releases/latest` ignores
prereleases, so the API-fallback feed is the only path that works today. Keep both feed sources and keep the
fallback exercised in tests.

**C1. Master MVP.** (L) Depends on C0.

`Conductor.Server` with announce intake, the UDP challenge verifier, TTL expiry, `GET /servers`, per-IP rate
limits and server-count caps, Postgres and SQLite via EF Core, and a docker deploy. Ship the two integration
tests from the plan with it: a container that announces and lists and expires, and a spoofed announce with no
UDP responder that must never appear.

**A2. Server instances, CLI only.** (L) Depends on A1. Game side is already landed.

Instance model, supervisor, orphan adoption, port pool, `vortex server *`, the three control paths, health
checks and flap detection, eventlog parsing including the four chat variants. This is the first genuinely
useful milestone for an operator: a headless box with real server management and no web surface at all.

Land the runner half of control modes here too, while the code is fresh: `control_mode` in `instance.json`,
`vortex server release`, and the control-event payload. The runner can enforce a mode it is the only client
of long before a second control plane exists.

## Phase 2: connect the game to the directory

**DS-7. Modern announce, game side.** (M) Depends on C0 and C1.

In the game repo, on its own branch off `feature/dedicated-server-v2`, coded against `Conductor.Protocol`.
Announce client on a worker thread beside the existing heartbeat, `sv_master_url` cvar, `sv_public 0`
disabling both lanes. Then the menu browser sources `GET /servers` while keeping the direct-getinfo ping path
untouched, plus the `server-browser-master` parity unit.

**C2. Public list live.** (S) Depends on DS-7 and the domain decision.

Hosted instance, public beta list, CDN in front of the browse endpoint.

**A3. Freeze `Launcher.Protocol`.** (M) Depends on A2.

Write the OpenAPI spec and the WS message schema from the instance operations A2 proved out, publish the
package. Freezing before A2 would mean guessing at the shape; freezing after A4 would mean Conductor coding
against a moving implementation.

## Phase 3: the control plane

**A4. WebServer and web MVP.** (L) Depends on A3.

`src/Launcher.WebServer/` with outbound runner links, bearer auth on every request including the WS upgrade,
the REST and WS surface, the SPA in `web/`, dashboard, live console, and service install. The architecture
test already forbids the Core reference; keep it that way when the first "just read the file directly"
temptation shows up.

**A6. Control modes, WebServer half.** (M) Depends on A4.

The `409 instance is orchestrated` contract on every mutating endpoint, the banner with scopes and audit
link, both exit buttons, and the read-only config and log views. The runner half already exists from A2.

**A5. Update, drain, rollback, metrics.** (M) Depends on A4. Independent of A6; either order.

## Phase 4: orchestration

**C3. Orchestrator MVP.** (L) Depends on A3 and C1.

Adoption queue fed by `available_for_control` announces, accept flow with scope selection, WS hub, key
handshake, command proxy over `Launcher.Protocol`, RBAC, audit. Build the fake-runner harness first; every
later test in this phase needs it.

**A9. Runner link.** (M) Pairs with C3.

`conductor_control` and `conductor_url` config, keypair generation and storage, `vortex runner link` and
`unlink`, outbound WSS with the fingerprint handshake.

**C4. Control-event ingest and alerts.** (M) Depends on C3 and A6.

Severity rules, the lost-contact split, per-operator history in the adoption queue. Test the mid-match
critical path and the killed-connection path explicitly; they are the two cases the design exists for.

## Phase 5: content and fleet operations

**C5 plus A7. Content store and fetch.** (L) Blocked on the map package format decision from Phase 0.

Upload with validation, sha256 storage, CDN, assignment commands, client distribution URLs on the Conductor
side. Fetch-by-hash, second-pass validation, quotas on the runner side. Do not start either half until the
package format is settled; every validation rule depends on it.

**C6. Fleet ops.** (L) Bulk and staged updates with canary-then-wave, config templates, scheduled tasks,
monitoring dashboards.

**A8. SourceProvider.** (L) git clone and compile pipeline, toolchain cache, repo and branch picker.
Independent of everything in Phases 3 and 4; slot it wherever there is capacity.

**C7. Hardening and scale.** (M) Multi-region master, key rotation, pen test against both protocols.

---

## What to do first

If one person is starting Monday: C0, then A0. C0 is a document that unblocks the game repo and the whole
directory chain, and A0 turns a single Avalonia project into the structure everything else assumes. Neither
depends on anything that does not already exist.

If two people: one takes C0 into C1 and stays on Conductor; the other takes A0 into A1 and A2 and stays on
the launcher. They do not need to talk again until A3.
