# ODD Tasks — Windows pipe transport

Scope: implement `11.prev-e` only. Keep the default Linux solution portable; do not start Unit 11 or delivery.

> **Status update (current session, documentation only):** the operator has **started the Windows verification run for
> 11.prev-e** (evidence items **E1–E7**) — execution is **in progress**. **No result, outcome, or verdict is recorded or
> accepted yet**; `docs/historical-ingestion-rebaseline/unit-11-prev-windows-pipe-security.md` remains a template with
> seven empty evidence rows, and acceptance letter (b) of 11.prev-e remains unsatisfied. The Linux-side verification
> record stands unchanged (0 errors / 826 passed / 0 failed / 0 skipped, previously recorded — not re-executed here).
> The pause is lifted only by recorded evidence, not by this note. **No task checkbox below is changed.**

- [ ] 1. Map the 11.prev-d transport seam and define Linux fail-closed / Windows security evidence boundaries.
- [ ] 2. Add strict-TDD RED coverage for transport limits, platform rejection, collisions, and endpoint safety.
- [ ] 3. Implement the Windows named-pipe transport behind OS guards and preserve the no-pipe Linux path.
- [ ] 4. Verify portable tests and record what requires a Windows operator evidence run.
