# Presentation.ConsoleUI

An interactive terminal application that demonstrates every capability of the Agentic Harness through nine runnable examples -- from single-agent conversations to multi-agent orchestration, RAG pipeline demos, and Azure AI Foundry persistent agents. Built with Spectre.Console for rich terminal UI (menus, tables, colored output).

When you run it, you see a selection menu with categorized choices. Pick an example, and it runs end-to-end against your configured AI backend, exercising the full stack: Domain models, Application CQRS handlers, Infrastructure AI services, and tool execution. This is the fastest way to verify the harness works without needing a browser.

## Architecture Context

```
┌──────────────────────────────────────────────────────────────────────┐
│  Presentation.ConsoleUI  (Composition Root -- Console Mode)          │
│                                                                      │
│  Program.cs                                                          │
│    services.GetServices(includeHealthChecksUI: false)                 │
│    services.AddTransient<ResearchAgentExample>()                      │
│    services.AddTransient<OrchestratorExample>()                       │
│    ... (register all example classes)                                 │
│    serviceProvider.BuildServiceProvider()                             │
│    → Start hosted services (skill seeding, MCP connections)          │
│    → Route to example or interactive menu                            │
│                                                                      │
│  ┌────────────────────────────────────────────────────────────────┐  │
│  │  App.cs (Spectre.Console interactive menu)                     │  │
│  │                                                                │  │
│  │  [Agents]         Research | Orchestrator | Meta-Optimizer     │  │
│  │  [Retrieval]      RAG Pipeline Demo                           │  │
│  │  [Advanced]       MCP Tools | Tool Converter | Persistent | A2A│  │
│  │  [Setup]          User Secrets | Show Configuration           │  │
│  │  Exit                                                         │  │
│  └────────────────────────────────────────────────────────────────┘  │
└──────────────────────────────────────────────────────────────────────┘
                              │
         MediatR dispatch (ExecuteAgentTurnCommand, RunConversationCommand, etc.)
                              │
                              ▼
         Application.Core → Infrastructure.AI → Azure OpenAI / MCP / Tools
```

Unlike AgentHub (which uses `IHost` and ASP.NET Core), ConsoleUI uses a bare `ServiceCollection` + `ServiceProvider`. It manually starts hosted services (skill seeding, etc.) since there's no generic host to do it automatically.

## Key Concepts

### The Nine Examples

#### Research Agent (Start Here)

The simplest demonstration. A standalone research agent with tool access runs a conversation: user message in, tool calls executed, synthesized answer back. Shows the basic orchestration loop via `ExecuteAgentTurnCommand`.

```bash
dotnet run --project src/Content/Presentation/Presentation.ConsoleUI -- --example research
```

#### Orchestrator Agent (Multi-Agent)

The flagship demo. Decomposes a complex task into subtasks, assigns each to specialist sub-agents, runs them independently, and synthesizes results. Exercises `RunConversationCommand` with nested `ExecuteAgentTurnCommand` calls.

#### RAG Pipeline Demo

End-to-end Retrieval-Augmented Generation: ingest a document (chunking, embedding), then query it with hybrid retrieval (dense + sparse), reranking, and CRAG evaluation.

#### MCP Tools Discovery

Connects to configured MCP servers, lists their tools, and tests connectivity. Shows `McpConnectionManager` transport setup and graceful degradation.

#### Tool Converter Demo

Resolves `ITool` instances from DI by key, converts each to `AITool` with JSON function-calling schemas, and displays the result. Demonstrates the bridge from keyed DI tools to LLM-visible function definitions.

#### Persistent Agent (Azure AI Foundry)

Creates or looks up a server-side persistent agent with threads that survive across sessions. Demonstrates `ChatClientFactory`'s AI Foundry path.

#### A2A Agent-to-Agent

Displays the local agent's capability card, discovers remote agents via `.well-known/agent.json`, and delegates a task over HTTP. Shows the full Agent-to-Agent discovery-and-delegation protocol.

#### Setup User Secrets

Interactive wizard that configures `dotnet user-secrets` for the harness: API endpoint, API key, deployment name, and other required settings.

#### Meta-Harness Optimizer

An advanced demo that uses an agent to analyze and optimize the harness's own configuration, demonstrating self-referential agent capabilities.

### Command-Line Interface

```bash
# Interactive menu (default)
dotnet run --project src/Content/Presentation/Presentation.ConsoleUI

# Direct example execution (no menu)
dotnet run --project src/Content/Presentation/Presentation.ConsoleUI -- --example research
dotnet run --project src/Content/Presentation/Presentation.ConsoleUI -- --example orchestrator
dotnet run --project src/Content/Presentation/Presentation.ConsoleUI -- --example rag-pipeline
dotnet run --project src/Content/Presentation/Presentation.ConsoleUI -- --example mcp-tools
dotnet run --project src/Content/Presentation/Presentation.ConsoleUI -- --example tool-converter
dotnet run --project src/Content/Presentation/Presentation.ConsoleUI -- --example persistent-agent
dotnet run --project src/Content/Presentation/Presentation.ConsoleUI -- --example a2a
dotnet run --project src/Content/Presentation/Presentation.ConsoleUI -- --example setup-secrets
dotnet run --project src/Content/Presentation/Presentation.ConsoleUI -- --example optimize
```

### Configuration Display

The "Show Configuration" menu option renders a formatted table showing current `AppConfig` values: deployment name, AI Foundry endpoint, MCP server count, A2A status, logging paths, cache type, and OTel sampling state.

## Project Structure

```
Presentation.ConsoleUI/
├── Common/
│   └── Helpers/
│       └── ConsoleHelper.cs             Spectre.Console utilities (headers, panels, errors)
├── Examples/
│   ├── ResearchAgentExample.cs          Single/multi-turn standalone agent
│   ├── OrchestratorExample.cs           Multi-agent task decomposition + synthesis
│   ├── RagPipelineExample.cs            Document ingestion + hybrid retrieval
│   ├── McpToolsExample.cs              MCP server connectivity + tool listing
│   ├── ToolConverterExample.cs          ITool → AITool conversion pipeline
│   ├── PersistentAgentExample.cs        Azure AI Foundry persistent agents
│   ├── A2AExample.cs                    Agent-to-Agent discovery + delegation
│   ├── SetupSecretsExample.cs           User secrets configuration wizard
│   └── OptimizeExample.cs              Meta-harness self-optimization
├── App.cs                               Interactive Spectre.Console menu + config viewer
├── Program.cs                           Entry point, DI composition, CLI routing
├── appsettings.json                     Default configuration
└── appsettings.Development.json         Development overrides
```

## Key Types Reference

| Type | Purpose | Entry Point |
|------|---------|-------------|
| `Program` | Composition root, CLI argument parsing, hosted service startup | `Main(args)` |
| `App` | Interactive menu loop with Spectre.Console prompts | `RunAsync()` / `RunExampleAsync(name)` |
| `ResearchAgentExample` | Simplest agent demo | `--example research` |
| `OrchestratorExample` | Multi-agent orchestration | `--example orchestrator` |
| `RagPipelineExample` | RAG ingest + search | `--example rag-pipeline` |
| `McpToolsExample` | MCP server discovery | `--example mcp-tools` |
| `ConsoleHelper` | Rich terminal output (headers, error panels, tables) | All examples |

## Configuration

Uses the same `appsettings.json` structure as AgentHub. Key settings:

```json
{
  "AppConfig": {
    "AI": {
      "AgentFramework": {
        "DefaultDeployment": "gpt-4o",
        "ClientType": "AzureOpenAI",
        "Endpoint": "-- set via user-secrets --",
        "ApiKey": "-- set via user-secrets --"
      },
      "Skills": { "BasePath": "skills" },
      "McpServers": { "Servers": [] }
    },
    "Logging": { "PipeName": "AgenticHarnessLogs.ConsoleUI" }
  }
}
```

**User Secrets ID:** `agentic-harness-console-ui` (shared with AgentHub)

```bash
# First-time setup
dotnet user-secrets set "AppConfig:AI:AgentFramework:Endpoint" "https://your-endpoint.openai.azure.com/" --id agentic-harness-console-ui
dotnet user-secrets set "AppConfig:AI:AgentFramework:ApiKey" "your-api-key" --id agentic-harness-console-ui
```

## How to Run

```bash
# Prerequisites: .NET 10 SDK, configured user secrets

# Interactive mode
dotnet run --project src/Content/Presentation/Presentation.ConsoleUI

# Direct example
dotnet run --project src/Content/Presentation/Presentation.ConsoleUI -- --example research

# Run the setup wizard first if secrets aren't configured
dotnet run --project src/Content/Presentation/Presentation.ConsoleUI -- --example setup-secrets
```

**Output goes to terminal** -- rich Spectre.Console formatting with colors, panels, and tables.

**Logs go to named pipe** -- start LoggerUI in a separate terminal to see detailed structured logs:
```bash
dotnet run --project src/Content/Presentation/Presentation.LoggerUI
```

## Common Tasks

### Adding a New Example

1. Create `Examples/MyNewExample.cs` implementing a `RunAsync()` method
2. Register it in `Program.cs`: `services.AddTransient<MyNewExample>()`
3. Inject it into `App.cs` constructor
4. Add menu entry in `MainMenuAsync()` and case in the switch
5. Add CLI routing in `RunExampleAsync()` and `Program.Main()` docs

### Debugging Agent Behavior

1. Run LoggerUI in a second terminal for structured logs
2. Set `"LogLevel": { "Default": "Debug" }` in `appsettings.Development.json`
3. Use the Research Agent example -- simplest agent loop with full tracing
4. Check `traces/executions/` folder for execution manifests (JSON trace output)

## Dependencies

**Project References:**
- `Presentation.Common` -- composition root (`GetServices()`)
- `Application.AI.Common`, `Application.Common`, `Application.Core` -- agent factories, CQRS
- `Domain.AI`, `Domain.Common` -- domain models, configuration

**NuGet Packages:**
- `Spectre.Console` -- rich terminal UI (menus, tables, panels, colored markup)
- `Microsoft.Extensions.Configuration.UserSecrets` -- developer secrets

## Testing

**Test project:** `Tests/Presentation.ConsoleUI.Tests`

```bash
dotnet test src/AgenticHarness.slnx --filter "FullyQualifiedName~Presentation.ConsoleUI"
```

**Coverage areas:**
- CLI argument parsing (`--example` routing)
- Example class DI resolution
- Configuration display formatting
- Error handling (missing secrets, unreachable endpoints)
