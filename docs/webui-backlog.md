# WebUI Backlog

Items from the chatbot-ui comparison that were deferred. Each is evaluated "later" — don't pull into the current iteration without discussion.

## Skipped: redundant with our harness

- **Presets** — saved chat settings bundles (model, temp, system prompt). Our `AGENT.md` manifests already bundle these per-agent; Presets would be a parallel (and confusing) config surface.
- **Collections** — groupings of files for retrieval. Our Skills system + MCP resources already provide scoped context; Collections would duplicate the mental model.
- **Assistants** — chatbot-ui's assistants are instructions + tools + files scoped per-workspace. Our Agents (from `AGENT.md`) are the richer equivalent — skills, MCP tools, keyed-DI tools, decision frameworks.
- **Custom model endpoints** — end-users wiring arbitrary model URLs. We configure models centrally via `AppConfig.AI.AgentFramework` (Azure OpenAI / Azure AI Foundry). Exposing this in the UI would undercut the Azure-first deployment story.

## Skipped: out of POC scope

- **Workspaces** — multi-tenant workspace isolation (chatbot-ui routes like `app/[locale]/[workspaceid]/...`). Orthogonal to the agentic harness POC; would require reworking conversation ownership, auth claims, and storage layout.
- **Supabase auth + Postgres** — chatbot-ui's auth/DB stack. We use Azure AD + file-backed conversation store by design.
- **i18n / locale routing** — not a POC requirement.
- **Setup wizard** — first-run config flow. We use `appsettings.json` + User Secrets.

## Revisit triggers

Pull something off this list if:
- A real deployment consumer asks for it (not hypothetical)
- Our Agents/Skills can't cleanly express a use case that Presets/Collections could
- We add multi-user/multi-tenant beyond the current single-dev-user model (→ Workspaces)
