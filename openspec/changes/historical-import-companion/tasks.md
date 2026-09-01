# Tasks: Historical Import Companion

## Review Workload Forecast

Decision needed before apply: No
Chained PRs recommended: Yes
Chain strategy: feature-branch-chain
400-line budget risk: Medium

auto-chain+feature-branch-chain; tracker `feat/import-companion` (draft) → `main`. Unit 3 (1,277 lines) → 3A+3B, each ≤800-line session budget; size exception rejected. Harness: 2+tmp, 3A no-LO, 3B LO26.8, 5 WAF, 6 win64+BFF; revert per PR.

## 1 | tracker | `--filter Protocol`
- [x] 1.1 `Rag.Companion.csproj` (`net10.0`, `win-x64`); Open XML 3.5.1, PdfPig 0.1.16; register in `Rag.sln`. RED: restore OK.
- [x] 1.2 `Protocol/{CanonicalString,CompanionSigner}.cs` (Base64 HMAC-SHA256; hex body hash). RED: known-answer; tamper⇒change.
- [x] 1.3 `Protocol/{LeaseRequest,LeaseResponse,EventRequest,EventResponse}.cs` (fixed shapes; reject unknowns). RED: extra prop ⇒ `400`.
- [x] 1.4 `Protocol/CompanionHttpClient.cs` (`204` no-work; running ≤20 s; retry fresh envelope; stale `409`). RED: bad key/HMAC/hash/ts/nonce⇒`401`; dup⇒`replayed:true`; regressed seq⇒`409`; unknown field⇒`400`.

## 2 | PR#1 | `--filter Snapshot`
- [x] 2.1 `Snapshot/ConfinedSnapshotService.cs` (roots-only; allow-list `{doc,docx,md,pdf,txt}`; reject reparse/symlink/junction). RED: `requirements.txt`/`run.sh`/`notes.mdx` ignored; `README.md` accepted; symlink-out rejected; tmp skipped.
- [x] 2.2 `Snapshot/HandleConfinedCopier.cs` (`FileShare.Read`, deny write/delete, non-reparse private, 3× race retry, fail-closed). RED: race retries succeed; drift closes.

## 3 | PR#2 | `--filter "Adapters.Managed&Normalizer&Payload"`
- [x] 3.1 `Adapters/Normalizer.cs` (BOM strip; CRLF/CR→LF; NFC; preserve ws; append 1 LF). RED: BOM/CR/CRLF/NFC equalize.
- [x] 3.2 `Adapters/{Txt,Markdown,Docx}Adapter.cs` (TXT/MD direct UTF-8; DOCX via Open XML 3.5.1). RED: paragraph order preserved; throw⇒`extraction_failed`.
- [x] 3.3 `Adapters/PdfAdapter.cs` (PdfPig 0.1.16). RED: ordered per-page text; password PDF⇒`extraction_failed`.
- [x] 3.4 `Adapters/{PayloadSizeGuard,AdapterRegistry}.cs` (1 MiB UTF-8 + 1,048,576-byte JSON ⇒ `extracted_text_too_large`; constructor only, no LibreOffice/CreateDefault). RED: 1 MiB+1 byte closes; unknown ext ⇒ not-found.
- NOTE: source for 3+4 NOT staged/committed; VCS deferred.

## 4 | PR#3 | `--filter "Adapters.Doc&LibreOffice&AdapterRegistry"`
- [x] 4.1 `LibreOffice/LibreOfficeRunner.cs` (pinned `soffice.com` LO 26.8 x64; `UseShellExecute=false`; `ArgumentList` only; isolated profile+outdir; no stdin; 60 s tree-kill; UTF-8 out). RED: metachar filename quoted; fake binary/timeout/missing/extra/reparse output/nonzero all close.
- [x] 4.2 `Adapters/DocAdapter.cs` (OLE-magic + runner). RED: non-OLE rejected.
- [x] 4.3 `AdapterRegistry.CreateDefault` + LO wiring; factory + Doc happy-path. RED: managed-only when no LO; full set when LO present; wiring closes.

## 5 | PR#4 | `--filter Ingestion`
- [ ] 5.1 `Ingestion/IngestionClient.cs` (token exchange; JWT cache; SHA-256 normalized UTF-8 ⇒ `external_reference`). RED: same normalized text⇒same ref.
- [ ] 5.2 `200` dup=null op (no poll); `202` polls `pending|running` to terminal. RED: non-null op+`200` closes; null-op `200` no poll; `202` polls terminal.
- [ ] 5.3 `Ingestion/RetryPolicy.cs` (3 attempts; exp 1s/2s/4s jittered; transient-only). RED: 4th impossible; `400`/`401`/`404` not retried.
- [ ] 5.4 Per-file failure recorder (structured log w/ `errorCode`; no paths/creds). RED: scrubber asserts no path/secret.

## 6 | PR#5 | ci-pr.yml+`companion-bff-smoke.sh`
- [ ] 6.1 `Host/Program.cs` (`run --config <path>`; non-zero exit on terminal fail). RED: invalid config exits non-zero, no secret leakage.
- [ ] 6.2 Modify `.github/workflows/ci-pr.yml`: Windows job (LO 26.8 x64 verify; `dotnet test Rag.sln`; win-x64 publish; integrity; SBOM+notices); no new workflow.
- [ ] 6.3 Modify `.github/workflows/ci-release.yml`: PR checks; BFF lease/expiry/terminal smoke; 5-format smoke; LO verify; integrity/SBOM pre-attach.
- [ ] 6.4 `tests/fixtures/import/manifest.json` + 5 fixtures (txt/md/docx/pdf/doc). RED: manifest SHA-256 matches every fixture.
- [ ] 6.5 `docs/historical-import-companion.md` (install; LO prereq; license; operation; rollback). RED: links resolve; deps listed.
- [ ] 6.6 BFF gate `scripts/companion-bff-smoke.sh` (lease/event; `200`/dup⇒`replayed:true`/regressed⇒`409`/bad HMAC⇒`401`; tag pre-attach). RED: CI fails on BFF down/sig diverge.
