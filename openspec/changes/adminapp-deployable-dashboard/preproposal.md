# Pre-proposal State: AdminApp Deployable Dashboard

```yaml
gentle-ai.sdd-preproposal/v1:
  revision: 1
  exploration_reference: "openspec/changes/adminapp-deployable-dashboard/exploration.md"
  research_request:
    change: adminapp-deployable-dashboard
    questions:
      - "What Dokploy manifest and operational runbook are required for the deployable admin dashboard?"
      - "How must deployment preserve the API, database, and model services as private-only?"
      - "What Cloudflare Access validation and exposure constraints affect the design?"
      - "How can Dokploy be added without migrating or modifying compose.coolify.yaml?"
    requested_source_classes:
      - documentation
      - open-web
  admission:
    outcome: blocked
    reason: "No evidence grants are available in this runtime."
    observed_exact_grants:
      documentation: []
      open-web: []
  evidence_references: []
  product_decisions:
    dokploy_additional_target: confirmed
    preserve_compose_coolify_yaml: confirmed
    add_explicit_dokploy_manifest_and_runbook: confirmed
    api_db_model_private_exposure: confirmed
    concrete_hostname_audience_team_domain_policy_and_secrets: pending
  proposal_ready: false
```

Proposal readiness is blocked because no requested source class was admitted and no evidence claims were validated.
