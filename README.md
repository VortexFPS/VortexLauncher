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
| Launch | `src/Launcher.Core/GameLauncher.cs` | spawns the game; `--data <store>` for core installs (fat installs self-resolve) |
| Artifact names | `src/Launcher.Core/LauncherConfig.cs` | the accepted release-artifact prefixes; see the rename note below |
| Self-update | `src/Launcher.Desktop/SelfUpdateService.cs` | Velopack against this repo's releases |

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

## Packaging the launcher itself (deferred — ADR-0015 §7)

Velopack packages, not yet wired into a workflow:

```bash
dotnet publish src/Launcher.Desktop -c Release -r win-x64 --self-contained -o pub
vpk pack -u VortexLauncher -v <ver> -p pub -e VortexLauncher.exe
```

## Known prototype gaps

- macOS: `System.IO.Compression` doesn't restore symlinks, and the fat/core macOS zips contain
  an `.app` with framework symlinks — the macOS install path needs `ditto`/`unzip` before it's real.
- No settings UI (channel pinning, install-root override) — `LauncherPaths` accepts an override, nothing exposes it.
- Release notes render as plain text (no markdown).
- Manifest signing (minisign) is the gate before this becomes the default install path — ADR-0015 cut list.
