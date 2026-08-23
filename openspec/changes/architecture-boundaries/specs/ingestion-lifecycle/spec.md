# Ingestion Lifecycle Specification

## Purpose

Define ingestion progress with D-006, without deciding work selection or retry identity.

## Accepted Architecture Baseline

D-003 PostgreSQL/pgvector, D-004 .NET/ASP.NET Core, D-005 modular monolith, and D-006 local worker are Accepted baseline decisions.

## Requirements

### Requirement: Operation Lifecycle

An operation MUST move `pending` → `running` → `succeeded` or `failed`. While running, it MUST load, parse, normalize, chunk, embed, and index in order. Failure MUST be traceable. Terminal operations MUST NOT advance in normal processing; authorized explicit retry MAY advance only a failed operation.

#### Scenario: Successful ingestion

- GIVEN pending processable content
- WHEN all stages complete
- THEN the operation is `succeeded`

#### Scenario: Stage failure

- GIVEN a running operation
- WHEN a stage fails
- THEN it is `failed` and the stage is traceable

#### Scenario: Completed operation

- GIVEN an operation is `succeeded` or `failed`
- WHEN normal processing continues
- THEN the operation remains terminal

### Requirement: Local Background Execution

The system MUST use a local worker in the modular deployable for long-running ingestion apart from request acceptance. When it begins an operation, it MUST transition it to `running` and perform the pipeline. Claim, lease, and work-selection semantics remain undecided.

#### Scenario: Local execution

- GIVEN a pending ingestion operation
- WHEN the local background worker begins it
- THEN the operation becomes `running`
- AND the ordered pipeline is performed

#### Scenario: Multiple pending operations

- GIVEN multiple pending ingestion operations
- WHEN the worker selects work
- THEN this specification does not require an ordering or claim mechanism

### Requirement: Manual Retry

The system MUST NOT retry failed ingestion automatically. An authorized explicit retry MUST start a traceable attempt. Whether it reuses or creates an Operation remains deferred to HTTP-contract design.

#### Scenario: Explicit recovery

- GIVEN a failed operation
- WHEN an authorized caller retries it
- THEN another attempt starts

#### Scenario: No recovery request

- GIVEN a failed operation
- WHEN retry is absent
- THEN no attempt starts

## Deferred Decisions and Non-goals

Claim, leasing, work selection, retry identity, and external queue/broker remain deferred. Implementation details are out of scope.
