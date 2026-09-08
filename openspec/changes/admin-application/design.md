# Design: Admin Application

## Technical Approach

Create `Rag.AdminApp.Host`, an ASP.NET Core BFF serving React. Browser `/bff/*` requests validate `Cf-Access-Jwt-Assertion` and an administrator allowlist. The BFF owns secrets and proxies only nine admin operations; `Rag.Api` remains unchanged. Historical import ships UI plus an unavailable gateway. A pull protocol defines future companion integration without implementing it.

## Architecture Decisions

| Option | Tradeoff | Decision and rationale |
|---|---|---|
| One container vs services | Coupled release; no CORS | Serve BFF and SPA together on Coolify. |
| Typed vs catch-all proxy | More mappings; bounded authority | Map exactly client create/list/detail, credential issue/list/detail/rotate/revoke, and audit list. |
| Stateless vs session | Revalidation cost; no session store | Validate Cloudflare JWT and allowlisted `sub` per browser request; fail closed. |
| Persist vs memory-only secret | Reload loses display; storage increases exposure | Keep credentials in React memory; require save confirmation before dismissal. |
| Pull lease vs BFF process control | Companion must poll; BFF cannot execute locally | Define signed lease/events carrying identifiers and counts only. No shell, path, file content, or provider command crosses the boundary. |

## Data Flow

```text
Browser -> Cloudflare Access -> BFF -> assertion + API HMAC -> nine existing admin routes
Browser -> import gateway (unavailable now) -> progress/result view
Future companion -> signed lease poll -> local snapshot work -> existing ingestion API
Future companion -> signed event -> BFF job state -> Browser polling
```

Dependencies: SPA -> BFF -> `Rag.AdminApp`; no `Rag.Api` type reference.

## File Changes

| File | Action | Description |
|---|---|---|
| `src/Rag.AdminApp/AdminRequestSigner.cs` | Create | Signer compatible with `AdminMachineProofVerifier`. |
| `src/Rag.AdminApp.Host/Program.cs`, `AdminProxy.cs`, `HistoricalImportContracts.cs`, `appsettings.json` | Create | Host, proxy, unavailable gateway, companion contract. |
| `apps/admin-ui/src/App.tsx`, `api.ts`, `features/*` | Create | Admin and snapshot-import UI. |
| `tests/Rag.AdminApp.Host.Tests/*`, `apps/admin-ui/src/**/*.test.tsx` | Create | BFF/contract/UI tests. |
| `Dockerfile`, `compose.coolify.yaml`, `.github/workflows/*.yml` | Modify | Private image/service and checks. |
| `docs/deployment/*.md`, `README.md` | Modify | Coolify terminology and rollout. |
| `src/Rag.Api/AdminEndpoints.cs` | None | Preserve all nine operations unchanged. |

## Interfaces / Contracts

The browser proxy preserves approved method/path/query, JSON, status, `Location`, `ETag`, and Problem Details. Mutations use browser UUID idempotency keys; reads use BFF UUIDs. Rotate/revoke require `If-Match`. Attempts receive fresh 60-second assertions and API HMACs. Authentication material is never logged.

Browser routes remain `POST /bff/import-jobs` with `{collectionId}` and `GET /bff/import-jobs/{jobId}` returning `{jobId,state,sequence,processed,failed,errorCode?}`. The gateway returns `503 companion_unavailable`.

The future non-browser protocol is:

- `POST /companion/v1/import-jobs/lease`, request `{protocolVersion:1}`; response `204` when empty or `200 {jobId,collectionId,leaseId,leaseExpiresAt,nextSequence,snapshot:true}`.
- `POST /companion/v1/import-jobs/{jobId}/events`, request `{protocolVersion:1,leaseId,eventId,sequence,state,processed,failed,errorCode?}` where state is `running|succeeded|failed`; response `200 {acceptedSequence,state,replayed}`.

Both routes require `X-Companion-Id`, `X-Companion-Key-Id`, `X-Companion-Timestamp`, `X-Companion-Nonce`, `X-Companion-Body-SHA256`, and `X-Companion-Signature`. Configuration maps `(companionId,keyId)` to its current/previous HMAC-SHA256 secret; browser identity is rejected. The Base64 signature covers `METHOD\nPATH\nBODY_SHA256\nCOMPANION_ID\nKEY_ID\nTIMESTAMP\nNONCE`. `X-Companion-Timestamp` is a Unix timestamp in integer seconds (UTC); `X-Companion-Body-SHA256` is the SHA-256 digest of the exact UTF-8 request body, encoded as lowercase hexadecimal. Timestamp tolerance and leases are 60 seconds; nonces are single-use. Unknown keys, stale/reused envelopes, or mismatches return `401` without mutation.

Jobs transition only `requested -> running -> succeeded|failed`, `running -> requested` on lease expiry, or `requested -> unavailable` while the gateway is absent. One active lease exists per job. Accepted running events renew it up to 60 seconds. Sequence must equal `nextSequence`; counts cannot decrease. An exact repeated `eventId` is idempotent; stale leases, regressions, conflicting duplicates, or terminal mutation return `409`. Unknown fields—including paths, content, commands, or Dropbox/provider data—return `400`. Terminal states are immutable. Companion implementation, local acquisition, scanning, scheduling, sync, deletion, and Dropbox behavior remain deferred.

## Testing Strategy

| Layer | What to Test | Approach |
|---|---|---|
| Unit | signing, allowlist, state/lease bounds, secret gate | xUnit; Vitest |
| Integration | exact proxy allowlist, unavailable gateway, companion rejection/idempotency | `WebApplicationFactory` and fake upstream |
| E2E | Image/build/exposure | CI build and Compose validator; browser E2E deferred. |

## Threat Matrix

| Boundary | Cases | Applicability | Design response / planned RED tests |
|---|---|---|---|
| Documentation-like paths | executable-looking names | N/A: no file/path classification or execution is implemented | Contract rejects file/path fields; no classification test. |
| Git repository selection | relative/absolute/`git -C` | N/A: no VCS operation | No selector or test. |
| Commit state | staged/`-a`/empty | N/A: no commits | No test. |
| Push state | tracking/first/refspec | N/A: no pushes | No test. |
| PR commands | head/env/composition | N/A: no PR automation | No test. |
| Companion process integration | spoof, replay, regression, injection | Applicable | Fail `401/400/409`, never execute or mutate; RED tests: unknown key, bad HMAC/hash, stale/reused nonce, stale lease, duplicate/regressed sequence, and path/command/provider fields. |

## Migration / Rollout

No migration. Deliver chained slices: trust/proxy, SPA operations, import contract/UI, then packaging/CI/docs. Rollback removes the admin service and trust material; the API is unchanged.

## Open Questions

None.
