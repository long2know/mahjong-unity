# GHCR publishing (approved artifacts, not hosted Unity compilation)

`build.sh` builds/loads locally; it does **not** push. The manually dispatched
`ghcr-publish.yml` packages genuine, prebuilt Unity output on an Ubuntu amd64
runner with the canonical Dockerfile and pushes to
`ghcr.io/long2know/mahjong-unity`. No Unity license or Editor is available on
public hosted CI. Repeatable automated **Editor builds** still require the
existing privately activated self-hosted runner.

These workflows must first be merged through `dev` to default branch `main`.
Only same-repository manual dispatches on `main` publish. Actions are SHA-pinned;
the repository `GITHUB_TOKEN` stays inside the hosted job. No PAT, license,
runtime JWT, database or `.env` belongs in the build input.

The manual packaging and runtime-input jobs need `contents: write`: GitHub restricts
unpublished draft-release reads to push-capable identities. `contents: read`
returns HTTP 403 even for the same repository's `GITHUB_TOKEN` (observed run
41366872573). This is used only for authenticated GET/download; the workflow
does not create/update releases, Git tags or source. Checkout credentials are not
persisted, the token is not passed into the Docker build, and the separate
promotion workflow retains read-only contents access. No repository-wide
permission default, secret or PAT is changed.

## 1. Stage the genuine build input privately

Use the exact clean source checkout that produced the artifacts. The manifest's
`rendererSourceRevision` **and** renderer tree fingerprint must match it.
Do not relabel old artifacts as rebuilt source. The tool invokes the existing
strict manifest/file validator, rejects dirty/development output, and creates
only the manifest plus its four hashed runtime files. Streaming-assets builds
are intentionally unsupported by this small handoff contract.

From the publishing-tools checkout:

```bash
SOURCE_SHA=ee58678676bc3f99ac141a27111922dc3110ab2e
python3 -B scripts/ghcr_publish.py bundle \
  --source ../mahjong-polish-runtime-fix --artifacts ../mahjong-polish-runtime-fix/artifacts/unity-webgl \
  --source-sha "$SOURCE_SHA" --output .work/unity-webgl.tar.gz
# Record the emitted bundle_sha256 independently, before uploading.
gh release create build-input-polish-ee58678 \
  --repo long2know/mahjong-unity --draft --target "$SOURCE_SHA" \
  --title 'Private approved Unity build input' \
  --notes 'Genuine clean production build input; not a public release or runtime acceptance.'
gh release upload build-input-polish-ee58678 .work/unity-webgl.tar.gz \
  --repo long2know/mahjong-unity
gh api repos/long2know/mahjong-unity/releases \
  --jq '.[] | select(.draft) | {id,tag_name,assets:[.assets[]|{id,name,size}]}'
```

Keep the release **DRAFT**; do not publish it or promote its tag. Draft input
access requires repository authorization. The workflow downloads only that
repository's authenticated release-asset API, checks draft membership, asset
size and the independently supplied SHA256, then safely extracts regular files
under `artifacts/unity-webgl/`. Links, traversal, duplicates, extra files and
other roots are rejected. Archive bytes are pinned, not just asset names.

## 2. Publish a candidate (never latest)

The source must be the exact head of a same-repository PR targeting `dev`,
either open or merged. This allows runtime qualification **before** merging the
candidate source; forks and mismatched PR/manifest/source identities fail.
Dispatching this workflow is an explicit authorization to package that PR head.

The corrected November 1, 2026 PR21 input was already packaged successfully by
run `41377303458`, attempt `1`. These parameters document that publication;
do not republish it merely to run qualification:

```bash
gh workflow run ghcr-publish.yml --repo long2know/mahjong-unity --ref main \
  -f source_sha=ee58678676bc3f99ac141a27111922dc3110ab2e -f source_pr=21 \
  -f release_id=431007157 -f asset_id=700226136 \
  -f bundle_sha256=bdec288a7ab1b8c5346ca8e38e93dcf17a3fac66ccd65d7a1c6af8967c007172
gh run list --repo long2know/mahjong-unity --workflow ghcr-publish.yml
gh run watch RUN_ID --repo long2know/mahjong-unity --exit-status
```

Successful packaging publishes `candidate-RUN_ID-ATTEMPT` and
`sha-SOURCE_SHA-RUN_ID-ATTEMPT`. Only the returned `sha256:...` image digest is
immutable; tags are convenient discovery names. The workflow summary records
the exact digest, source, asset ID and input hash. This is packaging evidence,
**not** runtime/browser/device acceptance.

### Package access is a separate required gate

The image includes `org.opencontainers.image.source` linking this repository.
For an existing package, its **Manage Actions access** must grant this repository
write access. A new GHCR package may initially be **private**, even for a public
repository. An owner must set package visibility to **Public** for anonymous
self-hosting, or provision the deploying Docker client's own supported GHCR
read authentication. Repository-only `gh` credentials do not imply
`read:packages`; never export the job's `GITHUB_TOKEN` to a host or print it.

Before claiming delivery, test an actual registry pull on the intended host:

```bash
docker pull --platform linux/amd64 ghcr.io/long2know/mahjong-unity@sha256:ACCEPTED_CANDIDATE_DIGEST
```

If denied, report the package visibility/access setting; do not call a local
tag or successful registry push proof of pullability. Runtime qualification
must use this exact pulled digest, genuine Unity readiness and the normal
browser/multiplayer/device gates, with disposable qualification data, not a
production database. Record the digest and real evidence in a repository PR
description.

## 2a. Qualify the exact published issue-20 candidate on isolated hosted runners

When the preview host cannot allocate Chromium's Docker VFS snapshots, use the
manual, main-only `polish-runtime.yml`. It **does not** build an image, rebuild
Unity, push/tag anything, deploy, merge, or approve `latest`. No dispatch inputs
can change its source, image, cases or evidence.

The optional boolean `focus_only` selects only the fixed focus partition; its
default still runs all five. It does not change any canonical case or identity.

The bounded workflow pins corrected source `ee58678676bc3f99ac141a27111922dc3110ab2e`,
the already published OCI digest
`sha256:57fb86eb080d4ccda38edbd695eff451e92bfcb8ec4595c53ca0bbef0a192c02`
and config ID
`sha256:98127da019eee62ddb83e0536149a8e1970e5a2499c5dd4e7d4e837d8caef674`.
It uses the new five-file WebGL input above (11,622,505 bytes), with manifest SHA256
`ae21183a8da87013b79b4c8404c7cc38b3d8b6684113e2fead9171042ea01b29`.
Supplemental draft asset `700226139` (13,108 bytes)
in release `431007157` is exactly the original new Editor receipt, its actual
171-case NUnit XML and the unchanged complete case plan; archive SHA256:
`1a3405a9cb9e6f590da34dd42db5bbddbf8fc03c117119c6739544af8c6b7a65`.
Receipt/XML live under `artifacts/test-results/polish/editor-ee58678/`;
the original plan is `polish/runtime-final/case-plan-ee58678.json` under
`artifacts/test-results/`. The source's actual 130 frontend + 6 renderer +
12 contracts results are separate from main tooling's 107-case frontend suite.
The helper pins each member's original bytes and rejects links, duplicates,
missing/extra files, changed evidence and existing destinations. It does not
reinterpret the Editor receipt, upload license/account files, or claim a hosted
licensed Editor run.

```bash
gh workflow run polish-runtime.yml --repo long2know/mahjong-unity --ref main
gh run watch RUN_ID --repo long2know/mahjong-unity --exit-status
gh run download RUN_ID --repo long2know/mahjong-unity \
  --pattern 'polish-runtime-ee58678-*-RUN_ID-ATTEMPT' \
  --dir artifacts/test-results/polish-hosted-RUN_ID
```

Five fixed partitions (`general`, `focus`, `camera`, `relay`, `mobile`) run on
**separate** Ubuntu amd64 runners, with `fail-fast: false`. Each pulls the same
immutable candidate and pinned Chromium image, verifies image/source/non-root
identity, builds only the matching Vite reference, then invokes the original
`ee58678` canonical runtime/player wrapper without changing a guard or case.
The mobile partition omits `--cases`, retaining the full native API lifecycle.
Headful focus retains the original private authenticated Xvfb procedure.
The token is confined to the draft-input step; registry permission is read-only.

The runner installs `acl` and creates only a **fresh**
`artifacts/test-results/migration-player-work` directory. A named-user UID1000
access/default POSIX ACL lets the unchanged wrapper's runner-owned UUID fixtures
and nested bootstrap cache inherit runtime write access. Existing fixture roots
and linked destinations are rejected. The default ACL also retains the runner's
cleanup access to runtime-created subdirectories. No workspace/operator permissions or
ownership are changed; the image still runs as `1000:1000` with its original
read-only root filesystem, dropped capabilities and no-new-privileges guard.
Source CI requires the real Ubuntu UID1001-to-UID1000 SQLite/bootstrap regression,
using the already-pinned browser tooling image with the canonical bind mounts
and runtime hardening, including the unbridged permission-denied control. This
avoids unrelated host-ancestor traversal that the mounted runtime never performs.
Local runs without
`POLISH_POSIX_UID_REGRESSION=1` explicitly skip that POSIX-only case, not claim it.

Headful browser tmpfs mounts must explicitly use the actual non-root browser
UID/GID and `mode=0700`; do not rely on an image mountpoint's inherited owner or
make private Xauthority temporary storage world-writable. The focused hosted
regression records the original exact `/probe/temp` mount's effective ownership
and denied exclusive authority-file write, then verifies private UID1001 tmpfs
and mode-0600 file creation for both `/probe/temp` and the dev harness's `/tmp`.
Runtime hardening and authenticated X11 procedures are unchanged.

Run `41372708835` and all its failed raw artifacts remain historical evidence,
not acceptance. Its frozen `0dae531` harness is not rewritten or interposed.
Corrected source `ee58678` already includes the reviewed canonical tmpfs fix
from before its genuine Editor build and image publication. The workflow now
selects only those new original inputs and that new immutable image; none of
the old source's evidence is rebound, rewritten or relabeled. Source/Editor
approval and publication do not imply the new image's 22+20 runtime acceptance.

### Private-display focus tooling, separate from the application

Run `41378618444` reached the genuine player but its focus guard rejected the
unchanged authenticated launcher's `DISPLAY=127.0.0.1:310`: the consumer accepted
only `:digits`. The corrected guard accepts only `:digits` or exact IPv4 loopback
`127.0.0.1:digits`, not remote hosts, alternative addresses, screen suffixes or
trailing whitespace. Cookie creation, Xauthority, native X11 focus/restore,
memory checks and all gameplay assertions remain unchanged.

The frozen canonical wrappers have no external probe-root selector. Therefore
`focus_probe_tooling.py` explicitly stages a bounded, reviewed test-tool
derivative under the publisher's fresh `.work/focus-tooling/`. All four original
wrapper/probe/launcher inputs are byte-pinned to `ee58678`. The gameplay probe
changes only that one guard. The two Python entrypoints retain every canonical
assertion, but explicitly select the frozen application root, derived child
entrypoint and corrected probe/unchanged launcher. No application file is edited;
there is no hidden executable shim, live monkeypatch, image change, dirty override
or build/receipt relabeling.

Group and raw runtime/player receipts distinguish `applicationSourceSha` (still
`ee58678676bc3f99ac141a27111922dc3110ab2e`) from `probeSourceSha` (the clean tracked
publisher/tool revision). They record the transform hash, original and executed
file hashes and explicit tool paths; the actual Docker bind command names the
selected files. Browser-source hashes inspect the actual selected tool bytes
before and after execution. Image/config/health/build/manifest/Editor checks
continue to bind to the frozen application, not the later tool revision.

Source CI checks the actual frozen inputs and remote-display rejection, then
launches the **unchanged** private-cookie Xvfb as UID1001. Under its actual
loopback `DISPLAY`, the old consumer must reject; the corrected original native
X11 function must open the authenticated display, change focus and restore it.
This is a focused tooling regression, not full player/focus/memory qualification.

Only after independent tooling approval and validation of the other four
original partitions may the parent request:

```bash
gh workflow run polish-runtime.yml --repo long2know/mahjong-unity --ref main \
  -f focus_only=true
```

Keep the original failed focus artifacts failed. Download the retake to a
different run directory and derive a union referencing all original successful
receipts plus the new focus receipt, including both run/attempts and the exact
probe revision/hashes. A focus-only success is never combined 22+20 approval.
The image/application source stays unchanged; no Editor rebuild is needed for
this probe-format correction.

Every partition always uploads its actual raw receipts, logs and PNGs plus a
`polish/runtime-hosted/GROUP-group.json` header pinning source/digest/config,
original evidence hashes, workflow run/attempt and raw receipt hashes. Preserve
failed attempts separately when downloading. Hosted runners are not the
protected preview host; a successful run does not mean that preview was replaced.

Before combined approval, require all five partitions to pass and validate the
union against the unchanged canonical validator: **16 default gameplay cases +
6 additional geometry/reachability records**, all **20 mobile cases**, actual
native lifecycle, ten served-CSS states, focus/memory, camera and all variants.
Download and **view** the actual desktop, portrait, landscape, progressed and
camera PNGs; pin that review in the final derived qualification receipt.
Distinct Tester approval, source merge ancestry, protected promotion and safe
preview/live checks remain separate gates. Desktop Chromium/emulated mobile
does not claim physical iOS/Android testing.

## 3. Promote only after runtime acceptance

An administrator must first create environment `ghcr-production` with **required
reviewers** and restrict deployment branches to `main`. Promotion fails closed
if the reviewer protection is missing or cannot be read. The reviewer must
inspect the referenced PR's actual runtime evidence for the **exact digest**;
neither a PR's existence nor the typed confirmation proves runtime acceptance.
The source must now be merged through `dev` into `main` (ancestor check).

After the accountable owner authorizes acceptance:

```bash
DIGEST=sha256:REPLACE_WITH_EXACT_ACCEPTED_DIGEST
gh workflow run ghcr-promote.yml --repo long2know/mahjong-unity --ref main \
  -f image_digest="$DIGEST" \
  -f source_sha=ee58678676bc3f99ac141a27111922dc3110ab2e \
  -f candidate_run_id=RUN_ID -f candidate_run_attempt=ATTEMPT \
  -f acceptance_pr=ACCEPTANCE_PR_NUMBER -f confirmation="runtime-qualified:$DIGEST"
```

The protected job checks the successful packaging run/attempt, candidate and
source tags' exact digest, then copies that digest to `latest` **without a
rebuild** and reads it back. No merge or deployment is automated.

## Production Compose: one-line image swap

Change **only** the existing service image:

```yaml
image: ghcr.io/long2know/mahjong-unity:latest
```

Keep `platform: linux/amd64`, `pull_policy: never`, `restart: unless-stopped`,
`8950:8080`, Production, `ASPNETCORE_URLS=http://+:8080`, Sqlite,
`ConnectionStrings__Sqlite=Data Source=/data/mahjong-autotable.db`, the **same**
JWT/signing/CORS settings, and `${DOCKER_DATA}/mahjong/data:/data` unchanged.
The service name, database and identity remain the existing deployment's.

Because `pull_policy: never` forbids Compose from refreshing the image, first
back up the existing database safely, then explicitly pull and recreate:

```bash
docker pull --platform linux/amd64 ghcr.io/long2know/mahjong-unity:latest
docker compose up -d --no-build
```

Verify `/health` reports the accepted source identity and normal real Unity
startup; do not replace credentials, initialize a new database path, or treat a
fake ready callback/legacy renderer as successful deployment.

## Focused validation

```bash
python3 -B tests/migration/test_ghcr_publish.py
python3 -B tests/migration/test_polish_runtime.py
```

The source CI runs these contracts without any publication or licensed Editor.
