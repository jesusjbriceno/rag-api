# Unit 11.prev-e — Windows named-pipe security evidence

**Slice:** 11.prev-e (Windows named-pipe transport + pipe security evidence).
**Status: EXECUTED on a real Windows host — run 1 FAILED, BF-1 FIXED, run 2 passes 5 of 7; SLICE STILL NOT\
CLOSED.** Both runs were made on 2026-09-22 on `DESKTOP-P7H1D96` (Windows 11 Home, build 26200), run 1 at
commit `1b89a6ea` and run 2 at that same commit **plus the BF-1 fix** (`Control/PeerPrefixStream.cs`,
`WindowsPipeTransport.cs`, and six new cases in `NamedPipeTransportTests.cs`).

| Run | E1 | E2 | E3 | E4 | E5 | E6 | E7 |
| --- | --- | --- | --- | --- | --- | --- | --- |
| **1 — as committed** | FAIL | pass | not executed | not executed | pass | pass | FAIL |
| **2 — after the BF-1 fix** | **pass** | **pass** | not executed | not executed | **pass** | **pass** | **pass** |

Run 1 found a blocking defect (BF-1) that run 2 fixes and re-verifies: the peer check used to deny every peer
— including the operator's own user — so the entire local control surface was unreachable on Windows. §5.1
keeps the run-1 root cause and the reproduction; §5.4 records run 2.

**11.prev-e is still NOT closed, and Unit 11 stays blocked.** Acceptance letter (b) requires foreign-user
denial and remote/non-local denial, and **E3 and E4 were never executed** on this host: it has no second
Windows account whose credentials the operator holds, and no second machine on the network. E2's descriptor is
strong supporting evidence for the DACL half of foreign-user denial, but it is not a connect attempt, and
nothing here proves `PIPE_REJECT_REMOTE_CLIENTS`.

> **The one sentence that matters:** the Linux suite proves the *seam*, the fail-closed decisions, and the
> early cut; it does **not** prove Windows ACL enforcement, same-user success, foreign-user denial, or
> remote-client denial — and the six new Linux cases prove only that the byte the peer check consumed reaches
> the host intact, never that Windows accepts the impersonation. Same-user success is now proven by execution
> (E1, E2, E5, E6, E7 pass on a real host); **foreign-user and remote denial are still unproven.**

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
| Peer identity | `WindowsNamedPipeTransport.TryVerifyPeer` | Every accepted peer is impersonated (`NamedPipeServerStream.RunAsClient`) and its token's user SID must equal this process's SID. **Windows only permits that impersonation once the peer has written to the pipe**, so the transport reads exactly one byte first and hands it back to the host unparsed through `Control/PeerPrefixStream.cs`; the byte it consumed is never lost. A peer that sends nothing inside the read deadline, or whose SID is not this process's, is **denied before any byte is parsed or dispatched** and before the host registers a connection slot. *(Run 1 of this slice called `RunAsClient` before any read and therefore denied every peer — the defect this row now describes the fix for; see §5.1 and §5.4.)* |
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

**Amendment after execution (2026-09-22).** N4 is now discharged *and refuted in production order*: on Windows the
peer check runs, but it runs `RunAsClient` **before any byte is read**, which the Windows named-pipe API refuses
with `ERROR_CANNOT_IMPERSONATE` (1368). `TryVerifyPeer` swallows that exception and reports "not the same user",
so the engine denies every peer — including the operator of the same user. N2 is therefore **false as
implemented**; see §5.1 for the execution evidence and §6 `L5`. N1 is partially discharged: the ACL is proven
correct by direct read of the object's security descriptor (§5, E2). N3 remains unproven (§5, E4).

**Resolution (run 2).** The ordering defect is fixed: the boundary now reads exactly one byte, verifies the
peer, and replays that byte through `PeerPrefixStream` (a portable type with six Linux cases in
`NamedPipeTransportTests.cs`). N2 is therefore **true as implemented** — same-user success is proven by E1, E2,
E5, E6 and E7 on a real host (§5.4). N1 has both a descriptor read (E2) and the fact that the ACL admits the
same user while a denied peer cannot reach the check at all. **N3 is still not proven**: E4 was never executed,
so `PIPE_REJECT_REMOTE_CLIENTS` remains a code fact plus an operator observation in §5/E5, not a denial.

## 4. Operator environment

| Field | Value |
| --- | --- |
| Machine / VM | `DESKTOP-P7H1D96` — physical workstation, 64-bit. Windows 8 / Server 2012 or later requirement: **satisfied**. |
| Windows edition + build (`winver`) | Microsoft Windows 11 Home, version `10.0.26200`, build `26200` (`Win32_OperatingSystem`) |
| Operator account (same-user test) | `DESKTOP-P7H1D96\jesus`, SID `S-1-5-21-404486456-3878587204-1188215715-1001`. Token privileges: `SeShutdownPrivilege`, `SeChangeNotifyPrivilege`, `SeUndockPrivilege`, `SeIncreaseWorkingSetPrivilege`, `SeTimeZonePrivilege`. **Standard user, not an administrator; `SeImpersonatePrivilege` is not held.** |
| Foreign account used for denial testing | **NONE — E3 NOT EXECUTED.** The host has no second account whose credentials the operator holds, and the operator is a standard user, so no account could be created for the test. |
| Engine commit / build hash (`Rag.HistoricalLoader.Engine.dll`) | commit `1b89a6ea538dd590935605327b022914f04e8a5d` (branch `feat/historical-ingestion-windows-probe`, `git status --porcelain` empty in run 1). **Run 1** (`Rag.HistoricalLoader.Engine.dll` SHA-256 `77af3bf826590bef6c1de96744175976095841c93e104771aa5d9011155864f5`, 227 328 bytes). **Run 2**: the same commit plus the BF-1 fix, uncommitted at run time (`Rag.HistoricalLoader.Engine.dll` SHA-256 `cdf0fee4036317c25e214c35be366a2b11a8c3c802b886c1d106121744e0a89c`); the fix touches `Control/WindowsPipeTransport.cs`, adds `Control/PeerPrefixStream.cs`, and adds six cases to `tests/Rag.HistoricalLoader.UnitTests/Control/NamedPipeTransportTests.cs`. |
| Database + lock path used | `C:\rag-evidence\e1\serve.sqlite` + `C:\rag-evidence\e1\historical-loader.lock`; E6 second instance used `C:\rag-evidence\e6\serve.sqlite` + `C:\rag-evidence\e6\historical-loader.lock` |
| Companion assembly path (`RAG_HISTORICAL_LOADER_COMPANION_ASSEMBLY`) | `C:\src\rag-api-windows-evidence\src\Rag.Companion\bin\Release\net10.0\win-x64\Rag.Companion.dll` (SHA-256 `bf1a14d8aa4986359b101af23c8fe12919a319324575b54edb84e2c66580fb76`). **Deviation from the run guide:** the guide's path omits the `win-x64` segment; `Rag.Companion.csproj` sets `<RuntimeIdentifier>win-x64</RuntimeIdentifier>`, so the produced path carries the RID directory. |
| API base URI (`RAG_API_BASE_URL`) | `http://127.0.0.1:8080` — loopback placeholder. No API server was started and no ingestion was attempted. |
| API collection GUID (`RAG_HISTORICAL_LOADER_API_COLLECTION_ID`) | `11111111-1111-1111-1111-111111111111` — synthetic, non-secret test value |
| Date | 2026-09-22, 07:43Z–07:55Z UTC (09:43–09:55 +02:00) |

**Additional recorded facts.**

| Fact | Value |
| --- | --- |
| Toolchain | .NET SDK `10.0.401`; `global.json` pins `10.0.111` with `rollForward: latestFeature`; `dotnet build Rag.sln --configuration Release` → **0 errors**, 43 warnings |
| Isolation | separate clone `C:\src\rag-api-windows-evidence` (no pre-existing personal changes; the operator's own working clone was not checked out) |
| Derived endpoint | `rag-historical-loader-v1-4edcadf3017c70a0914c7e6f96b7f488`, independently derived by the operator script from `MachineGuid 23a0dbcf-444d-4e99-b04d-68229a2602b9` + the operator SID, and matching the single pipe the engine created |
| Raw evidence directory | `C:\rag-evidence\` (`e0-meta.out.txt`, `e1\`, `e2\`, `e5\`, `e6\`, `e7\`, `e7g\`, `probe2-order.out.txt`, `probe-ctrlc.out.txt`, `client.ps1`) |

## 5. Evidence rows (observed, raw output)

| # | Claim | Exact procedure | Expected observation | Observed (paste) | Verdict |
| --- | --- | --- | --- | --- | --- |
| E1 | **Same-user connection succeeds** | Start `serve` as the operator; from a second process of the **same user**, connect to the derived pipe name and send the version-1 `hello` frame. | Connection established; `status: "ok"`; `hello` advertises the supported version, capabilities, and limits. | `client_user=DESKTOP-P7H1D96\jesus`<br>`client_sid=S-1-5-21-404486456-3878587204-1188215715-1001`<br>`operation=hello`<br>`CLIENT_ERROR: System.Management.Automation.MethodInvocationException : Excepción al llamar a "Write" con los argumentos "3": "Canalización interrumpida."`<br><br>The engine connected and then closed the connection **before a frame was served**; the client's first `Write` fails with `ERROR_BROKEN_PIPE` (109). The running instance stays alive and keeps listening. Root cause in §5.1. | **☑ pass (run 2)** — ☑ fail in run 1 |

**Run 2 (after the BF-1 fix) — same procedure, same host, fixed build** (`Rag.HistoricalLoader.Engine.dll` SHA-256 `cdf0fee4036317c25e214c35be366a2b11a8c3c802b886c1d106121744e0a89c`):
<br>`client_user=DESKTOP-P7H1D96\jesus`
<br>`client_sid=S-1-5-21-404486456-3878587204-1188215715-1001`
<br>`--- hello ---`
<br>`{"protocol_version":1,"request_id":"bab8326f-ccf7-4b3d-ad4e-36746d798805","status":"ok","payload":{"supported_versions":[1],"capabilities":["pause","resume","document_pages","event_feed"],"installation_id":"7a37977e-ce54-45f4-937b-0b97bdb6b3aa","engine_instance_id":"engine-instance-40e6fc2d3dd14fc6adfe4094cadb5cf9","limits":{"max_frame_bytes":1048576,"max_json_depth":16,"max_page_size":100}}}`
<br><br>Connection established and the version-1 handshake answered over the real pipe: `status: "ok"`, the supported protocol version, the four advertised capabilities, and the recorded limits. The defect that made this impossible in run 1 is described in §5.1 and its fix in §5.4. |
| E2 | **ACL enforcement verified** | With `serve` running, `Get-Acl \\.\pipe\<endpoint>` (or Sysinternals `accesschk`) on the pipe object. | Owner = operator SID; **one** allow ACE (operator, full control); no `Everyone`/`Users`/`Authenticated Users`/`Anonymous`/`Network` entry; inheritance disabled. | `Get-Acl` **cannot read a named-pipe object** (`InvalidOperationException`, Win32 error 87) and Sysinternals `accesschk64.exe` is not installed, so the descriptor was read directly: `CreateFileW("\\.\pipe\<endpoint>", READ_CONTROL)` → handle, then `GetSecurityInfo(handle, SE_KERNEL_OBJECT, OWNER|GROUP|DACL)` and `ConvertSecurityDescriptorToStringSecurityDescriptorW`. Raw result:<br>`SDDL=O:S-1-5-21-404486456-3878587204-1188215715-1001G:S-1-5-21-404486456-3878587204-1188215715-1001D:P(A;;0x1f019f;;;S-1-5-21-404486456-3878587204-1188215715-1001)`<br>`Owner=S-1-5-21-404486456-3878587204-1188215715-1001`<br>`Group=S-1-5-21-404486456-3878587204-1188215715-1001`<br>`ControlFlags=DiscretionaryAclPresent, DiscretionaryAclProtected, SelfRelative`<br>`DiscretionaryAclProtected=True`<br>`DiscretionaryAclAutoInherited=False`<br>`ACE[0] AceType=AccessAllowed AceFlags=None AccessMask=0x001F019F SID=S-1-5-21-404486456-3878587204-1188215715-1001`<br>`AceCount=1`<br><br>Owner and group are the operator SID; the DACL is protected (`D:P`) with **exactly one** allow ACE for the operator (`0x1F019F` = `PipeAccessRights.FullControl`); no `Everyone`/`Users`/`Authenticated Users`/`Anonymous`/`Network` ACE exists and no ACE is inherited. The same handle proves the **kernel ACL admits the operator**: the denial in E1 happens above the ACL, inside the engine. | **☑ pass** |

**Run 2 re-verified the identical descriptor on the fixed build** (`e2-acl.out.txt` of run 2):
`Group=S-1-5-21-404486456-3878587204-1188215715-1001`, `ControlFlags=DiscretionaryAclPresent, DiscretionaryAclProtected, SelfRelative`, `DiscretionaryAclProtected=True`, `DiscretionaryAclAutoInherited=False`, `ACE[0] AceType=AccessAllowed AceFlags=None AccessMask=0x001F019F SID=S-1-5-21-404486456-3878587204-1188215715-1001`, `AceCount=1` — byte-for-byte the same single-ACE owner-only DACL, so the fix changed no security descriptor. |
| E3 | **Foreign-user connection denied** | From a second logon session as a **different** user, attempt to connect to the same pipe name. | Access denied (`ERROR_ACCESS_DENIED`); **no** frame is served and the running instance keeps listening. | **NOT EXECUTED — no foreign account was available.** The host has no second account whose credentials the operator holds, and the operator is a standard user, so none could be created. The procedure's `runas /user:<other> powershell` step could not be performed. What *is* recorded instead: the E2 descriptor grants access to the operator SID only, so a foreign user has no allow ACE at all — but that is an ACL reading, **not** a foreign-user connect attempt, and it is not claimed as E3 evidence. | **☐ not executed** |
| E4 | **Remote / non-local connection denied** | From another machine on the network (or `\\<host>\pipe\<endpoint>` / a remote client attempt), try to connect. | Connect fails; nothing is dispatched; the server never reaches the peer-verification step (the kernel already refused it). | **NOT EXECUTED — no second machine on the network was available.** No remote named-pipe connect attempt was made, so `PIPE_REJECT_REMOTE_CLIENTS` remains **unproven on this host**. | **☐ not executed** |
| E5 | **Fail-closed with no TCP/public/anonymous fallback** | During E1–E4, enumerate listeners: `Get-NetTCPConnection -State Listen` (per process) and `[System.IO.Directory]::GetFiles('\\.\pipe\')`. | **No** new TCP listener for the engine process; **one** pipe instance name, the derived endpoint; no second/alternate name; no anonymous or network-wide pipe. | Baseline before `serve` (`label=baseline-before-serve`): `rag_pipes=0`, `total_tcp_listeners=55`, listeners owned by the engine PID = 0.<br>During `serve` (engine PID 2072, `label=e5-during-serve`):<br>`total_pipes=512`<br>`rag_pipes=1`<br>`  \\.\pipe\rag-historical-loader-v1-4edcadf3017c70a0914c7e6f96b7f488`<br>`total_tcp_listeners=54`<br>`listeners_owned_by_engine_pid_2072=0`<br>`(no dotnet/Rag process owns any TCP listener)`<br><br>Exactly one pipe appeared, and it is the derived endpoint — no second or alternate name. The engine process owns **zero** TCP listeners, and no listener appeared during the run (the total fell from 55 to 54; the single dotnet-owned loopback listener seen at baseline was an unrelated build server). No anonymous or network-wide pipe exists. | **☑ pass** |

**Run 2 (fixed build, engine PID 36256):** `total_pipes=464`, `rag_pipes=1` (`\.\pipe\rag-historical-loader-v1-4edcadf3017c70a0914c7e6f96b7f488`), `total_tcp_listeners=52`, `listeners_owned_by_engine_pid_36256=0`, and no dotnet/Rag process owning any TCP listener. After the E6 collision attempt and after shutdown, `rag_pipes` returned to exactly 1 and then to 0 — never a second name. |
| E6 | **Collision fails closed** | With one `serve` instance holding the endpoint, start a second instance with a different database path but the **same** derived endpoint (same user, same machine installation). | The second instance fails closed (`command_conflict`), does **not** replace or delete the first endpoint, and does **not** invent another name; the first instance keeps serving. | Second instance (`--db C:\rag-evidence\e6\serve.sqlite`), exit code captured from the process handle:<br>`second_serve_exit_code=4`<br>`--- stdout ---` (empty)<br>`--- stderr ---`<br>`serve: this engine could not own its pipe endpoint.`<br><br>Afterwards: `rag_pipes=1` (still exactly the derived endpoint) and the first instance is still alive and listening. The second instance neither replaced, deleted, nor renamed the endpoint.<br><br>**Deviation from the expected text:** the exit code matches `ConflictExitCode = 4`, but the emitted message is the human sentence from the *pipe-ownership* branch, not the literal token `command_conflict`, and the collision surfaced there rather than at the installation-lock branch — because the second invocation used a different `--db` directory and therefore a different lock path. Both branches share exit code 4. Recorded as an observability deviation, not a security failure. | **☑ pass** (literal code text differs; see note) |

**Run 2 (fixed build):** `second_serve_exit_code=4`, stderr `serve: this engine could not own its pipe endpoint.`, `rag_pipes=1` afterwards and the first instance still alive. Identical outcome. |
| E7 | **Production round trip smoke under `serve`** | With the composition variables set, run `serve`, then connect as the same user and issue `hello` and `get_state`. | Handshake answered over the real pipe; `get_state` returns the durable projection; the process exits cleanly on Ctrl+C after a drain. | **Round trip — FAILED, same cause as E1:**<br>`operation=hello`<br>`CLIENT_ERROR: … MethodInvocationException : Excepción al llamar a "Write" con los argumentos "3": "Canalización interrumpida."`<br>`operation=get_state`<br>`CLIENT_ERROR: … MethodInvocationException : Excepción al llamar a "Write" con los argumentos "3": "Canalización interrumpida."`<br>Neither `hello` nor `get_state` was answered; no durable projection was returned.<br><br>**Shutdown half — PASSED.** Because a synthetic `CTRL_C_EVENT` is not deliverable from this harness (§5.2), the shutdown was driven with `CTRL_BREAK_EVENT`, which .NET surfaces through the **same** `Console.CancelKeyPress` handler the engine wires in `ControlServeCommand.RunAsync` (`cancel.Cancel = true; shutdown.Cancel()`). Raw: `AttachConsole(17572)=True`, `generating ctrlEvent=1`, then an independent P/Invoke watcher on the process handle: `observed_pid=17572 exit_code=0`. After exit: `rag_pipes=0`, engine stdout and stderr both empty, lock released. A process killed by the console control default handler would exit `0xC000013A`, so `exit_code=0` proves the handler ran, the drain completed, and `ServeAsync` returned 0. Drain completed in ≈2.0–2.3 s (measured on the instrumented runs `e7d`/`e7e`/`e7f`), far inside the 30 s `ControlServiceOptions.DrainTimeout`. | **☑ pass (run 2)** — ☑ fail in run 1 |

**Run 2 — the round trip that run 1 could not reach.** The run guide's minimal client opens a **new connection per
call**, which cannot satisfy the per-connection handshake gate (§5.4 finding F-1), so run 2 used a client that
keeps one connection and sends both operations in order. Raw (`e7b-roundtrip.out.txt`):
<br>`--- hello (same connection) ---`
<br>`{"protocol_version":1,"request_id":"3b66be87-bb3f-42dd-8f79-b217d92b98f2","status":"ok","payload":{…"limits":{"max_frame_bytes":1048576,"max_json_depth":16,"max_page_size":100}}}`
<br>`--- get_state (same connection) ---`
<br>`{"protocol_version":1,"request_id":"7578768d-b908-421a-bac6-0982d9838765","status":"ok","payload":{"desired_state":"","observed_state":"","inventory":{"completeness":"none","candidate_count":0,"candidate_bytes":0},"document_counts":{},"engine_instance_id":"engine-instance-40e6fc2d3dd14fc6adfe4094cadb5cf9","event_high_water_mark":"0"}}`
<br><br>`get_state` returned the **durable projection** rather than a rejection: on a store with no run it reports
`completeness: "none"`, zero candidates, no document counts and event high-water mark `0` — the honest empty
projection, not an invented one. The `content_resolver_missing` limit (L4) is untouched by this test: it covers
`start` → upload, which E7 deliberately does not exercise.

**Run 2 shutdown half:** `observed_pid=36256 exit_code=0`, `rag_pipes=0` afterwards, engine stdout and stderr both
empty, signal delivered as `CTRL_BREAK_EVENT` for the reason recorded in §5.2. A default-handler kill would have
exited `0xC000013A`, so `0` again proves the `Console.CancelKeyPress` handler ran, the drain completed and
`ServeAsync` returned 0. |

### 5.1 Root cause of run 1's E1 and E7 — execution evidence, not inference

`WindowsNamedPipeTransport.TryVerifyPeer` impersonates the peer:

```csharp
var sameUser = false;
instance.RunAsClient(() =>
{
    using var client = WindowsIdentity.GetCurrent();
    sameUser = string.Equals(client.User?.Value, _userSid, StringComparison.Ordinal);
});
return sameUser;
```

and the accept loop calls it **before any byte is read**:

```csharp
// Peer identity first: a foreign, remote, or unverifiable peer is denied before any byte is read
// and before the host registers a connection slot.
if (!TryVerifyPeer(current)) { DisposeInstance(current); ... }
```

On a **byte-mode** named pipe, `ImpersonateNamedPipeClient` (which `RunAsClient` calls) refuses to impersonate
until the server has read data from that pipe. The failure is swallowed by `catch (Exception) { return false; }`,
so it is indistinguishable from a genuine foreign peer.

Proven with a minimal two-mode reproduction (`C:\rag-evidence\probe2`) on the same host, same user, same
byte-mode pipe created the same way, differing **only** in whether one byte is read before `RunAsClient`:

```
=== order=before (impersonate immediately — the engine's order) ===
server: listening as DESKTOP-P7H1D96\jesus
server: peer connected (order=before)
server: RunAsClient FAILED: System.IO.IOException | HResult=0x80070558 | Message=No se puede suplantar
        usando una canalización dada hasta que se hayan leído los datos de esa canalización.
client: connected as DESKTOP-P7H1D96\jesus
Unhandled exception. System.IO.IOException: Pipe is broken.
   at System.IO.Pipes.PipeStream.WriteCore(ReadOnlySpan`1 buffer)

=== order=after (read one byte first, then impersonate) ===
server: listening as DESKTOP-P7H1D96\jesus
server: peer connected (order=after)
server: read one byte from the peer = 0x41
server: impersonated peer = DESKTOP-P7H1D96\jesus
server: RunAsClient SUCCEEDED
client: connected as DESKTOP-P7H1D96\jesus
client: wrote one byte
```

`HResult=0x80070558` is Win32 `1368` (`ERROR_CANNOT_IMPERSONATE`). The `order=before` client reproduces E1
byte-for-byte: **"Pipe is broken"** on its first write.

Three consequences are established by execution, not by reading:

1. **`SeImpersonatePrivilege` is not the blocker.** The operator is a standard user without that privilege, and
   the `order=after` run impersonated the same-user peer successfully. The privilege is not required to
   impersonate a same-user client on a byte-mode pipe.
2. **The ACL is not the blocker.** The E2 probe opened the same pipe with `READ_CONTROL` and read its descriptor.
3. **The engine denies every peer, including the operator.** With no read before `RunAsClient`, the check cannot
   succeed, so `hello` and `get_state` are unreachable on a real Windows host — the whole local control surface is
   dead, not merely one operation.

This is precisely the outcome `L5` below predicted ("if E1 or E7 fails, that is the first place to look"), and it
is the reason `N2` in §3 is now recorded as **false as implemented**.

### 5.2 Deviation: how the shutdown signal was delivered

The procedure asks for **Ctrl+C in the serve console**. In this harness the console is owned by a chain of
MSYS/mintty → PowerShell → engine, and a synthetic `CTRL_C_EVENT` is accepted but delivered to nobody:

```
AttachConsole(<pid>)=True
caller ignores CTRL+C
generating ctrlEvent=0
GenerateConsoleCtrlEvent=True
→ the target stays alive for the full 45 s / 60 s window in every attempt
```

The same code path with `CTRL_BREAK_EVENT` (`ctrlEvent=1`) does deliver — verified on a throwaway probe
(`probe: cancel-key event received` / `probe: cancelled, draining` / `probe: exiting 0`) and then on the engine
itself (`exit_code=0`). .NET raises `Console.CancelKeyPress` for both keys, and the engine's handler does not
distinguish them, so the drain/exit path exercised is the same one a human Ctrl+C exercises. A final
human-typed Ctrl+C on a normal console window remains a recommended confirmation; it was not possible to
synthesise one here.

### 5.3 Raw artifact inventory (`C:\rag-evidence\`)

| Path | Content |
| --- | --- |
| `e0-meta.out.txt` | OS, operator identity/SID, `MachineGuid`, toolchain, git state, Engine/Companion hashes, local accounts |
| `e1\e1-hello.out.txt`, `e1\e1-run.ps1` | E1 raw client transcript |
| `e2\e2-acl.out.txt`, `e2\e2-acl.console.txt` | E2 raw security-descriptor transcript |
| `e5-baseline.out.txt`, `e5\e5-during.out.txt` | E5 baseline and during-serve captures |
| `e6\e6-console.txt`, `e6\serve.err.txt`, `e6\exitcode.txt`, `e6\e6-pipes.txt` | E6 raw stdout/stderr/exit code and post-collision pipe list |
| `e7\e7-roundtrip.out.txt`, `e7\e7-shutdown-ctrlc.txt`, `e7\e7-shutdown-ctrlbreak.txt`, `e7\post-shutdown-pipes.txt` | E7 round-trip failure, both shutdown-signal attempts, post-shutdown pipe list |
| `e7g\`, `e7f\`, `e7e\`, `e7d\`, `e7b\` | Instrumented shutdown runs; `e7g\exitcode.txt.observer` holds the watcher-recorded `0` |
| `e1\operator-privileges.txt` | Operator token privileges (`SeImpersonatePrivilege` absent) |
| `probe2-order.out.txt` | §5.1 root-cause reproduction, both orders |
| `probe-ctrlc.out.txt` | §5.2 signal-delivery reproduction |

### 5.4 Run 2 — the BF-1 fix and the re-run

**What was wrong, in one line.** `TryVerifyPeer` impersonated the peer before any byte had been read; a byte-mode
named pipe refuses that with `ERROR_CANNOT_IMPERSONATE` (1368); the exception was swallowed as "different user";
every peer was denied, including the operator's. §5.1 keeps the run-1 reproduction that proves it.

**What changed.**

| File | Change |
| --- | --- |
| `src/Rag.HistoricalLoader.Engine/Control/PeerPrefixStream.cs` | **New.** A portable duplex stream that yields bytes already read from a peer before the peer's own stream: in order, once each, nothing invented. It refuses seeking, and it does not own the inner stream because the transport owns every accepted instance's lifetime. |
| `src/Rag.HistoricalLoader.Engine/Control/WindowsPipeTransport.cs` | The accept loop now prepares the next instance, then reads **exactly one byte** under the recorded `ReadDeadline`, then verifies the peer, and only then dispatches — handing the host a `PeerPrefixStream` so the byte the check consumed is replayed unparsed. `TryVerifyPeer`'s contract comment and the class-level control list were corrected: the guarantee is "no byte is parsed and no request is dispatched before the SID check", which is what the code does, instead of the previous and false "before any byte is read". |
| `tests/Rag.HistoricalLoader.UnitTests/Control/NamedPipeTransportTests.cs` | **Six new cases** pinning the portable half of the fix. |

**Test evidence.** RED first: the six cases were added before the type existed and the project failed to compile with
one `CS0246` per case. Then GREEN: `6/6` pass. The full unit-test project on the fixed build reports **425 total,
422 passed, 3 failed**, and those three are the same three that fail on the **pristine commit**: two assert
`Assert.False(ControlPipeTransportFactory.IsPlatformSupported)` — a Linux-only assertion — and the third calls
`Directory.CreateSymbolicLink`, which a standard Windows user without `SeCreateSymbolicLinkPrivilege` cannot do.
They were proven pre-existing by stashing the fix, re-running them, and restoring it, so **no regression is
claimed or hidden**. The suite is designed for Linux; running it on Windows is a verification aid, not the
evidence.

**What the six Linux cases do NOT prove.** They prove the consumed byte reaches the host intact. They cannot prove
that Windows accepts the impersonation, because that is exactly the platform behaviour they cannot reach — E1 and
E7 on the real host are what prove it.

#### 5.4.1 Findings from run 2

- **F-1 — the protocol has a per-connection handshake gate, and the run guide's client cannot satisfy it.**
  `Control/Session.cs` refuses any operation other than `hello` on a connection whose `hello` has not yet
  succeeded, with `malformed_request`. The guide's `Invoke-LoaderRequest` opens a **new connection per call**, so
  `get_state` on its own connection is refused — correctly, by design — and E7 as written in the run guide can
  never pass, however healthy the transport is. This is a **gap in the guide/client**, not a product defect: run 2
  used a client that keeps one connection and sends `hello` then `get_state` in order (§5's E7 row). The gate is
  also good news for the security story: a fresh connection can never inherit a previous peer's handshake.
- **F-2 — 3 of the Linux suite's 425 cases cannot pass on Windows**, for the two unrelated platform reasons above.
  Recorded so a future Windows run is not mistaken for a regression.

#### 5.4.2 Run-2 artifact inventory (`C:\rag-evidence\r2\`)

| Path | Content |
| --- | --- |
| `endpoint.txt` | The derived endpoint, independently recomputed by the operator script and equal to the single pipe observed |
| `e1.ps1`, `e1-hello.out.txt` | E1 raw client transcript on the fixed build |
| `e7b.ps1`, `e7b-roundtrip.out.txt` | E7 `hello` + `get_state` on one connection |
| `e2-acl.out.txt` | E2 re-verified descriptor |
| `e5.out.txt` | E5 re-verified pipe/TCP enumeration |
| `e6-exitcode.txt`, `e6-serve.err.txt` | E6 re-verified collision (`4`, human sentence on stderr) |
| `watch.console.txt`, `exitcode.txt.observer`, `signal.status.txt` | E7 shutdown: the independent watcher's `exit_code=0` and the signal record |
| `serve.out.txt`, `serve.err.txt` | Engine output across the whole run (both empty) |

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
- **L5 — CLOSED by run 2. The interop handle, the managed wrapper and the peer check all work; run 1's peer
  check did not.** Run 1 established that `CreateNamedPipeW` + `NamedPipeServerStream(…, SafeHandle)` +
  `WaitForConnectionAsync` accepts connections, enforces the intended owner-only ACL, opens exactly one pipe and
  opens no TCP listener (so N5 was discharged), and that `TryVerifyPeer` was the one broken part: it called
  `RunAsClient` before any byte had been read, which a byte-mode pipe refuses with `ERROR_CANNOT_IMPERSONATE`
  (1368), and the refusal was swallowed into "different user". Run 2 fixed exactly that ordering and re-ran the
  gate: E1, E2, E5, E6 and E7 pass (§5.4), so the peer check is now a working control and this entry is closed
  rather than carried.
- **L6 — E3 and E4 remain unproven on this host.** No foreign account and no second machine were available, so
  foreign-user denial and `PIPE_REJECT_REMOTE_CLIENTS` are still unverified by execution. E2's descriptor read is
  strong *supporting* evidence for the DACL half of foreign-user denial, but it is not a connect attempt. **This is
  now the only thing standing between the slice and acceptance letter (b)**, because E1, E2, E5, E6 and E7 all pass
  on the fixed build (§5.4).
- **L7 — the peer check now consumes one byte before it decides.** The security-relevant property is unchanged —
  nothing is parsed and nothing is dispatched for an unverified peer, and a denied connection's byte is discarded
  — but the boundary is no longer literally byte-untouched before verification. A peer that connects and sends
  nothing is denied when the `ReadDeadline` (10 s) expires, which is the fail-closed direction; while that read is
  outstanding the accept loop is not blocked, because the next instance is prepared first.

## 7. Sign-off

| Field | Value |
| --- | --- |
| Rows passed / failed | **Run 1:** E2 pass · E5 pass · E6 pass · E1 FAIL · E7 FAIL · E3/E4 not executed → 3 pass, 2 fail, 2 not executed. **Run 2 (after the BF-1 fix):** E1 pass · E2 pass · E5 pass · E6 pass · E7 pass · E3/E4 not executed → **5 pass, 0 fail, 2 not executed** |
| Blocking findings | **None open.** BF-1 was blocking and is **fixed and re-verified** (§5.4): the peer check denied every peer because it impersonated before reading; it now reads one byte, verifies, and replays that byte through `PeerPrefixStream`. **E3 (foreign-user denial) and E4 (remote denial) are still not executed**, and acceptance letter (b) requires them, so the slice is not closed — but that is an environment gap, not a defect. |
| Non-blocking findings | **NF-1:** `Get-Acl` cannot read a named-pipe object and no Sysinternals tooling is present, so reading the pipe DACL requires `CreateFileW(READ_CONTROL)` + `GetSecurityInfo(SE_KERNEL_OBJECT)`; the run guide's `Get-Acl` instruction does not work as written and its fallback is uninstalled. **NF-2:** the E6 collision emits a human sentence rather than the literal `command_conflict` token, and surfaces at the pipe-ownership branch because the second instance used a different lock path. **NF-3:** the run guide's `RAG_HISTORICAL_LOADER_COMPANION_ASSEMBLY` path omits the `win-x64` RID segment. **NF-4:** a synthetic `CTRL_C_EVENT` cannot be delivered from this harness (§5.2). **F-1:** the version-1 protocol requires `hello` to succeed on the **same connection** before any other operation (`Control/Session.cs`), which the run guide's one-connection-per-call client cannot satisfy — so E7 as written can never pass; a same-connection client does (§5.4.1). **F-2:** 3 of the Linux suite's 425 cases fail on Windows for unrelated platform reasons (two Linux-only assertions, one symlink privilege), proven pre-existing by a stashed-tree re-run. |
| Operator | `DESKTOP-P7H1D96\jesus` (standard user, SID `S-1-5-21-404486456-3878587204-1188215715-1001`), on `DESKTOP-P7H1D96`, Windows 11 Home build 26200 |
| Date | 2026-09-22 (run 1: 07:43Z–07:55Z UTC; run 2: 11:22Z–11:45Z UTC) |
| Notes | Run 1: commit `1b89a6ea538dd590935605327b022914f04e8a5d`, Engine DLL SHA-256 `77af3bf826590bef6c1de96744175976095841c93e104771aa5d9011155864f5`. Run 2: the same commit **plus the BF-1 fix** (uncommitted at the time of the run), Engine DLL SHA-256 `cdf0fee4036317c25e214c35be366a2b11a8c3c802b886c1d106121744e0a89c`. Branch `feat/historical-ingestion-windows-probe`; separate clone at `C:\src\rag-api-windows-evidence`; the operator's working clone was not modified. Every server was stopped with a console control event (no forced kill), all engine and probe processes are confirmed gone, no `rag-historical-loader-v1*` pipe remains and the test locks are released. **11.prev-e is still NOT closed and Unit 11 stays blocked:** E3 and E4 were never executed, so acceptance letter (b) is unmet even though the transport now demonstrably serves the operator. Do not tick the acceptance-evidence item, and do not start Unit 11, until a foreign account and a second machine exist to run E3 and E4. |
