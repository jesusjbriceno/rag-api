# ODD Tasks — Control service and supervised `serve` host

Scope: implement `11.prev-d` only. No Windows named-pipe implementation (11.prev-e), no Core/Contracts/CLI/protected-path changes, no delivery.

Delivery decision: the user explicitly authorized a per-slice `size:exception` for 11.prev-d limited to this slice. It does not authorize delivery or 11.prev-e. Strict TDD is active for this slice; RED/GREEN evidence is recorded in `openspec/changes/historical-ingestion-rebaseline/apply-progress.md`.

- [x] 1. Map the existing test suites and seams for the service slice: locate the 11.prev-b store surface (`ControlStore` receipts/snapshot/pages), the 11.prev-c `Control/Dispatcher.cs` + `Control/Protocol.cs` seams, the transport seam used by the fake, and the `HistoricalPipeline.RunAsync`/`RequestPauseAsync`/`ResumeAsync` composition points; fix the exact new test files and target edit surfaces before any code.
- [x] 2. RED: add the failing service/host tests against the fake transport and fake extractor/API (command lifecycle and atomicity, one active run + installation lock, pause acknowledgement vs. durable safe boundary, `get_state`/`get_documents`/`get_events` bounded allowlisted projections, replay/dedup and no double pipeline, shutdown drain/recovery, unavailable-store and privacy-sentinel scans); capture the observed failing run.
- [x] 3. Implement the control service, safe projections, host supervision, and OS-backed installation lock under `src/Rag.HistoricalLoader.Engine/Control/`, and wire the explicit `serve` composition entry point in `Program.cs` by composing the existing `HistoricalPipeline` (never duplicating lifecycle or retry logic); capture the focused tests passing.
- [x] 4. Run focused verification plus the authorized solution build/test, record the evidence (RED number, GREEN number, privacy-sentinel and recovery results, existing CLI regression) in `apply-progress.md`, and reconcile this task record.
