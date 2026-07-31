# VortexArena/Conductor: official master server and orchestrator

**Status:** REVISED 2026-07-30, amending the PLANNED 2026-07-12 version.
**Repo:** `VortexFPS/Conductor`, not yet created. This document lives in the launcher repo until it exists.
**Related:** `launcher-host-agent-plan.md` (the runner and control plane Conductor proxies),
`dedicated-server-v2-plan.md` (DS-7, the game-side announce client), `implementation-roadmap.md`.

Conductor is the officially hosted service, with two roles in one application:

1. **Master:** the public server directory. Servers announce, clients browse.
2. **Orchestrator:** control of the servers we run, plus community servers whose owners opt in.

## What changed from the 2026-07-12 version

1. Five projects down to two. The Master/Orchestrator boundary is config, not assemblies.
2. Adoption is discovered through the announce channel. The old design had Conductor mint a pairing code
   before an operator could offer a server, which meant holding a Conductor account first.
3. Control is a mode on the instance rather than a grant merged with the owner's permissions. See
   `launcher-host-agent-plan.md` §5 for the full spec, §4 below for what Conductor must handle.
4. New §5: a content store, which is how map upload works without pushing blobs at community boxes.

---

## 1. Repo structure

```
Conductor.sln
├─ src/
│  ├─ Conductor.Server/    # one ASP.NET Core app + SPA
│  │                       #   Master:       announce intake, UDP challenge, GET /servers
│  │                       #   Orchestrator: adoption queue, WS hub, command proxy, RBAC, audit, alerts
│  │                       #   Content:      map package store
│  └─ Conductor.Protocol/  # announce DTOs + version constants, published as NuGet; game DS-7 consumes it
└─ tests/
```

Roles are config-gated rather than split across assemblies. A self-hoster running their own master with
`sv_master_url` pointed at it has to be able to turn the orchestrator off. A public directory has no business
requiring fleet management.

`Conductor.Server` references `Launcher.Protocol` from the launcher repo for the control side, and never
`Launcher.Core`. Conductor and `Launcher.WebServer` are peers: two consumers of one command protocol, not two
management implementations. Anything the runner API learns, both get.

Storage is Postgres in production and SQLite for dev and self-hosters, with EF Core keeping both honest.

## 2. Master

### What exists game-side today

`MasterServerLink` and `MasterServerProtocol` already speak the classic dpmaster UDP protocol: a heartbeat
every 180 s, `getservers` and `getserversResponse` for the browser, and per-server `getinfo` and
`infoResponse` carrying the `\key\value` infostring. ServerNet answers `getinfo` probes in production. That
lane stays for LAN discovery and legacy tooling; the modern lane is additive.

### Announce v1

Freeze before DS-7 starts. `POST {sv_master_url}/api/v1/announce` over HTTPS, JSON body:

- endpoint (port only; source IP comes from the connection unless an explicit override for split-horizon)
- hostname, map, gametype, players, maxplayers, bots
- protocol version, game version, mutator and mod flags
- `sv_public` policy fields
- `available_for_control` (new): the host operator has set `conductor_control 1`
- `control_key_fingerprint` (new): sha256 of the runner's public key, present when `available_for_control`

Re-announce on map change and every 180 s. TTL 300 s, matching dpmaster's freshness contract.

### Anti-spoof challenge

On first announce, and periodically after, Master sends a classic UDP `getinfo <challenge>` to the claimed
game endpoint and requires the matching `infoResponse` before listing. The existing responder handles it, so
no new game-side listener is needed. No verified callback means never listed, which kills both spoofed
registrations and NAT-broken servers players could not have reached anyway.

It is also step one of binding an adoption offer to a real box: an offer is endpoint-verified before anyone
in the Orchestrator panel ever sees it.

### Browse

`GET /api/v1/servers?gametype=dm&notfull=1&...` returns the announce fields plus master-observed metadata
(region via GeoIP, verified-at timestamp). Latency stays a direct `getinfo` ping from the game client, since
the master cannot measure a player's ping. ETag and If-None-Match plus a compact delta form keep browser
refresh cheap, and every list response is cacheable and CDN-friendly.

Abuse controls: per-IP announce rate limits, server-count-per-IP caps, listing bans, and protocol and version
floor filters.

### Game-side integration (DS-7)

Coded against `Conductor.Protocol`. The announce client sits beside the existing heartbeat in ServerNet, on a
worker thread using BCL `HttpClient`, never on the sim thread. `sv_master_url` defaults to the hosted
Conductor. `sv_public 0` disables both lanes, and Campaign already forces `sv_public 0`, which carries over
unchanged. The menu server browser sources its list from `GET /servers` and keeps the existing direct-getinfo
ping and detail path. Parity unit to add: `server-browser-master`.

## 3. Adoption

The flow inverts the 2026-07-12 design. A community operator can now offer a server without holding a
Conductor account first.

1. **Operator opts in locally.** `conductor_control 1` plus `conductor_url` in the runner config. The runner
   generates a keypair and keeps the private half on the box.
2. **The offer rides the announce.** `available_for_control` and `control_key_fingerprint` go out with the
   normal announce. The UDP challenge in §2 has already bound that announce to something answering at the
   endpoint, so the offer is endpoint-verified before it becomes visible.
3. **A Conductor operator accepts** from the adoption queue, choosing scopes at accept time.
4. **The runner completes it outbound.** The runner dials WSS to Conductor and proves possession of the
   private key matching the announced fingerprint. Acceptance alone grants nothing; the box has to answer.

What the binding buys: an attacker can announce a claim on an endpoint they do not own, but they cannot
receive the UDP challenge and cannot produce the key. Both halves have to be true at once, on the same box.

Outbound-only holds throughout, so no community box ever opens an inbound port. Revocation is local and
immediate: `vortex runner unlink`.

## 4. Scopes and control

Chosen at accept time, editable by the host operator afterward, enforced by the runner:

`view`, `control-instances`, `edit-config`, `moderate`, `chat-read`, `chat-write`, `manage-builds`,
`upload-content`, `shell-console`

`chat-read` is separate from `moderate` on purpose. Reading player chat on someone's community server is a
privacy-relevant grant and should not ride along with "can restart it".

Default grant for an adopted community server: `view`, `control-instances`, `moderate`. `edit-config`,
`shell-console`, and `upload-content` are explicit opt-ins at accept time.

Inside a granted scope set, Conductor can do what `Launcher.WebServer` could do, because it is the same
protocol: all server settings, restart, run commands, read chat, kick and ban, and set the instance's map
content.

### Control mode and the owner's two exits

Full specification in `launcher-host-agent-plan.md` §5. What Conductor has to handle:

While an instance is `orchestrated`, the host owner's WebServer is read-only on it and holds exactly two
actions: `release` (return to local, `when=now` or `end-of-match`, default `end-of-match`) and `stop`. Both
raise an alert. The runner sends the control event before performing the action and waits up to 2 seconds for
an ack, then proceeds regardless, so Conductor must treat the ack as best-effort.

Severity on ingest:

- **critical** when `players_connected > 0` and `match_live`
- **warning** otherwise

A connection that drops with no preceding control event is a **lost-contact** alert, not a release, and it
may resolve on reconnect. Rendering an unexplained drop as a release would fill the queue with network blips
and make the mid-match case unfindable.

Record every control event against the host operator. A host that repeatedly pulls servers mid-match is a
fact worth showing in the adoption queue the next time they offer one. No automated penalty; history at the
point of decision.

### Mechanics

Each linked runner holds a persistent outbound WSS connection into the hub. Heartbeats and instance-status
snapshots flow up, commands flow down. The command envelope is the runner's own REST semantics from
`Launcher.Protocol` tunneled over the socket, which is what keeps Conductor a proxy with auth rather than a
second implementation.

Commands queue with expiry while a runner is offline and reconcile on reconnect through idempotency keys.
RBAC covers orgs, users, roles (owner, operator, viewer), and per-instance grants. Every remote command is
logged twice: by Conductor with the acting user, and by the runner with what actually ran. The runner exposes
its half to the host owner, which is what makes the read-only audit trail in the owner's banner possible.

Master and Orchestrator join on identity. A listed server whose announce carries a fingerprint Conductor has
adopted shows in the fleet UI as listed and healthy, closing the loop from directory to management.

## 5. Content store

Uploading a map to an orchestrated server is the highest-risk capability in the system: it is a file write
into a third party's data directory. It is also the one capability whose obvious design leaves half the
problem unsolved, because the players joining that server need the same file.

Content addressing handles both.

- **Upload.** Conductor staff, or an operator holding `upload-content`, push a map package to
  `POST /api/v1/content`. Conductor validates the package, stores it by sha256, and fronts it with a CDN.
- **Assignment.** The orchestrator command is "instance X should have content set {sha256, ...}", never a
  blob. The runner fetches what it is missing, verifies hashes, validates packages, and installs. A failure
  leaves the instance on its previous set.
- **Client distribution.** Joining players pull the same object from the same CDN URL. One path for our
  fleet, community boxes, and players, and it is cacheable, resumable, and deduplicated by construction.

### Package format

Decided 2026-07-30: **`.pk3`**, which is a zip, carrying either a legacy `.bsp` map or a `.vmap` with its
caches. Two accepted shapes rather than one because the legacy format has to keep working while `.vmap`
becomes the norm.

Validation runs at upload and again on the runner. Both, not either: the runner must not trust a control
plane with arbitrary writes into its own data directory, and the store must not become a way to hand a
malicious archive to every host in the fleet at once.

| Check | Why |
|---|---|
| Parses as a zip; central directory matches local headers | A truncated or hand-edited archive is rejected before anything is extracted |
| Contains `maps/<name>.bsp`, or `maps/<name>.vmap` plus its caches | The two accepted shapes. Anything else is not a map package |
| No entry path escapes the archive root | `../` and absolute paths are the standard way a zip writes outside where you extracted it |
| No symlink entries | A symlink extracted into a data directory is a write primitive pointing anywhere on the box |
| Entry count and total uncompressed size within caps; compression ratio bounded | Zip bombs. A ratio cap catches the case the size cap alone misses |
| Declared sha256 matches the bytes | Content addressing only means something if it is verified where it is used |

The sha256 of the whole `.pk3` is its content address. Nothing inside the archive is trusted to name it.

## 6. Out of scope for v1

Payment or hosting marketplace, cross-org server transfer, running game binaries on Conductor itself, and any
inbound connection to a runner.

## 7. Milestones

| # | Deliverable | Contents |
|---|---|---|
| C0 | Announce v1 freeze | `protocol/announce-v1.md` plus a published `Conductor.Protocol`. Game DS-7 unblocks here |
| C1 | Master MVP | announce intake, UDP challenge verify, TTL expiry, `GET /servers`, abuse limits, docker deploy |
| C2 | Public list live | DS-7 lands game-side, menu browser reads the master, public beta list on the hosted instance |
| C3 | Orchestrator MVP | adoption queue, WS hub, command proxy over `Launcher.Protocol`, RBAC, audit |
| C4 | Control modes and alerts | mode transitions, control-event ingest, severity rules, lost-contact split, operator history |
| C5 | Content store | upload, validation, CDN, assignment commands, client distribution URLs |
| C6 | Fleet ops | bulk and staged updates, config templates, scheduled tasks, monitoring dashboards |
| C7 | Hardening and scale | CDN on list endpoints, multi-region master, key rotation, pen test on both protocols |

C3 depends on Launcher A3 (the `Launcher.Protocol` freeze) and on C1, since adoption rides the announce.

## 8. Testing

- Protocol golden tests in `Conductor.Protocol`: the same DTO bytes asserted in the game, launcher, and
  Conductor CIs.
- Master integration: a headless game server container announces, gets challenged, lists, then TTL-expires.
  Plus a spoofed announce with no UDP responder, which must never list.
- Adoption: a fake runner harness (`Launcher.FakeGameServer` behind a real runner) covering offer, accept,
  handshake, wrong-key rejection, revocation, and offline command queueing.
- Control events: a scripted mid-match release must produce a critical alert; a killed connection with no
  preceding event must produce lost-contact rather than a release.
- Load: k6 against `GET /servers`, the only hot public endpoint, with CDN-miss assumptions.

## 9. Decisions and open questions

**Multi-region: CDN caching, not master replication.** Settled 2026-07-30.

A player in Sydney querying a master in Europe is a real cost, and the obvious fix is a master per
region. That fix is much larger than it looks: replication has to answer what happens when two regions
disagree about whether a server is verified, which region owns a listing's TTL, and what an operator
sees when a server appears in one region and not another. Those are distributed-systems questions, and
answering them badly produces a directory that is wrong in ways nobody can reproduce.

The cacheable path is `GET /api/v1/servers`, and it is nearly identical for everyone. It carries a
strong ETag, a 30 second max-age, and no per-caller state, all of which were deliberate. A CDN in
front of one origin gives most of the latency win for a configuration change rather than a
distributed system, and it does it without introducing a single disagreement about what is listed.

What it does not fix: the announce path stays one origin, so a server in Sydney still announces across
the world every 180 seconds. That is one request per server per three minutes, from a machine sitting
in a datacentre, which is the cheapest half of the problem and the one nobody notices. Revisit
replication if announce latency ever becomes the complaint; it will not be first.

Settled 2026-07-30:

- **Hostnames.** `master.vortexfps.org` serves the announce protocol and is the `sv_master_url` default;
  `conductor.vortexfps.org` serves the orchestrator panel. One deployment answers both. Separate names so
  the game-facing directory and the human-facing panel carry different cache, WAF and rate-limit policy,
  and so the panel can go down without taking the server list with it.
- **Map package format.** `.pk3` with either a legacy `.bsp` or a `.vmap` plus caches. See §5.

Still open:

- Who operates the hosted deployment.
- Whether `Conductor.Server` also serves the classic dpmaster UDP lane. A thin UDP frontend would let
  stock DP-derived clients browse us. Cheap, but it pulls legacy parsing into the service. Leaning yes;
  decide at C1.
- SPA framework, shared with `Launcher.WebServer` so there is one component library across two apps.
