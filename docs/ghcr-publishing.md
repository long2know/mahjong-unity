# WebGL source candidates and protected promotion

The new registry namespace is **`ghcr.io/long2know/mahjong-web`**. A repository
rename does not move `ghcr.io/long2know/mahjong-unity:latest`; that old image is
intentionally left unchanged as a rollback. A future legacy alias needs its
own explicit release decision after new-image runtime qualification.

The main-only `ghcr-publish.yml` workflow accepts an exact clean source SHA
and same-repository PR to `dev`. It verifies repository/base/head identity and
cleanliness, then builds the frontend/API directly. No draft-release bundle,
Unity manifest, Editor receipt, activation or engine artifact is an input.
It publishes only unique `candidate-RUN-ATTEMPT` and
`sha-SOURCE-RUN-ATTEMPT` tags with build provenance, never `latest`.

PR source/image CI does **not** publish to any registry. Its real image smoke
is supplementary evidence for an independently accepted candidate digest.

`ghcr-promote.yml` remains main-only and uses the existing **ghcr-production**
protected environment. The script refuses promotion without required reviewers,
explicit `runtime-qualified:sha256:...` confirmation, source ancestry in main,
a successful main packaging attempt, a merged same-repository acceptance PR
naming the exact source/pullable digest, and candidate/source tags that both
resolve to that digest. Promotion copies the accepted digest without rebuilding.
It changes only the new `mahjong-web:latest`; it cannot silently rewrite the
legacy image alias or an operator's Compose configuration.

No publishing/promotion dispatch is part of renderer development or a PR
review. Keep the candidate/acceptance/digest evidence in the PR description.
