# Unit 10 Independent Verification Report

**Status: PASS — Unit 10 (verdict revised from the revision-1 FAIL; see "Final closure — revision 2")**
**Original revision-1 status: FAIL** (four CRITICAL findings; superseded by the final closure below).
**Scope:** Unit 10 — bounded pipeline, persisted retries, staged watermarks, pause/resume, and selected extractor.
**Verifier mode:** revision 1 was read-only verification; revision 2 is a documentation-only closure that changes no source, test, task, staging, commit, reset, stash, or review state.

## Structured status and action context

- Change selection: `historical-ingestion-rebaseline`, resolved from the only artifacts containing `Unit 10` after the unscoped status result was ambiguous.
- Native status: `artifactStore: openspec`; `nextRecommended: apply`; `verify: blocked` because failed verification evidence is incomplete.
- `actionContext`: `repo-local`; workspace and allowed edit root: `/opt/wf/rag-api`.
- Strict TDD: active (`openspec/config.yaml`).
- Native runtime attempt: continuation token `sha256:2be8a74e9a83efe371463bd3e5c0fce81d1338e743229e1643b772015ed42a43` acquired for this verification before test execution.

## Final closure — revision 2 (Unit 10 verdict: PASS)

**Verdict:** Unit 10 changes from **FAIL** to **PASS**. All four original CRITICAL findings are now closed — three by remediation evidence (durable poll accounting before dispatch; restart-durable staged/committed watermarks; real extractor + real API-client loopback proof) and one (strict-TDD evidence) by an explicit maintainer acceptance of the controlled compile-RED reconstruction. The change as a whole is **not** archive-ready (see "Scope boundary" below); that is a change-level backlog, not a Unit 10 verdict.

### Maintainer acceptance — controlled compile-RED reconstruction

The maintainer accepted the controlled compile-RED reconstruction (recorded in `apply-progress.md`, "Unit 10 controlled strict-TDD reconstruction") as sufficient strict-TDD provenance for the original six Unit 10 production files and their six test files. That reconstruction moved (never deleted) exactly the six original production files, observed an authentic compile-fail RED on the Unit 10 test surface — `CS0234` ×6 / `CS0246` ×4, 10 errors, test assembly not produced, 0 tests executed — then restored every byte (`sha256sum -c` 12/12 OK) and re-ran the focused GREEN (108/0/0) plus the unit-project safety net (203/0/0). Net changed production/test lines: 0. The maintainer accepted this in place of a per-assertion RED for the original authoring instant, which cannot be reconstructed from artifacts.

### Closure of the four original CRITICAL findings

| # | Revision-1 finding | Closure | Basis |
| --- | --- | --- | --- |
| 1 | Poll retry accounting not durable before dispatch (`DocumentLifecycleEngine.PollAsync` dispatched the HTTP poll before persisting `PollAttempts`). | `PollAsync` now records and persists the poll attempt (`poll_dispatch`) before `_api.PollAsync`, rolls the reservation back on a still-processing success, and refuses a fourth dispatch via `AttemptCounter.RecordDispatch()` before any HTTP call. RED `PollAttempt_IsPersistedBeforeDispatch_AndRolledBackWhenStillProcessing` (`Expected 1, Actual 0`) and `PollAttempt_NoFourthDispatch_AfterThreeReservedKills` → GREEN 2/2. | Evidence |
| 2 | Staged/committed watermark state not restart-durable (`WatermarkScheduler` in-memory only; no rehydration from durable staged rows). | Closed across three corrective subunits: (a) durable `SqliteRunStore.SaveStagedAsync`/`SaveLoadedAsync`/`GetWatermarkRowsAsync` (`WatermarkAccounting_IsDurableAcrossReopen`); (b) pipeline rehydration (`WatermarkScheduler.Rehydrate`/`TryGetStagedBytes`, `HistoricalPipeline` wiring) turning `Restart_RehydratesCommittedWatermark_FromDurableRows` and `Restart_RehydratesStagedWatermark_AndStillBoundsClaiming` green; (c) atomic staged-byte accounting at the staged boundary (`CrashDuringPoll_AfterCommit_SurvivesRestart_WithStagedCapacity`, `CrashDuringPoll_ThenRestart_WithTinyStagedWatermark_StopsBeforeClaiming`, `WatermarkRehydration_RestoresCapacity_ForRowsThatNeverRecordedAMeasurement`). | Evidence |
| 3 | Strict-TDD evidence incomplete (unit-level table rather than task/test-file provenance). | Closed by the controlled compile-RED reconstruction plus the maintainer acceptance above. `apply-progress.md` additionally carries the per-file strict-TDD provenance matrix and the authentic compile-RED trace for the original Unit 10 surface. | Acceptance + evidence |
| 4 | Real selected-extractor + real API-client loopback acceptance proof absent. | Closed by the loopback remediation: the real `CompanionReflectionExtractor` and the real `HistoricalApiClient` communicate over a real in-process HTTP/1.1 server on a loopback TCP socket. The idempotency-key/transport correlation defect was fixed in production and `PrimeApiClientState` was removed, so `Loopback_OverRealLoopbackHttpServer` passes **unprimed**, with the negative triangulation `Loopback_RealServerKeepsReportingPending_NeverReportsLoaded` (durable `RemotePending`, never `Loaded`). | Evidence |

### Final independent verification results

| Command | Result |
| --- | --- |
| `dotnet test tests/Rag.HistoricalLoader.UnitTests/Rag.HistoricalLoader.UnitTests.csproj --configuration Release` | **203 passed / 0 failed / 0 skipped** |
| `dotnet test tests/Rag.HistoricalLoader.IntegrationTests/Rag.HistoricalLoader.IntegrationTests.csproj --configuration Release` | **23 passed / 0 failed / 0 skipped** |
| `dotnet build Rag.sln --configuration Release` | **0 errors; 4 NU1903 warnings** |
| `dotnet test Rag.sln --configuration Release` | **603 passed / 0 failed / 0 skipped** |

These are the final independent results recorded on the parent's authority; this revision records them and did not re-execute the suites. They are internally consistent with the recorded corrective deltas: the Unit 10 corrections moved HistoricalLoader unit from 184 to 203 (+19) and integration from 19 to 23 (+4), so the solution total moved from 580 to 603 (+23). Recorded per-project breakdown: Companion 101, Rag.UnitTests 191, HistoricalLoader.UnitTests 203, HistoricalLoader.IntegrationTests 23, Rag.IntegrationTests 85 (= 603).

### NU1903 — open, unrelated dependency risk

`NU1903` on the transitive `SQLitePCLRaw.lib.e_sqlite3 2.1.11` remains open with 4 warnings. It is **not** introduced by Unit 10 and is **not** asserted as resolved by this closure; it is a standing, pre-existing dependency risk owned outside this unit. The Release build is otherwise clean (0 errors).

### Scope boundary — the change is not archive-ready

The revision-1 report's separate blocker, "24 unchecked implementation tasks remain elsewhere in the change", is **outside Unit 10 scope** and is unaffected by this verdict. The current backlog is 30 unchecked task rows: 24 `sdd-owner: implementation` (the configuration-consolidation REFACTOR and Units 11–14) and 6 `sdd-owner: parent` delivery rows. Unit 10 is PASS; the overall change remains not archive-ready until those rows and their decision gates are satisfied. This revision marks no task complete.

**The revision-1 criterion results and findings below are annotated as closed and retained as the historical record.**

## Criterion results

| Criterion | Result | Evidence |
| --- | --- | --- |
| Bounded concurrency | PASS | `HistoricalPipeline` processes a single claim per loop; focused test `Concurrency_IsBoundedToOne_ProcessesDocumentsSerially` passed (three documents, maximum concurrent extraction = 1). |
| Durable retry/run behavior through Unit 6 seams | FAIL → PASS (CRITICAL closed, see Final closure #1) | Reserve/upload/commit dispatches persist attempts before dispatch, but `DocumentLifecycleEngine.PollAsync` calls `_api.PollAsync` before persisting/incrementing `PollAttempts`. A kill after a poll dispatch can lose the consumed attempt. |
| Watermark advances only after commit | FAIL → PASS (CRITICAL closed, see Final closure #2) | In-process behavior is correct: `ReleaseCommitted` is invoked only after a durable `Loaded` result. But `WatermarkScheduler` is in-memory only and is constructed afresh by `HistoricalPipeline`; it is not rehydrated from Unit 6 durable staged rows after restart. A recovered `Staged` document can load with no staged key, so `ReleaseCommitted` returns false and committed watermark totals do not advance; restart can also bypass a previously reached staging watermark. |
| Fourth-attempt terminal handling | FAIL → PASS (CRITICAL closed, see Final closure #1) | Reserve-path tests prove three attempts and no fourth, but the unpersisted pre-result `PollAsync` dispatch above violates the same guarantee for the poll HTTP operation across a forced restart. |
| Pause/resume/cancellation | PASS | `PauseRequested_StopsWithoutClaiming_AndResumeContinues` and `CancellationBeforeRun_ConsumesNoAttempt` passed; pause state and resume use the Unit 6 run store. |
| Safe failure classification | PASS | Focused tests passed for transient/unknown network, auth, contract/data, document, local-capacity, and cancellation classification. Auth blocks the run and document faults permit later documents. |
| Selected extractor adapter | FAIL → PASS (acceptance gap closed, see Final closure #4) | Safe DOC, unsupported-format, missing-path, cancellation, and no compile-time Companion-reference branches passed. However no test executes a supported `.pdf`/`.docx`/`.md` reflection load against a real Companion adapter, and pipeline tests compose `FakeTextExtractor`/`ScriptedApiClient`, not the real extractor plus Unit 9 client required by Unit 10's decision gate. |

## Critical findings

1. **Poll retry accounting is not durable before dispatch.** `src/Rag.HistoricalLoader.Core/Lifecycle/DocumentLifecycleEngine.cs`, `PollAsync`: the HTTP poll is dispatched before `PollAttempts` is recorded. This is incompatible with the Unit 10 requirement that no HTTP operation reaches a fourth attempt after restart.
2. **Staged/committed watermark state is not restart-durable.** `WatermarkScheduler` holds state only in a dictionary; no Unit 6 store record or restart rehydration exists. This defeats bounded staging and makes committed totals inaccurate after a recovered staged document commits.
3. **Strict-TDD evidence is incomplete.** `apply-progress.md` has a unit-level `TDD cycle evidence` Phase/Evidence table, not task-row evidence tying each RED/GREEN assertion to the six actual test files and current execution. Strict TDD requires incomplete evidence to be CRITICAL.
4. **The required real extractor + real API-client loopback proof is absent.** The available focused tests verify seams and guarded adapter branches only.

**Revision-2 status:** all four findings above are now **CLOSED** — findings 1, 2, and 4 by remediation evidence and finding 3 by the controlled compile-RED reconstruction plus maintainer acceptance. They are retained above as the revision-1 record.

## Spec coverage

- Failure limit / terminal network outcome: **PASS (closed in revision 2)** — poll accounting is now persisted before dispatch; revision 1 was partial/FAIL.
- Document-error continuity: **covered; PASS**.
- Pause/restart safety and no unconfirmed loaded result: **covered by focused tests; PASS**.
- Durable lifecycle/operator-visible state: **PASS (closed in revision 2)** — restart-durable staged/committed watermark accounting is implemented and evidenced; revision 1 was partial/FAIL.
- Direct adapter isolation from Companion project references: **covered; PASS**. Functional supported-adapter dispatch: **PASS (closed in revision 2)** — the real extractor + real API client are proven over a real loopback socket; revision 1 was not covered/FAIL.

## Tests and validation

**Revision note (revision 2):** the table below is the revision-1 execution set. The final independent results recorded by revision 2 are **203 unit / 23 integration / Release build 0 errors + 4 NU1903 / solution 603** (see "Final closure — revision 2").

| Command | Result |
| --- | --- |
| `dotnet test tests/Rag.HistoricalLoader.UnitTests/Rag.HistoricalLoader.UnitTests.csproj --configuration Release --filter "FullyQualifiedName~Pipeline | FullyQualifiedName~Extraction"` | **92 passed, 0 failed, 0 skipped** |
| `dotnet test tests/Rag.HistoricalLoader.UnitTests/Rag.HistoricalLoader.UnitTests.csproj --configuration Release --no-build` | **184 passed, 0 failed, 0 skipped** |
| `dotnet test tests/Rag.HistoricalLoader.IntegrationTests/Rag.HistoricalLoader.IntegrationTests.csproj --configuration Release --no-build` | **19 passed, 0 failed, 0 skipped** |
| `dotnet build Rag.sln --configuration Release --no-restore` | **0 errors; 4 NU1903 warnings** for `SQLitePCLRaw.lib.e_sqlite3` |
| `dotnet test Rag.sln --configuration Release --no-build` | **580 passed, 0 failed, 0 skipped** (101 Companion, 191 Rag unit, 19 HistoricalLoader integration, 184 HistoricalLoader unit, 85 Rag integration) |
| Focused coverage: `dotnet test tests/Rag.HistoricalLoader.UnitTests/Rag.HistoricalLoader.UnitTests.csproj --configuration Release --no-build --filter "FullyQualifiedName~Pipeline | FullyQualifiedName~Extraction" --collect:"XPlat Code Coverage;Format=opencover" --results-directory /tmp/unit10-verify-coverage` | **92 passed, 0 failed, 0 skipped** |

Normal VSTest negotiation succeeded; `VSTEST_CONNECTION_TIMEOUT=180` was not used.

### Changed-file coverage

| File | Line | Branch | Rating |
| --- | ---: | ---: | --- |
| `Pipeline/HistoricalPipeline.cs` | 87.0% | 75.0% | Acceptable |
| `Pipeline/Model.cs` | 94.1% | 86.4% | Acceptable |
| `Pipeline/WatermarkScheduler.cs` | 90.3% | 75.0% | Acceptable |
| `Pipeline/PauseController.cs` | 100.0% | 50.0% | Excellent line coverage |
| `Pipeline/ResumeController.cs` | 100.0% | 50.0% | Excellent line coverage |
| `Extraction/CompanionReflectionExtractor.cs` | 48.3% | 26.9% | WARNING — live reflection-load path is untested |

## Strict TDD and assertion quality

- TDD table present: **accepted (revision 2)** — the controlled compile-RED reconstruction, the per-file provenance matrix, and the maintainer acceptance close the strict-TDD gap; revision 1 called this incomplete/CRITICAL.
- Test files cross-referenced and present: **6/6**.
- Current GREEN: **92/92 focused tests pass**.
- Assertion quality: **PASS** — no tautologies, ghost loops, type-only-only assertions, smoke-only tests, or CSS/implementation-detail assertions found in the six inspected files. The issue is missing behavioral coverage, not trivial assertions.
- Test layers: **92 focused unit tests across 6 files**; no Unit 10 integration/E2E test file.

## Review workload and scope

- Forecast: chained `feature-branch-chain`; Unit 10 only; `size:exception` required/requested.
- Apply record claims approximately 1,542 Unit 10 authored lines and an authorized <=1,800 exception. The native Unit 10 candidate inventory is exactly the six Engine files and six test files inspected.
- Current status of those twelve candidate paths: untracked and confined to that inventory; no additional Unit 10 source/test path was found. Unrelated dirty work exists elsewhere and was not attributed to or touched by this verification.
- **Scope violation attributable to Unit 10: none found.**

## Task completion

- Unit 10 implementation checkboxes: **all checked**.
- Global implementation tasks still unchecked: **24**. These are CRITICAL completeness/archive blockers; this report is not archive-ready. Parent-owned unchecked delivery/review rows are excluded from this implementation count.

### Exact unchecked implementation task lines

- [ ] REFACTOR: collapse the bindable options and migration scaffolding into a single `Configuration/HistoricalLoaderOptions.cs` namespace; re-run `dotnet test Rag.sln --no-build` and confirm green. <!-- sdd-owner: implementation -->
- [ ] **Acceptance evidence — Unit 1c (completes the inventory pilot):** (a) `dotnet test Rag.sln --configuration Release` is green; (b) the engine runs `inventory` on a representative fixture tree and produces `manifest.json`, `candidates.ndjson`, and `summary.md`; (c) hashes reconcile against stored rows; (d) no document content is opened (verified by fixture handle interception or by absence of read calls); (e) protected paths unchanged (`src/Rag.AdminApp/`, `src/Rag.AdminApp.Host/`, AdminApp Docker/Compose/CI, `src/Rag.Companion/`); (f) no API/UI/Companion/AdminApp code added; (g) `Rag.sln` registration is additive-only. **Decision gate — Unit 2 does not start until (a)–(g) are recorded in `apply-progress.md`.** <!-- sdd-owner: implementation -->
- [ ] RED: add failing view-model tests in `tests/Rag.HistoricalLoader.UnitTests/Desktop/InventoryViewModelTests.cs`, `RunViewModelTests.cs`, `AuditViewModelTests.cs`, `ErrorViewModelTests.cs` asserting each view-model binds to engine events without opening source folders, SQLite, reusable secrets, or the remote API; that the inventory view renders persisted manifest totals; that the run view distinguishes `pending`, `pausing`, `paused`, `running`, `remote_pending`, `loaded`, `skipped_document_error`, `retry_exhausted_network`, `blocked_auth`, `blocked_operator_action`; that the audit view shows allowlisted fields only; that the error view shows error codes and classification without content/paths/secrets/tokens. <!-- sdd-owner: implementation -->
- [ ] RED: add failing UI automation tests proving the shell opens inventory, starts a run, pauses, resumes, and inspects states without terminal use. <!-- sdd-owner: implementation -->
- [ ] GREEN: create `src/Rag.HistoricalLoader.Desktop/Rag.HistoricalLoader.Desktop.csproj` (WPF, .NET 10, win-x64), `App.xaml`, `MainWindow.xaml`, view-models under `ViewModels/`, and a named-pipe client that consumes the engine's versioned local contract. Register in `Rag.sln`. <!-- sdd-owner: implementation -->
- [ ] TRIANGULATE: extend tests with view-model state propagation during a paused→kill→restart→resume sequence, with audit allowlist enforcement, and with the operator action log being recorded as `audit_event` rows. <!-- sdd-owner: implementation -->
- [ ] REFACTOR: collapse view-models into a single `ViewModels/` namespace; re-run `dotnet test Rag.sln --no-build` and confirm green. <!-- sdd-owner: implementation -->
- [ ] **Acceptance evidence — Unit 11:** (a) UI automation/view-model tests are green; (b) the shell never opens source folders/SQLite/secrets/API directly; (c) `apply-progress.md` records the operator-tested inventory/start/pause/resume/state/audit/error flow; (d) WPF remains the provisional choice and Electron is not selected. **Decision gate — Unit 12 does not start until the local vertical proof with at least 100 documents is recorded.** <!-- sdd-owner: implementation -->
- [ ] RED: add a Gate L end-to-end test in `tests/Rag.HistoricalLoader.IntegrationTests/GateL/LocalProofTests.cs` driving at least 100 selected proof documents through inventory → benchmark-aligned extraction → reservation/stream/commit → polling → legacy search with attributable provenance; pause reaches a durable checkpoint; kill/restart resumes without reloading confirmed documents; transient fault injection proves no fourth attempt; document faults do not block later documents; idempotent unknown-outcome reconciliation and hash-conflict tests pass; token expiry, insufficient scope, application revocation, and Cloudflare-local-exception labeling are verified; privacy sentinel scan passes; only loopback API access is used locally. <!-- sdd-owner: implementation -->
- [ ] GREEN: assemble `docs/historical-ingestion-rebaseline/gate-l-evidence.md` from `apply-progress.md` records — inventory accounting, benchmark report (Unit 3), Companion disposition (Unit 5), ≥100 documents into legacy with search/provenance, pause/kill/restart safety, retry/document fault tests, idempotent ambiguity recovery, auth scope/expiry/revocation evidence, privacy scan, loopback-only proof, real-time compatibility (existing tests green). <!-- sdd-owner: implementation -->
- [ ] TRIANGULATE: cross-check every Gate L requirement against the recorded evidence; record any missing item as an explicit blocker. <!-- sdd-owner: implementation -->
- [ ] REFACTOR: consolidate the Gate L evidence doc with the design's Gate L requirements table. <!-- sdd-owner: implementation -->
- [ ] **Acceptance evidence — Unit 12 / Gate L:** (a) Gate L evidence pack is complete; (b) `apply-progress.md` records the at-least-100-document proof, the search/provenance evidence, the pause/kill/restart evidence, and the privacy scan; (c) real-time compatibility tests remain green; (d) the legacy collection contains the proven documents with provenance; (e) no full-corpus run, no production deployment, no AdminApp integration. **Decision gate — Unit 13 (Gate A) does not start until every Gate L requirement is satisfied.** A Gate L failure blocks OCI/Dokploy validation. <!-- sdd-owner: implementation -->
- [ ] RED: add failing environment-contract tests in `tests/Rag.HistoricalLoader.IntegrationTests/GateA/Arm64ContractTests.cs` proving the same contract/fault suite produces evidence for the OCI/Dokploy ARM64 environment as for local, with native `linux/arm64` API/operator/runtime images and exact immutable image identity recorded. <!-- sdd-owner: implementation -->
- [ ] GREEN: assemble `docs/historical-ingestion-rebaseline/gate-a-evidence.md` — verified native ARM64 images, migration/rollback/restore rehearsal, readiness, internal-only PostgreSQL/llama access, Cloudflare service-token allow/deny and direct-origin denial, historical scope/revocation/idempotency/provenance contract tests, llama.cpp CPU embedding observations on the actual ARM64 host (resource headroom, real-time queue impact), comparison with local evidence and documented regressions/limitations, no dependency on a Windows Companion binary on the server. <!-- sdd-owner: implementation -->
- [ ] TRIANGULATE: cross-check every Gate A requirement; record any missing item as a blocker. <!-- sdd-owner: implementation -->
- [ ] REFACTOR: consolidate the Gate A evidence doc with the design's Gate A requirements table. <!-- sdd-owner: implementation -->
- [ ] **Acceptance evidence — Unit 13 / Gate A:** (a) Gate A evidence pack is complete; (b) `apply-progress.md` records ARM64 image identity, Cloudflare allow/deny, direct-origin denial, llama.cpp CPU observations, and real-time impact; (c) infrastructure changes (if any) are isolated from AdminApp WIP and separately reviewed. **Decision gate — Unit 14 (Gate C) does not start until every Gate A requirement is satisfied.** A Gate A failure blocks Coolify validation. <!-- sdd-owner: implementation -->
- [ ] RED: add failing environment-contract tests in `tests/Rag.HistoricalLoader.IntegrationTests/GateC/CoolifyContractTests.cs` proving the same contract/fault suite produces evidence for the Coolify target environment with signed/verified immutable multi-platform image references and target architecture. <!-- sdd-owner: implementation -->
- [ ] GREEN: assemble `docs/historical-ingestion-rebaseline/gate-c-evidence.md` — signed/verified immutable multi-platform image references, secret/config inventory (no secret in Compose, manifests, logs, or reports), Cloudflare-only ingress and no direct internal-service exposure, additive migration and backup/restore evidence, bounded proof-run checkpoint/auth/provenance/search evidence matching prior gates, observability and capacity comparison with staging, explicit rollback procedure, and confirmation that no full-corpus run occurred. <!-- sdd-owner: implementation -->
- [ ] GREEN: assemble `docs/historical-ingestion-rebaseline/full-corpus-decision-packet.md` — the **decision document** that may authorize or reject scaling, citing inventory, weighted capacity range, operator window, reliability evidence, infrastructure headroom, Companion disposition, rollback/backup readiness, and unresolved risks. **This task does not authorize scaling; it produces the packet for a separate future decision.** <!-- sdd-owner: implementation -->
- [ ] TRIANGULATE: cross-check every Gate C requirement; record any missing item as a blocker. <!-- sdd-owner: implementation -->
- [ ] REFACTOR: consolidate the Gate C evidence doc and decision packet with the design's Gate C and Open-evidence-decisions tables. <!-- sdd-owner: implementation -->
- [ ] **Acceptance evidence — Unit 14 / Gate C:** (a) Gate C evidence pack is complete; (b) full-corpus decision packet is documented and **records the explicit decision that no full-corpus run occurs in this change**; (c) `apply-progress.md` records image identity, secret hygiene, ingress evidence, migration/backup, and rollback; (d) no production deployment, no full-corpus run, no AdminApp integration in this change. **Decision gate — Full-corpus and production deployment require a separate decision record, citing the Gate L/A/C evidence, and are not authorized by this change.** <!-- sdd-owner: implementation -->

## Blockers (revision 1 → revision 2)

All four Unit 10 criticals are closed; the only surviving blocker is the change-level task backlog, which is outside Unit 10.

- **CLOSED** — durable poll attempt accounting does not occur before dispatch. Fixed; `PollAttempt_*` RED → GREEN.
- **CLOSED** — staged and committed watermark accounting is not durable across restart. Fixed and rehydrated; `Restart_Rehydrates*` and `CrashDuringPoll_*` green.
- **CLOSED** — strict-TDD evidence is incomplete. Closed by the controlled compile-RED reconstruction plus explicit maintainer acceptance.
- **CLOSED** — real selected-extractor plus real API-client loopback acceptance proof is missing. Closed by the unprimed real-loopback regression.
- **OPEN (outside Unit 10 scope, unaffected by this verdict)** — 30 task rows remain unchecked: 24 `sdd-owner: implementation` (the configuration-consolidation REFACTOR and Units 11–14) and 6 `sdd-owner: parent` delivery rows. The change is not archive-ready; Unit 10 is PASS.

## Unit 10 verification dispatch — BLOCKED

> **Revision-2 note:** this administrative dispatch block was a prior verification attempt that stopped before runtime verification. It is superseded by the final independent results and the PASS verdict in "Final closure — revision 2"; it is retained as the historical record.

### Structured status and action context

Command executed: `gentle-ai sdd-status historical-ingestion-rebaseline --cwd /opt/wf/rag-api --json --instructions` (success). Native v2 selects the requested change, reports `nextRecommended: apply`, `dependencies.verify: blocked`, `dependencies.archive: blocked`, 83/113 completed tasks, and 30 pending tasks. `blockedReasons` is empty; the verification dependency is nevertheless blocked. Global installed status-contract guidance requires routing only the native selected action. This executor therefore stopped before runtime verification; it does not override the dispatcher for a partial Unit 10 run.

`actionContext.mode: repo-local`; workspace and allowed root: `/opt/wf/rag-api`. No root ambiguity. Native instructions additionally report an existing active runtime attempt; the parent must retain ownership of its acquisition/settlement. No attempt was acquired or settled here.

### Results and scope

Rehydration: NOT VERIFIED. Loopback: NOT VERIFIED. Strict TDD: NOT VERIFIED (evidence tables present in Unit 10 apply-progress, but test-file cross-reference, current GREEN, safety-net completeness and assertion quality audit were not performed after the dispatch block). Spec coverage and design coherence: not assessed. No implementation defect or runtime PASS/FAIL is inferred from this administrative block.

Focused tests, HistoricalLoader unit/integration suites, Release build and full solution tests: NOT RUN. Coverage, test-layer distribution and quality metrics: not assessed. Historical apply-progress results are not fresh verification evidence.

Review workload: Unit 10 apply-progress records feature-branch-chain and a size:exception; consent and actual changed-line boundaries were not independently verified. No PR or source changes made. Only this mandatory verification-report addendum was persisted; prior reports remain intact.

### Remaining scope / archive blockers

The request is a partial Unit 10 slice. The following exact unchecked lines remain scope, not newly discovered Unit 10 defects. Archive is not ready; this is not a clean PASS.

```text
- [ ] REFACTOR: collapse the bindable options and migration scaffolding into a single `Configuration/HistoricalLoaderOptions.cs` namespace; re-run `dotnet test Rag.sln --no-build` and confirm green. <!-- sdd-owner: implementation -->
- [ ] **Acceptance evidence — Unit 1c (completes the inventory pilot):** (a) `dotnet test Rag.sln --configuration Release` is green; (b) the engine runs `inventory` on a representative fixture tree and produces `manifest.json`, `candidates.ndjson`, and `summary.md`; (c) hashes reconcile against stored rows; (d) no document content is opened (verified by fixture handle interception or by absence of read calls); (e) protected paths unchanged (`src/Rag.AdminApp/`, `src/Rag.AdminApp.Host/`, AdminApp Docker/Compose/CI, `src/Rag.Companion/`); (f) no API/UI/Companion/AdminApp code added; (g) `Rag.sln` registration is additive-only. **Decision gate — Unit 2 does not start until (a)–(g) are recorded in `apply-progress.md`.** <!-- sdd-owner: implementation -->
- [ ] RED: add failing view-model tests in `tests/Rag.HistoricalLoader.UnitTests/Desktop/InventoryViewModelTests.cs`, `RunViewModelTests.cs`, `AuditViewModelTests.cs`, `ErrorViewModelTests.cs` asserting each view-model binds to engine events without opening source folders, SQLite, reusable secrets, or the remote API; that the inventory view renders persisted manifest totals; that the run view distinguishes `pending`, `pausing`, `paused`, `running`, `remote_pending`, `loaded`, `skipped_document_error`, `retry_exhausted_network`, `blocked_auth`, `blocked_operator_action`; that the audit view shows allowlisted fields only; that the error view shows error codes and classification without content/paths/secrets/tokens. <!-- sdd-owner: implementation -->
- [ ] RED: add failing UI automation tests proving the shell opens inventory, starts a run, pauses, resumes, and inspects states without terminal use. <!-- sdd-owner: implementation -->
- [ ] GREEN: create `src/Rag.HistoricalLoader.Desktop/Rag.HistoricalLoader.Desktop.csproj` (WPF, .NET 10, win-x64), `App.xaml`, `MainWindow.xaml`, view-models under `ViewModels/`, and a named-pipe client that consumes the engine's versioned local contract. Register in `Rag.sln`. <!-- sdd-owner: implementation -->
- [ ] TRIANGULATE: extend tests with view-model state propagation during a paused→kill→restart→resume sequence, with audit allowlist enforcement, and with the operator action log being recorded as `audit_event` rows. <!-- sdd-owner: implementation -->
- [ ] REFACTOR: collapse view-models into a single `ViewModels/` namespace; re-run `dotnet test Rag.sln --no-build` and confirm green. <!-- sdd-owner: implementation -->
- [ ] **Acceptance evidence — Unit 11:** (a) UI automation/view-model tests are green; (b) the shell never opens source folders/SQLite/secrets/API directly; (c) `apply-progress.md` records the operator-tested inventory/start/pause/resume/state/audit/error flow; (d) WPF remains the provisional choice and Electron is not selected. **Decision gate — Unit 12 does not start until the local vertical proof with at least 100 documents is recorded.** <!-- sdd-owner: implementation -->
- [ ] RED: add a Gate L end-to-end test in `tests/Rag.HistoricalLoader.IntegrationTests/GateL/LocalProofTests.cs` driving at least 100 selected proof documents through inventory → benchmark-aligned extraction → reservation/stream/commit → polling → legacy search with attributable provenance; pause reaches a durable checkpoint; kill/restart resumes without reloading confirmed documents; transient fault injection proves no fourth attempt; document faults do not block later documents; idempotent unknown-outcome reconciliation and hash-conflict tests pass; token expiry, insufficient scope, application revocation, and Cloudflare-local-exception labeling are verified; privacy sentinel scan passes; only loopback API access is used locally. <!-- sdd-owner: implementation -->
- [ ] GREEN: assemble `docs/historical-ingestion-rebaseline/gate-l-evidence.md` from `apply-progress.md` records — inventory accounting, benchmark report (Unit 3), Companion disposition (Unit 5), ≥100 documents into legacy with search/provenance, pause/kill/restart safety, retry/document fault tests, idempotent ambiguity recovery, auth scope/expiry/revocation evidence, privacy scan, loopback-only proof, real-time compatibility (existing tests green). <!-- sdd-owner: implementation -->
- [ ] TRIANGULATE: cross-check every Gate L requirement against the recorded evidence; record any missing item as an explicit blocker. <!-- sdd-owner: implementation -->
- [ ] REFACTOR: consolidate the Gate L evidence doc with the design's Gate L requirements table. <!-- sdd-owner: implementation -->
- [ ] **Acceptance evidence — Unit 12 / Gate L:** (a) Gate L evidence pack is complete; (b) `apply-progress.md` records the at-least-100-document proof, the search/provenance evidence, the pause/kill/restart evidence, and the privacy scan; (c) real-time compatibility tests remain green; (d) the legacy collection contains the proven documents with provenance; (e) no full-corpus run, no production deployment, no AdminApp integration. **Decision gate — Unit 13 (Gate A) does not start until every Gate L requirement is satisfied.** A Gate L failure blocks OCI/Dokploy validation. <!-- sdd-owner: implementation -->
- [ ] RED: add failing environment-contract tests in `tests/Rag.HistoricalLoader.IntegrationTests/GateA/Arm64ContractTests.cs` proving the same contract/fault suite produces evidence for the OCI/Dokploy ARM64 environment as for local, with native `linux/arm64` API/operator/runtime images and exact immutable image identity recorded. <!-- sdd-owner: implementation -->
- [ ] GREEN: assemble `docs/historical-ingestion-rebaseline/gate-a-evidence.md` — verified native ARM64 images, migration/rollback/restore rehearsal, readiness, internal-only PostgreSQL/llama access, Cloudflare service-token allow/deny and direct-origin denial, historical scope/revocation/idempotency/provenance contract tests, llama.cpp CPU embedding observations on the actual ARM64 host (resource headroom, real-time queue impact), comparison with local evidence and documented regressions/limitations, no dependency on a Windows Companion binary on the server. <!-- sdd-owner: implementation -->
- [ ] TRIANGULATE: cross-check every Gate A requirement; record any missing item as a blocker. <!-- sdd-owner: implementation -->
- [ ] REFACTOR: consolidate the Gate A evidence doc with the design's Gate A requirements table. <!-- sdd-owner: implementation -->
- [ ] **Acceptance evidence — Unit 13 / Gate A:** (a) Gate A evidence pack is complete; (b) `apply-progress.md` records ARM64 image identity, Cloudflare allow/deny, direct-origin denial, llama.cpp CPU observations, and real-time impact; (c) infrastructure changes (if any) are isolated from AdminApp WIP and separately reviewed. **Decision gate — Unit 14 (Gate C) does not start until every Gate A requirement is satisfied.** A Gate A failure blocks Coolify validation. <!-- sdd-owner: implementation -->
- [ ] RED: add failing environment-contract tests in `tests/Rag.HistoricalLoader.IntegrationTests/GateC/CoolifyContractTests.cs` proving the same contract/fault suite produces evidence for the Coolify target environment with signed/verified immutable multi-platform image references and target architecture. <!-- sdd-owner: implementation -->
- [ ] GREEN: assemble `docs/historical-ingestion-rebaseline/gate-c-evidence.md` — signed/verified immutable multi-platform image references, secret/config inventory (no secret in Compose, manifests, logs, or reports), Cloudflare-only ingress and no direct internal-service exposure, additive migration and backup/restore evidence, bounded proof-run checkpoint/auth/provenance/search evidence matching prior gates, observability and capacity comparison with staging, explicit rollback procedure, and confirmation that no full-corpus run occurred. <!-- sdd-owner: implementation -->
- [ ] GREEN: assemble `docs/historical-ingestion-rebaseline/full-corpus-decision-packet.md` — the **decision document** that may authorize or reject scaling, citing inventory, weighted capacity range, operator window, reliability evidence, infrastructure headroom, Companion disposition, rollback/backup readiness, and unresolved risks. **This task does not authorize scaling; it produces the packet for a separate future decision.** <!-- sdd-owner: implementation -->
- [ ] TRIANGULATE: cross-check every Gate C requirement; record any missing item as a blocker. <!-- sdd-owner: implementation -->
- [ ] REFACTOR: consolidate the Gate C evidence doc and decision packet with the design's Gate C and Open-evidence-decisions tables. <!-- sdd-owner: implementation -->
- [ ] **Acceptance evidence — Unit 14 / Gate C:** (a) Gate C evidence pack is complete; (b) full-corpus decision packet is documented and **records the explicit decision that no full-corpus run occurs in this change**; (c) `apply-progress.md` records image identity, secret hygiene, ingress evidence, migration/backup, and rollback; (d) no production deployment, no full-corpus run, no AdminApp integration in this change. **Decision gate — Full-corpus and production deployment require a separate decision record, citing the Gate L/A/C evidence, and are not authorized by this change.** <!-- sdd-owner: implementation -->
- [ ] Validate pre-commit receipt before any commit is staged for this change. Existing real-time route, AdminApp WIP, AdminApp Docker/Compose/CI, and `src/Rag.Companion/` must remain unchanged in the staged content unless a Companion-disposition record (Unit 5) explicitly authorizes a bounded follow-up. Unit 1a may additively modify `Directory.Packages.props` (one new `PackageVersion` for `Microsoft.Data.Sqlite`) and `Rag.sln` (additive Core + unit-test project registration only); nothing else. <!-- sdd-owner: parent -->
- [ ] Validate pre-push receipt before any push attempt (no tag, no manual publication, no RDD enablement). <!-- sdd-owner: parent -->
- [ ] Validate pre-PR receipt before opening the chosen chain slice (each slice targets its predecessor's branch in `feature-branch-chain`, or merges to `main` in `stacked-to-main`; only the final slice merges to `main` in the chain). The first slice is Unit 1a. <!-- sdd-owner: parent -->
- [ ] Start or reuse bounded review once each chain slice is fully implemented and locally verified (post-apply). <!-- sdd-owner: parent -->
- [ ] Enforce evidence gates: Unit 1b blocked until Unit 1a green; Unit 1c blocked until Unit 1a **and** Unit 1b green; Unit 2 blocked until Unit 1c green; Unit 3 blocked until Unit 2 + sample parameters recorded; Unit 5 blocked until Units 3 + 4 evidence recorded; Unit 6 blocked until Unit 5 disposition recorded; Unit 7 blocked until Unit 6 green; Unit 8 blocked until Unit 7 + real-time tests green; Unit 9 blocked until Unit 8 streaming/idempotency green; Unit 10 blocked until Unit 9 privacy/auth-block green; Unit 11 blocked until Unit 10 bounded pipeline green; Unit 12 (Gate L) blocked until Unit 11 view-model/UI green; Unit 13 (Gate A) blocked until Gate L evidence complete; Unit 14 (Gate C) blocked until Gate A evidence complete; full-corpus run is **never** authorized by this change. <!-- sdd-owner: parent -->
- [ ] Archive the change only after Gate C evidence is complete and the full-corpus decision packet explicitly records that no full-corpus run occurred in this change. <!-- sdd-owner: parent -->
```

### Exact blocker and next action

Native `dependencies.verify: blocked` and `nextRecommended: apply` do not authorize this verify executor. Parent must reconcile partial-slice verification with native dispatch/runtime authority before relaunching; do not mark unrelated tasks complete to bypass the guard. Native status also diagnoses the prior report as lacking a valid `gentle-ai.verify-result/v1` envelope; this blocked addendum does not claim to repair or validate terminal evidence.

Skill resolution: none. No skill paths were injected; the conventional executor skill path was absent. Global installed status and strict-TDD verification support guidance were read. Parent should inject the indexed executor SKILL.md on relaunch.

---

## Addendum — Unit 10 per-file strict-TDD provenance matrix (traceability-only, native ordinal 40)

Read-only traceability work responding to this report's CRITICAL finding #3. No source, test, task, staging, commit, reset, stash, or review state was touched, and **no test was executed** by this attempt. The full per-file matrix (the six `Rag.HistoricalLoader.Engine` production files and six `Rag.HistoricalLoader.UnitTests` test files, with recorded RED/GREEN, recorded verification commands, and limitations) is in `apply-progress.md` under "Unit 10 per-file strict-TDD provenance matrix".

### Verdict on CRITICAL finding #3 (strict-TDD evidence incomplete)

**Revision-2 update: CLOSED.** The maintainer accepted the controlled compile-RED reconstruction (authentic compile-fail RED on the six original Unit 10 test files, byte-identical restoration, green re-run) as sufficient provenance for the original Unit 10 surface. The revision-1 verdict below is retained as the historical record.

**Revision-1 verdict (superseded): Not closed — remained CRITICAL.** Per-assertion RED/GREEN tied to each of the six test files *and* current execution cannot be reconstructed from artifacts:

| Question | Honest answer |
| --- | --- |
| Any contemporaneous per-test RED for the original six files? | No. The only original RED was one namespace-level compile failure across all six files simultaneously (tests authored against absent `Engine.Pipeline`/`Engine.Extraction` namespaces; the writer timed out and no output was captured). |
| Any later runtime RED? | Yes, but only for tests the corrective units added/edited (`HistoricalPipelineTests` restart/crash/loopback; `WatermarkSchedulerTests` at compile level). That RED proves the later production text, not the original. |
| Current execution of the six files? | Not performed by this documentation-only attempt. The recorded GREEN figures (92/92, 184/184, 580/580; later 51 focused/200 unit/22 integration/599 solution, and 18/12) are historical executions. |

Closing this finding honestly needs either re-established RED for the original behaviour (controlled withdrawal + byte-identical restoration, the method used for Unit 9) or an explicit maintainer decision accepting reconstructed GREEN-only provenance. This addendum does **not** change the overall **FAIL** status; the other CRITICAL findings and the 24 unchecked implementation tasks remain.

> **Revision-2 note:** the controlled withdrawal + byte-identical restoration described here as the honest closure path was subsequently performed and accepted by the maintainer, and the other Unit 10 criticals were closed by remediation evidence; the Unit 10 verdict is now **PASS**. The 30 unchecked task rows remain, but they are outside Unit 10 scope.

### Recorded verification commands for the six files (historical, not re-run here)

| Command (as recorded) | Recorded result | Recorded by |
| --- | --- | --- |
| `dotnet test tests/Rag.HistoricalLoader.UnitTests/… --configuration Release --filter "FullyQualifiedName~Pipeline\|FullyQualifiedName~Extraction"` | 92 passed / 0 failed / 0 skipped | Native attempt ordinal 28 |
| `dotnet test tests/Rag.HistoricalLoader.UnitTests/… --configuration Release --no-build` | 184 / 0 / 0 | Ordinal 28 |
| `dotnet test tests/Rag.HistoricalLoader.IntegrationTests/… --configuration Release --no-build` | 19 / 0 / 0 | Ordinal 28 |
| `dotnet build Rag.sln --configuration Release --no-restore` | 0 errors | Ordinal 28 |
| `dotnet test Rag.sln --configuration Release --no-build` | 580 / 0 / 0 | Ordinal 28 |
| 51 focused + 200 unit + 22 integration + solution 599, build 0 errors (exact filters not captured) | all green | Ordinal 38 |
| 18 focused + 12 SQLite lifecycle integration, build 0 errors (exact filters not captured) | all green | Ordinal 39 |
