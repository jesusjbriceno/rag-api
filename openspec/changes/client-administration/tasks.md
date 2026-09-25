# Tasks: Client Administration Plane

## Status correction

This task record is limited to source inspection of commit `f220b41`. Prior checkmarks, branch/PR topology, approval records, completion claims, and test totals in this file are not retained as evidence because they cannot be established from that commit alone. RDD lifecycle approval was not started and is not claimed.

## Committed API scope observed at `f220b41`

| Area | Source-observed state | Evidence limit |
| --- | --- | --- |
| Enablement | `AdminPlane:Enabled` gates the admin scheme/policy/routes, observability middleware, admin options, and retention worker. | API source only; no deployment proof. |
| Authentication | The API requires an internally signed assertion and a machine proof, rejects forwarded identity headers, and reserves replay identifiers. | No external issuer or host is delivered. |
| Administration | Client, credential, and audit routes are mapped below `/api/v1/admin`. | Runtime scenario completion is unproven here. |
| Retention | The worker purges replay, operation, and configured audit data and records a retention event. | Production scheduling and retention operation are unproven here. |
| Logging | Admin request logging uses selected structured fields and excludes secret-bearing request inputs in its implementation. | This is not a metrics integration claim. |

## Delivery tasks

- [~] Confirm API enablement and configuration contracts against `f220b41` source.
- [~] Confirm internal-assertion and machine-proof authentication contracts against `f220b41` source.
- [~] Confirm client, credential, and audit route mappings against `f220b41` source.
- [~] Confirm retention-worker and structured-log implementation boundaries against `f220b41` source.
- [ ] Provide and validate an external assertion issuer and host. This component is not delivered by `f220b41`.
- [ ] Establish deployment-platform, Cloudflare, metrics, and end-to-end runtime evidence if those are required. They are outside this committed API evidence.
- [ ] Select and isolate an RDD candidate before any RDD lifecycle action. No RDD lifecycle is active for this documentation work unit.

## Out of scope

This work unit does not deliver an AdminApp, companion service, Cloudflare integration, platform deployment, external assertion issuer, metrics integration, public admin surface, or CLI break-glass tooling.
