# Application.AI.Common

This is the brain of the harness. While Application.Common handles generic cross-cutting concerns, Application.AI.Common handles everything that makes this an *agent* system: tool permission enforcement, context budget tracking, prompt composition, hook lifecycle management, content safety screening, and the factories that wire agents together from their manifests.

It defines 41 interfaces that the Infrastructure layer must implement, 6 MediatR pipeline behaviors specific to agent operations, and the services that manage what an agent knows, what it can do, and how much context it has left to work with.

---

## The Agent Pipeline

When a tool request flows through the harness, it passes through AI-specific pipeline behaviors layered on top of Application.Common's generic pipeline:

```
Request
  → UnhandledExceptionBehavior (1)      Safety net — catch, log, enrich
    → AgentContextPropagation (3)       Push agent identity to logging scope
      → AuditTrailBehavior (4)          Record who did what, when, outcome
        → ToolPermissionBehavior (6)    3-phase permission check
          → HookBehavior (6)            Fire pre/post lifecycle hooks
            → ContentSafetyBehavior (8) Screen input against safety policies
              → Handler
```

**ToolPermissionBehavior** implements the three-phase permission algorithm for any request marked with `IToolRequest`. Phase 1: check explicit rules (allow/deny lists). Phase 2: check safety gates (paths like `.git/` that are always dangerous). Phase 3: check denial rate limits (if a tool was denied 3+ times, auto-deny).

**HookBehavior** fires lifecycle hooks before and after request execution. A pre-hook returning `Continue=false` short-circuits the entire pipeline. Post-hooks can modify the response or inject additional context.

**ContentSafetyBehavior** screens `IContentScreenable` requests against content safety policies. If content is blocked, the request never reaches the handler.

## Factories

Two factories handle the complex construction of agent instances:

**AgentFactory** creates configured `AIAgent` instances from the Microsoft.Agents.AI framework. It wires up the middleware pipeline (observability, caching, function invocation, diagnostics), supports batch discovery of agents, and can provision persistent agents on Azure AI Foundry.

**AgentExecutionContextFactory** maps SKILL.md declarations to runtime execution contexts. It resolves tools (MCP-first, keyed DI fallback), assembles instructions, and configures middleware — turning a static manifest into a live agent configuration.

## Context Budget

The `ContextBudgetTracker` is the agent's accountant. It tracks token allocation per agent across four categories: system prompt, loaded skills, tool schemas, and conversation history. When utilization hits 80%, it raises warnings. When it's exhausted, it triggers compaction.

The `TieredContextAssembler` works alongside it, loading skill context at the appropriate tier based on remaining budget: Tier 1 at 3K tokens, Tier 2 at 8K, Tier 3 only when the skill is actively executing.

## Tool Conversion

The harness has its own `ITool` abstraction for tools — richer than the framework's `AITool`, with operation schemas, concurrency classification, and execution auditing. The `AIToolConverter` bridges the gap: it reads an `ITool`'s schema, generates the JSON function-calling definition, and wires up the execution callback. From the LLM's perspective, it's just a function. From the harness, it's a sandboxed, audited, permission-gated operation.

## The 41 Interfaces

The interface surface is organized by concern:

| Category | Key Interfaces | Purpose |
|----------|---------------|---------|
| **Agent** | `IAgentFactory`, `IChatClientFactory` | Agent and LLM client creation |
| **Agents** | `IAgentMailbox`, `ISubagentProfileRegistry`, `ISubagentToolResolver` | Inter-agent messaging and subagent management |
| **Compaction** | `IContextCompactionService`, `IAutoCompactStateMachine`, `ICompactionStrategyExecutor` | Context window reduction |
| **Config** | `IConfigDiscoveryService` | Filesystem config discovery |
| **Context** | `IContextBudgetTracker`, `ITieredContextAssembler`, `IToolResultStore` | Token budget and skill loading |
| **Hooks** | `IHookExecutor`, `IHookRegistry` | Lifecycle event interception |
| **Permissions** | `IDenialTracker`, `IPatternMatcher`, `IPermissionRuleProvider`, `ISafetyGateRegistry` | Tool access control |
| **Prompts** | `ISystemPromptComposer`, `IPromptSectionProvider`, `IPromptSectionCache`, `IPromptCacheTracker` | System prompt assembly and caching |
| **Safety** | `ITextContentSafetyService` | Content screening |
| **Tools** | `ITool`, `IToolConverter`, `IToolExecutionStrategy`, `IToolConcurrencyClassifier`, `IFileSystemService` | Tool abstraction and execution |
| **MCP** | `IMcpToolProvider` | External tool discovery |
| **Skills** | `ISkillLoaderService` | Skill loading and caching |

## Observability

11 OpenTelemetry components instrument agent operations:

- **Metrics** — Content safety evaluations, context budget utilization, LLM token costs, MCP server latency, orchestration turns, tool execution rates
- **Span Processors** — `AgentFrameworkSpanProcessor` enriches agent spans with identity and turn context; `ConversationSpanProcessor` links conversation-related spans
- **Telemetry Configurator** — Registers AI-specific sources (Microsoft.Agents.AI, Microsoft.Extensions.AI, Semantic Kernel)

## Exceptions

8 domain-specific exceptions for agent operations: `AgentExecutionException`, `AttackDetectionException`, `ContentSafetyException`, `ContextBudgetExceededException`, `McpConnectionException`, `SkillNotFoundException`, `SkillParsingException`, `ToolExecutionException`. Each carries structured context for debugging.

---

## Project Structure

```
Application.AI.Common/
├── Exceptions/                  8 agent-specific exception types
├── Extensions/                  AgentContext helpers, structured logging extensions
├── Factories/                   AgentFactory, AgentExecutionContextFactory
├── Helpers/                     PromptTemplateHelper (mustache substitution), TokenEstimationHelper
├── Interfaces/                  41 contracts across 13 subdirectories
├── MediatRBehaviors/            6 pipeline behaviors (exception, context, audit, permission, hook, safety)
├── Middleware/                   ObservabilityMiddleware, ToolDiagnosticsMiddleware (chat client)
├── Models/                      Context models, tool execution progress/request/result
├── OpenTelemetry/               Metrics (7), Processors (2), Configurator (1)
├── Services/                    ContextBudgetTracker, TieredContextAssembler, AIToolConverter
└── DependencyInjection.cs
```

## Dependencies

- **Application.Common** — Pipeline behaviors, logging, DI patterns
- **Domain.AI** — Agent manifests, skill definitions, tool declarations
- **Microsoft.Agents.AI** — Agent framework
- **Microsoft.Extensions.AI** — Chat client abstraction, AITool contract
- **Azure.AI.Agents.Persistent** — Persistent agent storage
