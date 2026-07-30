# planning/

Design docs for the launcher, the hosted service, and how they meet the game.

| File | Authority | Home |
|---|---|---|
| `launcher-host-agent-plan.md` | **Authoritative.** Rewritten 2026-07-30 | This repo |
| `conductor-master-orchestrator-plan.md` | **Authoritative.** Rewritten 2026-07-30 | Moves to `VortexFPS/Conductor` when that repo exists |
| `implementation-roadmap.md` | **Authoritative.** New 2026-07-30 | This repo, until Conductor exists and it needs a neutral home |
| `dedicated-server-v2-plan.md` | **Reference copy, do not edit.** Extracted 2026-07-30 from `migrated/feature-dedicated-server-v2` | The game repo owns it |

The two rewritten files keep their original filenames even though the titles moved on, because the game
repo's copy of the dedicated-server plan cross-references them by name and that repo is not being edited
here.

## Amendment to DS-7, not yet reflected in the game repo's copy

`dedicated-server-v2-plan.md` here is a faithful copy of what is on the game branch, so it does not know
about a change made on 2026-07-30. Announce v1 gained two fields:

- `available_for_control`: the host operator has set `conductor_control 1`
- `control_key_fingerprint`: sha256 of the runner's public key, sent when `available_for_control` is set

They exist because orchestration adoption is discovered through the announce channel rather than through a
pairing code minted in Conductor first. See `conductor-master-orchestrator-plan.md` §3.

For DS-7 this is additive: two more fields on a request body that was already being written from scratch. It
does not change the blocking relationship. DS-7 still cannot start until C0 freezes the protocol.

Fold this into the game repo's copy when someone next has that branch open. The branch is under active work
by another agent as of 2026-07-30, which is why it was not edited from here.

## Reproducing the reference copy

```bash
git -C ../VortexArena show migrated/feature-dedicated-server-v2:planning/dedicated-server-v2-plan.md > planning/dedicated-server-v2-plan.md
```
