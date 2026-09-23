# Research: AdminApp Deployable Dashboard

```yaml
gentle-ai.sdd-research/v1:
  revision: 1
  outcome: blocked
  admission:
    status: denied
    reason: "This runtime has no evidence grants; documentation and open-web evidence are both unavailable."
    observed_exact_grants:
      documentation: []
      open-web: []
  questions:
    - "What Dokploy manifest and operational runbook are required for the deployable admin dashboard?"
    - "How must deployment preserve the API, database, and model services as private-only?"
    - "What Cloudflare Access validation and exposure constraints affect the design?"
    - "How can Dokploy be added without migrating or modifying compose.coolify.yaml?"
  requested_source_classes:
    - documentation
    - open-web
  sources: []
  validated_claims: []
```

## Outcome

Research is blocked before source admission. No external-documentation or open-web claims are validated in this artifact.

## Retained request (not validated evidence)

The request identifies Dokploy as an additional deployment target. It requires retaining `compose.coolify.yaml` without migration, adding an explicit Dokploy manifest and runbook, and preserving `api`, `db`, and `model` as private services. It also names Cloudflare Access and Dokploy references as requested sources. These are retained as planning inputs only, not as validated claims.

## Pending product and infrastructure decisions

- The concrete Dokploy domain configuration and hostname remain unspecified.
- Cloudflare Access team domain, audience, policy, token configuration, and all secrets remain unspecified.
- The exact Dokploy service naming, routing configuration, and deployment topology remain pending source admission and product confirmation.
- No implementation or runtime configuration change is authorized by this blocked research record.
