# ODD Tasks — AdminApp publication chain

Scope: Unit 5 only: static CI workflow, marker schema v3, verifier fixtures, retention coverage, contract tests and publication wording. No push, release, GHCR package mutation, deployment, or Cloudflare change.

- [x] 1. Map publication marker/workflow invariants and record operator gates. <!-- Marker schema v3 (exactly admin/api/operator, two platforms per image, homogeneous metadata), workflow matrices/retention, and ci-pr wiring verified by reading; external gates recorded in apply-progress. -->
- [x] 2. Add strict-TDD RED coverage for the third AdminApp image and workflow contract. <!-- `scripts/test-ci-admin-workflow.sh`, `test-ci-publication-marker.sh` (positive + heterogeneous/mismatch/missing-admin negatives), and `test-ci-publication-verifier.sh` (three-image fixture) all present; suites observed green against the implemented scripts, so no RED was separately observed and none is claimed. -->
- [x] 3. Implement schema v3, AdminApp workflow matrices, retention and CI wiring. <!-- Verified by reading: marker/verifier scripts at schema v3; admin package+promotion rows and rag-adminapp retention in both ci-develop.yml and ci-release.yml; all five static suites plus admin target wired in ci-pr.yml. -->
- [x] 4. Verify static publication-chain evidence and record external operator gates. <!-- All five static suites run green in this batch; actionlint unavailable locally (CI-wired) and GHCR visibility, real workflow execution, and real publication/retention recorded as external gates in apply-progress. -->
