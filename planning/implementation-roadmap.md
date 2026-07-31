# Implementation roadmap: Launcher, Conductor, game

**Status:** rewritten 2026-07-30. Replaces the phase 0 to 5 plan, which described work that is now built.
**Scope:** the state of `VortexFPS/VortexLauncher` and `VortexFPS/Conductor`, and everything still open.

Everything the old version of this file scheduled as phases 0 through 5 exists. A0 through A9 landed in
two commits on the launcher's `feature/a0-a2-restructure`; C0 through C7 are the whole of Conductor's
two-commit history. Both solutions build on net8.0 with no warnings. The A-series and C-series
identifiers are retired, and open work uses the EXT/LNCH/COND/TEST/DOCS scheme in the second half of
this document.

**One correction to that paragraph, kept rather than edited away, because the way it was wrong is worth
knowing.** A8 was marked landed on the strength of `SourceProvider.cs` existing and compiling. Nothing
called it: no CLI verb, no API route, no test. A milestone read as done from a class, and a class that
nobody can invoke is not a feature however finished it looks. Worse, the two things it got wrong (the
engine release tag, and looking for an editor in a release that publishes only templates) could not
surface while it was unreachable, so "it compiles" had been standing in for "it works" for the whole of
its life. Landed under LNCH-6 below, with both fixed. The lesson generalises past this entry: a
milestone is done when something calls it, and the check is a caller, not a file.

A follow-up batch has since landed LNCH-1, LNCH-2, COND-1, part of COND-2, and TEST-1. Those are marked
**Landed** in place below rather than deleted, because the identifiers are cited in task lists and in
other documents and a vanished ID reads as a lost requirement. None of that batch is committed yet: it
is working-tree only in both repos, so a fresh clone of either branch still gets the state described in
the paragraph above.

`launcher-host-agent-plan.md` and `conductor-master-orchestrator-plan.md` are still the place to find
out *why* a thing is shaped the way it is. This file is for what state it is in.

One thing to know before reading anything else: the launcher's entire build sits on
`feature/a0-a2-restructure`, two commits ahead of `main` and unmerged. Clone `main` and you get the
original single Avalonia project with three test files, which is also what `ci.yml` is building on every
push to `main`.

---

## The shape of the system

One runner per box, two control planes that talk to it as peers, and one public directory.

```
                    ┌──────────────────────────────┐
  game servers ────►│   Conductor: master role     │◄──── players browse
  announce, HTTPS   │   UDP getinfo challenge      │      GET /api/v1/servers
                    └──────────────────────────────┘

  ┌──────────────────────┐            ┌──────────────────────────────────┐
  │  Launcher.WebServer  │            │  Conductor: orchestrator role    │
  │  host owner's panel  │            │  adopted servers, RBAC, audit    │
  └──────────┬───────────┘            └────────────────┬─────────────────┘
             │      Launcher.Protocol over a socket    │
             │      the runner dialed out on           │
             └────────────────────┬────────────────────┘
                                  ▼
                       ┌─────────────────────────┐
                       │  vortex runner          │  the only process
                       │  instances, builds,     │  that touches the box
                       │  content, supervision   │
                       └────────────┬────────────┘
                                    │  stdin, srcon, getinfo, eventlog
                                    ▼
                          game server processes
```

Four structural decisions hold that picture together, and none of them is obvious from the code alone.

**`Launcher.WebServer` cannot reference `Launcher.Core`.** `tests/Launcher.Tests/ArchitectureTests.cs`
reads the `.csproj` files off disk and fails the build if it does. That makes the rule a compile error
rather than a convention, and it means the same-box case has no shortcut available to it. A panel that
could read the build store directly when it happened to be local would grow a second code path that
drifts from the remote one.

**Runners always dial out, including to a plane on the same machine.** No community box opens an inbound
port for this, and revoking a grant is something the host operator does locally without asking anyone.
Making the local case an inbound exception would have produced two auth models and two reconnect stories.

**Conductor is one application with two roles, switched by `Conductor:Roles` in config rather than by
build.** Somebody self-hosting a directory with `sv_master_url` pointed at it must be able to turn the
orchestrator off. A public server list has no business requiring fleet management to run.

**An instance is `local` or `orchestrated`, never both and never a merged permission set.** Mutating
calls from the wrong plane get a 409 whose body names the controlling plane and both exits, so a UI
renders the banner without special-casing every endpoint that can produce one. The host owner keeps stop
and release whatever the mode says, because it is their hardware.

---

## What is built

### Two frozen protocols

`Conductor/protocol/announce-v1.md` covers the game-to-master lane: the announce body, the UDP challenge
sequence, TTL and expiry, the browse query, and `available_for_control` plus `control_key_fingerprint`
for offering a box to orchestration. Frozen, additive changes only. `Conductor.Protocol` implements it
and `tests/Conductor.Tests/fixtures/` holds golden JSON for every message in the spec, meant to be shared
with the game repo so a DTO edited in one place fails the other two rather than reaching production as a
field-name disagreement.

`VortexLauncher/protocol/runner-api-v1.yaml` is the plane-to-runner contract, OpenAPI 3.1, 473 lines.
Both `Launcher.WebServer` and Conductor code against it, which is why adding a verb there gives both of
them the feature with no further protocol work. The same operations travel two ways: over HTTP to a
browser, and tunneled inside a `CommandEnvelope` over the runner link. The runner implements them once,
in `CommandDispatcher`.

### VortexLauncher

| Project | What it holds |
|---|---|
| `src/Launcher.Protocol` | Instance DTOs, command envelope, control modes and events, scopes, `ApiError`. BCL only, no project references, packable |
| `src/Launcher.Core` | Release feeds with the GitHub API fallback, resumable sha256-verified download, install with atomic swap and N-1 rollback, the side-by-side build store, `GameControl` (srcon, getinfo, MD4, eventlog parsing), `Instances` (store, supervisor, content fetch, runner link), and the source-build pipeline (`SourceProvider`, `GameCheckout`, `GodotToolchain`, `SourceStore`). BCL only |
| `src/Launcher.Cli` | The `vortex` binary, which is also the runner daemon. `install`/`update`/`launch`, `builds list\|pin\|gc`, `source set\|list\|status\|build\|remove`, `server create\|list\|start\|stop\|restart\|delete\|console\|exec\|release`, `runner run\|status\|link\|unlink\|rotate-key\|install-service` |
| `src/Launcher.WebServer` | The host owner's panel. Bearer auth on everything including the WebSocket upgrade, loopback binding by default, the runner link endpoint, proxy routes, live-console socket, `wwwroot/` |
| `src/Launcher.Desktop` | The Avalonia player launcher, with Velopack for its own updates. Calls Core for feed, install and launch |
| `tests/Launcher.Tests` | Eight files: checksum, manifest, install lifecycle, artifact naming, game control, runner, web server, architecture |

The CLI and Desktop reach the player path through the same `InstallService` calls, so the CLI doubles as
the integration surface for it. One binary is what a host operator installs, versions and packages,
whether they are typing verbs or running `vortex runner run` under systemd. `install-service` prints a
unit with `KillMode=process` on purpose: stopping the runner must not stop the game servers it
supervises, because the next runner adopts them from their pidfiles.

### Conductor

`Conductor.Protocol` is the announce protocol as code, BCL only and referencing nothing else in the repo.
A package that dragged server internals into the game's build would be a worse seam than a duplicated
DTO.

`Conductor.Server` is the whole service, one ASP.NET application, four folders:

- **`Master/`** takes announces, applies per-address rate limits with `Retry-After`, per-host server
  caps, listing bans and hostname sanitization; `ChallengeVerifier` sends the UDP `getinfo` challenge as
  a hosted service and `ExpirySweeper` ages rows out; `ServerDirectory` serves the browse query with
  filters, ETag and cursor paging. Nothing lists until the challenge comes back, which is what makes a
  listing mean something, and it needs no new game-side code because the responder that answers it is
  already in production.
- **`Orchestrator/`** holds `RunnerHub` (the socket, command correlation), adoption (queue, accept,
  decline, signed enrollment, key rotation, revoke), alerting with the lost-contact split and a
  `LinkWatchdog`, the content store with `.pk3` validation that refuses traversal entries and extreme
  compression ratios, and staged wave updates in `FleetOperations`.
- **`Auth/`** issues API keys stored hashed, rotates them with an overlap window, and enforces
  viewer/operator/owner roles. An empty database mints one owner key and logs it once, so there is no
  shared token sitting in a config file.
- **`Data/`** is EF Core over SQLite for development and self-hosters, Postgres in production.

`deploy/` has a Dockerfile and a compose file with Postgres, a `BehindReverseProxy` switch, and comments
pointing `master.vortexfps.org` and `conductor.vortexfps.org` at one deployment. Nothing is running
anywhere yet.

Five test files cover announce handling, the browse query, auth and RBAC, the adoption handshake, content
validation, and the golden fixtures.

### Decisions that are settled

Map packages are `.pk3` carrying either a legacy `.bsp` or a `.vmap` with its caches, which is what every
validation rule in the content store and the runner's fetcher is written against. `master.vortexfps.org`
serves the announce protocol and is the `sv_master_url` default; `conductor.vortexfps.org` serves the
panel; one deployment answers both, under two names so they can carry different cache, WAF and rate-limit
policy and so the panel can go down without the server list following it.

---

## What is left

The old two-chains framing still earns its place, but it now means something different. The chains no
longer meet at a code milestone, because that milestone landed. They now describe two things that can
ship on separate dates: a public server list, and people operating servers through a panel. Nothing in
the first waits on the second.

**Shortest path to a public list:** finish COND-2, then EXT-1, then EXT-2. One link in the old chain is
gone: COND-2's workflow packs `Launcher.Protocol` from a launcher checkout into a runner-local feed, so
Conductor's CI restores today without LNCH-3 and without a sibling tree. What is still forced is the
other half, because the game cannot reference `Conductor.Protocol` until something publishes it, and
nothing does yet. TEST-6 wants to land before anyone trusts the list, because it is the only thing that
would prove a spoofed announce stays off it.

**Shortest path to usable orchestration:** COND-3, then LNCH-4. The two that used to come first are
done: a runner mints its own panel token, and the remote-binding switch now refuses to start rather
than quietly exposing the API. What is left in this chain is entirely UI.

### EXT: outside these two repos

**EXT-1. Apply the DS-7 announce client to the game repo.** `planning/ds-7/` holds a finished change set
(`MasterAnnounce.cs`, a README with six apply steps) for `VortexFPS/VortexArena`. It was written out
instead of committed because another agent had eight worktrees open on `feature/dedicated-server-v2` on
2026-07-30. Applying it means taking one file in, registering `sv_master_url` and `conductor_control`,
driving `Tick()` from wherever the dpmaster heartbeat is driven, and sourcing the menu browser from
`GET /api/v1/servers`. The classic dpmaster lane is untouched.

**EXT-2. Deploy Conductor.** `deploy/` builds and composes; nothing has ever run.

Where it runs, on what, and who owns the box are infrastructure questions, and they are answered in the
NetworkOps repo rather than here: `runbooks/deploy-conductor-ovh.md` for the procedure and the
host choice, `runbooks/deploy-conductor-ovh-tests.md` for the acceptance tests, and
`runbooks/conductor-load-test.js` for load. This repo deliberately does not describe a host. A
deployment detail written down in two places is a deployment detail that is wrong in one of them, and
the copy in the application repo is the one nobody updates.

What belongs here is only what the *code* requires of any host: TLS terminated in front, since the app
answers a hard 426 on plaintext rather than redirecting and an announce client must never learn that
http works; and unfiltered outbound UDP with the replies reaching the same ephemeral socket, because
the challenge verifier is what decides whether anything is ever listed. Lose the second and every
announce still returns 200, `/healthz` stays green, and the server list stays empty.

**EXT-3. Security review, deferred to post-beta.** A pen test against both protocols, plus key rotation
practice for Conductor's own keys and whatever multi-region story the master needs. Deferred on purpose:
reviewing a service nobody has deployed tells you about the code and not about the deployment.

### LNCH: the launcher repo

LNCH-1 through LNCH-6 are the server side, roughly in the order somebody trying to use it would hit them.
The last three are the player-facing launcher, which is still the prototype ADR-0015 describes.

**LNCH-1. Issue the WebServer's bearer token. Landed.** The contract won, as it should have:
`install-service` now mints a token when the box has none and prints it once to stderr, so redirecting
the command still writes a unit file rather than a unit file with a live credential in it.
`vortex runner new-token` rotates it with no overlap window, there being one operator and one token.
`WebServerOptions` no longer carries a plaintext token at all; `RunnerTokenStore` reads the hash out of
the runner's own `runner.json`, which is what lets a rotation take effect without restarting the panel.

**LNCH-2. Make the remote-binding switch mean something. Landed.** `BindingGate.Evaluate` runs before
Kestrel binds and refuses to start unless `AllowRemoteBinding` is paired with either a certificate this
process serves or an explicit `BehindReverseProxy` assertion. It exits 78 (`EX_CONFIG`) so a unit file
can set `RestartPreventExitStatus=78` and get one loud stop instead of a crash loop burying the reason.
Refusing rather than warning was the deliberate call: nobody reads the startup log of a service that
came up fine.

**LNCH-3. Publish `Launcher.Protocol` to a feed.** `Conductor.Server.csproj` falls back to
`PackageReference Include="Launcher.Protocol" Version="1.0.0"` when the sibling checkout is missing, and
that package does not exist anywhere. CI no longer waits on this, because COND-2's workflow packs the
project itself into a local feed, but a developer who clones Conductor alone still cannot restore it
without also cloning the launcher.

**LNCH-4. Build out the panel.** `wwwroot/index.html` is 104 lines: a status table and the orchestration
banner with its two buttons. The live console socket, instance creation, build management, drain, and the
CPU and memory the runner already reports each have a working API route and no UI in front of them.

**LNCH-5. Let the CLI edit an instance.** `vortex server` can create, list, start, stop, restart, delete,
tail, exec and release, and there is no verb that changes an existing spec, so switching a map, a port or
a build means hand-editing `instance.json` or curling the API. Drain has the same problem from the other
direction: the supervisor implements it and the runner API exposes it, and no subcommand reaches it.

**LNCH-6. Give `SourceProvider` an entry point. Landed, and it was not only an entry point.**
`vortex source set|list|status|build|remove` are registered in `Program.cs` and a source build reaches
the build store, where `builds pin` and `server create --build` treat it like a downloaded release.

Adding the verbs exposed two design errors in the 351 lines they were meant to call, both of which
would have produced a build that looked fine:

- `ReadGodotPin` regexed `docs/RUNNING.md` for a version and asked for a release tagged
  `engine-4.6.3`. The real tag is `engine-4.6.3-stable-vortex1`, so every source build would have
  404'd. The pin now comes from `tools/engine-patches/engine.lock.json`, which is the file
  `verify-engine-template.py` and the release workflow already check against. Prose drifts; a lockfile
  is the thing CI trusts.
- `EnsureToolchainAsync` searched that release for an EDITOR asset. The release carries three
  `template_release` binaries and no editor at all, so it could not have worked with the right tag
  either. That was a wrong model rather than a wrong string: the editor drives an export and may be
  stock, while the TEMPLATE is what gets embedded in the shipped game and decides what engine players
  run. The template is now fetched by the checkout's own `tools/data/fetch-engine-template.py` and the
  editor comes from `--godot`, `$VORTEX_GODOT` or PATH, with no download and no silent fallback.

The export is bracketed by `tools/verify-engine-template.py`, `--preset-config` before and
`--patches --binary` after, so a source build cannot quietly ship a stock engine; that is the trap
release.yml closed for CI and the launcher had no business reopening. Engine skew refuses and names
both versions. Verified end to end on Windows against the real lockfile, template and scripts: the
exported binary came back with `GetRawInputBuffer present (1x)`, so it carried the patched engine.

A later check found a third error, and it was the same mistake one layer down: staging into the build
store had been verified, and being *usable from* the build store had been assumed. `builds pin` on a
source build exited 0 and did nothing. `current.json` recorded only the version and the game directory
resolved as `versions/<version>/<root>`, an identity that holds for a release build (id, version and
directory name are one string) and breaks for a source build, whose id is
`source:<preset>:<ref>@<sha7>`, whose directory is a flattened form of that, and whose version is the
sha alone. The marker pointed at `versions/<sha7>/`, which never exists, so `vortex launch` answered
"nothing installed", `builds list` never marked it current, and `builds gc` (which protects the pinned
build by id and was handed the version) treated the pinned source build as reclaimable. `current.json`
now carries the build id and directory name beside the version, both optional so markers written before
this still load, and `InstallServiceTests` pins a build whose three names differ.

Still open: no `runner-api-v1.yaml` operation and no panel screen, so this is CLI-only; no automated
test of the pipeline itself, because its inputs are a Godot editor and a multi-gigabyte checkout and
the CI runner has neither; and no macOS run of the `.app` bundle path.

**LNCH-7. Sign manifests with minisign.** ADR-0015 names this as the gate before launcher-managed
installs become the default path. Until it exists, install integrity rests on sha256 values fetched from
the same place as the files they describe.

**LNCH-8. Close the player-launcher gaps.** `System.IO.Compression` does not restore symlinks and the
macOS zips contain an `.app` with framework symlinks, so the macOS install path needs `ditto` or `unzip`
before it is real. There is also no settings UI (`LauncherPaths` accepts an install-root override that
nothing exposes, and channel pinning has no control), release notes render as plain text, and Desktop
talks to `InstallService` without ever touching `BuildStore`, so a player cannot pin or roll back from
the UI while the CLI can do both.

**LNCH-9. Package the launcher in CI.** The Velopack `publish` and `vpk pack` commands are written down
in the README and no workflow runs them, so the launcher cannot ship its own updates.

### COND: the Conductor repo

**COND-1** is migrations. **Landed.** There is an `InitialCreate` migration with its model snapshot, a
design-time `ConductorDbFactory`, and `Program.cs` now calls `MigrateAsync()` on Postgres. SQLite keeps
`EnsureCreatedAsync()` and is therefore still never migrated, which is a stated cost rather than an
oversight: it is the development and self-hosting database, and the comment at the call site says so.
Production is the half that had no upgrade path, and now has one.

**COND-2** is CI. **Partly landed.** `.github/workflows/ci.yml` builds and tests the solution on every
push and pull request, and asserts that `Directory.Build.props` still sets `Nullable` and
`ImplicitUsings`, the second of which would otherwise fail silently and stop checking the annotations
`Conductor.Protocol` uses to state which announce fields are optional. It resolves `Launcher.Protocol`
by checking the launcher out under a name that is deliberately not the sibling path the csproj probes
for, then packing it into a local feed, so the run exercises the package fallback that a clone of this
repo alone would take rather than the developer path every local build already covers. Still missing the
piece EXT-1 waits on: nothing publishes `Conductor.Protocol` anywhere. The golden fixtures are asserted
by the test step, `GoldenFixtureTests` being part of the suite.

**COND-3** is the orchestrator panel. `conductor.vortexfps.org` is named in the compose file, the README
and the announce spec, and there is no HTML behind it. Adoption, alerts, fleet operations, content and
audit are all complete as API; nothing renders them, so accepting a server today means a hand-written
POST.

**COND-4** is the map catalog. `Conductor/protocol/map-catalog-v1.md` is a finished 206-line spec, still
untracked in git, with no code on either side. It lets players browse and download the maps a server
actually carries, keyed by package hash so four hundred servers running the same forty maps cost the
master one copy. Steady state is 64 extra bytes on an announce that already happens.

**COND-5** is a moderation surface for the public list. `ListingBan` is in the schema and checked on every
announce, and nothing creates, lists or lifts one. Removing an abusive server from the directory currently
means running SQL against production.

**COND-6** is the dpmaster UDP question, still open from the C1 plan. A thin UDP frontend would let stock
DarkPlaces-derived clients browse us, which is cheap to add and pulls legacy parsing into the service.
The plan leaned yes and deferred the call; it is still deferred.

### TEST: coverage the plans call for and the repos do not have

Both plan documents specify a test suite that the code does not yet have. The two repos have 13 test
files between them, all unit-level: nothing starts a process, nothing stands up an HTTP host, and nothing
exercises the UDP challenge. TEST-1 has since landed, which unblocks the first two of those but does not
by itself close any of them.

- **TEST-1. `Launcher.FakeGameServer`. Landed.** A real process the supervisor can spawn: it answers
  getinfo and status over UDP, takes console commands on stdin, writes eventlog lines to stdout, and
  binds `--port 0` while reporting the port it actually got, so tests do not have to guess a free one.
  The failure modes the supervisor exists to handle are scriptable through `FAKE_*` environment
  variables: `FAKE_CRASH_AFTER_MS` for restart policy and flap detection, `FAKE_HANG` for the case that
  separates liveness from process death, holding the port while answering nothing. It is in the
  solution and builds. TEST-2 and TEST-4 are now unblocked rather than done.
- **TEST-2. Supervisor integration.** `InstanceSupervisor` and `SupervisedInstance` are about 925 lines
  covering restart policy, flap detection, drain, orphan adoption and reading the real bind line out of
  stdout, with no test that starts a process.
- **TEST-3. Finish the control-mode matrix.** The 409 cases are covered in `RunnerTests`. The two cases
  the design exists for are not: both exits working with the controlling plane deliberately unreachable,
  and the control event being emitted before the action rather than after it.
- **TEST-4. Adoption harness.** Offer, accept, handshake, wrong-key rejection, revocation, and command
  queueing while a runner is offline.
- **TEST-5. Contract tests against `runner-api-v1.yaml`.** The spec is what Conductor codes to, and
  nothing currently asserts that the runner matches it.
- **TEST-6. Master integration.** A container that announces, gets challenged, lists, then TTL-expires,
  plus a spoofed announce with no UDP responder that must never appear. `ChallengeVerifier` runs as a
  hosted service and no test starts it, so the anti-spoof property the whole directory rests on is
  currently unverified.
- **TEST-7. Nightly end-to-end on Linux.** Download a release, create an instance, boot it, query it,
  stop it. That is what a fresh operator does on day one.
- **TEST-8. Load test `GET /api/v1/servers`** with k6, under CDN-miss assumptions. It is the only hot
  public endpoint.

### DOCS: the repo front doors

**DOCS-1. Bring the READMEs up to the code.** Conductor's says "C0 complete", "Nothing is deployed" and
"`Conductor.Server` does not exist yet", then lists a three-entry layout; the server, the auth stack, the
orchestrator and the deploy files all landed in the same commit range. The launcher's says
`Launcher.Cli` and `Launcher.WebServer` "land in A1 and A4" as future work, and both shipped.
`planning/README.md` still describes Conductor as a repo that does not exist. A newcomer reading either
README gets a picture roughly two commits stale, which is the same failure this file was rewritten to
fix.
