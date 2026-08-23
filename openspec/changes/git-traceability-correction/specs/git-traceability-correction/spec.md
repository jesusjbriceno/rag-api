# Git Traceability Correction Specification

## Purpose

Define factual, documentation-only corrections to traceability statements in the `architecture-boundaries` planning artifacts. This specification neither changes application behavior nor establishes Git governance.

## Requirements

### Requirement: Factual Traceability Statements

The corrected planning artifacts MUST state that HEAD is unborn and no ref points to a commit containing the Decision Log. They MUST NOT state that Git has no objects: dangling commit `c9b40bfb` exists, but does not contain the Decision Log. They MUST state that the Decision Log is gitignored and untracked, that seven accepted decision contents were verified, that Git cannot prove an exact seven-entry-only mutation, and that no history was recovered.

#### Scenario: Corrected traceability is documented

- GIVEN the corrected planning artifacts are reviewed
- WHEN their traceability statements are read
- THEN they distinguish unreachable history from existing Git objects
- AND they include each verified limitation and fact

#### Scenario: Unsupported history claim is absent

- GIVEN no Git evidence proves the exact Decision Log mutation
- WHEN the traceability statements are read
- THEN they do not claim recovered history or exact mutation proof

### Requirement: Accurate Affected-Area Inventory

The `architecture-boundaries` proposal MUST inventory both its own `proposal.md` and `tasks.md` as modified planning artifacts, with their traceability and rollback documentation impacts accurately described.

#### Scenario: Tasks artifact is inventoried

- GIVEN the proposal Affected Areas table is reviewed
- WHEN the correction scope is inspected
- THEN it includes the `tasks.md` artifact and its documentation impact

#### Scenario: Inventory remains scoped

- GIVEN the correction is documented
- WHEN affected areas are listed
- THEN no application capability, code artifact, or unmodified planning artifact is represented as changed

### Requirement: Accepted Decision Content Remains Unchanged

The correction documentation MUST state that the seven accepted Decision Log contents remain unchanged. It MUST NOT revise, reinterpret, or add to those decisions.

#### Scenario: Decision contents are preserved

- GIVEN the corrected artifacts are compared with the accepted Decision Log
- WHEN the correction is applied
- THEN all seven accepted decision contents remain unchanged

#### Scenario: Traceability scope does not expand decisions

- GIVEN a factual traceability correction is reviewed
- WHEN its statements are evaluated
- THEN they contain no new architecture or product decision

### Requirement: Non-Binding Advisory and Accurate Restoration Language

The documentation MAY advise future Decision Log editors to consider Git tracking or an external backup. The advisory MUST be non-binding and MUST NOT establish a policy or baseline requirement. Rollback language MUST limit Git restoration to an identified tracked pre-change baseline; future commits alone MUST NOT be represented as restoring untracked historical versions. Manual restoration requires an available pre-change version.

#### Scenario: Advisory remains optional

- GIVEN a future Decision Log edit is planned
- WHEN the advisory is read
- THEN it is presented as a consideration rather than a required control

#### Scenario: Restoration boundary is explicit

- GIVEN no tracked pre-change Decision Log baseline exists
- WHEN rollback language is read
- THEN it does not claim that later commits alone can restore an untracked historical version
