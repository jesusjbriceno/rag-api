# ODD Tasks — Windows pipe transport

Scope: implement `11.prev-e` only. Keep the default Linux solution portable; do not start Unit 11 or delivery.

- [ ] 1. Map the 11.prev-d transport seam and define Linux fail-closed / Windows security evidence boundaries.
- [ ] 2. Add strict-TDD RED coverage for transport limits, platform rejection, collisions, and endpoint safety.
- [ ] 3. Implement the Windows named-pipe transport behind OS guards and preserve the no-pipe Linux path.
- [ ] 4. Verify portable tests and record what requires a Windows operator evidence run.
