# Explore — Unit 11 (Replaceable WPF operator shell): first slice map

Change: `historical-ingestion-rebaseline`
Scope of this document: read-only exploration. No edits, tests, staging, commits, or review were performed.
Artifact store: `openspec` (this file). No Engram write (store is not `engram`/`both`).

## 1. Objective and binding constraints

Unit 11 delivers a **replaceable Windows operator shell** whose only data path is the engine's versioned
local contract:

- Inventory view, run view (start/pause/resume/states), audit view, error view, operator action log.
- View-model tests must be green and must prove the shell never opens source folders, SQLite, reusable
  secrets, or the remote API directly.
- Acceptance evidence (c) requires an **operator-tested** inventory/start/pause/resume/state/audit/error
  flow, so the shell must eventually run end-to-end against the engine.
- WPF stays provisional; Electron is not selected.

Source anchors: `tasks.md` L215–224; `design.md` L49–77, L353–355, L413–414, L439, L456–457, L489;
`specs/historical-ingestion/spec.md` "Windows Operator Workflow", "Durable Per-Document Lifecycle",
"Auditable, Privacy-Preserving Operations".

## 2. Observed current state (facts on disk)

| Surface | Observed state | Evidence |
| --- | --- | --- |
| Desktop project | Does not exist | `src/` list; `Rag.sln` project list |
| `IHistoricalLoaderEngine` | Exposes only `RunInventoryAsync`, `RunSelectSampleAsync`, `RunBenchmarkExtractionAsync` + `InventoryProgress` | `src/Rag.HistoricalLoader.Engine/EngineContract.cs` |
| Named-pipe host | **Stub only**; `IsListening => false`; comment says Unit 1c "intentionally never opens it" | `src/Rag.HistoricalLoader.Engine/NamedPipeHost.cs` |
| Engine CLI | `inventory`, `select-sample`, `benchmark extraction` only — **no `run`/control subcommand** | `src/Rag.HistoricalLoader.Engine/Program.cs` |
| Pipeline (exists, unhosted) | `HistoricalPipeline` with `RunAsync`, `RequestPauseAsync`, `ResumeAsync`, `Watermarks`; `PauseController`, `ResumeController`, `WatermarkScheduler` | `src/Rag.HistoricalLoader.Engine/Pipeline/*` |
| Lifecycle queries | Store exposes `GetAuditEventsAsync`, `AuditEvent`, `RunDocument`, `Run`, `DocumentState` (15 values), `RunObservedState` (6 values) | `src/Rag.HistoricalLoader.Core/Lifecycle/*` |
| Test project | `Rag.HistoricalLoader.UnitTests` targets `net10.0`, references Core + Engine, already has `Desktop/` absent | `tests/Rag.HistoricalLoader.UnitTests/*.csproj` |
| CI | `ci-pr`, `ci-develop`, `ci-release` all run `dotnet restore/build/test Rag.sln` on **ubuntu-latest** | `.github/workflows/*.yml` |
| Windows-only precedent | `WindowsCredentialManager` tested on Linux via `IWindowsCredentialStore` seam with a fake | `src/Rag.HistoricalLoader.Engine/Security/`, `tests/.../Security/WindowsCredentialManagerTests.cs` |

## 3. Critical finding — build/CI constraint that shapes the slice

`Rag.sln` is restored/built/tested on **Linux**. Two consequences:

1. A `net10.0-windows` + `UseWPF` project added to `Rag.sln` would break `dotnet build Rag.sln` on Linux
   (the `Microsoft.WindowsDesktop.App` reference pack is not restorable there). WPF cannot be compiled
   or tested in the current CI topology.
2. Therefore the **view-model and transport layer Unit 11 must test cannot live inside the WPF project**.
   The only way the mandated `dotnet test Rag.sln --no-build` evidence can include Unit 11 is if the
   tested code is a cross-platform `net10.0` surface with no WPF dependency.

The `WindowsCredentialManager` precedent confirms the project pattern: platform-specific work is reached
through a seam and verified with a fake, keeping the default test run cross-platform.

## 4. Dependency gap (engine side) — must be named now

The shell needs engine **run control** (start/pause/resume), a **snapshot** (run state + per-document
counts + audit rows), and an **event stream**. None of these is currently exposed:

- `IHistoricalLoaderEngine` has no run-control, snapshot, or event-stream members.
- `NamedPipeHost` never opens a pipe.
- No engine subcommand drives `HistoricalPipeline`.

So Unit 11 has a **forward dependency on an engine-side host surface** that Unit 10 did not deliver.
This does not block view-model RED work, but it does block Unit 11 acceptance evidence (c) and the
end-to-end path. It should be surfaced to the parent as a scope decision, not silently absorbed.

## 5. Smallest dependency-safe first implementation slice (recommended)

**Slice A — Desktop view-model core, cross-platform, no solution/CI/WPF risk.**

Deliverable: a new `net10.0` (no WPF) library `Rag.HistoricalLoader.Desktop` containing the
shell-facing contract seam and the first two view-models, plus their tests in the existing test project.

Contents (Slice A only):

- `src/Rag.HistoricalLoader.Desktop/Rag.HistoricalLoader.Desktop.csproj` — `net10.0`, nullable,
  implicit usings, no packages, **no ProjectReference to Engine or Core** (see §7).
- `Contracts/IEngineClient.cs` — the engine seam: `Task<ShellSnapshot> GetSnapshotAsync(...)`,
  `Task StartAsync/RequestPauseAsync/ResumeAsync(...)`, `IAsyncEnumerable<ShellEvent> SubscribeAsync(...)`.
- `Contracts/Model.cs` — Desktop-local safe DTOs: `ShellRunStatus` (10 members exactly as listed in
  `tasks.md`), `ShellDocument` (candidate id, state, attempt, classification; **no path, no content**),
  `ShellAuditEntry` (event id, UTC timestamp, run/candidate id, action, state transition, outcome code,
  attempt, measurements), `ShellInventoryTotals`, `ShellEvent`.
- `ViewModels/InventoryViewModel.cs` — renders persisted manifest totals from a snapshot; exposes
  explicit empty/unavailable states; performs no filesystem or DB access.
- `ViewModels/ErrorViewModel.cs` — shows error code + classification only; no content/paths/secrets/tokens.
- `tests/Rag.HistoricalLoader.UnitTests/Desktop/InventoryViewModelTests.cs`
- `tests/Rag.HistoricalLoader.UnitTests/Desktop/ErrorViewModelTests.cs`
- `tests/Rag.HistoricalLoader.UnitTests/Desktop/EngineClientSeamTests.cs` — reflection guard proving the
  Desktop assembly has no type/namespace reference to filesystem, SQLite, secret, HTTP/API, or Engine/Core.

Why this is the right first slice:

- It is the slice that retires the two real unknowns (cross-platform testability of the shell layer, and
  the shape of the engine seam) before any Windows-only or solution-wide change is attempted.
- Zero protected paths, zero CI, zero `Rag.sln`, zero Engine, zero WPF in this slice.
- It keeps strict-TDD RED→GREEN→TRIANGULATE→REFACTOR per view-model, satisfying the work-unit-commits
  rule without separating tests from behaviour.

## 6. Reviewable slice sequence and forecast

| Slice | Contents | Forecast authored lines |
| --- | --- | --- |
| **A** | Desktop csproj + `Contracts/` + `InventoryViewModel` + `ErrorViewModel` + 3 test files + `Rag.sln` registration of the Desktop lib only | ~300–380 |
| **B** | `RunViewModel` (state projection incl. pausing/paused distinction, event-driven updates) + `AuditViewModel` (allowlist enforcement) + their tests + TRIANGULATE pause→kill→restart→resume propagation | ~260–340 |
| **C** | Desktop `NamedPipeEngineClient` transport (framing + round-trip contract test against `IEngineClient`) | ~150–250 |
| **D** (Windows-only, separate decision) | `App.xaml`, `MainWindow.xaml`, WPF `Desktop.App` csproj, WPF UI-automation tests, and the `Rag.sln`/Windows-job strategy for the WPF exe | ~200–350 + build-config work |

Total Unit 11 ≈ 910–1320 lines, consistent with the recorded `~400–600` forecast but larger once the
transport and Windows-only split are made explicit. A, B, C each stay under the 400-line review budget.
The user's authorized Unit 11 `size:exception` remains intact for the unit as a whole; the split is for
review granularity, not code compression.

## 7. Exact allowed edit surfaces (Slice A)

Additive only:

- `src/Rag.HistoricalLoader.Desktop/**` (new project and files).
- `tests/Rag.HistoricalLoader.UnitTests/Desktop/**` (new test files) and an **additive**
  `<ProjectReference Include="../../src/Rag.HistoricalLoader.Desktop/...">` line in
  `tests/Rag.HistoricalLoader.UnitTests/Rag.HistoricalLoader.UnitTests.csproj`.
- `Rag.sln` — additive project registration + `ProjectConfigurationPlatforms` lines for the Desktop lib
  (mechanical, ~30 lines; the Unit 1c/1a precedent for solution registration).

Explicitly **not** touched by Slice A: `src/Rag.HistoricalLoader.Engine/**`,
`src/Rag.HistoricalLoader.Core/**`, `src/Rag.Api/**`, `src/Rag.Application/**`, `src/Rag.Domain/**`,
`src/Rag.Infrastructure/**`, `src/Rag.Companion/**` (read-only reference until Companion disposition),
`src/Rag.AdminApp/**`, `src/Rag.AdminApp.Host/**`, AdminApp Docker/Compose/CI, `Directory.Packages.props`,
any `.github/workflows/*.yml`, and the real-time `ingestions:txt` route.

## 8. Strict-TDD RED targets for Slice A

1. `InventoryViewModelTests`
   - RED: totals from a fake snapshot equal the persisted manifest totals (count, bytes, per-format);
     an incomplete manifest is surfaced as incomplete, never as complete.
   - RED: no filesystem/DB access — asserted via the reflection guard in `EngineClientSeamTests`.
   - TRIANGULATE: empty manifest, partially-populated manifest, and a manifest whose export is
     incomplete each render a distinct, explicit state.
2. `ErrorViewModelTests`
   - RED: renders an error code and a FailureClass-shaped classification; the rendered surface contains
     no absolute path, no content, no bearer token, no client/Cloudflare secret (sentinel strings).
   - TRIANGULATE: network-class vs document-class vs auth-class errors render distinguishable labels.
3. `EngineClientSeamTests`
   - RED: the `Rag.HistoricalLoader.Desktop` assembly references no filesystem, SQLite
     (`Microsoft.Data.Sqlite`), secret, or HTTP/API type, and no `Rag.HistoricalLoader.Core` /
     `Rag.HistoricalLoader.Engine` assembly; `IEngineClient` is the only engine-facing abstraction.

Deferred RED (later slices, listed so they are not lost): the 10-state run-view distinction and
pausing/paused separation (B), audit allowlist enforcement and `audit_event` for operator actions (B),
paused→kill→restart→resume propagation (B), named-pipe round-trip (C), and Windows UI automation (D).

## 9. WPF project integration constraints (for Slice D — not Slice A)

- `App.xaml`/`MainWindow.xaml` require `net10.0-windows` + `<UseWPF>true</UseWPF>` and a win-x64 runtime;
  this cannot live in the Linux CI solution build.
- Options to resolve the `Rag.sln`-registration requirement without breaking Linux CI, in preference order:
  1. Register the view-model library (`Rag.HistoricalLoader.Desktop`, `net10.0`) in `Rag.sln` and keep the
     WPF exe project in a Windows-only file/`slnf` (or a solution configuration guarded by a `Condition`),
     documented as a deviation from the literal task wording.
  2. Conditional `TargetFramework` + `UseWPF` guarded on `OS == Windows_NT`, accepting that the Linux
     build then silently drops the views.
- Either option needs empirical verification on this machine before commit; the `dotnet build` behaviour
  cannot be confirmed by inspection alone. This is the main unresolved build-system risk of Unit 11.

## 10. Risks

1. **CI/Linux vs WPF (High).** Any `net10.0-windows` WPF project in `Rag.sln` breaks every CI job. Mitigated
   by the cross-platform slice plan and by deferring the WPF exe to a Windows-only decision.
2. **Engine host gap (High).** No run-control/snapshot/event surface on the engine; the shell cannot bind
   to real behavior. Blocks Unit 11 acceptance evidence (c) and the end-to-end path; needs a parent scope
   decision (extend Unit 11 or open a bounded engine-host follow-up).
3. **UI-automation infeasibility in current CI (High).** "UI automation tests" in `tasks.md` L220 cannot
   run in the Linux job. If they are genuinely required, a Windows CI job is needed, which contradicts the
   "no CI change beyond additive tests invoked by `dotnet test Rag.sln`" non-negotiable. Recommend
   treating the WPF UI automation as Gate L (Unit 12) evidence collected on the operator machine.
4. **Layering leak (Medium).** Referencing Engine/Core from the Desktop project would let the shell see
   source/SQLite/API types. Mitigated by Desktop-local DTOs plus the reflection guard test.
5. **`Rag.sln` churn (Low–Medium).** Mechanical registration of the Desktop lib touches a shared file;
   must remain additive and must not alter existing project entries or configurations.
6. **`audit_event` for operator actions (Medium).** The Desktop has no DB access, so the operator action
   log can only become `audit_event` rows if the engine writes them on command. This is part of risk 2.
7. **Evidence honesty (Low).** Acceptance (c) requires an operator-tested flow; slices A–C cannot satisfy
   it alone, and no completion may be claimed from view-model tests only.

## 11. Open questions for the parent

1. Does the authorized Unit 11 `size:exception` cover the engine-side run-control/snapshot/event host
   surface needed for acceptance evidence (c), or is that a separate bounded unit?
2. Is the literal requirement "register in `Rag.sln`" satisfied by registering the cross-platform
   view-model library in `Rag.sln` while the WPF exe project stays Windows-only?
3. Where is the Windows UI-automation evidence to be collected — Unit 11 or Unit 12/Gate L?

## 12. Next recommended action

Proceed to `sdd-propose`/`sdd-tasks` refinement scoped to **Slice A only** (Desktop view-model core +
inventory/error view-models + seam guard), then apply under strict TDD. Do not open the WPF exe project,
the engine host surface, or any solution/CI change until Slice A is green and the questions above are
answered.
