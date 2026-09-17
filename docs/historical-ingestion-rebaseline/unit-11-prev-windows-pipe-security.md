# Unit 11.prev-e — Windows named-pipe security evidence

**Slice:** 11.prev-e (Windows named-pipe transport + pipe security evidence).
**Status: NOT YET EXECUTED.** This file is the record the operator fills in on a real Windows machine.
Every row below is currently empty on purpose: no Windows host was available to this work unit, and no
evidence is claimed that was not observed.

> **The one sentence that matters:** the Linux suite proves the *seam*, the fail-closed decisions, and the
> early cut; it does **not** prove Windows ACL enforcement, same-user success, foreign-user denial, or
> remote-client denial. Those four facts exist only when an operator records them here.

---

## 1. What the implementation enforces (code facts, not evidence)

| Control | Where | What it is |
| --- | --- | --- |
| Platform gate | `Control/PipeTransportFactory.cs`, `Control/WindowsPipeTransport.cs` (`WindowsNamedPipeBoundary.IsSupported`) | Windows only, and only `Windows 8 / Server 2012` and later (the kernel must support the explicit remote-client rejection flag). The gate is annotated `[SupportedOSPlatformGuard("windows")]`, so the analyzer verifies every Windows call site below it (CA1416 = 0). |
| Endpoint name | `Control/PipeEndpoint.cs` | `rag-historical-loader-v1-<32 lowercase hex chars>`: a fixed prefix plus the first 128 bits of `SHA-256(installationIdentity + "\n" + userIdentity)`. No public constructor, no `Parse`/`TryParse`, no member taking a pipe name or a path; both identity inputs are validated as opaque tokens (ASCII letters/digits/`-`/`_`, 1…256 chars), so `\`, `/`, `:`, `.`, `..`, `*`, `?`, `|`, space, control bytes, and blanks are refused. |
| Installation identity input | `WindowsPipeIdentity.MachineInstallationIdentity` | The machine installation GUID (`HKLM\SOFTWARE\Microsoft\Cryptography\MachineGuid`) — a machine-wide **non-secret** identifier read before the installation lock and the store. It is a local fact, not a credential. |
| User identity input | `WindowsPipeIdentity.CurrentUserSid` | The current process token's user SID (`S-1-5-21-…`). The same identity the ACL and the peer check use. |
| Owner-restricted ACL | `WindowsPipeSecurity.CurrentUserOnly` | A `PipeSecurity` with **owner and group set to the current user SID**, access-rule protection on (`SetAccessRuleProtection(true, false)`), and exactly **one** allow ACE: full control for that SID. `SYSTEM` and `Administrators` are deliberately absent. |
| Remote rejection | `WindowsNamedPipeInterop` | The instance is created with pipe mode `PIPE_REJECT_REMOTE_CLIENTS` (explicitly, through `CreateNamedPipeW`, because the managed overloads do not expose the mode). |
| Name collision | `WindowsNamedPipeInterop` (`FILE_FLAG_FIRST_PIPE_INSTANCE` on the first instance) | A competing owner of the same kernel-object name makes creation fail. The engine never deletes a foreign instance, never retries under another name, and reports `command_conflict`. |
| Peer identity | `WindowsNamedPipeTransport.TryVerifyPeer` | Every accepted peer is impersonated (`NamedPipeServerStream.RunAsClient`) and its token's user SID must equal this process's SID. A peer that cannot be impersonated or read is **denied before a single byte is dispatched**. |
| Byte-mode async I/O | `WindowsNamedPipeTransport` | `PIPE_ACCESS_DUPLEX \| FILE_FLAG_OVERLAPPED`, byte type / byte read mode / blocking wait; the handle is wrapped by `NamedPipeServerStream(…, isAsync: true, …)` for bounded async reads and writes. |
| Bounded instances | `WindowsNamedPipeTransport` | `MaxConcurrentConnections + 1` instances (4 + the idle listener), `ReadChunkBytes` (16 KiB) in/out buffers. Saturation is refused by the host *without dispatching* the extra connection. |

## 2. What Linux and the fake transport DO prove

Observed on this Linux workstation, against the RED contract `tests/Rag.HistoricalLoader.UnitTests/Control/NamedPipeTransportTests.cs`:

- the endpoint is a fixed prefix plus an opaque, deterministic, user- and installation-bound identity, and every
  path-shaped or caller-selected identity is rejected (a refused input raises, it is never sanitised);
- the portable surface exposes no `System.Net`, `System.IO.Pipes`, `System.Security`, or
  `System.Runtime.InteropServices` type and no anonymous/insecure/TCP/fallback member;
- the production invocation returns the contract's stable `platform_not_supported` with exit code 3 **and creates
  no file at all** — the guard runs before the installation lock, the store, every credential, and any ingestion;
- a caller-selected pipe endpoint on the command line is a usage error (exit 2) and opens nothing;
- the pipe opens only under an explicit `Start`, a boundary that cannot own its endpoint reports
  `command_conflict` and is never retried under another name, and the saturated boundary refuses the extra peer
  without writing a byte back;
- `serve` composes the full supervised host (installation lock → store → service → pipe) over the transport seam,
  answers the version-1 handshake, refuses a second instance on the installation lock before opening its own pipe,
  and returns 0 after a cancellation-requested drain.

## 3. What Linux and the fake transport do NOT prove

| # | Not proven | Why it cannot be proven here |
| --- | --- | --- |
| N1 | **ACL enforcement** (foreign user denied) | The ACL is enforced by the Windows object manager. Linux has no `PipeSecurity`, no SIDs, and no named-pipe namespace; the fake transport has no security descriptor at all. |
| N2 | **Same-user success** | Requires a real Windows logon token and a real pipe instance. |
| N3 | **Remote-client denial** | `PIPE_REJECT_REMOTE_CLIENTS` is a Windows kernel behaviour; no Linux process can attempt a remote named-pipe connection. |
| N4 | **Peer SID verification** | `RunAsClient` / `WindowsIdentity` throw off Windows and are never reached on Linux. |
| N5 | **The interop-created handle works with the managed wrapper** | `CreateNamedPipeW` + `NamedPipeServerStream(…, SafePipeHandle)` + `WaitForConnectionAsync` is a Windows-only execution path; on Linux the code compiles but never runs (compile and execution are different evidence). |
| N6 | **`GetImpersonationUserName` / elevation parity** | Not exercised at all on Linux. |
| N7 | **No hidden fallback** | Linux proves the *source* has no TCP/anonymous/public member and that the platform gate returns before anything is touched; it cannot prove that the Windows run opens no second endpoint. Only the operator's `netstat`/`Get-NetTCPConnection` observation does. |

## 4. Operator environment (fill in)

| Field | Value |
| --- | --- |
| Machine / VM | |
| Windows edition + build (`winver`) | |
| Operator account (same-user test) | |
| Foreign account used for denial testing | |
| Engine commit / build hash (`Rag.HistoricalLoader.Engine.dll`) | |
| Database + lock path used | |
| Companion assembly path (`RAG_HISTORICAL_LOADER_COMPANION_ASSEMBLY`) | |
| API base URI (`RAG_API_BASE_URL`) | |
| API collection GUID (`RAG_HISTORICAL_LOADER_API_COLLECTION_ID`) | |
| Date | |

## 5. Evidence rows (empty until observed — paste raw output)

| # | Claim | Exact procedure | Expected observation | Observed (paste) | Verdict |
| --- | --- | --- | --- | --- | --- |
| E1 | **Same-user connection succeeds** | Start `serve` as the operator; from a second process of the **same user**, connect to the derived pipe name and send the version-1 `hello` frame. | Connection established; `status: "ok"`; `hello` advertises the supported version, capabilities, and limits. | | ☐ pass ☐ fail |
| E2 | **ACL enforcement verified** | With `serve` running, `Get-Acl \\.\pipe\<endpoint>` (or Sysinternals `accesschk`) on the pipe object. | Owner = operator SID; **one** allow ACE (operator, full control); no `Everyone`/`Users`/`Authenticated Users`/`Anonymous`/`Network` entry; inheritance disabled. | | ☐ pass ☐ fail |
| E3 | **Foreign-user connection denied** | From a second logon session as a **different** user, attempt to connect to the same pipe name. | Access denied (`ERROR_ACCESS_DENIED`); **no** frame is served and the running instance keeps listening. | | ☐ pass ☐ fail |
| E4 | **Remote / non-local connection denied** | From another machine on the network (or `\\<host>\pipe\<endpoint>` / a remote client attempt), try to connect. | Connect fails; nothing is dispatched; the server never reaches the peer-verification step (the kernel already refused it). | | ☐ pass ☐ fail |
| E5 | **Fail-closed with no TCP/public/anonymous fallback** | During E1–E4, enumerate listeners: `Get-NetTCPConnection -State Listen` (per process) and `[System.IO.Directory]::GetFiles('\\.\pipe\')`. | **No** new TCP listener for the engine process; **one** pipe instance name, the derived endpoint; no second/alternate name; no anonymous or network-wide pipe. | | ☐ pass ☐ fail |
| E6 | **Collision fails closed** | With one `serve` instance holding the endpoint, start a second instance with a different database path but the **same** derived endpoint (same user, same machine installation). | The second instance fails closed (`command_conflict`), does **not** replace or delete the first endpoint, and does **not** invent another name; the first instance keeps serving. | | ☐ pass ☐ fail |
| E7 | **Production round trip smoke under `serve`** | With the composition variables set, run `serve`, then connect as the same user and issue `hello` and `get_state`. | Handshake answered over the real pipe; `get_state` returns the durable projection; the process exits cleanly on Ctrl+C after a drain. | | ☐ pass ☐ fail |

## 6. Known limits and residual risks (recorded, not hidden)

- **L1 — Same-user processes are inside the boundary.** The ACL bounds *other users*; it does not bound another
  process running as the operator (including a lower-integrity process of the same user). A mandatory-integrity
  label is not applied by this slice, so the engine does not claim protection against same-user malware.
- **L2 — Administrators can take ownership.** Members of `Administrators` are absent from the DACL, but on
  Windows they retain the ability to take ownership of the object. This is an operating-system property that this
  engine cannot remove; it is recorded here instead of being hidden behind a broader DACL.
- **L3 — Elevation parity on the client end.** The server verifies the peer's **user SID**. Verifying equal
  elevation is a `PipeOptions.CurrentUserOnly` property on the *client* side, so it is a recorded cross-slice
  obligation for the Unit 11 shell (the engine's client is not in this slice).
- **L4 — Ingestion below the boundary is not complete.** The production `serve` composition wires the real
  extractor and the real API client to the Windows Credential Manager, but the engine still has **no
  staged-content resolver** (`HistoricalApiClientOptions.ContentResolver`), so an actual `start` → upload fails
  closed with the client's own `content_resolver_missing` contract failure. E7 must therefore be limited to
  connect + `hello` + `get_state`. This is a recorded remaining obligation for the ingestion path, not a pipe
  defect, and this slice deliberately does not invent a resolver.
- **L5 — N5 is a design assumption until E1 passes.** The interop-created handle wrapped by
  `NamedPipeServerStream` is the one Windows-only wiring no Linux test can exercise; if E1 or E7 fails, that is
  the first place to look.

## 7. Sign-off

| Field | Value |
| --- | --- |
| Rows passed / failed | |
| Blocking findings | |
| Operator | |
| Date | |
| Notes | |
