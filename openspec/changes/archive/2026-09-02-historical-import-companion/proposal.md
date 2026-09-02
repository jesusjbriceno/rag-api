# Proposal: Historical Import Companion

## Intent

A trusted administrator needs to migrate historical documents from Dropbox-synchronized local folders into the RAG platform. No ingestion path exists for this today. The companion is a standalone .NET CLI that performs a one-time snapshot import, extracting text from local files and submitting it through the existing ingestion API.

## Scope

### In Scope
- Standalone .NET CLI (`Rag.Companion`) following the `Rag.Operator` pattern
- One-shot scan of administrator-selected local directories (Dropbox-synced)
- Text extraction from DOC, DOCX, Markdown, PDF, TXT → UTF-8 normalized TXT
- Submission to existing ingestion API (`POST /api/v1/collections/{id}/ingestions:txt`)
- Content hash (SHA-256) as `external_reference`; no source path crosses the BFF event boundary
- BFF companion protocol client: HMAC-signed lease acquisition and event reporting
- One target collection per run
- Transient failure handling: 3 retries with exponential backoff, then record failure and continue
- Service-client credentials created via `Rag.Operator`, delivered via approved secure channel

### Out of Scope
- BFF shell execution, browser filesystem access, Dropbox API/OAuth integration
- Continuous sync, scheduled runs, change detection, deletion propagation
- Remote directory browsing
- New ingestion API endpoints or format support in the RAG API
- Multi-collection runs

## Capabilities

### New Capabilities
- `historical-import-companion`: End-to-end CLI — configuration, directory scanning, multi-format text extraction, ingestion submission, BFF companion protocol lease/event reporting, retry logic, and structured logging.

### Modified Capabilities
None — no existing specs are affected.

## Approach

New console project `src/Rag.Companion/` in the solution. The admin runs it with a JSON config (or env vars) specifying API base URL, companion HMAC credentials, service-client credentials, target collection, and root directories. On execution: acquire BFF lease → scan directories → for each supported file, extract text → submit via ingestion API → report progress events → report terminal state → exit. Self-contained publish for distribution.

## Affected Areas

| Area | Impact | Description |
|------|--------|-------------|
| `src/Rag.Companion/` | New | Companion CLI project |
| `Rag.sln` | Modified | Add project reference |
| `Dockerfile` | Modified | Optional companion build target |

## Risks

| Risk | Likelihood | Mitigation |
|------|------------|------------|
| BFF companion endpoints not yet implemented | High | Develop against mock BFF; integration tests with `WebApplicationFactory` |
| HMAC signing canonicalization mismatch | Medium | Shared test vectors; unit tests for signing round-trips |
| Text extraction library quality (DOC/PDF) | Medium | Evaluate mature .NET libraries; unit test extraction with sample files |
| Dropbox sync timing (partial files) | Low | Skip `.tmp` extensions; document sync-complete prerequisite |

## Rollback Plan

The companion is a standalone CLI with no server-side changes. Rollback = stop running it. No data mutation occurs outside the existing ingestion API (which handles dedup via `external_reference`). Already-ingested documents remain in the collection.

## Dependencies

- BFF companion lease/event endpoints (from `admin-application` change) must be available for E2E operation
- Text extraction NuGet packages for DOC/DOCX/PDF
- Service-client credential provisioned via `Rag.Operator`

## Success Criteria

- [ ] Companion scans configured directories and extracts text from all supported formats
- [ ] Each file is submitted to the ingestion API with content hash as external reference
- [ ] BFF lease/event protocol is correctly implemented with HMAC signing
- [ ] Transient failures retry 3 times with exponential backoff, then record and continue
- [ ] No source file path appears in BFF events
- [ ] Self-contained binary runs without .NET runtime installed
