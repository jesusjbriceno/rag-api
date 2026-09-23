# ODD Tasks — AdminApp Coolify boundary

Scope: Unit 4 only: additive AdminApp service in the Coolify Compose boundary, paired-view validation, CI checks and operator runbook. No deployment, publication, Cloudflare configuration or changes to existing non-admin services.

- [x] 1. Map current Compose/validator invariants and define ephemeral paired-view inputs.
- [x] 2. Add strict-TDD RED coverage for AdminApp topology and provenance validation.
- [x] 3. Implement the additive Compose boundary, paired validator/driver, CI checks and runbook.
- [x] 4. Verify static, Compose and regression evidence; record operational limits.

Closure evidence (Unit 4 reconciliation batch): paired validator suite 41/41 OK, driver and untouched guard shell green, static admin Docker target green, Release 839/839 (parent-supplied), CI guard wired in `ci-pr.yml`, runbook and deployment docs present. Honest caveats: RED not separately observable (tests verified against the implemented validator, none claimed); actionlint not installed locally (workflow lint is CI/operator-side); Cloudflare/DNS/TLS/firewall are external inputs not verifiable from the repo. One OpenSpec REFACTOR polish row remains open (YAML anchor consolidation of the admin env list — no anchor or equivalent exists); it does not affect the deliverables of tasks 1–4.
