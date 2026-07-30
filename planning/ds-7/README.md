# DS-7 change set: modern announce, game side

**Status:** READY TO APPLY, not applied. **Target repo:** `VortexFPS/VortexArena`.

This is the last open item in the game repo's dedicated-server plan, and it was blocked on the
Conductor protocol being frozen. That block is gone: `protocol/announce-v1.md` is frozen and
`Conductor.Protocol` is published, so DS-7 can land whenever the branch is free.

It is delivered here rather than committed because as of 2026-07-30 another agent had eight worktrees
open on `feature/dedicated-server-v2` and its `migrated/*` siblings. Applying this would have meant
writing to branches somebody else is actively working on.

## Apply

1. Take `MasterAnnounce.cs` into `src/VortexArena.Net/`.
2. Add the `Conductor.Protocol` package reference to `VortexArena.csproj`.
3. Register the three cvars in `src/VortexArena.Server/Cvars.cs`:
   - `sv_master_url`, defaulting to `https://master.vortexfps.org`
   - `sv_public` already exists and is reused unchanged
   - `conductor_control`, default `0`
4. In `ServerNet`, construct `MasterAnnounce` beside the existing `MasterServerLink` and call
   `Tick()` from the same place the dpmaster heartbeat is driven.
5. Call `OnMapChanged()` from the map-change path that already notifies the heartbeat.
6. Menu browser: source the list from `GET /api/v1/servers` and keep the existing direct-`getinfo`
   ping and detail path untouched. Add the parity unit `server-browser-master`.

## What it does not change

The classic dpmaster lane is untouched. It keeps working for LAN discovery and legacy tooling, and the
modern lane is purely additive. `MasterServerProtocol` and the `getinfo` responder are not modified at
all, which matters more than it sounds: the responder is what answers the master's UDP challenge, so
DS-7 needs no new listener and no new open port.

## Two things that are easy to get wrong

**`sv_public 0` must not announce at all.** Not "announce and ask not to be listed". The announce is
itself the disclosure: it tells the master a server exists, at an address, running a named map. A
private server that sends one has already leaked the thing it was trying not to. Campaign already
forces `sv_public 0`, so this carries over for free, and the master rejects a `sv_public 0` body
anyway as a second line of defence.

**Never on the sim thread.** `HttpClient` on the tick loop is a network round trip inside the frame
budget. The class below owns a worker and the tick method only sets a flag.

## The control offer

Two new fields ride the announce when `conductor_control 1` is set: `available_for_control` and
`control_key_fingerprint`, the latter read from the runner's key file. Together they put an offer in
Conductor's adoption queue.

The offer grants nothing. Control begins only when the runner dials out and proves possession of the
private key matching the fingerprint. That split is what makes discovery-by-announce safe: someone can
announce a claim on an endpoint they do not own, but they cannot receive the master's UDP challenge,
and they cannot produce the key. Both have to be true on the same box.

A game server with no runner alongside it simply omits both fields.
