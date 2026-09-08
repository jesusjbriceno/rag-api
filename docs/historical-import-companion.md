# Historical Import Companion

A standalone Windows CLI (`Rag.Companion`) that performs a one-time snapshot import of historical
documents from Dropbox-synced local folders into the RAG platform. It extracts text locally,
submits it through the existing ingestion API, and reports progress through the signed BFF
companion protocol. It never transmits source paths or source content to the BFF.

## Quick path

1. Install the LibreOffice 26.8 x64 prerequisite (see [Prerequisites](#prerequisites)).
2. Write a JSON config file (see [Operation](#operation)).
3. Run: `Rag.Companion run --config <path>` and read the exit code.

## Prerequisites

| Dependency | Version | Purpose | License |
|---|---|---|---|
| LibreOffice | 26.8 x64 | Legacy DOC conversion (`soffice.com`) | MPL-2.0 |
| DocumentFormat.OpenXml | 3.5.1 | DOCX extraction | MIT |
| PdfPig | 0.1.16 | PDF extraction | Apache-2.0 |

The published binary is self-contained (untrimmed, single-file `win-x64`) and does not require a
.NET runtime. LibreOffice remains a separate prerequisite because its license forbids bundling.

## Install

1. Download the `Rag.Companion` `win-x64` ZIP, its SHA-256, SBOM, and notices from the release.
2. Verify the ZIP SHA-256 against the published value.
3. Install LibreOffice 26.8 x64 and confirm `soffice.com --version` reports `26.8`.
4. Extract the ZIP to a local folder and run `Rag.Companion.exe run --config <path>`.

## Operation

The admin runs one snapshot per invocation, targeting the collection granted by the BFF lease.

```bash
Rag.Companion run --config companion.json
```

Config fields (all `camelCase`; unknown fields are rejected):

| Field | Required | Meaning |
|---|---|---|
| `apiBaseUrl` | yes | Data-plane ingestion API base URL |
| `companionBaseUrl` | yes | BFF base URL |
| `companionId` / `companionKeyId` / `companionSecret` | yes | Companion HMAC signing identity |
| `serviceClientKeyId` / `serviceClientSecret` | yes | Service-client credentials for the ingestion token exchange |
| `roots` | yes | Local root directories to scan (the snapshot is confined to these) |
| `libreOfficePath` | no | Absolute path to `soffice.com` (default `soffice.com`) |
| `failureLogPath` | no | Local structured log for final per-file failures (default `companion-failures.jsonl`) |

Behavior:

- Supported formats are `doc`, `docx`, `md`, `pdf`, and `txt`; temp files (`.tmp`, `.dropbox`) and
  non-document manifests (`requirements.txt`, `CMakeLists.txt`) are skipped.
- Each file is extracted to normalized UTF-8 text, and the SHA-256 of that normalized text becomes
  the `external_reference` — never the source bytes or path.
- Transient failures retry three times with exponential backoff; a final per-file failure is
  recorded locally and the snapshot continues.
- Exit codes: `0` success (or no work), `1` terminal failure, `2` usage error, `3` config error.

## License notices

Third-party dependencies and their licenses:

- LibreOffice 26.8 x64 — MPL-2.0 (separate install; see `https://www.libreoffice.org/about-us/licenses/`)
- DocumentFormat.OpenXml 3.5.1 — MIT (`https://github.com/dotnet/Open-XML-SDK`)
- PdfPig 0.1.16 — Apache-2.0 (`https://github.com/UglyToad/PdfPig`)

Aspose.Words is explicitly prohibited.

## Rollback

The companion is a standalone CLI with no server-side changes. Rollback is to stop running it.
Ingested documents remain in the target collection (the ingestion API deduplicates by
`external_reference`), so re-running is idempotent.

## References

- BFF contract smoke gate: `scripts/companion-bff-smoke.sh`
- Fixture manifest and provenance: `tests/fixtures/import/manifest.json`
- Implementation: `src/Rag.Companion/`
