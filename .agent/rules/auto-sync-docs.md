---
trigger: always_on
---

# Auto-Sync Documentation Rule (DynamicAi)

Whenever a task modifies **behavior, public APIs/contracts, authentication/RBAC/middleware, configuration (`appsettings.json`), SQL/LLM pipeline logic, or automated test counts**, automatically synchronize the affected documentation in the **same task** (before proposing the final commit):

1. **`README.md`**: Update the Architecture Overview diagram, Key Capabilities, configuration defaults, and test counts.
2. **`docs/ERP_INTEGRATION_MIGRATION_GUIDE.md`**: Update ERP-facing endpoints, `X-Api-Key` roles (`Integration` vs `AdminOnly`), request/response schemas, clarification handling, and HTTP status tables.
3. **`docs/security/SECRETS_GUIDE.md`**: Update API key role scopes, secret configuration keys, and `appsettings.json` examples.
4. **`docs/testing/TESTING_GUIDE.md`**: Update automated test counts and covered components in the Unit/Integration/Security/Evaluation table.
5. **`Project_Description/Improvement_Plan/00_TRACKER_DASHBOARD.md`**: Update header date/counts, KPI scoreboard metrics, and append a concise row to the Section 4 Execution Changelog.

## Documentation Hygiene Guardrails

- **No local URIs:** NEVER write `file:///` links inside `.md` files; always use portable relative paths (e.g., `[README.md](README.md)` or `[TESTING_GUIDE.md](docs/testing/TESTING_GUIDE.md)`).
- **No secrets:** NEVER place real API keys, tokens, passwords, or private connection strings in documentation files.
- **Skip noise:** Skip doc edits for pure internal refactors that do not alter external contracts, configuration, behavior, or test counts.
