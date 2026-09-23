# ODD Tasks — Control codec and dispatcher

Scope: implement `11.prev-c` only. No host wiring, named pipes, Core changes, Contracts changes, or CLI changes.

- [x] 1. Add strict-TDD RED coverage for framing, fail-closed dispatch validation, saturation/deadline behaviour, and store↔wire vocabulary agreement.
- [x] 2. Implement the bounded byte-frame codec and transport-agnostic dispatcher under `src/Rag.HistoricalLoader.Engine/Control/`, with timeout/saturation mapped to `malformed_request`.
- [x] 3. Run focused and solution verification, record evidence in `apply-progress.md`, and reconcile this task record.
