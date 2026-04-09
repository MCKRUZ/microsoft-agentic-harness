# Application.Core

This is where the agent actually *runs*. Application.AI.Common defines what agents are and what they can do. Application.Core defines the three commands that make them do it.

Everything in this project follows the CQRS pattern: a command describes what should happen, a validator ensures it's safe to attempt, and a handler does the work. The three commands form a hierarchy — each level builds on the one below it.

---

## The Three Commands

### ExecuteAgentTurn — One Round

The simplest unit of agent work. A user sends a message, the agent processes it (possibly calling tools), and returns a response.

```
ExecuteAgentTurnCommand
  ├── AgentName          Which agent to run
  ├── UserMessage        The input
  ├── ConversationHistory  Prior context
  └── SystemPromptOverride Optional instruction override

→ AgentTurnResult
  ├── ResponseText       The agent's response
  ├── UpdatedHistory     Conversation with this turn appended
  └── ToolInvocations    List of tools the agent called
```

The handler creates an agent from the factory, runs a single turn via the Microsoft Agents AI framework's `RunAsync`, extracts the response (handling string, ChatResponse, and reflection fallback response types), and builds the updated conversation history.

### RunConversation — Multi-Turn Loop

Wraps `ExecuteAgentTurn` in a loop. Give it a list of user messages and a max turn limit, and it feeds them through one at a time, carrying conversation history forward between turns.

```
RunConversationCommand
  ├── AgentName
  ├── UserMessages[]     Sequence of user inputs
  ├── MaxTurns           Safety limit (1-100)
  └── ProgressCallback   Optional real-time status updates

→ ConversationResult
  ├── TurnSummaries[]    Per-turn results
  ├── FinalResponse      Last agent response
  └── TotalToolInvocations  Aggregate tool usage
```

This is the command the ConsoleUI examples use for standalone agent demos — the Research Agent answering questions, using tools, and building up context across multiple exchanges.

### RunOrchestratedTask — Multi-Agent Coordination

The most complex command. An orchestrator agent decomposes a task, delegates subtasks to specialized sub-agents, and synthesizes their results.

It runs in three phases:

**Phase 1 — Planning.** The orchestrator receives the task description and a catalog of available sub-agents (names, capabilities, tool access). It produces a plan: a list of `SUBTASK: agent_name - description` lines.

**Phase 2 — Delegation.** The handler parses the subtask lines, then runs `RunConversationCommand` for each sub-agent with its assigned subtask. Each sub-agent runs independently with its own conversation history.

**Phase 3 — Synthesis.** All sub-agent results are fed back to the orchestrator, which produces a final synthesized response that combines and reconciles the individual outputs.

```
RunOrchestratedTaskCommand
  ├── OrchestratorName
  ├── TaskDescription
  ├── AvailableAgents[]  Sub-agent pool
  ├── MaxTotalTurns      Budget across all agents
  └── ProgressCallback

→ OrchestratedTaskResult
  ├── SubAgentResults[]  Per-agent outputs
  └── FinalSynthesis     Orchestrator's combined answer
```

## Agent Definitions

`AgentDefinitions.cs` is a static factory that creates pre-configured `AgentExecutionContext` objects for the two built-in agents:

- **ResearchAgent** — A standalone agent with tool access for research tasks. Instructions loaded from an embedded SKILL.md file.
- **OrchestratorAgent** — A coordination agent that knows how to decompose tasks and delegate. Its SKILL.md is dynamically augmented with the list of available sub-agents.

Both load their instructions from embedded `SKILL.md` resources, stripping YAML frontmatter at load time. The skill files live in `Agents/Skills/` and are compiled into the assembly.

## Validation

Every command has a FluentValidation validator:

- `ExecuteAgentTurnCommandValidator` — Agent name required, message required and under 100KB
- `RunConversationCommandValidator` — Agent name required, at least one user message, max turns 1-100
- `RunOrchestratedTaskCommandValidator` — Orchestrator name required, task description under 50KB, at least one available agent, max turns 1-200

Validators are auto-discovered via assembly scanning and run through the `RequestValidationBehavior` pipeline before handlers execute.

## Command Composition

The commands compose through MediatR — handlers dispatch other commands via `ISender`:

```
RunOrchestratedTask
  └── calls RunConversation (per sub-agent)
       └── calls ExecuteAgentTurn (per turn)
```

This means every layer of the pipeline (validation, tracing, permissions, hooks) applies at every level of nesting. A tool permission check inside a sub-agent's third turn still flows through the full behavior chain.

---

## Project Structure

```
Application.Core/
├── Agents/
│   ├── AgentDefinitions.cs       Static factory for built-in agents
│   └── Skills/                   Embedded SKILL.md files
├── CQRS/Agents/
│   ├── ExecuteAgentTurn/         Command + Handler + Validator
│   ├── RunConversation/          Command + Handler + Validator
│   └── RunOrchestratedTask/      Command + Handler + Validator
└── DependencyInjection.cs        MediatR + FluentValidation registration
```

## Dependencies

- **Application.AI.Common** — Agent factories, tool interfaces, context management
- **Application.Common** — Pipeline behaviors, logging, DI
- **Domain.AI** / **Domain.Common** — Domain models, config, Result pattern
- **FluentValidation** — Command validation
- **MediatR** — CQRS dispatch
- **Microsoft.Agents.AI** — Agent execution framework
