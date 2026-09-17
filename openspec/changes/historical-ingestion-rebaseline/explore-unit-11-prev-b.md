# Explore — Unit 11.prev-b (Loader store: command receipts + coherent projections)

Change: `historical-ingestion-rebaseline`
Scope of this document: read-only exploration/map. No source, test, task, staging, commit, reset, stash, or
review action was performed. The only write is this artifact.
Artifact store: `openspec` (this file). No Engram write (store is not `engram`/`both`).

Skill resolution: `paths-injected` — `/home/jesusjbm/.pi/agent/npm/node_modules/gentle-pi/skills/gentle-ai/SKILL.md`
read before repository work. CodeGraph was unavailable to this executor (no MCP tool and no shell, so neither
`codegraph_explore` nor the read-only CLI could be invoked) and no `.codegraph/` exists at
`/opt/wf/rag-api`; all evidence below is from direct file reads, with no build or runtime claim.

## 1. Objective (as recorded, binding)

`tasks.md` → `#### 11.prev-b — Loader store: command receipts + coherent projections` and
`design.md` → "Unit 11.prev — Headless local control prerequisite" are the binding sources. The slice delivers
**durable loader-store state** for the v1 control protocol: an additive local-only versioned migration,
atomic command intent/receipt/operator-audit commits through the single writer, and bounded coherent
projection/page queries. It is **not** a codec, dispatcher, service, host, or transport slice.

Consumed acceptance: `11.prev-a` limited acceptance (a)–(d) is recorded in `apply-progress.md` (contract slice
only). Blocking conditions before any 11.prev-b work:

1. `tasks.md` → *"Delivery-decision record — Unit 11.prev (NOT YET AUTHORIZED; parent-gated)"* is **unchecked**.
2. `design.md` L175: the amendment "does not authorize implementation or a size exception" for 11.prev.
3. `11.prev-a` itself measured 560/462/415 lines and is **undelivered** (delivery blocked by the same row).

## 2. Observed current state (facts on disk)

| Surface | Observed state | Evidence |
| --- | --- | --- |
| Schema version | `SqliteStore.CurrentSchemaVersion = 3`; `Migrations = [1,2,3]`; v3 adds `run`, `run_document` + `idx_run_document_run_state` | `src/Rag.HistoricalLoader.Core/Persistence/SqliteStore.cs` L26, L92–128, L130–135 |
| Migration mechanics | `MigrateAsync` backs up with the **SQLite backup API** (`BackupDatabase`) to `{db}.pre-migration-v{current}.bak` once per run when `!isNewDatabase`, applies pending statements in order, then `PRAGMA user_version = n`; `SeedInstallationAsync` writes the installation row with `CurrentSchemaVersion` **only at creation** | `SqliteStore.cs` L319–345, L386–392, L396–407 |
| Single writer | `SemaphoreSlim _writer`; `ScalarAsync`/`NonQueryAsync`/`RunExclusiveAsync`/`RunInTransactionAsync` are all `internal`, same-assembly only. **No `InternalsVisibleTo` anywhere in Core.** | `SqliteStore.cs` L136, L341–380, L410–455 |
| Integrity gate | `PRAGMA quick_check` at open; `SqliteIntegrityException` blocks startup instead of rebuilding | `SqliteStore.cs` L150–160, L372–383 |
| Durability pragmas | `journal_mode=WAL`, `foreign_keys=ON`, `synchronous=FULL` | `SqliteStore.cs` L364–370 |
| Run store | `SqliteRunStore` uses only `SqliteStore`'s connection; **separate writes** for `SetRunDesiredStateAsync` / `SetRunObservedStateAsync`; `SaveAsync` already commits a document row + one `audit_event` row in one transaction; **no** command table, **no** snapshot query, **no** paging, **no** cursor/floor query | `src/Rag.HistoricalLoader.Core/Lifecycle/SqliteRunStore.cs` |
| Audit cursor | `audit_event(event_id INTEGER PRIMARY KEY AUTOINCREMENT)` — already an installation-scoped durable monotonic cursor, not a per-process counter | `SqliteStore.cs` L65–77 |
| States | 15 `DocumentState` values, 2 `RunDesiredState`, 6 `RunObservedState`; pure transition rules + `ToSnakeCase` used for the durable `state_transition` string (`"pending->staged"`) | `Lifecycle/Model.cs`; `SqliteRunStore.cs` L455–470 |
| Inventory facts | `manifest` (v1 DDL) has **only** `manifest_id, scan_version, state, start_time, end_time` — **no** persisted totals/export hashes; `candidate` carries `byte_size`; `ManifestState` = `Scanning | Complete` | `SqliteStore.cs` L43–63; `Data/Entities.cs` |
| Contracts slice (11.prev-a) | `net10.0`, no packages, no Core/Engine reference. Wire vocabulary already fixed: `ControlProtocol.Limits.MaxPageSize = 100`, `ControlErrorCodes` = `unsupported_version, unknown_operation, malformed_request, command_conflict, resync_required, platform_not_supported`; DTOs `InventoryTotals`, `StateSnapshot`, `DocumentSummary`, `DocumentPage`, `EventSummary`, `EventPage`, `CommandReceipt` | `src/Rag.HistoricalLoader.Contracts/{Protocol,Model}.cs` |
| Test conventions | Both loader test projects use **real temp-file SQLite** (`Path.Combine(Path.GetTempPath(), …)`) even in the unit project; the integration project covers reopen/restart. `Rag.HistoricalLoader.IntegrationTests.csproj` references **Core + Engine only — not Contracts** | `tests/Rag.HistoricalLoader.UnitTests/Lifecycle/DocumentLifecycleStateMachineTests.cs`; `…/IntegrationTests/Sqlite/LifecyclePersistenceTests.cs`; both csproj files |
| Schema-version assertions | Exactly **one** hard-coded `3`: `LifecyclePersistenceTests.cs:65` (`Assert.Equal(3, await store.GetSchemaVersionAsync())`) plus the test name `…SchemaV3WithBackupBeforeMigration`. `ManifestPersistenceTests.cs:96` uses `SqliteStore.CurrentSchemaVersion` | grep over `tests/**` |
| `Measurements` | Written as a bare numeric string (`stagedBytes.ToString()`) on actions `staged` / `staged_bytes`; no key=value vocabulary exists | `SqliteRunStore.cs` L142–190 |

## 3. Exact minimal durable slice

### 3.1 Migration (schema v4, additive, versioned, backed up)

`SqliteStore.cs`: `CurrentSchemaVersion` 3 → 4, add `SchemaV4`, append `new Migration(4, [SchemaV4])`.

```sql
CREATE TABLE loader_command (
    command_id TEXT PRIMARY KEY,
    fingerprint TEXT NOT NULL,
    operation TEXT NOT NULL,
    run_id TEXT NOT NULL,
    desired_state TEXT NOT NULL,
    observed_state TEXT NOT NULL,
    created_at INTEGER NOT NULL
);

CREATE TABLE loader_engine_instance (
    instance_id TEXT PRIMARY KEY,
    started_at INTEGER NOT NULL
);
```

Deliberate minimalism (decisions to record, not omissions):

- **No extra index.** Every new lookup is a primary-key lookup or `MAX(started_at)` over a table that holds one
  row per process start. Speculative indexing is rejected.
- **No new uniqueness constraint on `run`.** "One active run per installation" cannot be a declarative index
  here: any partial/unique index over `run(observed_state)` would risk the existing Unit 6/10 suites
  (`LifecyclePersistenceTests` creates multiple runs per database) and would violate 11.prev-b acceptance (c).
  The invariant stays an **in-transaction precondition** (see §3.3) so existing behaviour is provably untouched.
- **Raw request is never persisted.** The `loader_command` row stores the normalized fingerprint only.
- `loader_engine_instance` appends one row per store open; the snapshot reads the latest by
  `ORDER BY started_at DESC, instance_id DESC LIMIT 1` (deterministic tie-break). This satisfies the tasks.md
  RED clause "*… and engine-instance ID …*" inside one coherent read, and makes "a new engine instance triggers a
  refresh, not a counter reset" observable at store level. Accepted trade-off: one extra row per process start.
  *Cheaper alternative (parent option):* drop the table and let the 11.prev-d service compose the runtime
  instance ID into the snapshot — saves ~40 lines including tests but leaves that RED clause unmet at store level.

### 3.2 New production file — `src/Rag.HistoricalLoader.Core/Lifecycle/ControlStore.cs`

REFACTOR row names this file ("collapse receipt/query types into `Lifecycle/ControlStore.cs`"), so types and the
store live together. `SqliteRunStore` is **not modified** (preserves existing lifecycle/attempt/audit semantics).

Public surface (sketch; names are the contract the RED tests bind to):

```csharp
public enum CommandCommitOutcome { Committed, Replayed, Conflict, Rejected }

public sealed record CommandIntent(string CommandId, string Operation, string Fingerprint, Guid RunId);

public sealed record CommandReceiptRecord(
    string CommandId, string Operation, Guid RunId,
    string DesiredState, string ObservedState, DateTimeOffset CreatedAt);

public sealed record CommandCommitResult(
    CommandCommitOutcome Outcome, CommandReceiptRecord? Receipt, string? RejectionCode);

public sealed record StartCommandPlan(
    CommandIntent Intent, Run Run, IReadOnlyList<RunDocument> Documents, string AuditAction);

public sealed record ActiveRunView(bool HasActiveRun, Guid? RunId, string? CollectionId, string? ObservedState);
public sealed record RunStateView(bool Found, RunDesiredState DesiredState, RunObservedState ObservedState);

public delegate Task<string?> StartPrecondition(ActiveRunView activeRun, CancellationToken cancellationToken);
public delegate Task<string?> DesiredStatePrecondition(RunStateView run, CancellationToken cancellationToken);

public sealed record InventoryTotalsProjection(string? ManifestId, string Completeness, int CandidateCount, long CandidateBytes);

public sealed record ControlSnapshot(
    bool HasRun, Guid? RunId, string? DesiredState, string? ObservedState,
    InventoryTotalsProjection Inventory, IReadOnlyDictionary<string, int> DocumentCounts,
    DateTimeOffset? CheckpointAt, string? BlockCode, string EngineInstanceId, long EventHighWaterMark);

public sealed record DocumentSummaryRecord(
    Guid DocumentId, Guid CandidateId, string State, IReadOnlyDictionary<string, int> Attempts,
    string? Classification, DateTimeOffset UpdatedAt);

public sealed record DocumentPageCursor(DateTimeOffset CreatedAt, Guid DocumentId);
public sealed record DocumentPageResult(Guid RunId, IReadOnlyList<DocumentSummaryRecord> Documents, DocumentPageCursor? NextCursor);

public sealed record EventPageRequest(int Limit, Guid? RunId = null, long AfterEventId = 0, long? ThroughEventId = null);
public enum EventPageOutcome { Ok, ResyncRequired, InvalidRequest }
public sealed record EventPageResult(
    EventPageOutcome Outcome, IReadOnlyList<AuditEvent> Events, long? NextCursor,
    long HighWaterMark, long MinRetainedEventId);

public static class ControlStoreVocabulary { /* snake_case state/desired/completeness values; MaxPageSize = 100 */ }

public sealed class SqliteControlStore                       // ctor: SqliteControlStore(SqliteStore store)
{
    Task RecordEngineInstanceAsync(string engineInstanceId, CancellationToken ct = default);
    Task<CommandCommitResult> CommitStartAsync(StartCommandPlan plan, StartPrecondition precondition, CancellationToken ct = default);
    Task<CommandCommitResult> CommitDesiredStateAsync(CommandIntent intent, RunDesiredState desired, string auditAction, DesiredStatePrecondition precondition, CancellationToken ct = default);
    Task<CommandReceiptRecord?> GetCommandReceiptAsync(string commandId, CancellationToken ct = default);
    Task<ControlSnapshot> GetControlSnapshotAsync(Guid? runId, CancellationToken ct = default);
    Task<DocumentPageResult> GetDocumentPageAsync(Guid runId, int limit, DocumentPageCursor? after, CancellationToken ct = default);
    Task<EventPageResult> GetEventPageAsync(EventPageRequest request, CancellationToken ct = default);
}
```

Key design decisions, each with the reason it is the minimal honest option:

1. **Named atomic composites instead of a public transaction delegate.** The persistence contract needs
   "command mutation + receipt + operator audit commit together". Exposing
   `Func<SqliteConnection, SqliteTransaction, …>` publicly (or via `InternalsVisibleTo`) would leak SQLite types
   into the service slice and let the Engine hand arbitrary SQL to Core, contradicting "the pipe dispatcher
   validates and delegates; it owns no ingestion policy". Two named composites cover the entire v1 mutation
   set. *Rejected alternative:* a generic delegate API — smaller, but it moves SQL policy into Engine.
2. **Policy stays in the service through narrow, typed preconditions.** `CommitStartAsync` /
   `CommitDesiredStateAsync` evaluate a caller-supplied precondition **inside** the transaction, so
   "validate approval/target/capacity/one-active-run, then create" is atomic without Core knowing approval or
   capacity rules. The views (`ActiveRunView`, `RunStateView`) are deliberately narrow and path/secret-free.
   Residual, accepted TOCTOU: batch resolution happens before the transaction; the precondition re-checks the
   critical durable invariant, and one instance + one writer bound the window.
3. **Replay/conflict semantics are store-owned and total.** Replay lookup → same `fingerprint` ⇒ return the
   durable receipt, write nothing, emit no second audit row; different fingerprint ⇒ `Conflict`; precondition
   rejection ⇒ `Rejected` with a stable code, write nothing.
4. **Contract-fitting rejection codes (no Contracts change).** `unknown run_id`, invalid transition, and
   active-run contention map to `command_conflict`; `resync_required` is reserved for cursor invalidation.
   `ControlErrorCodes` has no dedicated code for the first three, and adding one would modify the delivered
   11.prev-a allowlist (invalidating its recorded acceptance). *If the parent wants distinct codes, that is a
    Contracts change and reopens 11.prev-a.*
5. **Core owns the snake-case wire vocabulary for durable state.** Precedent already exists and is unavoidable:
   `audit_event.state_transition` stores `"pending->staged"`, and `get_events` must forward historical
   transition strings unchanged. `ControlStoreVocabulary` therefore owns `pending`, `loaded`, `blocked_auth`,
   `complete`/`incomplete`/`none`, attempt keys `reserve|upload|commit|poll`, and `MaxPageSize = 100`.
   **Cross-slice obligation:** these duplicate `Contracts.ControlDocumentStates` / `ControlRunStates` /
   `ControlProtocol.Limits.MaxPageSize`, and **no test in this slice can compare the two assemblies** (Core must
   not reference Contracts; the integration project does not reference Contracts). The agreement test must land
   in 11.prev-c (Engine references both) — record it as a required 11.prev-c RED, otherwise the store and the
   wire can drift silently.
6. **`BlockCode` is derived, not a new column.** A run row has no block column; `ObservedState == BlockedAuth`
   / `BlockedOperatorAction` is the durable safe block code (snake-cased). No schema addition.
7. **Inventory totals are computed from `candidate` + `manifest.state`, never from a running scan.** No manifest
   totals exist in the shipped schema, so the projection is `COUNT(*)`/`SUM(byte_size)` for the latest manifest
   (`start_time DESC, manifest_id DESC`) with completeness `complete|incomplete|none`. An incomplete scan is
   never reported as complete.
8. **Event cursor validity is total and gap-safe.**
   - `MinRetainedEventId = COALESCE(MIN(event_id), 0)` derives the retention floor **without a new table**;
     pruning (not in this unit) raises it, and a cursor below it ⇒ `ResyncRequired`, never a silent gap.
   - `AfterEventId > HighWaterMark` (where `HighWaterMark = COALESCE(MAX(event_id), 0)`) ⇒ `ResyncRequired`
     (restore/rollback of the database).
   - `ThroughEventId < AfterEventId` or `ThroughEventId > HighWaterMark` ⇒ `InvalidRequest`.
   - `Limit` outside `1..ControlStoreVocabulary.MaxPageSize` ⇒ `InvalidRequest`.
   - Otherwise `Ok`: ascending `event_id > after` and `<= COALESCE(through, highWater)`, `NextCursor` = last
     returned ID when the page filled the limit, else null.
9. **Measurements stay raw at the store boundary, with a numeric-only guard.** The wire projection
   (`IReadOnlyDictionary<string,double>` allowlisted per action) needs vocabulary that does not exist yet
   (`Measurements` is a bare numeric string today), so it belongs to 11.prev-d. 11.prev-b asserts only that every
   returned `Measurements` is null or parseable as a number — cheap defence in depth, no invented vocabulary.
10. **The document cursor is typed, not encoded.** `DocumentPageCursor(CreatedAt, DocumentId)` is a Core type
    with keyset ordering (`created_at, run_document_id`); the opaque `AfterDocumentId` string encoding (and
    malformed-cursor → `malformed_request`) belongs to 11.prev-d. Recorded as an explicit 11.prev-d obligation.
11. **`SqliteRunStore.ToSnakeCase` is not reused.** Sharing it would modify an existing production file; the new
    helper is duplicated in `ControlStore.cs` on purpose. Review-readability may flag it; de-duplication is an
    explicit out-of-slice follow-up.

## 4. Strict-TDD RED targets (exact, per behaviour group)

RED form: **RED-0** is compile-form (tests authored against the not-yet-existing `SqliteControlStore` surface →
test assembly not produced, `CS0246`, 0 tests executed) — the same honest limitation 11.prev-a recorded. Unlike
11.prev-a, this slice modifies an existing store, so **authentic per-assertion runtime RED is achievable and
required**: each group below must be added and observed failing at runtime before its implementation lands
(implement groups in order; later groups fail as not-yet-implemented while earlier ones pass).

| # | Group | RED target |
| --- | --- | --- |
| 1 | Migration (integration) | v3-seeded DB → `user_version == 4`; `{db}.pre-migration-v3.bak` exists; `loader_command`/`loader_engine_instance` exist and are **empty**; every pre-existing table/index DDL captured from `sqlite_master` before initialization is **byte-identical** after (additive-only proof); pre-existing run/run_document/audit rows byte-identical; `PRAGMA foreign_keys` still ON |
| 2 | Engine instance | `RecordEngineInstanceAsync("i1")` → snapshot reports `i1`; then `"i2"` → snapshot reports `i2` with run/document/audit rows and counters unchanged (refresh, not reset) |
| 3 | Command atomicity + replay | start commits run + documents + one operator audit + receipt in one transaction; replay of same `command_id`+fingerprint ⇒ `Replayed`, durable receipt returned, zero additional run/document/audit rows; changed fingerprint ⇒ `Conflict` with zero writes; precondition rejection ⇒ `Rejected`/`command_conflict` with zero writes; receipt fields are allowlisted only (sentinel raw request JSON containing an absolute path + secret-shaped value is absent from every `loader_command` column) |
| 4 | Acknowledgement-boundary fault injection | `CommitStartAsync` with a plan containing a duplicate `(run_id, candidate_id)` → real `UNIQUE` violation raised mid-transaction → afterwards **zero** run rows, zero documents, zero audit rows, zero receipts (whole-transaction rollback); precondition-throw path writes nothing. **Test seam deliberately not added**: the store must not pre-validate the plan, or the honest crash-window RED disappears |
| 5 | Coherent snapshot | all fields from one read; `sum(DocumentCounts) == document count`; `CheckpointAt` from the run row; `BlockCode == "blocked_auth"` iff observed state is `BlockedAuth` (null otherwise); inventory `complete`/`incomplete`/`none`; no run ⇒ `HasRun == false`, empty counts, inventory still reported; sentinel: seeded `configuration_snapshot` JSON never appears in the serialized snapshot; concurrency triangulation: concurrent document inserts with 50 interleaved snapshot reads never yield a mixed/incoherent count |
| 6 | Document pages | keyset paging by `(created_at, id)` → no duplicates, no gaps; a state change between pages is reflected by the later page (current-state pages, not an immutable snapshot); no `source_document_key`, `extraction_hash`, remote ids or config JSON in the serialized page; `Attempts` keys exactly `reserve/upload/commit/poll` with durable values; `limit <= 0` ⇒ `ArgumentOutOfRangeException` |
| 7 | Event pages | ascending; exclusive `after_event_id`; full page ⇒ `NextCursor`; empty page at high-water ⇒ `NextCursor == null`; `through_event_id` bound honoured; run filter; `HighWaterMark == MAX(event_id)`; cursor above high-water ⇒ `ResyncRequired` (restore); cursor below the floor after simulated retention pruning (`DELETE FROM audit_event WHERE event_id <= k` by raw SQL — no production pruning code added) ⇒ `ResyncRequired`; out-of-range limit / `through < after` / `through > high-water` ⇒ `InvalidRequest`; every `Measurements` null-or-numeric |
| 8 | Restart durability (integration) | reopen ⇒ receipts, audit rows, loaded/terminal rows, per-operation attempt counters and the three-attempt ceiling preserved; snapshot counts identical before/after; a cursor above the restored high-water mark (simulated by deleting the newest events) ⇒ `ResyncRequired` |
| 9 | Regression / safety net | existing loader unit+integration suites stay green; `dotnet build Rag.sln --configuration Release` 0 errors (pre-existing `NU1903` only); `dotnet test Rag.sln --configuration Release` green; per-project counts unchanged except the new cases |

Test surfaces:

- `tests/Rag.HistoricalLoader.UnitTests/Lifecycle/ControlStoreTests.cs` — groups 2, 3, 4, 5, 6, 7 (single
  process, temp-file SQLite, public API only — tests cannot reach the `internal` writer helpers).
- `tests/Rag.HistoricalLoader.IntegrationTests/Lifecycle/ControlStorePersistenceTests.cs` — groups 1, 8.

## 5. Unavoidable edit outside the stated allowed surface

`tasks.md` 11.prev-b allows only `Core/Lifecycle/**` + the two new test files + `apply-progress.md`. The
mandated **versioned** migration requires two edits outside that list:

1. `src/Rag.HistoricalLoader.Core/Persistence/SqliteStore.cs` — `CurrentSchemaVersion` 3 → 4, `SchemaV4`,
   `Migrations` entry. `design.md`'s projected-change table *does* authorize "`Core/Lifecycle/` and loader
   persistence migration surface", so the design sanctions it; the tasks.md line is narrower than the design.
2. `tests/Rag.HistoricalLoader.IntegrationTests/Sqlite/LifecyclePersistenceTests.cs:65` — the hard-coded
   `Assert.Equal(3, …)` becomes `4` and the test name `Initialize_MigratesSchemaV3WithBackupBeforeMigration`
   becomes `…V4…` (leaving the name unchanged would make it lie). One assertion + one identifier. This is
   **required** for acceptance (c) "existing lifecycle/retry/audit tests remain green" — without it, an existing
   green test turns red the moment v4 lands.

*Rejected alternative:* create the two tables with the idempotent `CREATE TABLE IF NOT EXISTS` pattern used by
`SampleSetStore`/`BenchmarkObservationStore` (no `SqliteStore` change, no existing-test edit) — but that gives
no migration version and no `pre-migration` backup, directly contradicting this slice's RED clause and the
design's backup-before-migration requirement. Note the repository already carries that second pattern, so this
is a genuine fork that must be decided explicitly rather than silently.

Also recorded, not fixed here: `loader_installation.schema_version` is stamped with `CurrentSchemaVersion`
**only at creation**, so a migrated v3 database keeps `schema_version = 3` while `user_version = 4`. Pre-existing
inconsistency; fixing it is out of this slice and a later verify must not read it as a regression.

## 6. Dependency constraints (binding)

- **Predecessor gates:** 11.prev-a limited acceptance (a)–(d) recorded ✅; parent delivery-decision row ⛔
  unchecked; `design.md` authorizes no implementation and no exception for 11.prev.
- **Direction:** Core stays independent of Contracts and Engine (protocol-to-store mapping belongs to the
  service slice). No new project, no `Rag.sln` change, no csproj change, no new package.
- **Assembly access:** `SqliteControlStore` reaches the single writer through `internal` `SqliteStore` members
  (same assembly). Tests use the public surface only — Core has no `InternalsVisibleTo`.
- **Cross-platform:** `net10.0` only, Linux-buildable; no `EnableWindowsTargeting`, `UseWPF`, Windows runtime,
  conditional test omission, or CI change. No `Program.cs`/`NamedPipeHost.cs`/Engine/Contracts/WPF/Desktop change.
- **Test facts:** temp-file SQLite (existing convention); integration project has no Contracts reference;
  `Microsoft.Data.Sqlite` is already available to both test projects through Core.
- **Protected paths:** `src/Rag.Companion/**`, `src/Rag.AdminApp*`, `src/Rag.Api/**`, `Dockerfile`,
  `compose*.yaml`, `.github/**`, and every pre-existing dirty/untracked path remain untouched.

## 7. Line forecast (honest surface)

| Surface | Forecast added/changed lines |
| --- | --- |
| `Core/Persistence/SqliteStore.cs` (v4 DDL + const + migration entry) | 30–40 |
| `Core/Lifecycle/ControlStore.cs` (new: types + store + SQL/mapping) | 400–560 |
| `UnitTests/Lifecycle/ControlStoreTests.cs` (new) | 320–460 |
| `IntegrationTests/Lifecycle/ControlStorePersistenceTests.cs` (new) | 240–340 |
| `IntegrationTests/Sqlite/LifecyclePersistenceTests.cs` (assertion + name) | 2 |
| `tasks.md` (RED/GREEN/TRIANGULATE/REFACTOR + acceptance rows) | 6 |
| `apply-progress.md` | 60–110 |
| **Total** | **≈ 1,060–1,520** (mid ≈ 1,250) |

Against the 400-line review budget this is **≈ 2.6×–3.8×**; against this slice's recorded `~350–500` forecast in
`tasks.md` it is **≈ 2–3×**. The recorded forecast is optimistic because the mandated RED list is genuinely wide
(migration + backup + additive proof, atomic receipts/replay/conflict, crash-window rollback, coherent
multi-field snapshot, keyset document pages, event cursors + resync + retention/restore, restart durability,
concurrency, projection privacy). Per the plan's own rule, the surface is **reported, not compressed**.

Sub-slice options if a re-slice is preferred (sequential, each independently verifiable):

| Option | Contents | Forecast |
| --- | --- | --- |
| `b1` | v4 migration + backup/additive proof + engine instance row + command receipts atomicity/replay/conflict + fault injection (groups 1–4) | ~460–640 |
| `b2` | coherent snapshot + inventory projection + privacy sentinel (group 5) | ~230–330 |
| `b3` | document keyset pages + event pages/cursors/resync (groups 6–7) | ~400–560 |
| `b4` | restart durability + regression net (groups 8–9) | ~150–250 |

Re-slicing does **not** guarantee sub-400 slices: `b1` and `b3` remain over budget because tests travel with the
behaviour they verify. A `size:exception` on the measured surface (the 11.prev-a pattern) is the realistic path;
`b2`/`b4` alone are the only comfortably sub-400 pieces.

## 8. Risks

1. **Budget (High).** Honest surface 1,060–1,520 vs a 400 budget and a 350–500 recorded forecast; no exception is
   authorized for this unit. Requires the `ask-on-risk` pause to resolve before apply.
2. **Unauthorized-surface edit (High, bounded).** `LifecyclePersistenceTests.cs:65` hard-codes schema version 3
   and turns red with v4. The design authorizes the migration surface; the tasks.md allowed-edit line does not.
   Needs an explicit surface amendment (one assertion + one test name).
3. **Wire-vocabulary duplication with no local cross-check (Medium).** `ControlStoreVocabulary` duplicates
   `Contracts.ControlDocumentStates`/`ControlRunStates`/`MaxPageSize`, and no test in this slice can compare the
   assemblies. Without the required 11.prev-c agreement test, the store and the wire can drift silently.
4. **Contract gap for rejection codes (Medium).** Unknown run / invalid transition / active-run contention have
   no dedicated v1 code; mapping them to `command_conflict` is a decision, and choosing otherwise reopens
   11.prev-a's delivered allowlist.
5. **Engine-instance persistence is a design add-on (Medium).** tasks.md lists it inside the coherent read; the
   design treats it as runtime identity. Persisting it costs a table, a startup write, and ~40 lines of tests.
6. **Fault-injection shape is fragile by design (Medium).** The crash-window RED relies on a real SQLite
   constraint violation (duplicate candidate in the plan). Any future pre-validation of the plan removes the
   only honest acknowledgement-boundary test; this must be stated in the implementation.
7. **Duplicated snake-case helper (Low–Medium).** Deliberate, to keep `SqliteRunStore` untouched; expect a
   readability finding and record a de-dup follow-up rather than expanding this slice's surface.
8. **`loader_installation.schema_version` staleness (Low).** Pre-existing; must not be read as a regression by a
   later verify.
9. **Design/schema gap on inventory totals (Low).** The design's data model mentions manifest summary totals and
   export hashes that the shipped `manifest` DDL does not carry; the projection is candidate-derived by
   interpretation and must never present an incomplete scan as complete.
10. **Pre-existing `NU1903` (Low, unrelated).** `SQLitePCLRaw.lib.e_sqlite3 2.1.11`; not introduced here.

## 9. Open questions for the parent

1. **Delivery decision (must precede apply):** `size:exception` for 11.prev-b at its measured surface, or an
   approved re-slice (`b1`–`b4`)? This is the unchecked parent-owned row plus the `ask-on-risk` pause.
2. **Surface amendment:** authorize `Core/Persistence/SqliteStore.cs` (v4 migration) + the one-assertion edit in
   `IntegrationTests/Sqlite/LifecyclePersistenceTests.cs`? Or choose the `CREATE TABLE IF NOT EXISTS` fork
   (which contradicts this slice's RED clause)?
3. **Engine-instance ID:** persist in the new table (recommended, satisfies the tasks.md RED clause), or compose
   it in the 11.prev-d service?
4. **Rejection codes:** confirm `command_conflict` for unknown run / invalid transition / active-run contention,
   or authorize a Contracts allowlist change (reopens 11.prev-a)?
5. **Ownership seams:** confirm 11.prev-b exposes the typed `DocumentPageCursor` and raw `Measurements`, with the
   opaque cursor encoding and the numeric measurements allowlist owned by 11.prev-d — and that the
   store↔wire vocabulary agreement test is a required 11.prev-c RED.

## 10. Next recommended action

Do **not** start 11.prev-b. Pause for the parent-owned delivery decision (question 1) and the surface amendment
(question 2). If `size:exception` (or a re-slice) is granted, amend the `tasks.md` forecast/surface rows and then
run 11.prev-b under strict TDD: migration first, then receipts atomicity, then projections/pages, each with its
tests and its own limited acceptance (a)–(d), all inside the `b1`→`b4` order above.
