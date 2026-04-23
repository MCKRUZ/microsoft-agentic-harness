# Module 3: The Conversation Loop

## Teaching Arc
- **Metaphor:** A chess game with a coach — the agent (player) thinks about the position, decides on a move (tool call or response), the coach (harness) validates the move is legal, then the opponent (reality/tool result) responds. The game continues until checkmate (task complete) or the clock runs out (turn limit).
- **Opening hook:** "You know that moment when an AI assistant pauses, calls a tool, gets results, and then keeps going? That's not one step — it's an entire loop running behind the scenes, and every iteration goes through a gauntlet of checks."
- **Key insight:** The conversation isn't one API call — it's a loop. Each turn goes through validation, content safety, performance monitoring, and the actual LLM call. The harness decides when to keep looping and when to stop. This is what separates a toy demo from a production agent.
- **"Why should I care?":** When your agent gets stuck in a loop, burns through tokens, or gives weird answers, understanding the conversation loop is how you diagnose *why*. Is it the turn limit? The validation rejecting something? Content safety blocking a response? The loop structure tells you where to look.

## Screens (5)

### Screen 1: The Loop Visualized (Flow Animation)
Step-by-step animation of one conversation with 3 turns:
Turn 1: User message → Validation → Content Safety → Handler → LLM says "I need the file_system tool" → Tool executes → Result back
Turn 2: Tool result + context → Handler → LLM says "I also need web_search" → Tool executes → Result back
Turn 3: Both results → Handler → LLM says "Here is my answer" → IsComplete=true → Loop ends

### Screen 2: The CQRS Pipeline (Numbered Step Cards)
Show the pipeline behaviors in order: Validation → Caching → Content Safety → Tool Permission → Performance Logging → Handler. Each step is a card with what it does and why it matters.

### Screen 3: ExecuteAgentTurn — One Round (Code Translation)
Code↔English of the turn handler — the single most important piece of code.

### Screen 4: RunConversation — The Full Loop (Code Translation)
Code↔English of the conversation loop — how turns chain together.

### Screen 5: RunOrchestratedTask — Going Multi-Agent + Quiz
Brief explanation of how the orchestrator decomposes tasks and runs sub-agents in parallel, then quiz.

## Code Snippets

### Snippet 1: ExecuteAgentTurnCommandHandler.Handle
```csharp
public async Task<AgentTurnResult> Handle(ExecuteAgentTurnCommand request, CancellationToken cancellationToken)
{
    using var turnActivity = AgentActivitySource.StartTurn(
        request.Context.AgentName, request.TurnIndex);

    _logger.LogDebug("Executing turn {TurnIndex} for agent {AgentName}",
        request.TurnIndex, request.Context.AgentName);

    try
    {
        var response = await request.Agent.InvokeAsync(
            request.Messages, request.Context.AgentOptions, cancellationToken);

        var updatedMessages = request.Messages.ToList();
        updatedMessages.Add(response);

        var hasToolCalls = response.Contents.OfType<FunctionCallContent>().Any();
        var isComplete = !hasToolCalls;

        OrchestrationMetrics.ToolCalls.Add(
            hasToolCalls ? response.Contents.OfType<FunctionCallContent>().Count() : 0,
            new KeyValuePair<string, object?>("agent.name", request.Context.AgentName));

        return new AgentTurnResult(updatedMessages, isComplete, request.TurnIndex);
    }
    catch (Exception ex)
    {
        _logger.LogError(ex, "Turn {TurnIndex} failed for {AgentName}",
            request.TurnIndex, request.Context.AgentName);
        throw new AgentExecutionException(request.Context.AgentName, request.TurnIndex, ex);
    }
}
```

### Snippet 2: RunConversationCommandHandler (loop)
```csharp
while (turnCount < request.MaxTurns)
{
    turnCount++;
    var turnResult = await _mediator.Send(
        new ExecuteAgentTurnCommand(agent, messages, context, turnCount),
        cancellationToken);

    messages = turnResult.UpdatedMessages;

    if (turnResult.IsComplete)
    {
        _logger.LogInformation("Conversation completed after {TurnCount} turns", turnCount);
        break;
    }
}
```

### Snippet 3: RunOrchestratedTask decomposition
```csharp
// Phase 1: Create orchestrator and get task decomposition
var agentCatalog = BuildAgentCatalog(request.AvailableAgents);

var orchestratorContext = await _contextFactory.CreateAsync(
    request.OrchestratorName, cancellationToken);
var orchestrator = _agentFactory.CreateAgent(orchestratorContext.Definition);

// Phase 2: Decompose and delegate
var decomposition = await GetTaskDecomposition(
    orchestrator, request.Task, agentCatalog, cancellationToken);

// Phase 3: Run sub-agent conversations in parallel
var subtaskResults = await Task.WhenAll(
    decomposition.Subtasks.Select(subtask =>
        RunSubagentConversation(subtask, cancellationToken)));
```

## Interactive Elements

- [x] **Data flow animation** — 3-turn conversation loop showing the full cycle with pipeline behaviors. Actors: User, Pipeline (Validation/Safety), Handler, LLM, Tool System. 9 steps total.
- [x] **Numbered step cards** — 6 pipeline behaviors in execution order
- [x] **Code↔English translation** — ExecuteAgentTurnCommand handler (single turn) and RunConversation loop
- [x] **Quiz** — 4 questions: (1) What determines if the conversation loop keeps going? (2) Scenario: the agent makes 15 tool calls but never finishes — what config would you check? (3) Which pipeline behavior runs first? (4) What is the difference between RunConversation and RunOrchestratedTask?
- [x] **Glossary tooltips** — CQRS, pipeline behavior, handler, turn, tool call, FunctionCallContent, span (tracing), activity, cancellation token, Task.WhenAll, MediatR, orchestrator, decomposition

## Reference Files to Read
- `references/content-philosophy.md` → all sections
- `references/gotchas.md` → all sections
- `references/interactive-elements.md` → "Message Flow / Data Flow Animation", "Numbered Step Cards", "Code ↔ English Translation Blocks", "Multiple-Choice Quizzes", "Glossary Tooltips"

## Connections
- **Previous module:** "Meet the Cast" — introduced the actors. This module shows them in action during a real conversation.
- **Next module:** "Skills: What Agents Know" — will explain how the agent knows what to do (the skills system) and how it manages its memory budget.
- **Tone/style notes:** The flow animation is the hero visual. The chess metaphor should be introduced early and referenced when explaining turns. Keep the orchestrator section brief — it's a teaser, not a deep dive.
