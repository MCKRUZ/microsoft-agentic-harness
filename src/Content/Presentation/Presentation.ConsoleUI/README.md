# Presentation.ConsoleUI

This is where the harness comes to life. Presentation.ConsoleUI is an interactive terminal application built with [Spectre.Console](https://spectreconsole.net/) that demonstrates every capability of the Agentic Harness through six runnable examples — from a simple single-agent conversation to full multi-agent orchestration with task decomposition and synthesis.

It's not a production application. It's a playground, a demo, and a proof that all the abstractions in the layers below actually work together end-to-end.

---

## Running It

```bash
# Interactive menu
dotnet run --project src/Content/Presentation/Presentation.ConsoleUI

# Skip the menu — run a specific example directly
dotnet run --project src/Content/Presentation/Presentation.ConsoleUI -- --example research
dotnet run --project src/Content/Presentation/Presentation.ConsoleUI -- --example orchestrator
dotnet run --project src/Content/Presentation/Presentation.ConsoleUI -- --example mcp
dotnet run --project src/Content/Presentation/Presentation.ConsoleUI -- --example tools
dotnet run --project src/Content/Presentation/Presentation.ConsoleUI -- --example persistent
dotnet run --project src/Content/Presentation/Presentation.ConsoleUI -- --example a2a
```

## The Six Examples

### Research Agent — Start Here

The simplest demonstration. A standalone research agent with tool access runs a conversation: user message in, tool calls out, synthesized answer back. Shows the basic orchestration loop — `ExecuteAgentTurnCommand` dispatched via MediatR, flowing through the full pipeline (validation, tracing, permissions, hooks, safety).

Runs in both single-turn (one question, one answer) and multi-turn (follow-up questions with conversation history carried forward) modes.

### Orchestrator Agent — Multi-Agent Coordination

The flagship demo. Give it a complex task and watch it:

1. **Plan** — The orchestrator decomposes the task into subtasks and assigns each to a specialist sub-agent
2. **Delegate** — Each sub-agent runs its own conversation independently via `RunConversationCommand`
3. **Synthesize** — All sub-agent results are fed back to the orchestrator for a coherent final answer

This exercises `RunOrchestratedTaskCommand` — the most complex command in Application.Core — and demonstrates the full MediatR composition chain (orchestrator → conversation → agent turn).

### MCP Tools Discovery — External Tool Integration

Connects to configured external MCP servers, lists their available tools, and tests connectivity. Demonstrates the MCP client infrastructure: `McpConnectionManager` handling transport setup, `McpToolProvider` discovering tools, and graceful degradation when a server is unreachable.

### Tool Converter — The ITool-to-AITool Bridge

Shows the tool conversion pipeline in detail: resolves `ITool` instances from the DI container by key, converts each to a Microsoft.Extensions.AI `AITool` with JSON function-calling schemas, and displays the result. This is the bridge that makes keyed DI tools visible to the LLM.

### Persistent Agent — Azure AI Foundry

Creates (or looks up) a persistent agent on Azure AI Foundry — a server-side agent with threads that survive across sessions. Demonstrates the `ChatClientFactory`'s AI Foundry path and the persistent agent lifecycle.

### A2A Agent-to-Agent — Distributed Collaboration

Demonstrates the Agent-to-Agent protocol: displays the local agent's capability card, discovers remote agents via their `.well-known/agent.json` endpoints, and delegates a task over HTTP. Shows the full discovery-and-delegation flow that enables distributed multi-agent architectures.

## Application Bootstrap

`Program.cs` acts as the composition root for the console application:

1. Calls `Presentation.Common.GetServices()` to register all layers
2. Adds the six example classes as transient services
3. Parses command-line arguments (`--example <name>`)
4. Either routes to a specific example or launches the interactive menu

`App.cs` drives the interactive experience: a Spectre.Console selection prompt listing all examples plus a configuration viewer that displays the current `AppConfig` as a formatted table.

---

## Project Structure

```
Presentation.ConsoleUI/
├── Common/Helpers/
│   └── ConsoleHelper.cs               Spectre.Console utilities (headers, panels, tables, markup)
├── Examples/
│   ├── ResearchAgentExample.cs        Single/multi-turn conversation demo
│   ├── OrchestratorExample.cs         Multi-agent task decomposition + synthesis
│   ├── McpToolsExample.cs             MCP server discovery and tool listing
│   ├── ToolConverterExample.cs        ITool → AITool conversion pipeline
│   ├── PersistentAgentExample.cs      Azure AI Foundry persistent agents
│   └── A2AExample.cs                  Agent-to-Agent discovery + delegation
├── App.cs                             Interactive menu + config viewer
└── Program.cs                         Entry point + DI composition
```

## Dependencies

- **Presentation.Common** — Composition root (registers all layers)
- **Application.AI.Common** / **Application.Core** — Agent factories, CQRS commands
- **Domain.AI** / **Domain.Common** — Domain models, config
- **Spectre.Console** — Rich terminal UI (menus, tables, panels, colors)
- **Microsoft.Extensions.Configuration.UserSecrets** — Developer secrets
