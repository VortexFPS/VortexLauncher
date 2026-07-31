# ADR-0015 — Launcher/updater: Avalonia shell, Velopack self-update, split game payload

**Status:** Accepted (2026-07; pipeline changes in the same change)

> **Relocated 2026-07-30.** Written in the game repo, where the code sat under `launcher/`. It now lives
> in its own repo (`VortexFPS/VortexLauncher`) per repo-restructure stage 6 item 39, and paths in this
> document read accordingly: `XonoticGodot.Launcher/` here is what `launcher/XonoticGodot.Launcher/`
> was. References to `tools/make-manifest.py` and `tools/package.sh` still mean the **game** repo — they
> emit the `latest.json` this launcher consumes, and that manifest is the only interface between the two.

## Context

ADR-0014 deliberately cut auto-update ("no release channel exists yet"). A channel now exists:
`release.yml` publishes **fat per-platform zips** (binary + Godot runtime + ALL Xonotic data,
~1.5 GB each) plus `SHA256SUMS-<ver>.txt` to GitHub Releases on `v*` tags. Two runtime seams make
an external updater clean:

- **Data resolution is executable-relative** (`DataPaths.Resolve`), so a game install is a plain
  directory — no registry, no installer state.
- **`--data <dir>`** (`Main.cs:104` → `Shell.DataPath`) overrides the asset root explicitly.

The economics that motivate this ADR: the **asset pack barely changes** (pinned to Xonotic 0.8.6
release pk3s + shallow pk3dir clones; its identity is already computed in CI as
`hashFiles('download-assets.sh')`, the assets-job cache key), while the **game core changes every
release** and is only tens of MB. Today every release re-ships ~1.5 GB of identical data per
platform, and a player updating re-downloads all of it. Evidence that this already bites:
`v0.1.0-alpha` carries **only** the SHA256SUMS file — all four zips were built and checksummed but
never landed on the release (upload of 4×~1.5 GB assets is the flakiest, most quota-hostile step
of the pipeline).

Upstream ships no auto-update either (rsync channel, `Base/Makefile:67-73`), so there is no
QuakeC/engine behavior to be faithful to — this is port-native infrastructure, constrained only
by our own build reality (all-C# team, .NET 8, GitHub Releases as the only distribution point).

## Decision

### 1. A standalone launcher app — update logic lives OUTSIDE the game

A running Godot host cannot atomically replace its own runtime; in-engine update is rejected
outright. The launcher is a separate small desktop app that owns: check → download → verify →
install → launch. The game remains fully usable **without** it (the fat zips stay; unzip-and-play
is unchanged) — the launcher is a convenience layer, not a gate.

### 2. UI stack: Avalonia 11 on .NET 8

| Option | Verdict |
|---|---|
| **Avalonia** | ✅ Same C#/.NET 8 stack as the whole repo; first-class Linux (we ship `linux-client`); MVVM; single small binary per OS |
| .NET MAUI | ❌ no real Linux support — disqualifying for a FOSS shooter port |
| Second Godot project | ❌ ~60 MB runtime for a stub UI, and the same can't-swap-itself problem the launcher exists to solve |
| Tauri | ❌ smallest binaries, but a Rust toolchain context-switch for an all-C# repo |
| Electron | ❌ heaviest option, nothing the others don't do |

Pinned to the **11.3.x** line (11.3.18 at time of writing) rather than the ~2-month-old 12.0.x
major; revisit after 12.x stabilizes.

### 3. Update mechanics: Velopack for the launcher itself; launcher-managed zips for the game

Two different update problems, two mechanisms:

- **The launcher self-updates via Velopack** (MIT, cross-platform .NET, successor of
  Squirrel/Clowd.Squirrel): installer + atomic self-update + delta packages + rollback are its
  whole job, and it reads GitHub Releases natively (`GithubSource`). Hand-rolling self-update is
  exactly where launchers corrupt installs. The prototype guards all Velopack calls behind
  `UpdateManager.IsInstalled`, so an unpackaged dev build (`dotnet run`) is inert.
- **The game is NOT Velopack-packaged.** Game releases stay plain zips (usable standalone,
  upstream-shaped), so the launcher carries its own small installer for them:
  download (resumable) → verify sha256 against the release manifest → extract to a staging dir →
  atomic directory swap → record installed version. Keep the previous version for one-click
  rollback (N-1).

### 4. Split payload: `-core` zips + a content-addressed assets pack (fat zips kept)

> **Renamed since.** What this section calls a **fat** zip is now called a **complete** one, because
> the word reached the player: the launcher renders the layout straight into its INSTALLED box, and
> `0.2.0 (fat)` is jargon at best. The change is the name only — same artifact, same split, same
> reasoning below.
>
> Both spellings are live and have to stay that way for a while. `latest.json` is written by the
> game repo and read here, so the `platforms.<key>.fat` key below is still accepted
> (`ManifestPlatform.Bundle` resolves either), and `current.json` markers written before the rename
> still say `"fat"` — `InstalledState.NormalizeLayout` folds them onto `complete` on read. Nothing
> writes the old word any more. `runner-api-v1.yaml` lists all three values for the same reason.

`release.yml` + `tools/package.sh` additionally emit, per release:

| Artifact | Contents | Size | Changes |
|---|---|---|---|
| `XonoticGodot-<ver>-<platform>-core.zip` | binary + Godot runtime + licenses/README/run scripts, **no `assets/data`** | tens of MB | every release |
| `XonoticGodot-assets-<hash12>.zip` | the full `assets/data` tree | ~1.5 GB | only when `download-assets.sh` changes |
| fat zips (unchanged) | everything | ~1.5 GB | every release |

`<hash12>` = first 12 chars of `hashFiles('download-assets.sh')` — the same content key the CI
asset cache already uses. The release job **dedupes uploads**: if a previous release already
carries `XonoticGodot-assets-<hash12>.zip`, it is not re-uploaded; the manifest points at the old
release's asset URL (manifest entries carry absolute URLs precisely to allow this). The launcher
maintains one shared asset store and launches core installs with
`--data <store>/<hash12>/assets/data`; a core update never re-downloads assets.

### 5. Feed: `latest.json` attached to every release

A machine-readable manifest (`tools/make-manifest.py`, run in the release job) is attached to each
release beside the zips:

```json
{ "schema": 1, "version": "0.2.0", "tag": "v0.2.0", "channel": "stable",
  "notesUrl": "https://github.com/…/releases/tag/v0.2.0",
  "assets": { "name": "XonoticGodot-assets-<hash12>.zip", "version": "<hash12>",
              "size": …, "sha256": "…", "url": "…" },
  "platforms": { "windows-x86_64": {
      "fat":  { "name": "…", "root": "windows-client", "size": …, "sha256": "…", "url": "…" },
      "core": { "name": "…-core.zip", "root": "windows-client", "size": …, "sha256": "…", "url": "…" } },
    "linux-x86_64": { … }, "linux-dedicated-x86_64": { … }, "macos-universal": { … } } }
```

- **Stable channel** fetches `https://github.com/<repo>/releases/latest/download/latest.json` — a
  plain HTTP redirect to the newest **full** release's copy. No API call, no 60 req/hr
  unauthenticated rate limit, works at any polling frequency.
- **Sharp edge:** `/releases/latest` ignores prereleases and drafts. Until the first stable
  release exists (today's only release, `v0.1.0-alpha`, is a prerelease), and for the future
  beta channel, the launcher falls back to the **GitHub Releases API listing** and synthesizes a
  manifest from the release's assets + `SHA256SUMS` file. The API path is the fallback, never the
  default.
- `root` records the zip's internal top-level directory (package.sh zips `dist/<target>/`, so a
  fat windows zip unpacks to `windows-client/…`) so the installer doesn't guess.
- macOS remains best-effort (ADR-0014): `make-manifest.py` tolerates missing platforms.

### 6. Install layout + invariants

```
<LocalApplicationData>/XonoticGodot/Launcher/
  settings.json                 channel, install root override, update + notification policy (§8)
  game/versions/<ver>/          one extracted install per version (current + N-1 for rollback)
  game/current.json             { version, layout: fat|core, platformKey, root }
  game/staging/                 download + extract scratch; deleted on success or next start
  assets/store/<hash12>/        shared asset packs (core layout only)
```

Non-negotiable invariants, in priority order:

1. **Never gate Play on the network.** Feed unreachable → Play the installed version, say so quietly.
2. **Verify before swap.** sha256 (from the manifest / SHA256SUMS) checked on the downloaded zip
   before anything touches `versions/`. Staging extract, then a rename into place.
3. **Resumable downloads** (HTTP Range) — mandatory at 1.5 GB first-install sizes.
4. **Rollback** — previous version dir retained; `current.json` flips back on demand.

### 7. Release train: the launcher ships with the game release — ~~SUPERSEDED~~

> **Superseded by the extraction.** The launcher is now its own repo (`VortexFPS/VortexLauncher`)
> with its own release cadence, and its packages ship on **its own** `v*` releases, cut by
> `.github/workflows/release.yml` on every commit to `stable`. `LauncherConfig.LauncherRepo` is the
> repo `GithubSource` is pointed at; `LauncherConfig.Repo` remains the game.
>
> A shared release train is no longer merely unnecessary, it is actively harmful. GitHub resolves
> `releases/latest` to the newest non-draft, non-prerelease release of a repo, so a launcher release
> published to the *game* repo hijacks that redirect: `latest.json` 404s and every launcher in the
> field falls back to the unauthenticated API feed at 60 requests/hour. The failure is quiet — the
> launcher keeps working, just rate-limited — which is why `ReleaseTrainTests` asserts the two repos
> stay distinct rather than leaving it to this note.
>
> What survives from the original decision is the reasoning below about version stories, now applied
> within one repo instead of across two. The rest is kept for the record.

Launcher Velopack packages (`vpk pack` output) attach to the **same `v*` release** as the game
zips — one release train, one version story, and `GithubSource` always finds its packages on the
latest release. (Deferred until the launcher leaves prototype; the prototype runs unpackaged.)

### 8. Update policy: what happens without being asked, and what is asked

§3 settled the *mechanism* for both updates and left the *policy* unstated, which in practice meant
"check at startup, install on a button press, and restart the launcher whenever Velopack felt like
it". Three policies now, each a setting, each with a default that has to survive the question "what
does this do to someone who is about to play a match".

**Game updates default to download-then-ask.** Downloading ahead of the press is what keeps
invariant #1 honest — Play is only instant if the bytes arrived before Play was pressed — and asking
before the swap is what keeps a new build from replacing the one a player is about to join a server
with. The two halves are separable for free because invariant #2 already required them to be:
verify-before-swap means the install does everything expensive out-of-tree and then renames, so
`StageAsync` is just `InstallAsync` stopping before the `current.json` flip. Fully automatic and
notify-only are both available; neither is the default because one surprises the player and the
other makes them wait.

A swap is refused while the game is running. It rewrites `current.json` and GCs old builds, and the
running game is reading files out of one of them.

**Launcher updates default to automatic and can be switched off.** Off is a hazard worth naming:
`latest.json` is a cross-repo contract (§5), so a launcher far enough behind can lose the ability to
read the feed and therefore to install the game at all. Off therefore still checks and still
reports, because a launcher that has quietly stopped being able to do its job should say so.

**The restart is gated, and the check no longer performs it.** `ApplyUpdatesAndRestart` terminates
the process; calling it from a startup check — as the first implementation did — can throw away an
in-flight install and looks to the player like a crash. Checking and applying are separate calls,
and the restart requires that no install, no game, and no open sheet is in flight.

**The channel applies to the launcher too.** The first implementation passed `prerelease: true`
unconditionally, quietly serving prerelease launchers to stable-channel players. One channel, both
artifacts.

**Notification reach is asked, not defaulted.** In-app banner, native OS notification, or a
tray-resident process that keeps checking with the window closed. This is the only preference here
with no defensible default: the answer turns on whether the player wants a process of ours resident
on their machine, and nothing on disk can infer that. Defaulting to the quietest reach would mean
the feature silently does not work for anyone who does not already keep the launcher open;
defaulting to the loudest would install a background process nobody agreed to. So it is the single
question first run asks, and nothing notifies until it is answered.

**Unknown settings never escalate.** Every mode string is normalized on load, and an unrecognised
value reads as the shipped default — never as the more eager option, never as a promotion from
"asked" to "resident". Same rule `ReleaseChannels.Normalize` already applied to the channel, for the
same reason: a file written by a newer launcher, or edited by hand, must not be able to widen what
this one does.

### 9. Repo layout: `launcher/` in-repo, with the two Godot-repo hazards handled

`launcher/XonoticGodot.Launcher` (app) + `launcher/XonoticGodot.Launcher.Tests` (xunit, matching
the repo's test stack). In-repo because it shares the release train, CI, and review flow — a
separate repo pre-1.0 is pure coordination overhead. Two hazards this repo specifically imposes:

- **`XonoticGodot.csproj` globs `**/*.cs` from the repo root** (Godot.NET.Sdk) — `launcher/**`
  is added to its `<Compile Remove>` list, exactly like `src/` and `tests/`.
- **The Godot editor imports everything it sees** — `launcher/.gdignore` keeps it out of the
  importer, exactly like `assets/` and `_scratch/`.

CI: the per-push gate builds the launcher and runs its tests (plain .NET job, no Godot);
`ci/ci.sh` mirrors it.

## Cut list (deferred, with rationale)

- **Manifest signing (minisign over `latest.json`):** REQUIRED before promoting the launcher to
  the default install path — an updater that trusts an unsigned manifest lets whoever controls the
  host feed it arbitrary binaries; checksums alone verify transport, not authorship. Deferred only
  because the prototype's blast radius is dev-machines. Tracked as the launcher's v1.2 gate.
  **Verifying half landed** (`src/Launcher.Core/Signing/`): the launcher checks a minisign signature
  over `latest.json` when a release carries one, and refuses the release when that check fails. The
  signing half is the game repo's release job and is still cut; `release-signing.md` states what it
  has to produce, and why the policy defaults to verify-if-present rather than required until it does.
- **Code signing (Windows/macOS):** inherits the ADR-0014 cut. Consequence: SmartScreen friction
  on every Windows download and Gatekeeper refusal on macOS (the launcher can clear its *game*
  install's quarantine xattr, but nothing clears the launcher's own). Revisit at wider release.
- **Binary deltas for the game core:** once the core zip is tens of MB, zip-level granularity is
  fine; Velopack deltas already cover the launcher. Revisit if core size grows.
- **CDN fronting (R2/jsDelivr):** GitHub Releases bandwidth is fair-use, not a CDN; revisit on
  real traffic numbers, not preemptively.
- **Launcher-managed dedicated-server updates:** `linux-dedicated-core.zip` exists and helps
  operators, but the launcher UI targets players; server orchestration stays scripts.
- **Steam/itch, torrents, self-hosted infra:** out of scope, unchanged from ADR-0014.

## Consequences

- The **next release** is the first that carries `-core` zips, the assets pack, and `latest.json`;
  the launcher's manifest path is dead until then (the API fallback works today against
  `v0.1.0-alpha`, modulo its missing zips — see below).
- The stable channel stays empty until the first **non-prerelease** release is cut; pre-1.0
  testing runs on the API-fallback/beta path.
- **Pipeline health flag:** `v0.1.0-alpha` shows the fat-zip upload step can fail silently-ish
  (checksums present, zips absent — likely the 4×~1.5 GB upload). The split payload directly
  shrinks the per-release upload to ~4 core zips + manifest in the steady state, which is also the
  reliability fix.
- `tools/package.sh` gains `-core` zip emission (core zipped before assets are laid in, so no
  archiver-specific exclusion syntax); `--no-zip` behavior is unchanged.
- The launcher adds the repo's first non-Godot GUI dependency set (Avalonia, Velopack,
  CommunityToolkit.Mvvm) — confined to `launcher/`, restored from nuget.org, never referenced by
  the game or `src/` libraries.
