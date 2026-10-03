# ODD Tasks — Coolify shared-network hygiene and reproducible model mount

Scope: fix the defects surfaced by the first real deployment of `compose.coolify.yaml` on
Coolify. No deployment, no tag, no push, no changes to image references, no EdificIA
integration in this feature.

## Evidence this feature rests on

Verified against the deployed resource (Coolify LAN panel `http://192.168.1.26:8000`,
project `Estudio`, application `rag-api`, uuid `kwihmtooewuqg35mbrbjvw3n`,
`running:healthy`, `git_commit_sha e0876a8aed15f5c738c5fc81af550eb9d452adf2`,
`fqdn None`, `build_pack dockercompose`, `/compose.coolify.yaml`, 11 environment
variables present with the expected shapes).

1. Coolify attaches **every** container of a Docker Compose resource to the shared
   destination network once *Connect To Predefined Network* is enabled
   (coollabsio/coolify#5597: "it will add the containers to the coolify network ... it does
   not alter the compose file"). Our stack therefore registered `postgres`, `api`,
   `llama-cpp`, `migrate` and `model-download` as aliases on `coolify`.
2. `postgres` collides with Coolify's own database container `coolify-db` on that network
   and Docker DNS round-robins between them. Known Coolify failure mode
   (coollabsio/coolify#5160); the maintainer-level answer is *"either do not put your
   project in the coolify network or rename the postgres service in your compose"*.
3. Declaring our own `networks:` section is **not** a valid fix and is actively harmful:
   Coolify's docs state custom networks cause intermittent HTTPS outages because the proxy
   only joins the resource-specific network
   (coolify.io/docs/applications/build-packs/docker-compose). Coolify's documented
   mitigation is *"Names can be prefixed to prevent collisions"*. The existing validator
   rule (implicit `default` network only) is therefore correct and stays.
4. `docs/deployment/coolify.md` claimed PostgreSQL and llama.cpp "resolve only inside the
   RAG stack". False under this topology; corrected.
5. First deployment required a manual, non-reproducible fix: Docker auto-created the bind
   mount source `./scripts/download-llamacpp-model.sh` as a directory before the repo
   checkout, so the mount stayed empty; `model-download` exited 0 having published
   nothing, and llama.cpp started without a model.

## Tasks

- [x] 1. Prefix every service name in `compose.coolify.yaml` with `rag-` and update every
  internal reference (`depends_on` keys, `ConnectionStrings__Rag` host,
  `LlamaCpp__BaseUrl`). Image references, volume names, healthchecks and the absence of a
  `networks:` section are unchanged. Commit `6ef68b1`.
- [x] 2. Replace the single-file bind mount of the downloader script with a directory mount
  (`./scripts:/scripts:ro`) so the mount is immune to creation-before-checkout ordering,
  and harden the validator so a missing, non-file or empty script cannot pass silently.
  Commit `6ef68b1`.
- [x] 3. Update `scripts/validate-coolify-compose.py` and
  `scripts/test-validate-coolify-compose.py` for the renamed services and the directory
  mount, and keep the "implicit default network only" rule with a comment citing the
  Coolify warning about custom networks. Commit `6ef68b1`.
- [x] 4. Correct `docs/deployment/coolify.md` and `README.md`. Commit `2aa12af`.
- [x] 5. Local verification: `scripts/test-validate-coolify-compose.py` 13/13 OK;
  `scripts/validate-coolify-compose.sh` passes; `docker compose config` exit 0 for both
  the production file alone and the production file plus `compose.dev.yaml`.
- [ ] 6. Hand the infrastructure operator a bounded runbook: redeploy from a clean stack
  state, plus the exact read-only checks that prove the collision is gone
  (`docker network inspect coolify`) and that `/ready` still returns 200.

## Collateral found and fixed

`compose.dev.yaml` overlays `api` and `migrate`. The rename broke that merge, which
would have broken the documented local path
(`docker compose -f compose.coolify.yaml -f compose.dev.yaml up --build`). Fixed in the
same commit; the merged configuration was verified to carry `build` and the loopback port
on `rag-api` and `build` on `rag-migrate`.

## Out of scope

Custom Coolify destination networks, per-service network opt-out, redeploy execution,
capacity measurement, EdificIA integration, any change to the published image references
or the JWT/secret material.
