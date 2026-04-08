# Microsoft Agentic Harness

A production-grade agent orchestration framework built on **Clean Architecture**, the **Microsoft Agent Framework**, and **Semantic Kernel**. Modeled after [Claude Code](https://claude.ai/claude-code)'s architecture — skills, tools, MCP, and a context-aware orchestration loop — running on .NET 10.

```
┌─────────────────────────────────────────────────────────────────────┐
│                        Presentation Layer                          │
│  ConsoleUI (Spectre.Console)  ·  LoggerUI  ·  MCP Server (WebAPI) │
├─────────────────────────────────────────────────────────────────────┤
│                        Application Layer                           │
│  CQRS Commands/Handlers  ·  Agent Factories  ·  Context Budget    │
│  Skill Loader  ·  Tool Converter  ·  FluentValidation Pipeline    │
├─────────────────────────────────────────────────────────────────────┤
│                       Infrastructure Layer                         │
│  Azure OpenAI / AI Foundry  ·  MCP Client  ·  Connectors          │
│  Observability (OTel)  ·  State Management  ·  API Access          │
├─────────────────────────────────────────────────────────────────────┤
│                          Domain Layer                              │
│  Agent Manifests  ·  Skill Definitions  ·  Tool Declarations      │
│  A2A Agent Cards  ·  Workflow State  ·  Configuration Hierarchy    │
└─────────────────────────────────────────────────────────────────────┘
```

---

## Features

### Agent Orchestration
- **Conversation loop** with configurable turn limits and token budgets
- **Multi-agent orchestration** — an orchestrator agent decomposes tasks and delegates to specialized sub-agents
- **Persistent agents** via Azure AI Foundry for long-running, stateful interactions
- **Agent-to-Agent (A2A)** protocol for cross-service agent discovery and task delegation

### Progressive Disclosure Skills
A 3-tier context loading system inspired by Claude Code's skills architecture:

| Tier | Analogy | Token Budget | When Loaded |
|------|---------|--------------|-------------|
| **Tier 1** — Index Card | Name, description, tags | ~100 tokens | Always (startup) |
| **Tier 2** — Folder | Full instructions, tool declarations | ~5,000 tokens | On skill selection |
| **Tier 3** — Filing Cabinet | Scripts, references, templates | Unbounded | On skill execution |

Skills are declared in `SKILL.md` files — human-readable Markdown parsed at runtime — so agents can be configured without recompilation.

### Tool System
- **Keyed DI registration** — tools registered with string keys (`"file_system"`, `"calculation_engine"`) for lazy resolution
- **ITool → AITool conversion** — bridges internal tool implementations to the Microsoft.Extensions.AI `AITool` contract
- **Sandboxed execution** — `FileSystemService` restricts file access to explicitly allowed base paths
- **Schema generation** — tool JSON schemas derived from .NET types for LLM function-calling

### MCP (Model Context Protocol)
- **MCP Server** — ASP.NET Core WebAPI exposing tools, prompts, and resources via MCP HTTP transport with JWT Bearer authentication
- **MCP Client** — discovers and invokes tools from external MCP servers, converting them into the native `AITool` format
- **Rate limiting** on all MCP endpoints

### Observability
- **OpenTelemetry** tracing, metrics, and logging across the full agent execution pipeline
- **Prometheus** metrics exporter
- **Jaeger** trace exporter
- **LLM-aware span processing** — tags conversation IDs, turn indices, and agent names on AI spans
- **Health checks** with configurable UI dashboard

### Enterprise Patterns
- **CQRS + MediatR** pipeline with validation, caching, performance logging, and exception handling behaviors
- **FluentValidation** on all DTOs with assembly-scanned auto-discovery
- **Result\<T\>** pattern for expected failures — no exceptions for business logic errors
- **Strongly-typed configuration** hierarchy (`AppConfig`) with Options pattern and `IOptionsMonitor<T>`
- **Permission-based authorization** with dynamic policy names and claim-based evaluation
- **Security hardening** — path traversal protection, timing-safe comparisons, CORS allowlists, attack detection middleware

---

## Architecture

The project follows **Clean Architecture** with strict dependency inversion — each layer only depends inward.

### Solution Structure

```
src/
├── AgenticHarness.slnx                          # Lightweight solution file
│
├── Content/Domain/                               # 🔵 Domain Layer (depends on nothing)
│   ├── Domain.Common/
│   │   ├── Config/AI/                            #   AppConfig, AIConfig, A2AConfig, AIFoundryConfig
│   │   ├── Constants/                            #   ClaimConstants, PolicyNameConstants
│   │   └── Workflow/                             #   IStateManager, WorkflowState
│   └── Domain.AI/
│       ├── Agents/                               #   AgentManifest, AgentExecutionContext, SkillReference
│       ├── Skills/                               #   SkillDefinition, ContextContract, ContextLoading
│       ├── Tools/                                #   ToolDeclaration
│       └── A2A/                                  #   AgentCard
│
├── Content/Application/                          # 🟢 Application Layer (depends on Domain)
│   ├── Application.Common/
│   │   ├── Behaviors/                            #   MediatR pipeline behaviors
│   │   ├── Extensions/                           #   Guard clauses, string helpers
│   │   ├── Factories/                            #   AzureCredentialFactory
│   │   └── Interfaces/                           #   Cross-cutting contracts
│   ├── Application.AI.Common/
│   │   ├── Factories/                            #   AgentFactory, AgentExecutionContextFactory
│   │   ├── Interfaces/                           #   IAgentFactory, IChatClientFactory, ISkillLoaderService
│   │   │   ├── A2A/                              #   IA2AAgentHost
│   │   │   └── Context/                          #   IContextBudgetTracker, ITieredContextAssembler
│   │   ├── Models/Context/                       #   ContextModels (tier enums, budget types)
│   │   └── Services/
│   │       ├── Context/                          #   ContextBudgetTracker, TieredContextAssembler
│   │       └── Tools/                            #   AIToolConverter
│   └── Application.Core/
│       ├── Agents/
│       │   ├── AgentDefinitions.cs               #   Agent name constants
│       │   └── Skills/                           #   SKILL.md files per agent
│       │       ├── orchestrator-agent/
│       │       └── research-agent/
│       ├── CQRS/Agents/
│       │   ├── ExecuteAgentTurn/                 #   Single turn: send messages, receive + execute tool calls
│       │   ├── RunConversation/                  #   Full conversation loop with turn management
│       │   └── RunOrchestratedTask/              #   Multi-agent: decompose → delegate → synthesize
│       └── DependencyInjection.cs
│
├── Content/Infrastructure/                       # 🟠 Infrastructure Layer (implements Application interfaces)
│   ├── Infrastructure.Common/                    #   Identity service, claim extensions, base DI
│   ├── Infrastructure.AI/
│   │   ├── Factories/                            #   ChatClientFactory (Azure OpenAI, AI Foundry, SK)
│   │   ├── A2A/                                  #   A2AAgentHost (discovery, delegation)
│   │   ├── Helpers/                              #   AgentFrameworkHelper
│   │   ├── Tools/                                #   FileSystemService (sandboxed), CalculationService
│   │   ├── StateManagement/                      #   JSON + Markdown dual persistence, checkpoints
│   │   └── Generators/                           #   Schema generation for tool declarations
│   ├── Infrastructure.AI.Connectors/             #   Unified external API adapter system with ITool bridge
│   ├── Infrastructure.AI.MCP/                    #   MCP client — discover and invoke external MCP tools
│   ├── Infrastructure.AI.MCPServer/              #   MCP server — expose tools/prompts/resources via HTTP
│   ├── Infrastructure.APIAccess/                 #   HTTP config, resilience policies, middleware
│   └── Infrastructure.Observability/             #   OTel pipeline, Prometheus, Jaeger, LLM span processor
│
└── Content/Presentation/                         # 🔴 Presentation Layer (composition root)
    ├── Presentation.Common/                      #   DI composition root, service wiring
    ├── Presentation.ConsoleUI/                   #   Interactive Spectre.Console menu + 6 examples
    └── Presentation.LoggerUI/                    #   Named pipe log viewer
```

### Dependency Flow

```
Domain.Common ◄─── Domain.AI
     ▲                  ▲
     │                  │
Application.Common ◄── Application.AI.Common ◄── Application.Core
     ▲                  ▲                              ▲
     │                  │                              │
Infrastructure.*  ─────►│◄──── Infrastructure.AI ──────┘
                        │
Presentation.Common ────┘ (composition root — references all layers)
```

---

## Getting Started

### Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
- An Azure OpenAI resource (or OpenAI API key)
- Optional: [Jaeger](https://www.jaegertracing.io/) for distributed tracing
- Optional: Azure AI Foundry project for persistent agents

### Clone & Build

```bash
git clone https://github.com/MCKRUZ/microsoft-agentic-harness.git
cd microsoft-agentic-harness
dotnet build src/AgenticHarness.slnx
```

### Configure

The harness uses a strongly-typed `AppConfig` hierarchy. Edit `appsettings.json` or use [User Secrets](https://learn.microsoft.com/en-us/aspnet/core/security/app-secrets):

```json
{
  "AppConfig": {
    "Agent": {
      "MaxTurnsPerConversation": 10,
      "DefaultTokenBudget": 128000
    },
    "AI": {
      "AgentFramework": {
        "DefaultDeployment": "gpt-4o",
        "ClientType": "AzureOpenAI"
      },
      "Skills": {
        "BasePath": "skills",
        "AdditionalPaths": []
      }
    },
    "Observability": {
      "EnableTracing": true,
      "EnableMetrics": true,
      "SamplingRatio": 1.0
    },
    "Cache": {
      "CacheType": "Memory"
    }
  }
}
```

For Azure OpenAI, set your credentials via User Secrets:

```bash
cd src/Content/Presentation/Presentation.ConsoleUI
dotnet user-secrets set "AppConfig:AI:AgentFramework:Endpoint" "https://your-resource.openai.azure.com/"
dotnet user-secrets set "AppConfig:AI:AgentFramework:ApiKey" "your-api-key"
```

### Run

```bash
# Interactive menu
dotnet run --project src/Content/Presentation/Presentation.ConsoleUI

# Run a specific example directly
dotnet run --project src/Content/Presentation/Presentation.ConsoleUI -- --example research
dotnet run --project src/Content/Presentation/Presentation.ConsoleUI -- --example orchestrator
dotnet run --project src/Content/Presentation/Presentation.ConsoleUI -- --example a2a
```

### Run Tests

```bash
dotnet test src/AgenticHarness.slnx
dotnet test src/AgenticHarness.slnx --collect:"XPlat Code Coverage"
```

---

## Console Examples

The `Presentation.ConsoleUI` provides an interactive [Spectre.Console](https://spectreconsole.net/) menu with six runnable demonstrations:

| Example | Description |
|---------|-------------|
| **Research Agent** | Standalone agent with tool use — sends a query, receives tool calls, executes them, returns synthesized results |
| **Orchestrator Agent** | Multi-agent orchestration — decomposes a complex task into subtasks, delegates to specialized sub-agents, synthesizes final output |
| **MCP Tools Discovery** | Connects to external MCP servers, discovers available tools, and demonstrates remote tool invocation |
| **Tool Converter Demo** | Shows the `ITool` → `AITool` conversion pipeline, including schema generation and keyed DI resolution |
| **Persistent Agent** | Creates a long-running agent via Azure AI Foundry with persistent threads and state |
| **A2A Agent-to-Agent** | Demonstrates agent card publishing, remote agent discovery via well-known endpoints, and cross-agent task delegation |

---

## Key Systems Deep Dive

### CQRS Pipeline

Every agent operation flows through the MediatR pipeline:

```
Request → ValidationBehavior → CachingBehavior → PerformanceBehavior → Handler → Response
                  │                                       │
          FluentValidation                         Logs slow queries
          (auto-discovered)                        (> SlowThresholdSec)
```

Three CQRS commands drive agent operations:

| Command | Purpose |
|---------|---------|
| `ExecuteAgentTurn` | Single turn — send messages to the LLM, execute any returned tool calls, return the response |
| `RunConversation` | Full conversation loop — repeats `ExecuteAgentTurn` until the agent completes or hits the turn limit |
| `RunOrchestratedTask` | Multi-agent — the orchestrator decomposes the task, creates `RunConversation` commands for sub-agents, and synthesizes their results |

### Context Budget Tracking

The `IContextBudgetTracker` monitors token consumption across the agent's context window:

- Tracks allocations per source (system prompt, skills, tools, conversation history)
- Enforces a configurable `DefaultTokenBudget` (default: 128K)
- The `ITieredContextAssembler` uses budget availability to decide which skill tier to load

### Agent Factory Chain

```
SkillDefinition
      │
      ▼
AgentExecutionContextFactory.MapToAgentContext()    ← maps skill → execution context
      │
      ▼
AgentExecutionContext
      │
      ▼
AgentFactory.CreateAgent()                          ← wires chat client + tools + middleware
      │
      ▼
IChatClient (with tool dispatch, observability, content safety middleware)
```

### A2A Protocol

The Agent-to-Agent protocol enables distributed agent collaboration:

1. **Agent Card** — each agent publishes a card (name, description, capabilities URL)
2. **Discovery** — agents query `/.well-known/agent.json` endpoints to find peers
3. **Delegation** — the orchestrator sends tasks to remote agents via HTTP, receiving structured results

---

## Configuration Reference

The `AppConfig` hierarchy provides strongly-typed configuration throughout the application:

| Section | Key Settings |
|---------|-------------|
| `AppConfig.Common` | `SlowThresholdSec` — MediatR performance behavior threshold |
| `AppConfig.Agent` | `MaxTurnsPerConversation`, `DefaultTokenBudget` |
| `AppConfig.AI.AgentFramework` | `DefaultDeployment`, `ClientType` (AzureOpenAI, OpenAI, AIFoundry) |
| `AppConfig.AI.Skills` | `BasePath`, `AdditionalPaths` for SKILL.md discovery |
| `AppConfig.AI.AIFoundry` | `ProjectEndpoint` for persistent agents |
| `AppConfig.AI.A2A` | `Enabled`, `AgentName`, `AgentDescription`, `BaseUrl`, `DiscoveryEndpoints` |
| `AppConfig.AI.MCP` | `ServerName` — MCP server identity |
| `AppConfig.AI.McpServers` | `Servers[]` — external MCP server connections |
| `AppConfig.Observability` | `EnableTracing`, `EnableMetrics`, `SamplingRatio` |
| `AppConfig.Cache` | `CacheType` (Memory, Redis) |
| `AppConfig.Logging` | `LogsBasePath`, `PipeName` for named pipe log streaming |

---

## Tech Stack

| Category | Technology |
|----------|-----------|
| **Runtime** | .NET 10, C# 14 |
| **AI Framework** | Microsoft.Agents.AI, Microsoft.Extensions.AI, Semantic Kernel |
| **LLM Providers** | Azure OpenAI, OpenAI, Azure AI Foundry |
| **Architecture** | Clean Architecture, CQRS, MediatR |
| **Validation** | FluentValidation (assembly-scanned) |
| **MCP** | ModelContextProtocol SDK (HTTP transport, JWT auth) |
| **Observability** | OpenTelemetry, Prometheus, Jaeger, Azure Monitor |
| **Security** | Azure Identity, MSAL, JWT Bearer, CORS allowlists |
| **Caching** | IMemoryCache, StackExchange.Redis |
| **Console UI** | Spectre.Console |
| **Testing** | xUnit, Moq, coverlet |
| **Configuration** | Options pattern, User Secrets, Azure App Configuration, Key Vault |

---

## Design Principles

This project follows a set of deliberate architectural choices:

1. **Immutability first** — records, `init`-only properties, `IReadOnlyList<T>`, `with` expressions
2. **Validate at boundaries, trust internal code** — FluentValidation at the MediatR pipeline level, no defensive checks inside handlers
3. **Result\<T\> over exceptions** — expected failures return typed results; exceptions are for truly exceptional conditions
4. **Keyed DI for extensibility** — tools and connectors registered with string keys for lazy resolution from skill declarations
5. **Progressive disclosure** — load only the context an agent needs, when it needs it, to stay within token budgets
6. **Replace, don't deprecate** — no compatibility shims, no legacy fallbacks
7. **Factory chain** — consistent construction via `SkillDefinition → AgentExecutionContext → IChatClient` pipeline

---

## Project Status

This is an active proof-of-concept. The core architecture, domain models, application services, infrastructure implementations, and console examples are complete and building.

**Implemented:**
- Clean Architecture layer separation with strict dependency inversion
- Domain models for agents, skills, tools, A2A, and workflow state
- Application-layer factories, context budget tracking, and tiered context assembly
- CQRS commands for single-turn, conversation, and orchestrated multi-agent execution
- Infrastructure implementations: Azure OpenAI, MCP client/server, connectors, observability
- ConsoleUI with 6 interactive examples
- Security hardening: path traversal protection, sandboxed file access, timing-safe auth
- Full OpenTelemetry pipeline with LLM-aware span processing

---

## License

This project is provided as-is for educational and reference purposes.
