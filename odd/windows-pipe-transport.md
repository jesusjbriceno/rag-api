# ODD Tasks — Windows pipe transport

Scope: implement `11.prev-e` only. Keep the default Linux solution portable; do not start Unit 11 or delivery.

- [x] 1. Map the 11.prev-d transport seam and define Linux fail-closed / Windows security evidence boundaries.
  Evidence: the seam is `src/Rag.HistoricalLoader.Engine/Control/**` (15 files, from `BatchResolver.cs` and
  `InstallationLock.cs` through the new `PeerPrefixStream.cs`, `PipeTransportFactory.cs` and
  `WindowsPipeTransport.cs`). The Linux fail-closed and Windows security evidence boundaries were recorded where
  the Windows run is described: `docs/historical-ingestion-rebaseline/unit-11-prev-windows-pipe-security.md`
  separates them into §1 (code facts), §2 (what Linux and the fake transport prove), §3 (what they do **not**
  prove, N1–N7) and §5 (the E1–E7 evidence rows), with the run procedure itself in
  `unit-11-prev-e-windows-operator-runbook.md`.
- [x] 2. Add strict-TDD RED coverage for transport limits, platform rejection, collisions, and endpoint safety.
  Evidence: `tests/Rag.HistoricalLoader.UnitTests/Control/NamedPipeTransportTests.cs` holds 19 cases — 17
  `[Fact]` and 2 `[Theory]` methods (16 `[InlineData]` rows), six of them the `PeerPrefixStream` cases added by
  the BF-1 fix. It pins the endpoint identity and its path-shaped rejection, the declared IPC limits, the
  `platform_not_supported` early cut, saturation, the pipe-name collision, and the no-fallback surface. The
  Windows-only guarantees — ACL enforcement, foreign-user denial, and remote/non-local denial — are
  deliberately **not** asserted by these Linux tests; the file says so itself, and the operator record owns
  that evidence.
- [x] 3. Implement the Windows named-pipe transport behind OS guards and preserve the no-pipe Linux path.
  Evidence: the implementation is `src/Rag.HistoricalLoader.Engine/Control/WindowsPipeTransport.cs`
  (`WindowsNamedPipeBoundary`, `WindowsNamedPipeIdentity`, `WindowsPipeSecurity`, `WindowsNamedPipeInterop`,
  `WindowsNamedPipeTransport`), `Control/PipeTransportFactory.cs`, the portable `Control/PeerPrefixStream.cs`, and
  `src/Rag.HistoricalLoader.Engine/NamedPipeHost.cs` (now the listening host that replaced the former
  non-listening stub). The OS guard predicate is
  `WindowsNamedPipeBoundary.IsSupported => OperatingSystem.IsWindows() && OperatingSystem.IsWindowsVersionAtLeast(6, 2)`,
  annotated `[SupportedOSPlatformGuard("windows")]`, and the stable refusal code is `platform_not_supported`
  (`ControlErrorCodes.PlatformNotSupported`, `src/Rag.HistoricalLoader.Contracts/Protocol.cs:47`). The default
  Linux solution stays portable: both projects target the single `net10.0` TFM, there is no `#if` in the engine
  or the unit tests, and the Control tests carry no `WindowsFact`/`Skip` conditional omission.
- [x] 4. Verify portable tests and record what requires a Windows operator evidence run.
  Evidence: the evidence file is `docs/historical-ingestion-rebaseline/unit-11-prev-windows-pipe-security.md` and
  the procedure is `docs/historical-ingestion-rebaseline/unit-11-prev-e-windows-operator-runbook.md`. On a real
  Windows host (Windows 11 Home build 26200, 2026-09-22), run 2 executed and passed **E1** (same-user connection
  with the version-1 handshake), **E2** (owner-only protected DACL read back as SDDL), **E5** (fail-closed, zero
  TCP listeners, exactly one derived pipe), **E6** (collision fails closed) and **E7** (production round trip:
  `hello` + `get_state`, then a clean Ctrl+C-equivalent drain to exit code 0). Run 1 had failed E1 and E7 on the
  BF-1 ordering defect, which run 2 fixed and re-verified. **Foreign-user denial (E3) and remote/non-local denial
  (E4) were never executed on any host**: they are carried by a maintainer-authorized evidence exception (§6
  L8, authorized 2026-09-22) that rests on E2's descriptor read and on `PIPE_REJECT_REMOTE_CLIENTS` being passed
  to `CreateNamedPipeW`, not on a refused connection — so a reader must not read the tick above as evidence of
  those two facts.

Recorded closed on 2026-09-25: the slice reached `develop` with PR #33 (`815924a`) and later rounds carried the
server half. The four boxes above track the plan, not fresh work; the only open item this record leaves behind is
the unexecuted foreign-user and remote-denial evidence, which needs a Windows host with a second account and a
second networked machine.
