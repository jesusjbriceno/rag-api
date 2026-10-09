# ODD Tasks — Fix the retention wiring and the duplicated destructive prune

Scope: make retention actually work, and remove the second copy of the destructive selector.
No change to publication, promotion, signing or attestation.

## What went wrong, verified

1. **The fixed prune never ran.** Replacing the inline shell in the `ci-release` retention
   job with a call to `scripts/ci-prune-staging-versions.sh` added a dependency on the
   repository, but that job has no `checkout` step — it never needed one while the logic was
   inline. The v0.2.0 run failed with exit code 127 ("command not found") and pruned nothing.
   The offline suite could not catch this: it exercises only the `select` mode over stdin and
   never the workflow wiring.

2. **A second copy of the destructive selector still deletes published releases.**
   `ci-develop.yml` has its own retention job with `any(startswith("staging-"))`, and
   `ci-develop` runs on every push to develop. Merging the PRs for #50 and #51 ran it and
   deleted the published `v0.1.0` image versions, which carried both `v0.1.0` and
   `staging-v0.1.0`. Issue #42 was scoped to the release job, so the twin was never audited.

## Consequence to mitigate outside this slice

The deployment pins `RAG_API_IMAGE_REFERENCE` and `RAG_OPERATOR_IMAGE_REFERENCE` to
`:v0.1.0`, and those versions no longer exist in the registry. With `pull_policy: always`
the next redeploy fails. The mitigation is to move both references to `:v0.2.0`, which is
published and verified.

## Tasks

- [ ] 1. Add the missing checkout to the retention job in `ci-release.yml` so the script is
  present in the workspace, and confirm the step's failure mode is no longer silent.
- [ ] 2. Replace the inline destructive retention in `ci-develop.yml` with the same shared
  script, preserving its develop-image retention intent (keep the newest N, drop only those
  older than its own threshold) while applying the same absolute rule as the staging path: a
  version carrying any tag outside the class being pruned is never deleted.
- [ ] 3. Extend `scripts/ci-prune-staging-versions.sh` with the develop class and keep the
  selection rule explicit and testable in `select` mode.
- [ ] 4. Extend `scripts/test-ci-prune-staging-versions.sh` to cover the develop class,
  including a version carrying both a develop tag and a release tag that must never be pruned.
- [ ] 5. Add an automated guard that fails when a workflow invokes a repository script without
  a checkout step, so this class of wiring defect cannot recur silently.

## Out of scope

Recovering the deleted `v0.1.0` versions (impossible through the registry), the retention
policy thresholds themselves, and any change to the Coolify deployment.
