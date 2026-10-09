# ODD Tasks — Collection listing endpoint (issue #46)

Scope: add a read-only collection listing so a client can enumerate its own collections. No
other route changes, no delete or archive path.

## Why this is the blocker

`POST /api/v1/collections` is the only collection route. A client can create a collection
and then cannot see it, and an operator cannot remove a mistaken one: enumeration exists only
through the operator CLI, and only for unowned collections. This is the gap recorded in the
client integration contract of `docs/deployment/environment-contract.md`.

Confirmed against the published contract at this revision: `/api/v1/collections` declares
`post` only, and no collection route declares `get` or `delete`.

## Patterns to mirror (do not invent a new convention)

- Cursor pagination already exists in the administration plane: `limit` (1..100) plus an
  opaque `cursor`, a page shape of `{ items, nextCursor }`, and a 400 problem response when
  the limit is out of range or the cursor is invalid.
- The cursor codec is `AdminCursor` / `AdminCursorKey` with `AdminSupport.ResolveLimit` and
  `AdminSupport.ResolveCursor` in the Application layer. Reuse them unchanged: the wire
  format must stay consistent across planes. The naming is admin-flavoured for a public
  route; that rename is a separate, behaviour-neutral refactor and is deliberately out of
  scope here.
- The calling client is resolved with `ApiEndpointSupport.GetClientId(context.User)`, the
  same helper the create route uses.
- Public routes live in `Program.cs` with `WithName`, `Produces` and `DeclaresProblem`
  annotations; the published contract is generated from those annotations and is committed.

## Tasks

- [ ] 1. Add the repository query for a page of collections owned by one service client,
  keyset-ordered and consistent with the existing cursor semantics.
- [ ] 2. Add the application handler and the page representation, mirroring the existing
  admin listing handler.
- [ ] 3. Map `GET /api/v1/collections` on the authenticated public plane with the limit and
  cursor parameters, the 200 page response and the 400 problem response.
- [ ] 4. Regenerate the published contract and confirm the contract tests pass.
- [ ] 5. Tests: a client lists its own collections, a client cannot see another client's
  collections, pagination walks the full set without duplicates or gaps, and the invalid
  limit and cursor cases answer 400.

## Out of scope

Delete or archive of a collection; renaming `AdminCursor`; any change to the create,
ingestion, retrieval or operation routes; the environment or deployment.
