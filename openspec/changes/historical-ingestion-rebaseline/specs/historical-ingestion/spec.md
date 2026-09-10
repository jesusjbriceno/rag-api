# Historical Ingestion Specification

## Purpose

Provide an operator-controlled historical-document ingestion path for a local Windows corpus. The path SHALL establish inventory and benchmark evidence before any full-corpus scaling decision, while remaining independent of the existing real-time ingestion flow and AdminApp work in progress.

## Requirements

### Requirement: Windows Operator Workflow

The system MUST provide a local Windows operator workflow whose primary interface is non-terminal and that presents inventory, start, pause, resume, processing-state, audit-log, and error-viewing capabilities. The selected UI framework is not prescribed.

#### Scenario: Operator controls a prepared proof batch

- GIVEN an inventory and a prepared proof batch
- WHEN an operator opens the local workflow
- THEN the operator can inspect the inventory, start processing, pause it, resume it, inspect document states, and view audit and error records without relying on a terminal as the primary interface.

### Requirement: Candidate Inventory and Manifest

The system MUST scan configured Windows source folders without ingesting their contents and produce both a structured manifest and a human-readable summary. The evidence MUST include total candidate count and size, format distribution, size distribution, eligibility outcomes, and identifiable reasons for excluded or unsupported candidates.

#### Scenario: Inventory completes for configured source folders

- GIVEN configured Windows source folders containing eligible, unsupported, and ineligible files
- WHEN the operator runs inventory
- THEN the resulting manifest and summary account for each candidate category and record the evidence needed to select a representative benchmark sample.

### Requirement: Representative Benchmark Evidence

The system MUST benchmark a sample selected from inventory evidence across observed format and size categories. The benchmark report MUST record extraction duration per document, normalized-text size, embedding duration per chunk, observed throughput, and observed CPU, memory, and disk-I/O usage; it MUST record sustained extraction reliability observations, including timeouts or hangs when observed.

The system MUST document a scaling recommendation and a Companion disposition of reuse, adapt, or reference only, each traceable to benchmark evidence. It MUST NOT claim extraction capacity, embedding capacity, a full-corpus duration, or scaling feasibility before such evidence exists.

#### Scenario: Benchmark evidence is sufficient for a disposition decision

- GIVEN a completed inventory with multiple observed format or size categories
- WHEN a representative benchmark is run
- THEN its report identifies the sampled categories, measurements, reliability observations, limitations, scaling recommendation, and evidence-based Companion disposition.

### Requirement: Durable Per-Document Lifecycle

The system MUST durably record each selected document's lifecycle state and relevant transition outcome before representing that outcome as complete to the operator. Lifecycle evidence MUST distinguish at least pending/not loaded, in progress, loaded, skipped due to a classified document error, and retry-exhausted or other error states. A loaded state MUST be associated with the corresponding manifest candidate and ingestion run.

#### Scenario: Process interruption preserves document outcomes

- GIVEN a proof batch with documents in pending, in-progress, loaded, and error-related states
- WHEN the local process stops unexpectedly
- THEN a subsequent launch recovers a durable state from which previously recorded outcomes can be inspected and processing can continue without treating already loaded documents as newly pending.

### Requirement: Pause, Resume, and Restart Safety

The system MUST pause by reaching a durable, operator-visible checkpoint that prevents unconfirmed work from being reported as loaded. It MUST resume from confirmed checkpoints after an operator resume action or local process restart, and it MUST preserve the audit trail across multi-night runs.

#### Scenario: Pause and restart resumes only unconfirmed work

- GIVEN an active proof batch
- WHEN the operator pauses it, exits the local process, and later restarts and resumes it
- THEN confirmed loaded, skipped, and terminal-error outcomes remain visible and only work not confirmed at the checkpoint is eligible for continued processing.

### Requirement: Failure Classification and Continuity

The system MUST classify failures as network-related or document-related before applying their handling policy. A network-related operation MUST be attempted no more than three times in total; its terminal outcome MUST be recorded when the maximum is reached. A document-related format, size, or extraction failure MUST be classified, recorded, and skipped so that later eligible documents continue to be processed.

#### Scenario: Network failure reaches the retry limit

- GIVEN a document operation that fails for a network-related reason
- WHEN the same operation fails on three total attempts
- THEN the system records a retry-exhausted network outcome and does not make a fourth attempt for that operation in the run.

#### Scenario: Document error does not block later documents

- GIVEN a batch in which one document has a classified extraction failure and a later document is eligible
- WHEN the failure is recorded
- THEN the failed document is marked skipped with its classification and the later eligible document remains processable.

### Requirement: Auditable, Privacy-Preserving Operations

The system MUST retain an audit record for operator actions, lifecycle transitions, retry and skip decisions, and security-relevant credential events. Audit and error views MUST contain sufficient identifiers and timestamps to trace a manifest candidate and run outcome while avoiding document content, reusable credentials, access tokens, or unnecessary sensitive local-path disclosure.

#### Scenario: Error record supports diagnosis without secret disclosure

- GIVEN a document processing error and an associated audit event
- WHEN an operator views the records
- THEN the records identify the run, candidate, classification, and time while not displaying document content, client secrets, or access tokens.

### Requirement: Legacy Collection Provenance

The system MUST ingest approved historical documents into a searchable `legacy` collection and retain provenance sufficient to relate each loaded document to its manifest candidate and source context. The system MUST NOT automatically assign project taxonomy or classification to historical documents.

#### Scenario: Loaded legacy document remains attributable

- GIVEN a document loaded through the historical path
- WHEN it is retrieved from the `legacy` collection
- THEN its retained metadata permits association with the originating manifest candidate and source context without asserting an automatic project classification.

### Requirement: Direct Data-Plane Authentication

The loader MUST authenticate directly to the data-plane API with client credentials exchanged for short-lived, scoped access tokens. The authorization model MUST support least-privilege scopes for historical-ingestion operations and must reject absent, expired, revoked, or insufficiently scoped tokens. Long-lived client credentials and short-lived tokens MUST be stored locally only through an operating-system-appropriate secure storage mechanism; neither may be written to manifests, benchmarks, checkpoints, audit logs, or ordinary error output.

A future AdminApp control plane MUST be able to provision and revoke loader client credentials without performing local traversal or ingestion execution. This change MUST NOT require, modify, import, or depend on AdminApp WIP to perform the proof workflow.

#### Scenario: Revoked or insufficient authorization is denied

- GIVEN a loader token that is revoked, expired, or lacks the required historical-ingestion scope
- WHEN the loader requests a protected data-plane operation
- THEN the operation is denied and the security-relevant outcome is auditable without exposing the token or client secret.

### Requirement: Controlled External Boundary

The historical path MUST expose only the approved API boundary to the loader through the controlled Cloudflare access boundary using the applicable service-token or access controls. PostgreSQL, llama.cpp, and other internal processing or persistence services MUST remain non-public and inaccessible as direct loader endpoints.

#### Scenario: Loader access is limited to the approved boundary

- GIVEN a configured historical loader
- WHEN it connects from its local environment
- THEN it can use the approved protected API boundary, while direct access to PostgreSQL, llama.cpp, and other internal services is unavailable.

### Requirement: Phased Validation and Scaling Gate

The system MUST validate the proof workflow in this order: local Windows proof environment, OCI/Dokploy ARM64 staging environment, then the Coolify target environment. Each phase MUST record environment-specific evidence for the capabilities in scope before the next phase is accepted. A full-corpus run or production deployment MUST NOT be authorized by this specification; it requires a separate decision based on inventory, benchmark, proof, and environment-validation evidence.

#### Scenario: Insufficient local proof evidence blocks later validation

- GIVEN the local proof has not demonstrated required checkpoint, failure-handling, and authentication outcomes
- WHEN a later-environment validation is proposed
- THEN the proposal is blocked until the missing local evidence is recorded.

### Requirement: Isolation and Compatibility Boundaries

The historical ingestion path MUST remain separate from the existing real-time ingestion endpoint and MUST preserve and isolate AdminApp WIP, including its source, Docker/Compose work, and CI work. It MUST NOT require Companion reuse; any reuse, adaptation, or reference-only decision MUST follow the benchmark requirement.

#### Scenario: Proof workflow does not alter protected work

- GIVEN the proof workflow is implemented and exercised
- WHEN its changed behavior is evaluated
- THEN it does not require changes to the existing real-time ingestion endpoint, AdminApp WIP, AdminApp Compose/CI work, or a predetermined Companion architecture.

## Non-Requirements

- This specification does not select Electron or any other UI framework.
- This specification does not select a queue, checkpoint, manifest, or durable-storage implementation.
- This specification does not prescribe an API wire protocol, endpoint shape, token format, or Cloudflare product configuration.
- This specification does not set performance, throughput, concurrency, capacity, full-corpus duration, or scaling targets.
- This specification does not authorize GPU or external embedding services, automatic taxonomy, full-corpus ingestion, production deployment, AdminApp integration, branches, PRs, CI changes, or changes to the existing real-time ingestion path.

## Validation Unknowns

The following MUST be resolved by inventory, benchmark, or subsequent design evidence before a scaling commitment: corpus count and distribution; extraction throughput and sustained reliability; normalized-text and chunk characteristics; CPU embedding throughput and capacity; Windows operator-machine resource headroom; checkpoint durability approach; direct-auth protocol and credential lifecycle details; Companion disposition; and OCI/Dokploy ARM64 and Coolify environment compatibility.
