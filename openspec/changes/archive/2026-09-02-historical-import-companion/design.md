# Design: Historical Import Companion

## Technical Approach

Add a `win-x64` .NET 10 CLI patterned after `Rag.Operator`: lease a snapshot; verify handles; extract locally with LibreOffice (DOC), Open XML (DOCX), PdfPig (PDF), or direct Markdown/TXT decoding; normalize/hash UTF-8; ingest sequentially; report signed events; exit. It adds no remote service, browser access, or BFF shell execution.

## Architecture Decisions

| Option | Tradeoff | Decision and rationale |
|---|---|---|
| Managed-only vs local converter | Open XML excludes DOC; NPOI adds a revenue-user EULA; POI/Tika adds Java and orphaned HWPF. | Use LibreOffice 26.8 x64 for DOC; official filters support Word 97–2003 and UTF-8 TXT under MPL-2.0. |
| One extractor vs format adapters | More adapters; smaller process boundary | Pin `DocumentFormat.OpenXml` 3.5.1 (MIT) and `PdfPig` 0.1.16 (Apache-2.0); direct-decode Markdown/TXT. Aspose.Words is prohibited. |
| Platform breadth | First release is Windows-only | Support `win-x64`; enables handle-path verification and matches LibreOffice x64. |
| Parallel vs sequential | Lower throughput; deterministic lease state | Process one file at a time; send renewal progress at most every 20 seconds. |

Sources: [Open XML/MIT](https://github.com/dotnet/Open-XML-SDK), [PdfPig/Apache-2.0](https://github.com/UglyToad/PdfPig), [LibreOffice CLI](https://help.libreoffice.org/latest/en-US/text/shared/guide/start_parameters.html), [filters](https://help.libreoffice.org/latest/en-US/text/shared/guide/convertfilters.html), [MPL-2.0](https://www.libreoffice.org/about-us/licenses/).

## Data Flow

```text
lease -> confined scan -> locked snapshot -> adapter -> NFC/LF -> SHA-256
      -> token -> TXT ingestion -> optional poll -> signed event -> terminal
```

Normalization strips a BOM, maps CRLF/CR to LF, applies NFC, preserves whitespace, and appends one LF to non-empty text.

## File Changes

| File | Action | Description |
|---|---|---|
| `src/Rag.Companion/*` | Create | Host, snapshot, adapters, bounded LibreOffice runner, clients, orchestration. |
| `tests/Rag.Companion.Tests/*`, `tests/fixtures/import/*` | Create | Unit/integration tests and redistributable fixture provenance. |
| `Directory.Packages.props`, `Rag.sln` | Modify | Pin packages and register projects. |
| `.github/workflows/ci-pr.yml` | Modify | Add a Windows job to the tracked PR workflow: provision/version-verify pinned LibreOffice 26.8 x64; test; publish untrimmed, single-file, self-contained `win-x64`; verify executable/ZIP/SHA-256 integrity; emit SBOM/notices. |
| `.github/workflows/ci-release.yml` | Modify | Extend the tracked tag workflow, never add another: require PR-equivalent checks, live BFF contracts, clean-machine DOC/DOCX/Markdown/PDF/TXT smoke, LibreOffice 26.8 x64 version verification, executable/ZIP/SHA-256 integrity, SBOM, and notices before asset attachment. |
| `docs/historical-import-companion.md` | Create | Install, license notices, operation, rollback. |

## Interfaces / Contracts

`LeaseRequest {protocolVersion:1}` receives `204`, or `LeaseResponse {jobId,collectionId,leaseId,leaseExpiresAt,nextSequence,snapshot:true}`. `EventRequest {protocolVersion:1,leaseId,eventId,sequence,state,processed,failed,errorCode?}` receives `EventResponse {acceptedSequence,state,replayed}`. Six headers and canonical `METHOD\nPATH\nBODY_SHA256\nCOMPANION_ID\nKEY_ID\nTIMESTAMP\nNONCE` are fixed; events reject extension data and exclude paths/content/commands/provider data.

Send `running` at `nextSequence` before scanning. Accepted `running` renews the 60-second lease; send within 20 seconds. Retries reuse the exact body/eventId with a fresh signed envelope; `replayed:true` succeeds without count/sequence advancement. Lease expiry or stale-lease `409` stops ingestion without reacquisition and exits failed. Terminal `succeeded|failed` is immutable: exact replay succeeds; mutation/regression returns `409`.

TXT ingestion returns `{document_id,document_version_id,operation_id}`. For `202`, require an operation and poll `pending|running` to terminal. Duplicate `200` requires null operation and succeeds immediately; never poll null. Normalized text over 1 MiB, or serialized JSON over 1,048,576 bytes, fails `extracted_text_too_large` without truncation, splitting, or retry.

Open each source once, deny write/delete sharing, verify its handle path under a non-reparse root, and copy to a private snapshot. Retry sharing races three times; fail closed on identity changes, escapes, or residual handles. Managed adapters read the snapshot. DOC validates OLE magic, then starts absolute `soffice.com` with `UseShellExecute=false`, fixed `ArgumentList`, isolated profile/output, no stdin, 60-second timeout/tree kill, and regular output validation.

## Testing Strategy

| Layer | What to Test | Approach |
|---|---|---|
| Unit | Five adapters, normalization, limits, races, DTO state/replay, signing | xUnit fakes and generated fixtures. |
| Integration | API `200/null` and `202/poll`; live BFF lease/expiry/terminal contract | `WebApplicationFactory`; BFF endpoints are a release gate, not mock-only. |
| Packaging | Clean `win-x64` host plus LibreOffice 26.8 | Windows CI smoke test; fixture manifest records origin, license, and SHA-256. |

## Threat Matrix

| Boundary | Applicability | Design response / planned RED tests |
|---|---|---|
| Documentation-like paths | Applicable | Only listed extensions are data; test `requirements.txt`/`.md`, ignore `CMakeLists.txt`/`.mdx`/`README.sh`, reject reparse escape. |
| Git repository selection | N/A: no VCS | No selector/test. |
| Commit state | N/A: no commits | No state/test. |
| Push state | N/A: no pushes | No destination/test. |
| PR commands | N/A: no PR automation | No composition/test. |
| LibreOffice process | Applicable | Fixed executable/args/private paths; test metacharacter filenames, fake executable, timeout, extra/missing/reparse output, nonzero exit. |
| BFF process integration | Applicable | Fail `401/400/409`; RED: unknown key, bad HMAC/hash, stale/reused nonce/lease, duplicate/regressed sequence, forbidden fields. |

## Migration / Rollout

No migration. CI publishes an untrimmed, self-contained, single-file `win-x64` ZIP, SHA-256, SBOM, and notices to GitHub Releases; LibreOffice remains a separate prerequisite. Release gates are live BFF contracts and clean-machine DOC/DOCX/Markdown/PDF/TXT smoke tests. Rollback stops runs; ingested data remains.

## Open Questions

None.
