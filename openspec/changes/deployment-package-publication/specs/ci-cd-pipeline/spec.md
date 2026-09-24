# ci-cd-pipeline Specification

## Purpose

Validate changes and promote only verified develop and release candidates.

## Requirements

### Requirement: Pull Request Validation

The system MUST build and test pull requests targeting `develop`; failed validation MUST prevent a passing gate. It MUST NOT perform SonarQube analysis or publish an image for a pull request.

#### Scenario: Valid pull request
- GIVEN a pull request targets `develop`
- WHEN build and tests succeed
- THEN the validation gate passes without analysis or publication

#### Scenario: Failing pull request
- GIVEN a pull request targets `develop`
- WHEN build or tests fail
- THEN the validation gate fails and no image is published

### Requirement: Develop and Release Eligibility

The system MUST build and test every `develop` push, and MUST run SonarQube Community analysis only for that branch. A semver release tag MUST be built and tested before publication and create a corresponding release record; `v0.1.0-rc.1` MUST be classified as a pre-release, not a stable release.

#### Scenario: Verified develop candidate
- GIVEN a commit is pushed to `develop`
- WHEN build, tests, and analysis succeed
- THEN the candidate becomes eligible for package publication

#### Scenario: Analysis failure
- GIVEN a commit is pushed to `develop`
- WHEN SonarQube analysis fails
- THEN the candidate is not eligible for publication

#### Scenario: Valid pre-release tag
- GIVEN `v0.1.0-rc.1` builds and tests successfully
- WHEN publication eligibility is determined
- THEN its release record is marked as a pre-release

#### Scenario: Unsupported analysis branch
- GIVEN a pull request or non-`develop` branch event
- WHEN validation runs
- THEN SonarQube Community analysis is not requested
