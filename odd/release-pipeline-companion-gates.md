# ODD Tasks — Decouple the companion live gates from the release pipeline

Scope: make `ci-release` able to publish a GitHub Release, and publish `v0.1.0`. No change to
image publication, signing, attestation or promotion logic. No change to the companion code.

## Evidence this feature rests on

- `gh release list` is empty: **no GitHub Release has ever been published in this repository.**
- `ci-release` has exactly three runs, all `failure`, all on tag `v0.1.0-rc.2`. The only
  failing jobs in all three are `Verify live BFF companion contract` and
  `Clean-machine 4-format smoke`.
- `gh secret list` returns exactly two secrets, `SONAR_HOST_URL` and `SONAR_TOKEN`. None of
  the seven secrets those two jobs read exist (`COMPANION_BFF_URL`, `COMPANION_ID`,
  `COMPANION_KEY_ID`, `COMPANION_SECRET`, `COMPANION_API_BASE_URL`,
  `COMPANION_SERVICE_CLIENT_KEY_ID`, `COMPANION_SERVICE_CLIENT_SECRET`), and even with them
  the jobs would need a live BFF plus the deployed API, neither of which is part of this
  delivery.
- `release.needs` is `completion`, `companion`, `companion-bff-smoke`,
  `companion-format-smoke`. Because the two live gates can never pass, `release` never runs
  and the Release record is never created. Image build, scan, sign, publish, promotion,
  completion and attestation verification all pass.
- The `companion` build job passes and its artifact is attached to the release by the
  `Attach companion package and evidence` step, so the build must stay in the release path.

## Tasks

- [x] 1. Remove the two live gate jobs from `ci-release.yml` and set
  `release.needs` to `completion` and `companion` only, so the release record is no longer
  blocked by gates that require infrastructure this delivery does not include.
- [x] 2. Move those two gates into a dedicated `workflow_dispatch` workflow with their own
  minimal win-x64 build (checkout, setup-dotnet, publish), plus a header comment recording
  that they require the seven `COMPANION_*` secrets and a live BFF, and that they must be
  re-coupled to the release path when the companion delivery lands.
- [x] 3. Align `README.md` with the workflow set and the release behaviour.
- [x] 4. Verify locally: `actionlint` clean, YAML parses, and the release job's dependency
  set no longer contains either gate.
- [x] 5. Publish `v0.1.0`: confirm `ci-release` succeeds, the Release record exists, the
  companion asset is attached and the published images carry validated digests.

## Out of scope

The companion delivery itself, provisioning the `COMPANION_*` secrets or a live BFF,
changing image publication, any change to the Coolify deployment.
