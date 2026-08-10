# Application.Core

## What This Is

Application.Core is where agents actually *run*. It contains the CQRS command handlers that orchestrate agent execution -- from a single turn (one user message in, one agent response out) to multi-turn conversations to multi-agent task delegation. Think of Application.AI.Common as defining what agents *can* do, and Application.Core as the code that *tells them to do it*.

It solves the problem of having orchestration logic tangled with agent construction or presentation concerns. By isolating the "run the agent" commands into their own project, any Presentation host (ConsoleUI, WebUI, AgentHub SignalR) can dispatch the same commands through MediatR without duplicating orchestration code.

This project depends on Application.AI.Common (factories, interfaces), Application.Common (pipeline behaviors), Domain.AI, and Domain.Common. It is depended upon by Presentation layers that dispatch its commands. It also embeds its own SKILL.md files as assembly resources for built-in agent definitions.

## Architecture Context

```
    ┌──────────────────────────────────────────────────┐
    │               Presentation Layer                  │
    │  (ConsoleUI, AgentHub, WebUI all dispatch these   │
    │   commands via IMediator.Send(...))               │
    └──────────────────────────┬───────────────────────┘
                               │ IMediator.Send(command)
                               ▼
    ╔══════════════════════════════════════════════════╗
    ║              Application.Core                    ║  ← YOU ARE HERE
    ║  RunConversation → ExecuteAgentTurn (loop)      ║
    ║  RunOrchestratedTask → RunConversation (fan-out)║
    ╚══════════════════════════╤═══════════════════════╝
                               │ Uses
         ┌─────────────────────┼─────────────────────┐
         ▼                     ▼                     ▼
┌─────────────────┐  ┌─────────────────┐  ┌─────────────────┐
│ App.AI.Common    │  │ App.Common       │  │ Domain.AI        │
│ (AgentFactory,   │  │ (Pipeline,       │  │ (Agent models,   │
│  Interfaces)     │  │  Validation)     │  │  Skill defs)     │
└─────────────────┘  └─────────────────┘  └─────────────────┘
```

Application.Core is the topmost Application layer project. It contains concrete CQRS handlers (the use cases) while delegating infrastructure concerns to the layers below. The commands it defines are the public API that Presentation layers consume -- they dispatch commands via `IMediator` and receive typed results.

## Key Concepts

### The Command Hierarchy

The three commands form a composition hierarchy. Each higher-level command dispatches lower-level commands through MediatR, meaning the full pipeline (validation, permissions, hooks, tracing) applies at every nesting level:

```
RunOrchestratedTask (multi-agent delegation)
  └── dispatches RunConversation (per sub-agent)
       └── dispatches ExecuteAgentTurn (per turn)
```

### ExecuteAgentTurn -- One Atomic Round

The simplest unit of agent work. A user sends a message, the agent responds (potentially calling tools multiple times), and returns a response with updated conversation history.

```csharp
var result = await mediator.Send(new ExecuteAgentTurnCommand
{
    AgentName = "research-agent",
    UserMessage = "What are the key trends in quantum computing?",
    ConversationHistory = previousMessages,
    ConversationId = sessionId,
    TurnNumber = 1
});

if (result.Success)
{
    Console.WriteLine(result.Response);
    Console.WriteLine($"Tools used: {string.Join(", ", result.ToolsInvoked)}");
    Console.WriteLine($"Tokens: {result.InputTokens} in / {result.OutputTokens} out");
    Console.WriteLine($"Cost: ${result.CostUsd:F4}");
    // result.UpdatedHistory carries the full conversation for the next turn
}
```

The handler:
1. Resolves the agent via `IAgentFactory.CreateAgentFromSkillAsync(agentName)`
2. Runs a single turn via the Microsoft Agents AI framework's `RunAsync`
3. Extracts the text response (handles `string`, `ChatResponse`, and reflection fallback)
4. Captures token usage and cost from `ILlmUsageCapture`
5. Returns `AgentTurnResult` with the response, updated history, and metrics

This command implements `IAgentScopedRequest` (sets agent context), `IContentScreenable` (screens user input), and `IHasObservabilitySession` (records metrics to the observability store).

### RunConversation -- Multi-Turn Loop

Wraps `ExecuteAgentTurn` in a sequential loop. Feed it a list of user messages (or a single message for a back-and-forth), and it carries conversation history forward between turns:

```csharp
var result = await mediator.Send(new RunConversationCommand
{
    AgentName = "research-agent",
    UserMessages = ["Analyze the competitive landscape for AI chips",
                    "Focus on the NVIDIA vs AMD rivalry",
                    "Summarize your findings in bullet points"],
    MaxTurns = 10,
    OnProgress = progress => Console.WriteLine($"Turn {progress.TurnNumber}: {progress.Status}")
});

Console.WriteLine($"Completed {result.Turns.Count} turns, {result.TotalToolInvocations} tool calls");
Console.WriteLine(result.FinalResponse);
```

The handler:
1. Starts an observability session via `IObservabilityStore`
2. Iterates over user messages, dispatching `ExecuteAgentTurnCommand` for each
3. Carries `UpdatedHistory` from each turn to the next
4. Accumulates token usage, cost, and tool invocation counts
5. Reports progress via the optional callback
6. Records session metrics (turns, tokens, cost, cache hit rate) to the observability store
7. Emits OpenTelemetry metrics: conversation duration, turns per conversation, tool calls

If any turn fails, the conversation stops and returns the partial result with the error.

### RunOrchestratedTask -- Multi-Agent Coordination

The most complex command. An orchestrator agent decomposes a task, delegates subtasks to specialized sub-agents, and synthesizes their results. Three phases:

```csharp
var result = await mediator.Send(new RunOrchestratedTaskCommand
{
    OrchestratorName = "orchestrator",
    TaskDescription = "Produce a market analysis report for the EV battery sector",
    AvailableAgents = ["research-agent", "analysis-agent", "writing-agent"],
    MaxTotalTurns = 20,
    OnProgress = p => Console.WriteLine($"[{p.AgentName}] {p.Status}")
});

foreach (var subResult in result.SubAgentResults)
    Console.WriteLine($"  {subResult.AgentName}: {subResult.Summary}");
Console.WriteLine(result.FinalSynthesis);
```

**Phase 1 -- Planning:** The orchestrator receives the task + a catalog of available sub-agents. It produces `SUBTASK: agent_name - description` lines.

**Phase 2 -- Delegation:** The handler parses subtask lines, runs `RunConversationCommand` for each assigned sub-agent with its subtask as the user message.

**Phase 3 -- Synthesis:** All sub-agent outputs are fed back to the orchestrator for a final combined response.

### RAG Commands

Two CQRS operations for the RAG (Retrieval-Augmented Generation) pipeline:

**IngestDocumentCommand** -- Ingests a document into the RAG pipeline (parse, chunk, enrich, embed, index):

```csharp
var result = await mediator.Send(new IngestDocumentCommand
{
    DocumentUri = new Uri("file:///docs/architecture.md"),
    CollectionName = "project-docs",
    OverrideStrategy = ChunkingStrategy.StructureAware
});
// result.ChunksCreated, result.DocumentId, result.Duration
```

**SearchDocumentsQuery** -- Searches the RAG pipeline (classify, transform, retrieve, rerank, evaluate, assemble):

```csharp
var result = await mediator.Send(new SearchDocumentsQuery
{
    Query = "How does the permission system work?",
    TopK = 5,
    CollectionName = "project-docs"
});
// result.Context (assembled text), result.Citations, result.Strategy used
```

### MetaHarness Command

`RunHarnessOptimizationCommand` runs the automated harness optimization loop -- it evaluates agent performance against a regression suite and proposes configuration improvements.

### Planner Commands (Phase 4)

Seven CQRS operations for the DAG-based plan execution subsystem, organized in `CQRS/Planner/`:

**GeneratePlanCommand** -- Generates a structured plan from a natural-language task description via LLM inference, validates it, and persists it:

```csharp
var planId = await mediator.Send(new GeneratePlanCommand
{
    TaskDescription = "Analyze competitor pricing and produce a summary report",
    Constraints = new PlanGenerationConstraints { MaxSteps = 10 }
});
```

**CreatePlanCommand** -- Validates and persists a pre-built `PlanGraph` (for programmatically constructed plans, bypassing LLM generation):

```csharp
var planId = await mediator.Send(new CreatePlanCommand { Plan = myPlanGraph });
```

**ExecutePlanCommand** -- Starts or resumes execution of a plan. If a checkpoint exists, resumes from the last saved state:

```csharp
var summary = await mediator.Send(new ExecutePlanCommand { PlanId = planId });
// summary.StepResults, summary.TotalDuration, summary.OverallStatus
```

**CancelPlanCommand** -- Cancels a running plan. Steps already in progress complete but no new steps start:

```csharp
await mediator.Send(new CancelPlanCommand { PlanId = planId });
```

**RetryPlanStepCommand** -- Retries a specific failed step within a plan:

```csharp
await mediator.Send(new RetryPlanStepCommand { PlanId = planId, StepId = failedStepId });
```

**GetPlanQuery** -- Retrieves a plan graph along with its current per-step execution state as a `PlanSnapshot`:

```csharp
var snapshot = await mediator.Send(new GetPlanQuery { PlanId = planId });
// snapshot.Graph (plan structure), snapshot.StepStates (per-step status)
```

**ListPlansQuery** -- Lists plans with optional filtering by status and date range:

```csharp
var plans = await mediator.Send(new ListPlansQuery
{
    StatusFilter = StepExecutionStatus.Completed,
    From = DateTimeOffset.UtcNow.AddDays(-7)
});
```

**GetPlanHistoryQuery** -- Retrieves the execution audit trail for a plan:

```csharp
var history = await mediator.Send(new GetPlanHistoryQuery { PlanId = planId });
// List<PlanExecutionLogEntry> — state transitions, timestamps, error messages
```

Each command has a FluentValidation validator (e.g., `GeneratePlanCommandValidator`, `ExecutePlanCommandValidator`). `PlanSnapshot` is a read model combining the plan graph with per-step execution states, returned by `GetPlanQuery`.

### Validation

Every command has a FluentValidation validator discovered via assembly scanning:

```csharp
public class ExecuteAgentTurnCommandValidator : AbstractValidator<ExecuteAgentTurnCommand>
{
    public ExecuteAgentTurnCommandValidator()
    {
        RuleFor(x => x.AgentName).NotEmpty();
        RuleFor(x => x.UserMessage).NotEmpty().MaximumLength(100_000);
    }
}
```

Validators run automatically via `RequestValidationBehavior` before handlers execute. If validation fails, the handler is never called and a `Result.ValidationFailure(errors)` is returned.

### Built-In Agents

Agents are defined on disk, not in this project: an `AGENT.md` under the repo-root `agents/`
directory (discovered by `IAgentMetadataRegistry`) plus a companion `SKILL.md` under `skills/`
(discovered by `ISkillMetadataRegistry`). `ResearchAgent` and `OrchestratorAgent` are both
defined this way — see `agents/research-agent/AGENT.md` and `agents/orchestrator-agent/AGENT.md`.
This project's CQRS handlers (`ExecuteAgentTurnCommandHandler`, `RunConversationCommandHandler`,
`RunOrchestratedTaskCommandHandler`) resolve an agent by name through those registries; nothing
in `Application.Core` hardcodes agent instructions.

## Project Structure

```
Application.Core/
├── Agents/
│   └── (agent definitions live in the repo-root agents/ and skills/ directories, not here)
├── CQRS/
│   ├── Agents/
│   │   ├── ExecuteAgentTurn/
│   │   │   ├── ExecuteAgentTurnCommand.cs          # Single-turn command + result types
│   │   │   ├── ExecuteAgentTurnCommandHandler.cs   # Creates agent, runs turn, captures metrics
│   │   │   └── ExecuteAgentTurnCommandValidator.cs # AgentName + UserMessage validation
│   │   ├── RunConversation/
│   │   │   ├── RunConversationCommand.cs           # Multi-turn loop command + result types
│   │   │   ├── RunConversationCommandHandler.cs    # Sequential turn execution with metrics
│   │   │   └── RunConversationCommandValidator.cs  # Messages required, MaxTurns 1-100
│   │   └── RunOrchestratedTask/
│   │       ├── RunOrchestratedTaskCommand.cs       # Multi-agent delegation command
│   │       ├── RunOrchestratedTaskCommandHandler.cs # Plan → delegate → synthesize
│   │       └── RunOrchestratedTaskCommandValidator.cs # Task + agents + budget validation
│   ├── MetaHarness/
│   │   ├── RunHarnessOptimizationCommand.cs        # Optimization loop trigger
│   │   ├── RunHarnessOptimizationCommandHandler.cs # Evaluate → propose → validate
│   │   └── RunHarnessOptimizationCommandValidator.cs
│   ├── Planner/
│   │   ├── GeneratePlanCommand.cs                  # LLM-based plan generation
│   │   ├── GeneratePlanCommandHandler.cs           # Generate → validate → persist
│   │   ├── GeneratePlanCommandValidator.cs
│   │   ├── CreatePlanCommand.cs                    # Persist pre-built plan graph
│   │   ├── CreatePlanCommandHandler.cs             # Validate → persist
│   │   ├── CreatePlanCommandValidator.cs
│   │   ├── ExecutePlanCommand.cs                   # Start/resume plan execution
│   │   ├── ExecutePlanCommandHandler.cs            # Delegates to IPlanExecutor
│   │   ├── ExecutePlanCommandValidator.cs
│   │   ├── CancelPlanCommand.cs                    # Cancel running plan
│   │   ├── CancelPlanCommandHandler.cs
│   │   ├── CancelPlanCommandValidator.cs
│   │   ├── RetryPlanStepCommand.cs                 # Retry failed step
│   │   ├── RetryPlanStepCommandHandler.cs
│   │   ├── RetryPlanStepCommandValidator.cs
│   │   ├── GetPlanQuery.cs                         # Retrieve plan + step states
│   │   ├── GetPlanQueryHandler.cs
│   │   ├── GetPlanQueryValidator.cs
│   │   ├── ListPlansQuery.cs                       # List plans with filters
│   │   ├── ListPlansQueryHandler.cs
│   │   ├── ListPlansQueryValidator.cs
│   │   ├── GetPlanHistoryQuery.cs                  # Execution audit trail
│   │   ├── GetPlanHistoryQueryHandler.cs
│   │   ├── GetPlanHistoryQueryValidator.cs
│   │   └── PlanSnapshot.cs                         # Read model: graph + step states
│   └── RAG/
│       ├── IngestDocument/
│       │   ├── IngestDocumentCommand.cs            # Document ingestion command
│       │   ├── IngestDocumentCommandHandler.cs     # Parse → chunk → enrich → embed → index
│       │   ├── IngestDocumentCommandValidator.cs   # URI required
│       │   └── IngestDocumentResult.cs             # ChunksCreated, DocumentId, Duration
│       └── SearchDocuments/
│           ├── SearchDocumentsQuery.cs             # RAG search query
│           ├── SearchDocumentsQueryHandler.cs      # Classify → retrieve → rerank → assemble
│           ├── SearchDocumentsQueryValidator.cs    # Query required
│           └── SearchDocumentsResult.cs            # Context, Citations, Strategy
└── DependencyInjection.cs                          # MediatR + FluentValidation registration
```

## Key Types Reference

| Type | Purpose | Used By |
|------|---------|---------|
| **Agent Commands** | | |
| `ExecuteAgentTurnCommand` | Single-turn agent execution | RunConversationHandler, Presentation hosts |
| `RunConversationCommand` | Multi-turn conversation loop | RunOrchestratedTaskHandler, ConsoleUI |
| `RunOrchestratedTaskCommand` | Multi-agent task delegation | Presentation hosts |
| **Results** | | |
| `AgentTurnResult` | Turn output (response, history, tokens, cost) | RunConversationHandler |
| `ConversationResult` | Full conversation output (turns, final response) | Presentation hosts |
| `TurnProgress` | Real-time progress callback payload | SignalR hub, ConsoleUI |
| **Planner Commands** | | |
| `GeneratePlanCommand` | LLM-based plan generation and persistence | Presentation hosts, agent tools |
| `CreatePlanCommand` | Persist a pre-built plan graph | Programmatic plan construction |
| `ExecutePlanCommand` | Start/resume DAG plan execution | Presentation hosts |
| `CancelPlanCommand` | Cancel a running plan | Presentation hosts |
| `RetryPlanStepCommand` | Retry a specific failed step | Presentation hosts |
| `GetPlanQuery` | Retrieve plan graph + step states | Presentation hosts, dashboards |
| `ListPlansQuery` | List plans with status/date filters | Presentation hosts, dashboards |
| `GetPlanHistoryQuery` | Execution audit trail | Presentation hosts, dashboards |
| `PlanSnapshot` | Read model combining graph + step states | GetPlanQueryHandler |
| **RAG Commands** | | |
| `IngestDocumentCommand` | Trigger document ingestion | Admin APIs, CLI |
| `SearchDocumentsQuery` | Semantic document search | Agent tools, search APIs |
| **Infrastructure** | | |
| `DependencyInjection` | MediatR + validator registration | Presentation composition root |

## Common Tasks

### How to Add a New Agent Command

1. Create a folder under `CQRS/Agents/YourCommand/`
2. Define the command (implement `IRequest<YourResult>` and relevant marker interfaces):

```csharp
public record YourCommand : IRequest<Result<YourResult>>, IAgentScopedRequest, IHasTimeout
{
    public TimeSpan? Timeout => TimeSpan.FromMinutes(2);
    public required string AgentName { get; init; }
    public string AgentId => AgentName;
    public string ConversationId { get; init; } = Guid.NewGuid().ToString();
    public int TurnNumber { get; init; }
    // Your specific properties...
}
```

3. Create the validator:

```csharp
public class YourCommandValidator : AbstractValidator<YourCommand>
{
    public YourCommandValidator()
    {
        RuleFor(x => x.AgentName).NotEmpty();
        // Your rules...
    }
}
```

4. Create the handler:

```csharp
public class YourCommandHandler : IRequestHandler<YourCommand, Result<YourResult>>
{
    private readonly IAgentFactory _agentFactory;

    public async Task<Result<YourResult>> Handle(YourCommand request, CancellationToken ct)
    {
        var agent = await _agentFactory.CreateAgentFromSkillAsync(request.AgentName, ct);
        // Your orchestration logic...
        return Result<YourResult>.Success(new YourResult { ... });
    }
}
```

All three files are auto-discovered by assembly scanning -- no manual registration needed.

### How to Add a Built-In Agent

1. Create `agents/your-agent/AGENT.md` at the repo root with `id`, `name`, `description`, and a
   `skill: your-agent` reference (see `agents/research-agent/AGENT.md` for the shape).
2. Create `skills/your-agent/SKILL.md` at the repo root with the agent's instructions and tool
   declarations (see `skills/research-agent/SKILL.md`).
3. Nothing else to wire up: `IAgentMetadataRegistry` and `ISkillMetadataRegistry` discover both
   files on the next process start by walking the configured search paths (`AppConfig.AI.Agents`,
   `AppConfig.AI.Skills`).

## Dependencies

| Reference | Why |
|-----------|-----|
| `Application.AI.Common` | `IAgentFactory`, `ILlmUsageCapture`, `IObservabilityStore`, pipeline behaviors |
| `Application.Common` | `IHasTimeout`, `EmbeddedResourceHelper`, pipeline infrastructure |
| `Domain.AI` | `AgentExecutionContext`, `SkillDefinition`, telemetry conventions |
| `Domain.Common` | `Result<T>`, `AppConfig` |
| `FluentValidation` | Command validators |
| `MediatR` | CQRS dispatch (both receiving and sending sub-commands) |
| `Microsoft.Agents.AI` | `AIAgent.RunAsync()` for turn execution |

## Testing

Tests live in `Application.Core.Tests`. Key test strategies:

- **Handler tests**: Mock `IAgentFactory` (return a fake agent), mock `IObservabilityStore`, verify correct turn sequencing, verify error propagation stops the conversation.
- **Validator tests**: Verify required fields, length limits, range constraints.
- **Integration tests**: Use `WebApplicationFactory<Program>` with real pipeline behaviors and a mocked chat client to test full command flow end-to-end.

```bash
dotnet test src/AgenticHarness.slnx --filter "FullyQualifiedName~Application.Core"
```

For handler tests, mock `IMediator` when testing RunConversation/RunOrchestratedTask (they dispatch sub-commands). For ExecuteAgentTurn, mock `IAgentFactory` and verify the agent receives correct parameters.
