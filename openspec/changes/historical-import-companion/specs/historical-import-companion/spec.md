# Historical Import Companion Specification

## Purpose

Perform a trusted, one-time local snapshot import through existing APIs without exposing local-source information to the BFF.

## Requirements

### Requirement: Bounded local snapshot execution

The standalone .NET CLI MUST accept administrator-selected local root directories and execute one snapshot only. It MUST process DOC, DOCX, Markdown, PDF, and TXT files, and MUST NOT provide Dropbox API/OAuth, remote browsing, browser filesystem access, sync, scheduling, change detection, deletion propagation, or multi-collection execution.

#### Scenario: Selected directories are imported

- GIVEN an accepted snapshot lease and configured local roots
- WHEN the administrator starts the CLI
- THEN it SHALL discover supported files below only those roots
- AND it SHALL target only the lease collection for that execution

#### Scenario: No lease is available

- GIVEN the lease endpoint returns `204`
- WHEN the CLI requests work
- THEN it MUST not scan or ingest local files and MUST exit without an import

### Requirement: Normalized TXT ingestion

The CLI MUST extract each supported file locally into normalized UTF-8 TXT and compute `external_reference` as the SHA-256 of that extracted, normalized UTF-8 text. It MUST NOT compute `external_reference` from the original source-file bytes. The CLI MUST exchange service-client credentials for a token, submit the TXT through `POST /api/v1/collections/{id}/ingestions:txt`, and poll the returned operation to its terminal result. It MUST NOT create ingestion endpoints or transmit source paths or source content to the BFF.

#### Scenario: A supported document succeeds

- GIVEN extraction produces normalized UTF-8 TXT
- WHEN the CLI submits it to the leased collection
- THEN the ingestion SHALL use the SHA-256 of the locally extracted, normalized UTF-8 text as `external_reference`
- AND the CLI SHALL count it processed only after a successful terminal operation

#### Scenario: Source bytes do not define document identity

- GIVEN two supported source files extract to identical normalized UTF-8 text
- WHEN the CLI derives `external_reference` for each file
- THEN it MUST derive the same value regardless of their original file bytes or formats

#### Scenario: Extraction fails

- GIVEN a supported file cannot be extracted to TXT
- WHEN the CLI processes that file
- THEN it MUST record a final per-file failure and continue with remaining files

### Requirement: Bounded transient recovery

The CLI MUST retry only transient extraction, token, ingestion, or operation-polling failures, using bounded exponential backoff and no more than three retries per file operation. After the third retry fails, it MUST record the final per-file failure and continue. It MUST NOT retry a non-transient failure.

#### Scenario: A transient ingestion failure recovers

- GIVEN an ingestion attempt receives a transient failure
- WHEN a retry succeeds within three retries
- THEN the file SHALL be processed and SHALL not be recorded failed

#### Scenario: Retries are exhausted

- GIVEN a file operation remains transiently unavailable
- WHEN its third retry fails
- THEN the CLI MUST record that file failed and MUST continue the snapshot

### Requirement: Canonical signed companion protocol

The CLI MUST use only `POST /companion/v1/import-jobs/lease` with `{protocolVersion:1}` and `POST /companion/v1/import-jobs/{jobId}/events` with `{protocolVersion:1,leaseId,eventId,sequence,state,processed,failed,errorCode?}`. Every request MUST include `X-Companion-Id`, `X-Companion-Key-Id`, `X-Companion-Timestamp`, `X-Companion-Nonce`, `X-Companion-Body-SHA256`, and `X-Companion-Signature`.

`X-Companion-Timestamp` MUST be an integer Unix UTC second; `X-Companion-Body-SHA256` MUST be lowercase hexadecimal SHA-256 of the exact UTF-8 body; and the signature MUST be Base64(HMAC-SHA256(secret, exactly this UTF-8 string)):

```text
METHOD\nPATH\nBODY_SHA256\nCOMPANION_ID\nKEY_ID\nTIMESTAMP\nNONCE
```

#### Scenario: A signed event is accepted

- GIVEN a leased job and a fresh nonce
- WHEN the CLI signs the exact event body using the canonical representation
- THEN it SHALL send the next required sequence and non-decreasing counts

#### Scenario: Event boundary is preserved

- GIVEN the CLI reports progress or a failure
- WHEN it creates an event body
- THEN it MUST omit paths, content, commands, provider metadata, and unknown fields

### Requirement: Lease-bound completion reporting

The CLI MUST report `running` and a terminal `succeeded` or `failed` event using the accepted lease and event sequencing. It MUST report `failed` when any file has a final failure, and MUST preserve final per-file failures in local structured logs without secrets.

#### Scenario: Snapshot completes with a failed file

- GIVEN one file exhausts its permitted retries and later files finish
- WHEN the CLI completes the snapshot
- THEN it MUST send a terminal `failed` event with final counts
