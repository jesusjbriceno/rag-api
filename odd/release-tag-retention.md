# ODD Tasks — Release retention must never delete published versions

Scope: stop the release retention job from deleting published GHCR versions. No change to
publication, promotion, signing or attestation logic.

## Evidence this feature rests on

- A published index carries the release tag **and** the staging tag on the same GHCR
  version. Live payload for the published release:
  `["v0.1.0","sha256-93c58b5c559d4d25db7b8ba99da150db067203435ab625781108ad0a575df224","staging-v0.1.0"]`.
- The retention selector was `any(startswith("staging-"))`, so it matched every published
  release and then deleted the whole version, tags included, once the version passed 24
  hours.
- Timeline that confirms it: `v0.1.0-rc.2` published 2026-09-25 with the same tag shape;
  the run five minutes later was too young to prune; no ci-release run occurred until
  2026-10-04, when the prune deleted it at nine days old. The tag no longer exists in GHCR
  and a deployment pinned to it failed to pull.
- `v0.1.0` carried `staging-v0.1.0` too, so the next tag push would have deleted the
  published `v0.1.0` images. The `develop-<sha>` pre-release images were matched by the
  same rule.
- Untagging one tag is impossible: the GHCR API cannot remove a single tag from a version,
  so the only non-destructive behaviour is to keep versions that carry a release tag.

## Tasks

- [x] 1. Extract the prune into `scripts/ci-prune-staging-versions.sh` with an
  offline-testable `select` mode and a `prune` mode that re-verifies each candidate before
  deleting. Commit `432d3e9`.
- [x] 2. Change the selection rule to require that **every** tag starts with `staging-`,
  and never prune a version carrying any other tag. Commit `432d3e9`.
- [x] 3. Add `scripts/test-ci-prune-staging-versions.sh` covering the regression shape and
  wire it into `ci-pr.yml`. Commit `432d3e9`.
- [x] 4. Document the retention rule in `README.md`. Commit `432d3e9`.
- [ ] 5. Publish the fix to `main` and verify against the live GHCR payload that the
  published `v0.1.0` version is not selectable.

## Consequence to decide separately

With the fix, nothing that carries a real tag is ever pruned, so published release and
`develop-<sha>` images accumulate in GHCR. The retention job now only removes staging-only
versions left behind by an aborted promotion. An explicit retention policy for develop
pre-release images, if storage matters, is a separate decision.

## Out of scope

Storage policy for develop images, any change to publication or promotion, the Coolify
deployment.
