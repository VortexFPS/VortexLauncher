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
dotnet run --project XonoticGodot.Launcher                 # the UI
dotnet run --project XonoticGodot.Launcher -- --smoke      # headless feed/paths check
dotnet test XonoticGodot.Launcher.Tests                    # unit tests
```

Dev builds are NOT Velopack-installed, so self-update is inert (`UpdateManager.IsInstalled`
guard) — everything else works, including real game installs into
`%LOCALAPPDATA%/XonoticGodot/Launcher` (`~/.local/share/…` on Linux).

## Map

| Piece | File | Job |
|---|---|---|
| Feeds | `Core/ReleaseFeeds.cs` | `latest.json` via `/releases/latest/download` (no API quota) → GitHub API fallback (sees prereleases) |
| Manifest | `Core/Manifest.cs` | `latest.json` model (emitted by `tools/make-manifest.py` **in the game repo's** release job) |
| Download | `Core/DownloadService.cs` | resumable (Range), sha256-verified — refuses checksum-less files |
| Install | `Core/InstallService.cs` | staging extract → atomic move → `current.json` flip; keeps N-1 for rollback; shared content-addressed asset store for `-core` installs |
| Launch | `Core/GameLauncher.cs` | spawns the game; `--data <store>` for core installs (fat installs self-resolve) |
| Self-update | `Core/SelfUpdateService.cs` | Velopack against this repo's releases |

Invariants (ADR-0015 §6): never gate Play on the network; verify before swap; resume
interrupted downloads; keep the previous version.

## The contract with the game repo

This repo builds and ships the launcher. It does **not** build the game, and the game does not
reference it. The two meet at exactly one interface: **`latest.json`**, emitted by the game repo's
release job (`tools/make-manifest.py`) and modelled here by `Core/Manifest.cs`. Changing the manifest
shape is a cross-repo change and both sides have to land together.

Two consequences worth knowing before touching either side:

- **`ManifestFeed` reads `/releases/latest/download/latest.json`**, and GitHub resolves
  `releases/latest` to the newest *non-draft, non-prerelease* release of the game repo. Any
  non-game-build release published there hijacks the feed: `latest.json` 404s and every launcher falls
  back to `GitHubApiFeed`, which is unauthenticated GitHub API at 60 req/hr. That degrades quietly —
  the launcher keeps working, just on a rate-limited path. Anything else published to the game repo's
  releases (an engine template, for instance) must be marked `prerelease`.
- **The artifact rename breaks update continuity.** When the game's artifacts go `XonoticGodot-*` →
  `VortexArena-*` (restructure stage 5, item 35), existing installs do not follow. That first
  `VortexArena`-named release is a deliberate cutover and needs documenting here and in the game's
  `docs/RELEASING.md` *before* the tag is pushed.

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

Projects and assemblies still carry the `XonoticGodot` codename, matching the game repo, which has not
run its Tier-1 rename yet. Renaming here is cheap — no published releases, no consumers — but it
should land as its own commit, after the game's rename, so the two agree.

## Packaging the launcher itself (deferred — ADR-0015 §7)

Velopack packages, not yet wired into a workflow:

```bash
dotnet publish XonoticGodot.Launcher -c Release -r win-x64 --self-contained -o pub
vpk pack -u XonoticGodotLauncher -v <ver> -p pub -e XonoticGodotLauncher.exe
```

## Known prototype gaps

- macOS: `System.IO.Compression` doesn't restore symlinks, and the fat/core macOS zips contain
  an `.app` with framework symlinks — the macOS install path needs `ditto`/`unzip` before it's real.
- No settings UI (channel pinning, install-root override) — `LauncherPaths` accepts an override, nothing exposes it.
- Release notes render as plain text (no markdown).
- Manifest signing (minisign) is the gate before this becomes the default install path — ADR-0015 cut list.
