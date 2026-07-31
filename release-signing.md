# Release signing: what the game repo's release job must produce

The launcher verifies a minisign signature over `latest.json`. Nothing signs it yet. This document
is the contract the signing side has to meet, written from the verifying side so the game repo can
implement it without reading C#.

Verifying side (this repo, done): `src/Launcher.Core/Signing/`, wired into `ManifestFeed` in
`src/Launcher.Core/ReleaseFeeds.cs`.
Signing side (game repo, not done): `release.yml`, beside the step that runs `tools/make-manifest.py`.

Background: ADR-0015 cut list, "Manifest signing (minisign over `latest.json`)".

## Why the manifest and not the zips

`latest.json` already carries the size, URL and sha256 of every file in the release, and the
installer refuses to hand the extractor any file whose bytes do not match its published checksum
(`DownloadService.DownloadAsync`). So one signature over the manifest covers the whole release:

    trusted key -> signature -> exact bytes of latest.json -> sha256 in those bytes -> bytes on disk

Every link is mandatory, and the installer only ever fetches URLs that came out of the manifest, so
there is no installed byte the signature does not transitively cover. Signing the zips instead would
be four signatures and four extra fetches per release, and it would leave the document that decides
*which* zip a player gets, and what URL it comes from, unsigned. An attacker holding the release host
could then serve a genuinely signed but older or wrong-platform build and still be within the rules.

Signing `SHA256SUMS-<ver>.txt` as well is optional and buys nothing while `latest.json` exists. It
would only start to matter if the launcher's GitHub API fallback path needed to be trusted, and that
path has a deeper problem: see "Known gaps" below.

## Requirements

1. **One keypair, generated once.**

   ```
   minisign -G -W -p vortex-release.pub -s vortex-release.key
   ```

   `-W` leaves the secret key unencrypted, so the release job needs no interactive passphrase; the
   secret-key file is then itself the secret. Store its contents in the game repo's Actions secrets
   (`MINISIGN_SECRET_KEY`), write it to disk inside the job, and never commit it. If you prefer a
   passphrase, keep it in a second secret and feed it to `minisign` on stdin.

   Publish `vortex-release.pub` somewhere a human can check it against the launcher (the game repo's
   README is enough). Send its base64 line to this repo; it goes in
   `ReleaseSigning.TrustedKeyLines`.

2. **Sign `latest.json` on every release, including prereleases.** The stable channel reads
   `latest.json` through the `/releases/latest/download/` redirect, which skips prereleases, but the
   beta channel will need the same guarantee and the file is cheap.

   ```
   minisign -S -s vortex-release.key -m latest.json -t "VortexArena $GITHUB_REF_NAME"
   ```

3. **Attach the output as `latest.json.minisig`, to the same release as `latest.json`.** That exact
   name: the launcher fetches `<manifest URL>.minisig` and treats a 404 as "this release is not
   signed". Any other HTTP status is an error, so a broken or half-uploaded signature asset fails
   loudly instead of quietly reading as unsigned.

4. **Sign the bytes you upload.** The signature covers `latest.json` byte for byte, including a
   trailing newline or a BOM if one is there. Generate the manifest once, sign that file, upload that
   same file. Regenerating it after signing breaks verification if anything in it varies between runs
   (a timestamp, key order, whitespace).

5. **Either signature mode is fine.** minisign's legacy mode signs the file; prehashed mode (`-H`)
   signs its BLAKE2b-512 hash. The launcher reads both, so the flag does not have to be coordinated.

6. **Signing tool is your choice.** `minisign`, `rsign2`, or a GitHub Action wrapping either, as long
   as the output is minisign's four-line format with a trusted comment and its global signature. The
   launcher checks that global signature too, so a tool that omits the trusted-comment line will be
   rejected.

Not required: signing the zips, the assets pack, or `SHA256SUMS-<ver>.txt`.

## What the launcher checks

Against `latest.json.minisig`, in order:

- the file parses as minisign's format: two base64 payloads (74 and 64 bytes) plus a trusted comment;
- the 8-byte key id in the signature matches a key compiled into the launcher. **An unrecognized key
  id is a failure, not a fallback to "unsigned"** - otherwise anyone could generate a keypair and the
  check would be theatre;
- the Ed25519 signature is valid over `latest.json` (or over its BLAKE2b-512 hash in prehashed mode);
- the global signature is valid over `signature || trusted comment`, so the comment cannot be
  rewritten under a valid signature;
- S is canonical (below the group order), so the signature cannot be malleated into a second valid
  form.

A failure stops the update with the reason shown to the player, and does **not** fall through to the
GitHub API fallback feed. Falling through would turn "this manifest was tampered with" into "try a
source with weaker checks". The installed version still launches either way: never gating Play on
the network is ADR-0015 invariant #1, and none of this touches an install that already exists.

A feed that has no signature to check is a different case and is skipped rather than fatal, so the
chain moves on to a feed that might have one.

## Rollout order, and why there is no flag day

The launcher's policy has three states (`ManifestSignaturePolicy`): `off`, `verify-if-present`,
`required`. It ships as `verify-if-present`.

The enforcing half of this feature is a binary already installed on players' machines. There is no
moment at which "everyone flips at once" is available, because changing what an installed launcher
requires means shipping that launcher a new version, and the release it needs in order to update
itself is the same release the new rule would be judging. So the two halves can only change in some
order, and only one order survives:

- **Signatures first, enforcement second** works. Old launchers ignore an extra asset they never ask
  for; new ones verify it. Both populations keep updating throughout.
- **Enforcement first, signatures second** breaks every launcher in the field at once. All of them
  refuse every release in existence, including the one carrying the fix, and the recovery channel is
  the channel that just got blocked.

`verify-if-present` is what spans the gap, and it is not a compromise on strictness: a signature that
is present and wrong is refused under it exactly as it would be under `required`. The only thing it
tolerates is a release with no signature at all.

Order of operations:

1. Generate the keypair (requirement 1).
2. Add the public key line to `ReleaseSigning.TrustedKeyLines` in this repo and ship a launcher
   release. **Before the release job starts signing**, not after: a signature from a key the fleet
   does not have is "signed by an unknown key", which fails closed for everybody.
3. Turn on signing in the release job (requirements 2-6). Cut a release. Existing launchers keep
   working; ones carrying the key start reporting `signed by key <id>` in
   `ReleaseManifest.SignatureStatus`.
4. Verify with a real launcher build before trusting the default:
   `VORTEX_LAUNCHER_SIGNATURE_POLICY=required` forces `required` for one run, so the strict path can
   be exercised against a real release without shipping it to anyone.
5. Once every release the launcher can reach carries a signature, change `ReleaseSigning.DefaultPolicy`
   to `Required` and ship it. Note what step 5 also does: it drops the GitHub API fallback feed out of
   the chain, because a manifest the launcher assembled itself out of an API listing has nothing to
   verify. Read the beta-channel gap below before taking it.

`SigningTests.No_release_key_is_provisioned_yet` fails the moment step 2 lands, as a reminder that
step 3 is now unblocked.

## Key rotation

Trusted keys are a list, and trusting two at once is the rotation mechanism. Same ordering rule as
the rollout: add the new public key to the launcher and ship it, wait for that build to reach
players, then switch the release job to the new secret key, then drop the old public key in a later
launcher release.

There is no revocation channel. The launcher polls nothing that could tell it a key went bad. If the
secret key leaks, the response is a launcher release that removes the compromised key, delivered over
the launcher's own Velopack self-update, plus re-signing whatever releases still need to be
reachable. That makes the self-update path the recovery channel for this one, and the launcher's own
packages are not signed (ADR-0014's code-signing cut, restated in ADR-0015). Worth knowing before it
is needed.

## Known gaps

- **Rollback.** A signature proves who wrote a manifest, not that it is the newest one. Someone
  holding the release host can replay an older, validly signed manifest and walk players back to a
  build with a known bug. The fix is a freshness rule: record the highest version ever seen and
  refuse to go backwards without an explicit rollback action from the player. Not implemented; the
  launcher already keeps N-1 on disk for deliberate rollback, so the two need designing together.
- **The GitHub API fallback is unsigned by construction.** It builds a manifest client-side from an
  API listing, so there is no published document to sign. Under `verify-if-present` it stays usable;
  under `required` it drops out of the feed chain. Anything that must be reachable under `required`
  needs a real `latest.json` on its release.
- **The beta channel currently depends on that fallback.** `ChannelFeeds.FeedFor` asks the API feed
  first for beta, because `/releases/latest/download/` structurally cannot see a prerelease. Under
  `required` the API feed drops out and beta silently resolves to the newest *stable* release
  instead. Fixing that needs two things: signed `latest.json` attached to prereleases (requirement 2
  already asks for it), and a launcher that can fetch a manifest by tag rather than only through the
  `/releases/latest/` redirect. The second does not exist yet. Until it does, step 5 of the rollout
  costs the beta channel.
- **`verify-if-present` accepts a stripped signature.** Deleting `latest.json.minisig` from the host
  reads as "unsigned" and passes. That is inherent to the transition state and is the reason it is a
  transition state.
- **The launcher's own binary is not signed.** Velopack packages carry no signature of ours, and
  there is no Authenticode or Apple Developer ID certificate (ADR-0014 cut). Manifest signing raises
  the floor for game installs; it does nothing for the launcher's own update path.
- **The signing key lives in CI.** Whoever can run arbitrary steps in the game repo's release
  workflow can sign. This moves the trust boundary from "the release host" to "the release
  workflow", which is the point, but it does not remove it.
