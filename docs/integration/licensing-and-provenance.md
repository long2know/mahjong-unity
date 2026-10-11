# Licensing and source provenance

## What this iteration authorizes—and does not

Stephen Long authorized development and the allowlisted baseline import. No
root license has been selected for this repository or for original fork/backend
additions. The inherited browser `package.json` license field and nested MIT
`COPYING` are **not** a grant for all code, dependencies, artwork, or binaries.
Authorized local development/build/testing can continue; owner license
selection and complete shipped notices are gates **before image/build
redistribution**, not blockers for that local work or proof supplied by opening
or merging a source PR. This document is a provenance record, not legal advice
or a replacement license.

## Imported source

The behavior baseline is `long2know/mahjong-autotable` at immutable commit
`d10d351e543aa3e341960de9800828b6c0662a82`. The import receipt is
[`contracts/source-provenance.json`](../../contracts/source-provenance.json):
1,443 selected paths and 1,824 excluded paths, imported by exact Git blob ID
rather than a working-tree copy. Its large `inventory` records file-level
selection/exclusion; `licenseObligations`, `retainedNotices`, and
`configurationPolicy` are the concise review entrypoints.

The inherited renderer pin is
`pwmarcz/autotable@8b81d92aa37997dcfbcc6724d3bd3f694f9cc53a`.
[`THIRD_PARTY_NOTICES.txt`](../../THIRD_PARTY_NOTICES.txt) repeats the original
code notice verbatim; the original remains at
[`src/frontend/autotable-src/COPYING`](../../src/frontend/autotable-src/COPYING).
The upstream marker, inherited README, and browser synthesis credits are
retained in the frontend source tree. Retaining documentation that describes
old assets does not mean those assets were imported.

The baseline server/rule tests, not linked rule guides or the Unity example's
Japanese rules, define compatibility. The five retained modes are Changsha,
four-player, three-player, Bamboo, and Minefield. Changsha retains its
server-authoritative path; the other modes retain their existing collaborative
relay semantics. This iteration is not a rules or trust-model redesign.

## Asset boundary

| Material | First-iteration disposition |
| --- | --- |
| Inherited Kanojo tile artwork described as CC BY-NC-SA, and its tile/icon/atlas/model derivatives | Excluded. Exact license version and the complete derivative chain are not established; no noncommercial artwork is shipped. |
| External table textures, static sounds, fonts, and cheatsheet PDFs | Excluded even where an upstream document names CC0, OFL, or a CC license. Later reuse needs per-file provenance and its own obligations. |
| Existing browser synthesizer | Source retained with inherited notices/credits; not a claim that synthesis code or generated audio is automatically CC0. |
| `ArcturusZhang/Mahjong@c833d787146210f55e2ceec2cfcded40ecf53a4d` | Inspect-only. No project/code/assets copied; its MIT code license does not cover Majsoul resources or bundled third-party SDKs/fonts. |
| `tomowatt/unity-docker-example` | Inspect-only static-serving example. No files imported; it is not a Unity compiler or an activation solution. |
| New Unity meshes/textures and browser labels/icons | Original procedural work, not inherited tile art. The owner still needs to choose a license for new contributions. |

Unity records its creator, original geometry/glyph/texture sources, exclusions,
and pending license disposition in
[`unity/MahjongRenderer/Documentation/art-provenance.json`](../../unity/MahjongRenderer/Documentation/art-provenance.json).
Engine/package components retain separate applicable terms; an original artwork
record is not a grant for those components or all fork code.

Do not restore excluded assets merely to satisfy an old reference, bundle test,
or missing-image warning. Resolve a source dependency with original artwork
inside its assigned implementation scope.

## Unity tools are not a Unity license

The repository's default Docker builder packages a pinned Editor and Web Build
Support. It does **not** create an entitlement, activate an account, or guarantee
that a license file works in a headless container.

**The host activation/build gate has now passed.** On **October 10, 2026**,
the actual pinned **6000.3.24f1** host Editor accepted the existing legitimate
portable Hub licensing through its LicensingClient, passed **99/99 EditMode
tests** after independent repairs, and produced genuine non-development WebGL
output. Actual build evidence is
`artifacts/test-results/relay-native-target-repair/editor-rebuild-receipt.json`.
Scoped browser/Docker gameplay approval is
`artifacts/test-results/migration-player-final-relay-review.json`, recorded
**October 11, 2026, 01:01:59 UTC (October 10, 18:01:59 PDT)**.
No account credentials or activation/license-file contents were inspected by
the agents for that evidence.
Repeat that route with the [per-process portable Hub HOME/XDG environment](docker-build-and-run.md#verified-host-editor);
do not reactivate it, change the user's normal HOME, or export license contents.

This is observed host Editor acceptance, **not** a selection of a license tier,
a paid/free headless activation claim, proof that the same activation works in
Docker, or clearance for redistribution. The approved local runtime used a
dirty-source artifact. The final committed **99-test clean WebGL rebuild and
strict validation passed**: `artifacts/unity-webgl/renderer-build.json` records
**October 11, 2026, 01:07:03 UTC**, the final committed source revision and
**`sourceDirty: false`, `developmentBuild: false`**. Canonical clean-image
packaging, native runtime checks and the live user-preview upgrade also passed:
`artifacts/test-results/final-clean-runtime/summary.json` and
`artifacts/test-results/final-clean-runtime/preview-upgrade-receipt.json`.
The preview now serves **`mahjong-unity:final-684f678`** without dirty opt-in;
protected private configuration/key and persistent state were preserved.
This hosting success does not establish a transfer of Hub activation to a
Docker Editor. Actual local gameplay approval, physical-device qualification and
source licensing are distinct; physical iOS/Android and touchscreen double-tap
discard are unclaimed limits, not blockers to the first working iteration.

Primary documentation checked on **October 10, 2026 (UTC)**:

- [Unity 6.3 command-line license management](https://docs.unity3d.com/6000.3/Documentation/Manual/ManagingYourUnityLicense.html)
  describes an alternate activation method for **Pro**, and explicitly excludes
  Personal from those procedures.
- [Unity 6.3 manual activation](https://docs.unity3d.com/6000.3/Documentation/Manual/ManualActivationGuide.html)
  limits manual activation to the supported Enterprise/Industry assigned-seat
  and legacy serial-based cases; Personal activation is through Hub.
- [GameCI activation](https://game.ci/docs/github/activation/) describes Personal
  provisioning through Hub and CI secrets. That is not a supported,
  Docker-only/no-Hub activation recipe for this repository.
- [GameCI troubleshooting](https://game.ci/docs/troubleshooting/common-issues/)
  acknowledges that manual Personal activation is no longer supported. Do not
  use its UI workaround to bypass an unavailable activation option.
- [Unity Editor Software Terms](https://unity.com/legal/editor-terms-of-service/software)
  govern entitlement and tier eligibility separately from this repository's
  source licensing; the owner must check the terms applicable to their use.

The working host build uses **existing Hub activation**, not a newly devised
Hub-free or manual `.ulf` setup. The Docker wrapper only consumes existing,
privately provisioned compatible licensing; it does not implement GameCI's
credential-based activation or a license server. Confirm compatibility
separately before choosing that container route. If a different environment
rejects licensing, stop and use the supported Unity process; do not substitute
a fake build. The existence of a `.ulf` file is not evidence of acceptance.

Keep `.ulf`/`.alf`, account credentials, serials, and private licensing state out
of source, Docker build arguments/layers, published artifacts, and logs. Never
paste them into issue/PR/chat/state records. The host command points to the
existing portable Hub environment without copying its private state. For the
Docker alternative, use only the wrapper's narrow read-only private input;
do not mount a home directory or relax permissions globally. Raw Editor logs
remain private and must be reviewed/redacted before sharing. See
[Docker build and run](docker-build-and-run.md) for the implemented input boundary.

## Redistribution checklist

1. Owner selects a root/original-additions license, with the inherited code
   grant's scope kept separate.
2. Preserve `COPYING`, `UPSTREAM_SHA`, and the shipped third-party notices.
3. Inventory the actual npm/NuGet/Unity and runtime/base-image dependency
   versions; include required license/NOTICE files and any applicable Apache/MPL
   obligations. Manifest metadata is not a completed distribution review.
4. Confirm legitimate Unity entitlement/activation and applicable tool/runtime
   terms separately from the source license.
5. Review the final artifact tree for excluded art, credentials, and private
   configuration. Passing static tests or manifest integrity checks does not
   satisfy these gates by itself.
