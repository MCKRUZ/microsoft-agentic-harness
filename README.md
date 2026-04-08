# Microsoft Agentic Harness

If you've ever used [Claude Code](https://claude.ai/claude-code) and wondered *"how does this thing actually work under the hood?"* — this project is an answer to that question, built on the Microsoft stack.

The Agentic Harness is a proof-of-concept that reconstructs the architecture behind modern AI coding agents: the skills system that decides what an agent knows, the tool system that decides what it can do, the context budget that decides how much it can hold in its head at once, and the orchestration loop that ties it all together. It runs on .NET 10, uses Clean Architecture, and speaks the same protocols (MCP, A2A) that the broader agent ecosystem is converging on.

It's not a chatbot wrapper. It's the plumbing that makes agents feel intelligent.

---

## The Problem

Most "agent" implementations are a prompt, an API call, and a prayer. They work for demos. They fall apart when you need an agent that can:

- **Use tools safely** — not just call functions, but do so within a security sandbox where file access is restricted to explicitly allowed paths and every tool call flows through content safety middleware.
- **Manage its own context** — LLMs have finite context windows, and a naive agent that dumps everything into the prompt will drown before it gets useful. The agent needs to know what to load, when to load it, and when to let go.
- **Collaborate with other agents** — not through shared memory or global state, but through real protocols where agents discover each other, negotiate capabilities, and delegate work.
- **Be observable** — when an agent makes a bad decision three tool calls deep in a multi-turn conversation, you need traces, not guesswork.

This harness solves those problems with real engineering, not abstractions on top of abstractions.

---

## How It Works

### The Orchestration Loop

At its core, the harness runs a conversation loop. A user sends a message. The agent processes it, decides whether to respond directly or use a tool, executes any tool calls, and feeds the results back into the next turn. This repeats until the agent has an answer or hits a configurable turn limit.

This sounds simple, but the devil is in the execution. Every turn flows through a CQRS pipeline:

```
Request --> Validation --> Caching --> Performance Logging --> Handler --> Response
```

Validation catches malformed requests before they reach the LLM. Performance logging flags turns that take too long. The handler itself is where the actual AI interaction happens — sending messages to Azure OpenAI (or AI Foundry, or a Semantic Kernel backend), processing tool calls, and managing conversation state.

Three commands drive everything:

- **ExecuteAgentTurn** handles a single turn — one round of messages in, tool calls out, results back.
- **RunConversation** wraps the full loop — calling ExecuteAgentTurn repeatedly until the agent says it's done.
- **RunOrchestratedTask** is where it gets interesting — an orchestrator agent decomposes a complex task into subtasks, spins up sub-agents for each one, runs their conversations in parallel, and synthesizes the results.

### Skills: Teaching Agents What They Know

The skills system is inspired directly by how Claude Code loads context. The insight is that an agent doesn't need to know everything all the time — it needs the right knowledge at the right moment, and it needs to stay within its token budget.

Skills use a three-tier progressive disclosure model:

**Tier 1 — The Index Card (~100 tokens).** A name, a description, a few tags. This is all the agent sees at startup. Enough to know the skill exists and decide whether it's relevant. Every skill in the system is loaded at this tier — the overhead is negligible.

**Tier 2 — The Folder (~5,000 tokens).** Full instructions, tool declarations, behavioral guidelines. When the agent selects a skill, Tier 2 loads into context. This is where the skill becomes useful — the agent now knows *how* to use it, not just *that* it exists.

**Tier 3 — The Filing Cabinet (unbounded).** Scripts, reference documents, templates, examples. Only loaded when the skill is actively executing. This is the heavy context that would blow the budget if loaded eagerly.

Skills are declared in `SKILL.md` files — plain Markdown that humans can read and edit without touching code. An orchestrator agent's skill file describes how to decompose tasks. A research agent's describes how to find and analyze information. Drop a new `SKILL.md` into the skills directory and the agent picks it up at runtime.

The `IContextBudgetTracker` watches over all of this, tracking how many tokens are allocated to the system prompt, loaded skills, tool schemas, and conversation history. When budget runs low, the `ITieredContextAssembler` knows to stop loading Tier 2 content and fall back to Index Cards.

### Tools: What Agents Can Do

Tools are the agent's hands. The harness treats them as first-class citizens with their own lifecycle:

Tools are registered in the DI container with string keys — `"file_system"`, `"calculation_engine"`, etc. They're not eagerly loaded. When a skill declares that it needs the `file_system` tool, the harness resolves it from the container at that moment. This keeps the tool surface dynamic: different skills can offer different tools to the same underlying agent.

Every internal `ITool` implementation gets converted to the `AITool` contract that Microsoft.Extensions.AI expects. The `AIToolConverter` handles this bridge — reading the tool's schema, generating the JSON function-calling definition, and wiring up the execution callback. From the LLM's perspective, it's just a function it can call. From the harness's perspective, it's a sandboxed, audited, type-safe operation.

The `FileSystemService` is a good example of why this matters. It implements file operations — read, write, list — but only within explicitly allowed base paths. Any attempt to traverse outside those paths is caught and rejected. The agent thinks it has a file system. It actually has a cage.

### MCP: Extending the Tool Surface

The Model Context Protocol is how the harness connects to the outside world. It works in both directions.

As an **MCP Server**, the harness exposes its tools, prompts, and resources over HTTP with JWT Bearer authentication. External systems — other agents, IDEs, automation pipelines — can discover and invoke these capabilities through a standardized protocol. Rate limiting protects against abuse.

As an **MCP Client**, the harness discovers tools hosted on external MCP servers and converts them into native `AITool` instances. This means an agent can seamlessly use tools from third-party services alongside its built-in capabilities. The agent doesn't know or care where a tool lives — it just calls it.

### A2A: Agents Talking to Agents

The Agent-to-Agent protocol handles distributed collaboration. Each agent publishes an Agent Card — a JSON document describing its name, capabilities, and endpoint URL — at a well-known location (`/.well-known/agent.json`). When the orchestrator needs to delegate work, it discovers available agents by querying these endpoints, selects the right one for the job, and sends it a task over HTTP.

This is how the multi-agent orchestration actually works in practice. The orchestrator doesn't have hardcoded knowledge of its sub-agents. It discovers them, reads their capabilities, and makes routing decisions dynamically.

### Observability: Seeing Inside the Black Box

LLM-powered agents are notoriously hard to debug. A conversation that goes sideways might involve dozens of turns, multiple tool calls, and branching logic that's invisible from the outside.

The harness instruments everything with OpenTelemetry. Every agent turn creates a span. Every tool call creates a child span. Conversation IDs, turn indices, and agent names are tagged automatically by a custom span processor that understands AI workloads — it recognizes spans from Microsoft.Extensions.AI, Semantic Kernel, and Azure.AI.OpenAI and enriches them with agentic context.

Traces flow to Jaeger for visualization. Metrics flow to Prometheus. Logs flow through structured JSON logging. When something goes wrong, you don't guess — you trace the exact path the agent took, see what tools it called, and read the token counts at each step.

---

## Architecture

The project follows Clean Architecture with strict dependency inversion. Each layer depends only inward — Infrastructure implements Application interfaces, never the reverse.

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

### Solution Structure

```
src/
├── AgenticHarness.slnx
│
├── Content/Domain/
│   ├── Domain.Common/
│   │   ├── Config/AI/                  AppConfig, AIConfig, A2AConfig, AIFoundryConfig
│   │   ├── Constants/                  ClaimConstants, PolicyNameConstants
│   │   └── Workflow/                   IStateManager, WorkflowState
│   └── Domain.AI/
│       ├── Agents/                     AgentManifest, AgentExecutionContext, SkillReference
│       ├── Skills/                     SkillDefinition, ContextContract, ContextLoading
│       ├── Tools/                      ToolDeclaration
│       └── A2A/                        AgentCard
│
├── Content/Application/
│   ├── Application.Common/
│   │   ├── Behaviors/                  MediatR pipeline behaviors
│   │   ├── Extensions/                 Guard clauses, string helpers
│   │   ├── Factories/                  AzureCredentialFactory
│   │   └── Interfaces/                 Cross-cutting contracts
│   ├── Application.AI.Common/
│   │   ├── Factories/                  AgentFactory, AgentExecutionContextFactory
│   │   ├── Interfaces/                 IAgentFactory, IChatClientFactory, ISkillLoaderService
│   │   ├── Models/Context/             ContextModels (tier enums, budget types)
│   │   └── Services/                   ContextBudgetTracker, TieredContextAssembler, AIToolConverter
│   └── Application.Core/
│       ├── Agents/Skills/              SKILL.md files per agent
│       └── CQRS/Agents/               ExecuteAgentTurn, RunConversation, RunOrchestratedTask
│
├── Content/Infrastructure/
│   ├── Infrastructure.Common/          Identity service, claim extensions
│   ├── Infrastructure.AI/              ChatClientFactory, A2AAgentHost, sandboxed tools, state management
│   ├── Infrastructure.AI.Connectors/   Unified external API adapters with ITool bridge
│   ├── Infrastructure.AI.MCP/          MCP client — discover and invoke remote tools
│   ├── Infrastructure.AI.MCPServer/    MCP server — expose tools/prompts/resources via HTTP
│   ├── Infrastructure.APIAccess/       HTTP resilience policies, security middleware
│   └── Infrastructure.Observability/   OTel pipeline, Prometheus, Jaeger, LLM span processor
│
└── Content/Presentation/
    ├── Presentation.Common/            DI composition root
    ├── Presentation.ConsoleUI/         Interactive menu + 6 runnable examples
    └── Presentation.LoggerUI/          Named pipe log viewer
```

### Dependency Flow

```
Domain.Common <--- Domain.AI
     ^                  ^
     |                  |
Application.Common <-- Application.AI.Common <-- Application.Core
     ^                  ^                              ^
     |                  |                              |
Infrastructure.*  ----->|<---- Infrastructure.AI ------'
                        |
Presentation.Common ----' (composition root — references all layers)
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

The harness uses a strongly-typed `AppConfig` hierarchy bound through the Options pattern. The defaults in `appsettings.json` get you started:

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
        "BasePath": "skills"
      }
    },
    "Observability": {
      "EnableTracing": true,
      "EnableMetrics": true,
      "SamplingRatio": 1.0
    }
  }
}
```

Secrets go in User Secrets — never in config files:

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

## Try It Out

The ConsoleUI launches an interactive [Spectre.Console](https://spectreconsole.net/) menu with six examples that demonstrate the harness at different levels of complexity:

**Start here:** The **Research Agent** runs a standalone conversation — one agent, a few tools, a question to answer. It shows the basic loop: user message in, tool calls out, synthesized answer back.

**Then try:** The **Orchestrator Agent** adds multi-agent coordination. Give it a complex task and watch it decompose the work, spin up sub-agents, and merge their results into a coherent output.

**Go deeper:** **MCP Tools Discovery** connects to external MCP servers and pulls in remote tools at runtime. **Tool Converter Demo** shows the `ITool` to `AITool` bridge that makes keyed DI tools visible to the LLM. **Persistent Agent** creates a long-running agent on Azure AI Foundry with threads that survive across sessions. **A2A Agent-to-Agent** demonstrates the full discovery-and-delegation protocol between distributed agents.

---

## Configuration Reference

| Section | What it controls |
|---------|-----------------|
| `AppConfig.Agent` | Turn limits (`MaxTurnsPerConversation`), token budget (`DefaultTokenBudget`) |
| `AppConfig.AI.AgentFramework` | Model deployment (`DefaultDeployment`), provider (`ClientType`: AzureOpenAI, OpenAI, AIFoundry) |
| `AppConfig.AI.Skills` | Skill discovery paths (`BasePath`, `AdditionalPaths`) |
| `AppConfig.AI.AIFoundry` | Persistent agent endpoint (`ProjectEndpoint`) |
| `AppConfig.AI.A2A` | Agent-to-Agent settings (`Enabled`, `AgentName`, `BaseUrl`, `DiscoveryEndpoints`) |
| `AppConfig.AI.MCP` | MCP server identity (`ServerName`) |
| `AppConfig.AI.McpServers` | External MCP server connections (`Servers[]`) |
| `AppConfig.Observability` | Tracing, metrics, and sampling (`EnableTracing`, `SamplingRatio`) |
| `AppConfig.Cache` | Cache backend (`CacheType`: Memory or Redis) |
| `AppConfig.Logging` | Log output (`LogsBasePath`, `PipeName` for named pipe streaming) |

---

## Tech Stack

| | |
|---|---|
| **Runtime** | .NET 10, C# 14 |
| **AI** | Microsoft.Agents.AI, Microsoft.Extensions.AI, Semantic Kernel |
| **LLM Providers** | Azure OpenAI, OpenAI, Azure AI Foundry |
| **Architecture** | Clean Architecture, CQRS (MediatR), FluentValidation |
| **Protocols** | MCP (HTTP transport, JWT auth), A2A (agent discovery + delegation) |
| **Observability** | OpenTelemetry, Prometheus, Jaeger, Azure Monitor |
| **Security** | Azure Identity, JWT Bearer, CORS allowlists, sandboxed tool execution |
| **Testing** | xUnit, Moq, coverlet |

---

## License

This project is provided as-is for educational and reference purposes.
