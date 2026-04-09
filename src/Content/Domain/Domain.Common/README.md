# Domain.Common

Every system needs a shared language — a set of types that every layer can speak without introducing coupling. Domain.Common is that language for the Agentic Harness.

This project sits at the absolute bottom of the dependency graph. It has no knowledge of AI agents, MCP protocols, or LLM providers. What it knows is the shape of the application itself: how configuration is structured, how errors are represented, how workflows transition between states, and how results flow back to callers without relying on exceptions for control flow.

---

## The Result Pattern

The most important type in this project is `Result<T>`. Every CQRS handler in the harness returns a `Result<T>` instead of throwing exceptions for expected failures. Validation fails? That's a `Result.Fail()`. Permission denied? `Result.Fail()` with a `PermissionRequired` failure type. Content blocked by safety middleware? Same pattern.

Exceptions are reserved for truly exceptional conditions — database connectivity loss, null references, things that shouldn't happen. Everything else flows through `Result<T>` with typed failure categories: `Validation`, `Unauthorized`, `Forbidden`, `ContentBlocked`, `NotFound`, `PermissionRequired`.

The `ResultExtensions` class adds functional composition — `Map`, `Bind`, `Ensure`, `OnSuccess`, `OnFailure` — so handlers can chain operations without nested if-else blocks.

## The Configuration Hierarchy

The harness has a lot of knobs. AI model deployments, MCP server connections, observability sampling ratios, connector credentials, HTTP resilience policies, cache backends. All of it is represented as strongly-typed POCOs bound through the Options pattern.

`AppConfig` is the root. Everything hangs off it:

```
AppConfig
├── AI/
│   ├── AgentFrameworkConfig      Model deployment, provider type (AzureOpenAI, OpenAI, AIFoundry)
│   ├── A2AConfig                 Agent-to-Agent protocol settings
│   ├── AIFoundryConfig           Persistent agent endpoint
│   ├── ContextManagement/        Budget, compaction, tool result storage
│   ├── Hooks/                    Lifecycle hook configuration
│   ├── MCP/                      Server identity, auth, external server connections
│   ├── Orchestration/            Subagent profiles, streaming execution
│   └── Permissions/              Tool permission rules and safety gates
├── Azure/                        Entra ID, Key Vault, App Insights, Graph API
├── Cache/                        Memory or Redis backend
├── Connectors/                   GitHub, Jira, Azure DevOps, Slack credentials
├── Http/                         Client configs, resilience policies, OpenAPI/Swagger
├── Infrastructure/               State management, content providers
├── Logging/                      File paths, pipe names
└── Observability/                Exporters, sampling, PII filtering, LLM pricing
```

Every config section is a plain record or class with no behavior — just data. The Infrastructure and Presentation layers read these through `IOptionsMonitor<T>` for runtime reload support.

## Workflow State

The harness supports stateful agent workflows — multi-step processes where an agent moves through phases, makes decisions, and checkpoints its progress. The workflow engine lives here as pure abstractions:

- `IStateManager` — Load, save, and transition workflow state
- `WorkflowState` / `NodeState` — The state graph itself
- `DecisionFramework` — Rules that map conditions to outcomes at decision points
- Three domain exceptions for invalid transitions, missing rules, and evaluation failures

The actual persistence (JSON files, markdown checkpoints) is implemented in Infrastructure. Domain.Common only knows the shape of state, not how it's stored.

## Everything Else

- **Constants** — `ClaimConstants` and `PolicyNameConstants` for authorization claim types and policy names
- **Extensions** — Enum utilities, string helpers, and the Result<T> fluent API
- **Helpers** — Input sanitization (`SecureInputValidatorHelper`), JSON key sorting, Result construction utilities
- **Logging** — `ExecutionScope` record for structured logging context (trace ID, user, agent, step number)
- **Models** — `AuditEntry` for compliance trails, `LogEntry` for structured output, `RunManifest` for execution metadata
- **Telemetry** — `AppInstrument` and `AppSourceNames` for OpenTelemetry source registration

---

## Project Structure

```
Domain.Common/
├── Config/
│   ├── AI/                      Agent framework, A2A, MCP, permissions, hooks, context management
│   ├── Azure/                   Entra ID, Key Vault, App Insights, databases
│   ├── Cache/                   Cache backend selection
│   ├── Connectors/              Third-party service credentials
│   ├── Http/                    Client configs, OpenAPI, resilience policies
│   ├── Infrastructure/          State management, content providers
│   └── Observability/           Exporters, sampling, PII filtering
├── Constants/                   Claim types, policy names
├── Enums/                       AuthPermissions
├── Extensions/                  Result<T> fluent API, enum/string helpers
├── Helpers/                     Input validation, JSON sorting, Result construction
├── Logging/                     ExecutionScope, FileLoggerOptions
├── Middleware/                   GlobalErrorHandlerOptions
├── Models/                      AuditEntry, LogEntry, RunManifest, EndpointHealthResult
├── Telemetry/                   Instrumentation helpers, source name constants
├── Workflow/                    IStateManager, WorkflowState, DecisionFramework
└── Result.cs                    The Result<T> pattern
```

## Dependencies

None beyond `Microsoft.Extensions.Hosting.Abstractions` and `Microsoft.Extensions.Logging.Abstractions`. This is intentional — Domain.Common is the foundation everything else builds on, so it can't depend on anything above it.
