# Unit 11.prev-e — Windows operator runbook (E1–E7)

**Purpose.** The procedure an operator runs on a real Windows host to produce the evidence that
`docs/historical-ingestion-rebaseline/unit-11-prev-windows-pipe-security.md` records. Linux proves the seam, the
fail-closed decisions and the early cut; it cannot prove ACL enforcement, same-user success, foreign-user
denial or remote-client denial. Those four facts exist only when this runbook is executed and its raw output is
pasted into that record.

**Read this before writing a client.** The version-1 protocol has a **per-connection handshake gate**
(`Control/Session.cs`): `hello` must succeed **on the same connection** before any other operation is delegated,
and any other operation on a fresh connection is rejected with `malformed_request`. A client that opens one
connection per call — which is the obvious way to write one — can therefore never reach `get_state`,
`get_documents`, `get_events`, `start`, `pause` or `resume`. Use the same-connection client in §3. The gate is
deliberate: a fresh connection can never inherit a previous peer's handshake.

**Prerequisites, none of which can be substituted.**

| Requirement | Why |
| --- | --- |
| Windows 8 / Server 2012 or later (`winver`) | `PIPE_REJECT_REMOTE_CLIENTS` needs that kernel |
| A clean, separate working copy at the recorded commit | Never check out over a copy with personal changes |
| **Two** Windows accounts: the operator, and a different one | E3 needs a second logon session |
| **A second machine on the network** | E4 needs a non-local connect attempt |
| A console you can press Ctrl+C in | E7's drain is observed through the console control path |

E3 and E4 are the two the environment most often lacks. Without them the gate cannot be closed, however green
everything else is: acceptance letter (b) requires foreign-user denial and remote/non-local denial.

---

## 1. Prepare the working copy

```powershell
git clone https://github.com/jesusjbriceno/rag-api C:\src\rag-api-windows-evidence
cd C:\src\rag-api-windows-evidence
git checkout feat/historical-ingestion-windows-probe
git rev-parse HEAD      # must equal the commit this run reports
winver                  # Windows 8 / Server 2012 or later
dotnet --info
dotnet build Rag.sln --configuration Release
```

Create a raw-evidence directory outside the repository and never edit what you save into it:

```powershell
New-Item -ItemType Directory -Force C:\rag-evidence\e1 | Out-Null
```

## 2. Configure the run, and derive the endpoint

`Rag.Companion.csproj` sets `<RuntimeIdentifier>win-x64</RuntimeIdentifier>`, so the built assembly carries a
RID segment in its path. A path without `win-x64` will fail to load.

```powershell
$env:RAG_HISTORICAL_LOADER_COMPANION_ASSEMBLY = "C:\src\rag-api-windows-evidence\src\Rag.Companion\bin\Release\net10.0\win-x64\Rag.Companion.dll"
$env:RAG_API_BASE_URL = "http://127.0.0.1:8080"
$env:RAG_HISTORICAL_LOADER_API_COLLECTION_ID = "<a valid test GUID>"
dotnet run --project src\Rag.HistoricalLoader.Engine -c Release -- serve --db C:\rag-evidence\e1\serve.sqlite
```

Do not put secrets in this guide or in any capture. These `hello` / `get_state` checks need no credential.

The endpoint is derived, never chosen. Compute it in a second console and confirm a **single** pipe exists:

```powershell
$guid = (Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Cryptography').MachineGuid
$sid  = [System.Security.Principal.WindowsIdentity]::GetCurrent().User.Value
$bytes = [System.Text.Encoding]::UTF8.GetBytes("$guid`n$sid")
$digest = [System.Security.Cryptography.SHA256]::Create().ComputeHash($bytes)
$hex = -join ($digest[0..15] | ForEach-Object { $_.ToString('x2') })
$endpoint = "rag-historical-loader-v1-$hex"
$endpoint
```

## 3. The client

Two functions. `Invoke-LoaderRequest` opens one connection and sends **one** operation, so it is only valid for
`hello`. `Invoke-LoaderSequence` keeps one connection and sends the operations in order, which is the required
form for `hello` followed by anything else.

```powershell
function Read-Exact([System.IO.Stream]$stream, [byte[]]$buffer) {
  $offset = 0
  while ($offset -lt $buffer.Length) {
    $read = $stream.Read($buffer, $offset, $buffer.Length - $offset)
    if ($read -eq 0) { throw 'Pipe closed before a complete frame was received.' }
    $offset += $read
  }
}

function Send-LoaderFrame([System.IO.Stream]$pipe, [string]$operation) {
  $request = @{ protocol_version = 1; request_id = [guid]::NewGuid().ToString(); operation = $operation } | ConvertTo-Json -Compress
  $payload = [System.Text.Encoding]::UTF8.GetBytes($request)
  $length = [BitConverter]::GetBytes([uint32]$payload.Length)
  $pipe.Write($length, 0, $length.Length); $pipe.Write($payload, 0, $payload.Length); $pipe.Flush()
  $header = New-Object byte[] 4; Read-Exact $pipe $header
  $responseLength = [BitConverter]::ToUInt32($header, 0)
  $response = New-Object byte[] $responseLength; Read-Exact $pipe $response
  [System.Text.Encoding]::UTF8.GetString($response)
}

# One connection, one operation. Valid for 'hello' only: any other operation on a fresh connection is refused
# with malformed_request because the handshake is per connection.
function Invoke-LoaderRequest([string]$name, [string]$operation, [string]$server = '.') {
  $pipe = [System.IO.Pipes.NamedPipeClientStream]::new(
    $server, $name, [System.IO.Pipes.PipeDirection]::InOut, [System.IO.Pipes.PipeOptions]::None)
  try { $pipe.Connect(5000); Send-LoaderFrame $pipe $operation } finally { $pipe.Dispose() }
}

# One connection, operations in order: 'hello' first, then whatever the test needs.
function Invoke-LoaderSequence([string]$name, [string[]]$operations, [string]$server = '.') {
  $pipe = [System.IO.Pipes.NamedPipeClientStream]::new(
    $server, $name, [System.IO.Pipes.PipeDirection]::InOut, [System.IO.Pipes.PipeOptions]::None)
  try {
    $pipe.Connect(5000)
    foreach ($operation in $operations) { Send-LoaderFrame $pipe $operation }
  } finally { $pipe.Dispose() }
}
```

## 4. Run E1–E7

| # | Do | Success looks like |
| --- | --- | --- |
| E1 | With `serve` active, same user: `Invoke-LoaderRequest $endpoint 'hello'` | Connection and a JSON response with `status: "ok"`, the supported version, capabilities and limits |
| E2 | Read the pipe's descriptor (see below) | Owner = the operator SID; **one** allow ACE for that SID; no `Everyone` / `Users` / `Authenticated Users` / `Anonymous` / `Network`; no inheritance |
| E3 | From another logon session as a **different** user, repeat the client | `ERROR_ACCESS_DENIED`; no frame served; the first instance keeps listening |
| E4 | From another machine, import the functions and call `Invoke-LoaderRequest $endpoint 'hello' '<operator-host>'` | The connection fails. Record the literal text: access denied or file not found are both acceptable |
| E5 | While `serve` is active: `Get-NetTCPConnection -State Listen` and `Get-ChildItem \\.\pipe\` filtered on `rag-historical-loader-v1*` | No new TCP listener for the engine process; **one** pipe, the derived endpoint |
| E6 | In a third console, start a second `serve` with a different `--db` | Fail closed: exit code **4**. The first instance keeps serving and no second pipe appears |
| E7 | `Invoke-LoaderSequence $endpoint @('hello','get_state')`, then Ctrl+C in the `serve` console | Both frames answered — `get_state` returns the durable projection (on an empty store: `completeness: "none"`, zero counts, `event_high_water_mark: "0"`) — and a clean stop with exit code 0 after the drain |

**E2 — `Get-Acl` does not work on a named pipe** (`InvalidOperationException`, Win32 error 87), and Sysinternals
`accesschk64.exe` may not be installed. Read the descriptor directly instead:

```powershell
Add-Type -TypeDefinition @'
using System; using System.Collections.Generic; using System.Runtime.InteropServices; using System.Security.AccessControl;
public static class PipeAcl {
  [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)] static extern IntPtr CreateFileW(string p, uint a, uint s, IntPtr sa, uint c, uint f, IntPtr t);
  [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
  [DllImport("advapi32.dll", SetLastError=true)] static extern uint GetSecurityInfo(IntPtr h, int o, uint i, out IntPtr ow, out IntPtr g, out IntPtr d, out IntPtr sc, out IntPtr sd);
  [DllImport("advapi32.dll", SetLastError=true)] static extern bool ConvertSecurityDescriptorToStringSecurityDescriptorW(IntPtr sd, uint rev, uint i, out IntPtr s, out uint len);
  [DllImport("kernel32.dll")] static extern IntPtr LocalFree(IntPtr h);
  public static List<string> Read(string fullPipe) {
    var lines = new List<string>();
    IntPtr h = CreateFileW(fullPipe, 0x00020000, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);          // READ_CONTROL, share R|W, OPEN_EXISTING
    if (h == new IntPtr(-1)) { lines.Add("CreateFileW failed " + Marshal.GetLastWin32Error()); return lines; }
    try {
      IntPtr ow, g, d, sc, sd;
      uint rc = GetSecurityInfo(h, 6, 0x7, out ow, out g, out d, out sc, out sd);              // SE_KERNEL_OBJECT, OWNER|GROUP|DACL
      lines.Add("GetSecurityInfo rc=" + rc);
      if (rc != 0) return lines;
      try {
        IntPtr s; uint len;
        if (ConvertSecurityDescriptorToStringSecurityDescriptorW(sd, 1, 0x7, out s, out len)) {
          string sddl = Marshal.PtrToStringUni(s);
          LocalFree(s);
          lines.Add("SDDL=" + sddl);
          var rsd = new RawSecurityDescriptor(sddl);
          lines.Add("Owner=" + rsd.Owner + " Group=" + rsd.Group);
          lines.Add("Protected=" + ((rsd.ControlFlags & ControlFlags.DiscretionaryAclProtected) != 0));
          int i = 0;
          foreach (GenericAce ace in rsd.DiscretionaryAcl) {
            var c = ace as CommonAce;
            lines.Add("ACE[" + i + "] " + ace.AceType + " flags=" + ace.AceFlags + " mask=0x" + c.AccessMask.ToString("X8") + " sid=" + c.SecurityIdentifier.Value);
            i++;
          }
          lines.Add("AceCount=" + i);
        }
      } finally { LocalFree(sd); }
    } finally { CloseHandle(h); }
    return lines;
  }
}
'@
[PipeAcl]::Read("\\.\pipe\$endpoint") | ForEach-Object { Write-Output $_ }
```

Note that this probe consumes one transient connection, so run it while the instance is otherwise idle.

**E5 — enumerate pipes and listeners.**

```powershell
$all = [System.IO.Directory]::GetFiles('\\.\pipe\')
Write-Output ("total_pipes=" + $all.Count)
Write-Output ("rag_pipes=" + @($all | Where-Object { $_ -like '*rag-historical-loader-v1*' }).Count)
$all | Where-Object { $_ -like '*rag-historical-loader-v1*' }
Get-NetTCPConnection -State Listen | Where-Object { $_.OwningProcess -eq <engine pid> }
```

**Shutdown.** Press Ctrl+C in the `serve` console and let it drain. A synthetic `CTRL_C_EVENT` is **not**
deliverable from an MSYS/mintty → PowerShell → process chain: `GenerateConsoleCtrlEvent(0, 0)` after
`AttachConsole` is accepted but reaches nobody. `CTRL_BREAK_EVENT` (event `1`) does arrive and .NET surfaces both
through the same `Console.CancelKeyPress` handler, so it exercises the same drain path when no real console is
available — but say so in the record rather than implying a human pressed Ctrl+C. Record the exit code from an
independent observer, because `Start-Process -PassThru` followed by `$p.ExitCode` is unreliable; a
`PROCESS_QUERY_LIMITED_INFORMATION` handle opened **before** the process exits is not.

**E6 — the emitted text.** The second instance exits `4` and prints
`serve: this engine could not own its pipe endpoint.` It does **not** print the literal token `command_conflict`,
and the collision is reported by the pipe-ownership branch rather than the installation-lock branch whenever the
second instance uses a different `--db` (and therefore a different lock path). Both branches share exit code 4.
Record this as an observability deviation; do not read the missing token as a failure.

## 5. What to save, and where

Version, both account names, date, the commit, and the SHA-256 of `Rag.HistoricalLoader.Engine.dll`. The
non-secret environment variables, the test database and lock paths. Stdout, stderr and the exit code of every
server and client invocation. E2's full descriptor, E3's and E4's literal errors, E5's enumerations. Then fill
sections 4, 5 and 7 of `unit-11-prev-windows-pipe-security.md`.

**Redact before publishing.** The raw captures contain the machine name, the operator account and SID, the
machine installation GUID, and local paths. The repository is public: keep the raw transcripts in the operator's
own evidence directory and put redacted forms in the record, noting that the unredacted originals are held
locally. The ACL *shape* is the evidence, not the account's RID.

## 6. Close, or roll back

If any of E1–E7 fails, do not mark the acceptance-evidence item complete: save the evidence and report which
letter failed. E3 and E4 unexecuted is a failure of the letter, not a pass.

Stop both servers with Ctrl+C; do not force-kill processes. Afterwards the SQLite file and the test lock may be
deleted. The recovery point stays the recorded commit; whether to push is the maintainer's decision, not a step
of this runbook.
