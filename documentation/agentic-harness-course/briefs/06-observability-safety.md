# Module 6: Seeing Inside & Staying Safe

## Teaching Arc
- **Metaphor:** A hospital monitoring room — every patient (agent conversation) has vital signs displayed on screens. Heart rate = token usage, blood pressure = response latency, temperature = error rate. When something goes wrong, alarms fire and nurses (content safety, permissions) intervene. The meta-harness is like the hospital administrator who reviews outcomes and updates treatment protocols.
- **Opening hook:** "AI agents are black boxes by default. A conversation goes sideways and you have no idea why — was it the prompt? A bad tool result? A context overflow? The harness solves this by instrumenting *everything* with OpenTelemetry, so you can trace any conversation turn-by-turn, tool call by tool call."
- **Key insight:** Observability isn't optional for production agents. Without it, debugging is guesswork. The harness creates a trace for every turn, tags it with agent name, turn index, and tool calls, and exports it to Jaeger/Prometheus. The meta-harness goes further — it uses these traces to automatically propose improvements to the agent's own skill files.
- **"Why should I care?":** When you tell your AI coding tool "my agent is giving bad answers," the first question should be "what do the traces show?" Understanding observability means you can pinpoint *exactly* where in the pipeline things went wrong, instead of guessing and restarting.

## Screens (5)

### Screen 1: The Observability Stack (Pattern Cards)
Show the three pillars as cards:
- Traces (Jaeger) — follow one conversation from start to finish
- Metrics (Prometheus) — aggregate numbers: avg tokens/turn, tool call frequency, error rates
- Logs (structured JSON) — detailed event-level records

Plus the custom LLM span processor that enriches traces with agentic context.

### Screen 2: Content Safety & Permissions (Flow Animation)
Flow animation showing a message that triggers content safety:
1. User sends message → Pipeline starts
2. ContentSafetyBehavior screens the input → "Flagged: potential prompt injection"
3. Pipeline halts → Agent never sees the message
4. Then a clean message goes through → ContentSafety approves → continues to handler

### Screen 3: The Permission System (Code Translation)
Code↔English of the 3-phase permission resolution: Deny gates → Ask rules → Allow rules. Show SafetyGate and ToolPermissionRule.

### Screen 4: Meta-Harness — The Agent That Improves Itself
Explanation of the optimization loop: trace history → proposer agent reads traces → proposes skill file changes → evaluator runs tasks → scores → keeps best candidate. This is the "whoa" moment of the course.

### Screen 5: The Full Picture + Final Quiz
Tie everything together — the harness as a complete system. Then a comprehensive final quiz covering concepts from all 6 modules.

## Code Snippets

### Snippet 1: ContentSafetyBehavior
```csharp
public class ContentSafetyBehavior<TRequest, TResponse>
    : IPipelineBehavior<TRequest, TResponse>
    where TRequest : IContentScreenable
{
    public async Task<TResponse> Handle(
        TRequest request, RequestHandlerDelegate<TResponse> next,
        CancellationToken cancellationToken)
    {
        var content = request.GetContentToScreen();

        if (!string.IsNullOrWhiteSpace(content))
        {
            var safetyResult = await _contentSafetyService.ScreenContentAsync(
                content, cancellationToken);

            if (!safetyResult.IsSafe)
            {
                _logger.LogWarning(
                    "Content safety violation detected: {Categories}",
                    string.Join(", ", safetyResult.FlaggedCategories));

                ContentSafetyMetrics.Violations.Add(1);
                throw new ContentSafetyException(safetyResult);
            }
        }

        return await next();
    }
}
```

### Snippet 2: SafetyGate and ToolPermissionRule
```csharp
public sealed record SafetyGate(string PathPattern, string Description)
{
    public bool IsBypassImmune => true;
}

public sealed record ToolPermissionRule
{
    public required string ToolName { get; init; }
    public string? OperationPattern { get; init; }
    public required PermissionBehaviorType Behavior { get; init; }
    public required PermissionRuleSource Source { get; init; }
    public int Priority { get; init; }
}
```

### Snippet 3: Meta-harness optimization loop
```csharp
// Phase 1: Create orchestrator and get task decomposition
var history = await _historyStore.GetRecentTracesAsync(config.TraceWindowSize);

// Phase 2: Proposer reads traces and suggests improvements
var proposal = await _proposer.ProposeAsync(
    currentSkillContent, history, cancellationToken);

// Phase 3: Evaluate proposed changes against benchmark tasks
var evalResult = await _evaluator.EvaluateAsync(
    proposal.CandidateSkill, config.EvalTasks, cancellationToken);

// Phase 4: Keep if improved, discard if not
if (evalResult.Score > currentBestScore + config.ScoreImprovementThreshold)
{
    await _candidateRepo.SaveAsync(proposal.Candidate);
    _logger.LogInformation("New best candidate: score {Score} (was {Previous})",
        evalResult.Score, currentBestScore);
}
```

### Snippet 4: OTel metric instruments
```csharp
public static class OrchestrationMetrics
{
    public static Histogram<double> ConversationDuration { get; } =
        AppInstrument.Meter.CreateHistogram<double>(
            OrchestrationConventions.ConversationDuration, "ms");

    public static Histogram<int> TurnsPerConversation { get; } =
        AppInstrument.Meter.CreateHistogram<int>(
            OrchestrationConventions.TurnsPerConversation, "{turn}");

    public static Counter<long> SubagentSpawns { get; } =
        AppInstrument.Meter.CreateCounter<long>(
            OrchestrationConventions.SubagentSpawns, "{spawn}");
}
```

## Interactive Elements

- [x] **Pattern cards** — 3 observability pillars (Traces, Metrics, Logs) with icons and descriptions
- [x] **Data flow animation** — content safety screening: 2 scenarios (blocked vs. approved), 8 steps total
- [x] **Code↔English translation** — ContentSafetyBehavior and SafetyGate/ToolPermissionRule
- [x] **Callout box** — "The meta-harness is an agent that optimizes other agents. Let that sink in."
- [x] **Quiz** — 5 questions (final quiz): (1) Scenario: agent response is slow — which observability tool do you check first? (2) What happens when ContentSafety flags a message? (3) What are safety gates and why are they bypass-immune? (4) How does the meta-harness decide if a proposed change is better? (5) Trace a message through all 6 modules — put the pipeline steps in order (drag-and-drop)
- [x] **Glossary tooltips** — OpenTelemetry, trace, span, metric, histogram, counter, Jaeger, Prometheus, content safety, prompt injection, safety gate, bypass-immune, meta-harness, proposer, evaluator, benchmark, causal trace, regression suite

## Reference Files to Read
- `references/content-philosophy.md` → all sections
- `references/gotchas.md` → all sections
- `references/interactive-elements.md` → "Pattern/Feature Cards", "Message Flow / Data Flow Animation", "Code ↔ English Translation Blocks", "Multiple-Choice Quizzes", "Drag-and-Drop Matching", "Callout Boxes", "Glossary Tooltips"

## Connections
- **Previous module:** "Tools & The Outside World" — introduced tool permissions briefly. This module goes deeper into the full safety and observability picture.
- **Next module:** None — this is the finale. End with a "you now understand the full system" wrap-up and a callout encouraging them to explore the actual codebase.
- **Tone/style notes:** The hospital monitoring metaphor should ground the observability section. The meta-harness section should feel like a "whoa" reveal — this is the most mind-bending concept in the whole course. End on a high note.
