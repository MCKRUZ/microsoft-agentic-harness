# Presentation.FoundryHost

Packages the full agentic harness as an **Azure AI Foundry hosted agent** — a container Foundry
Agent Service starts, scales, and exposes over the OpenAI-compatible `/responses` protocol. Unlike a
Foundry *prompt agent* (where the platform owns the loop), a hosted agent runs *your* code, so the
harness's in-process pipeline — skills, governance, tool-output compression, prerequisite gating,
telemetry — survives intact.

The host reuses the same composition root every other host uses (`GetServices`), builds the agent
through `IAgentFactory` exactly as the desktop and web hosts do, and hands it to the Foundry hosting
library. **There is no new agent-type concept**: which agent this deployment exposes is selected by
id (`FOUNDRY_AGENT_ID`, default `default`), resolved against the same `AGENT.md` manifests the rest
of the harness discovers.

## Runtime persistence model

Foundry runs each conversation in its own **session** — a VM-isolated sandbox with a **persistent
`$HOME`**. Key facts (verified against Microsoft Learn, *What are hosted agents?*):

- `$HOME` (and the `/files` upload area) **survive idle/resume**: when a session is idle for 15
  minutes the compute is deprovisioned, but `$HOME` is preserved and **restored on resume**.
- A session — and its `$HOME` — is **permanently deleted after 30 days of inactivity**.
- Conversation history is stored **durably in Foundry**, independently of compute. The host sets
  the harness to *not* re-persist it, so Foundry remains the single source of truth.
- **Only `$HOME`/`/files` are guaranteed to persist** — *not* the container's app/publish
  directory. So the host re-roots the harness's per-session disk stores under `$HOME/agent-state`:
  the planner database, the JSONL audit trails (drift, governance, escalation, change proposals +
  evidence, egress, and the compliance trail of erasures, exports and document removals at
  `compliance/`, with its folder of records held for replay at `compliance-pending/`; every other trail keeps its
  held records inside its own folder too, for example `governance/governance-pending/`), the audit
  verification checkpoints (`audit-verify/`; without them a chain cut off while the container was
  down verifies as intact), the audit pseudonym key (`keys/audit-pseudonym.key`; without it every
  restart made a new key, so person references written before and after a restart could not be
  matched), the erasure journal (`erasure/`; without it an erasure that failed part-way cannot be
  finished), the conversation, document-ledger and schedule databases, workflow state
  (`workflows/`), execution traces (`traces/`), offloaded tool results, subagent delegation
  records and mailbox, the embedded (Kuzu) graph database, and file logs. Stores that are off by
  default are rooted too, so turning one on lands it here. Four are not: durable governance state,
  whose database must stay inside the application directory (`GovernanceStatePaths` refuses any
  other); prompt usage, configured by a whole connection string an override would replace (point
  `AppConfig:AI:PromptUsage:ConnectionString` yourself, as `Data Source=` followed by the absolute
  path of a file under `$HOME/agent-state`, in a folder that already exists, since SQLite does not
  create it); and
  skill-training checkpoints and the eval dashboard's run reports, which the personal-data
  inventory records as holding no personal data. Setting `AppConfig:AI:Audit:PseudonymKey` from a
  secret store replaces the key file and is still the right choice when more than one instance
  writes the trails. (RAG's FAISS vector index and FTS5 BM25 store are in-memory only —
  there is no on-disk index to root; they rebuild from ingested documents.)

**Net effect:** out of the box, every conversation gets durable, idle-surviving memory with **zero
external services**. External managed stores are needed *only* to share knowledge **across different
conversations or users**, or beyond the 30-day window.

## Erasing and exporting a person's data

This host serves no erasure or export endpoint (#1003). Foundry routes only the protocol endpoints to the container, and
each call reaches one session's sandbox, whose `$HOME` holds everything this host writes. Erase a person's data by
deleting their Foundry sessions (`DELETE .../agents/{agent}/endpoint/sessions/{id}`); copy it with the session files API.
Stores promoted to external services are shared by every session, so session deletion does not reach them. The host
runs on container protocol 2.0.0 and runs each request as the caller Foundry verified (`x-agent-user-id`, #1008): a
Neo4j or PostgreSQL knowledge graph and an Azure AI Search index record that caller as owner, so an erasure or export
naming that id, sent to a host bound to the same store, reaches them. The observability database and Redis key nothing by
person, and a provider the template does not ship may never write an owner; the host warns about those at startup. A
request without the header runs unscoped off the platform, but in Foundry a `/responses` request without it is
refused with 401, as is one whose header names no usable user. The procedure, and what it does not cover (Foundry's own conversation
history, the audit trails deleted with the session, records with no owner), is in the
[security guide](../../../../documentation/security/08-data-protection.html#foundry-erasure).

## Environment variables

### Injected by Foundry at deploy time

| Variable | Purpose |
|---|---|
| `FOUNDRY_PROJECT_ENDPOINT` | Foundry project endpoint used for model inference. |
| `AZURE_AI_MODEL_DEPLOYMENT_NAME` | Model deployment the agent runs against. |
| `APPLICATIONINSIGHTS_CONNECTION_STRING` | Telemetry sink. By default its presence turns on the harness's Azure Monitor exporter, and the Foundry hosting layer exports its own traces and logs to it. Set the opt-out `EnableFromHostConnectionString` to `false` and FoundryHost removes it before startup, so nothing is sent to Foundry's resource; where to set it and how each source spells it is in the [security guide](../../../../documentation/security/08-data-protection.html#foundry-telemetry-opt-out). FoundryHost logs which applies. |
| `FOUNDRY_AGENT_ID` | (Optional) which discovered `AGENT.md` to expose. Defaults to `default`. |
| `HOME` | Persistent session storage root. The host roots all local state under `$HOME/agent-state`. |

### Optional — external stores for cross-conversation / cross-user persistence

Each subsystem promotes itself from its in-process default to an external managed service **only when
its connection variable is supplied**. Supply none and the container still boots (durable
per-conversation memory via `$HOME`); supply a variable and that subsystem goes external. A
*malformed* connection still fails loudly at startup. Add the ones you need to `agent.yaml`'s
`environment_variables`.

| Variable(s) | Promotes | Notes |
|---|---|---|
| `GRAPH_PROVIDER` (`neo4j`\|`postgresql`) **and** `GRAPH_CONNECTION_STRING` | Knowledge graph + cross-session memory; also enables GraphRAG retrieval | Both required. `GRAPH_PROVIDER` is validated (a value other than `neo4j`/`postgresql` fails at startup with a clear message rather than an opaque DI error). |
| `AZURE_SEARCH_ENDPOINT` | RAG vector + BM25 store → Azure AI Search | Without it, RAG uses the in-process FAISS/FTS5 store (per-session, rebuilt from ingested docs). |
| `AZURE_SEARCH_API_KEY` | — | Optional; omit to use the agent's managed identity (preferred). |
| `AZURE_SEARCH_INDEX` | — | Optional; defaults to the harness's configured index name. |
| `REDIS_ENDPOINT` | `IDistributedCache` → Redis | Shared cache across sessions/replicas. |
| `REDIS_SECRET` | — | Optional Redis password. |

Any of the above can also be set directly via the native `AppConfig__...` env key, which the host
never overwrites.

> **Vector store note:** with no `AZURE_SEARCH_ENDPOINT`, the host applies an in-process FAISS
> default *via an environment override*, which takes precedence over an `appsettings`-only
> `VectorStore` section. To run against Azure AI Search, set `AZURE_SEARCH_ENDPOINT` (or the native
> `AppConfig__AI__Rag__VectorStore__Endpoint` env key) — do not rely on an `appsettings`-only
> endpoint, which the FAISS default would shadow.

### Capability note: sandbox

The `Foundry` profile sets `Sandbox:Enabled=false`. Docker-in-Micro-VM is unavailable and the
process sandbox's Windows Job-Object limits don't apply on the Linux runtime, so sandboxed tool
execution is the one harness capability degraded under hosted agents. The closed-by-default model
means such tools are refused with a clear capability message rather than running unsandboxed.

## Deploy

```bash
azd provision   # Foundry project, model deployment, App Insights, container registry
azd deploy      # builds the Dockerfile, pushes to ACR, registers the hosted agent
```

The translation from these environment variables to harness config keys lives in
`FoundryHostBootstrap.BuildConfigOverrides` (pure and unit-tested in `Presentation.FoundryHost.Tests`).
