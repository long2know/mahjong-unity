# GHCR publishing (approved artifacts, not hosted Unity compilation)

`build.sh` builds/loads locally; it does **not** push. The manually dispatched
`ghcr-publish.yml` packages genuine, prebuilt Unity output on an Ubuntu amd64
runner with the canonical Dockerfile and pushes to
`ghcr.io/long2know/mahjong-unity`. No Unity license or Editor is available on
public hosted CI. Repeatable automated **Editor builds** still require the
existing privately activated self-hosted runner.

Both workflows must first be merged through `dev` to default branch `main`.
Only same-repository manual dispatches on `main` publish. Actions are SHA-pinned;
the repository `GITHUB_TOKEN` stays inside the hosted job. No PAT, license,
runtime JWT, database or `.env` belongs in the build input.

## 1. Stage the genuine build input privately

Use the exact clean source checkout that produced the artifacts. The manifest's
`rendererSourceRevision` **and** renderer tree fingerprint must match it.
Do not relabel old artifacts as rebuilt source. The tool invokes the existing
strict manifest/file validator, rejects dirty/development output, and creates
only the manifest plus its four hashed runtime files. Streaming-assets builds
are intentionally unsupported by this small handoff contract.

From the publishing-tools checkout:

```bash
SOURCE_SHA=0dae531a58d87548488d76a55297700d5ff3589c
python3 -B scripts/ghcr_publish.py bundle \
  --source ../mjp --artifacts ../mjp/artifacts/unity-webgl \
  --source-sha "$SOURCE_SHA" --output .work/unity-webgl.tar.gz
# Record the emitted bundle_sha256 independently, before uploading.
gh release create build-input-polish-0dae531-20261101 \
  --repo long2know/mahjong-unity --draft --target "$SOURCE_SHA" \
  --title 'Private approved Unity build input' \
  --notes 'Genuine clean production build input; not a public release or runtime acceptance.'
gh release upload build-input-polish-0dae531-20261101 .work/unity-webgl.tar.gz \
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

The already-staged November 1, 2026 input for PR21 is:

```bash
gh workflow run ghcr-publish.yml --repo long2know/mahjong-unity --ref main \
  -f source_sha=0dae531a58d87548488d76a55297700d5ff3589c -f source_pr=21 \
  -f release_id=430946697 -f asset_id=699921814 \
  -f bundle_sha256=ff7b561d4067e31937162b193c414aaa614fd94a083391151cc52b13d97c6eb2
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
  -f source_sha=0dae531a58d87548488d76a55297700d5ff3589c \
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
```

The source CI runs these contracts without any publication or licensed Editor.
