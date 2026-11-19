# Module 6: Seeing Inside & Staying Safe

## Teaching Arc
- **Metaphor:** A hospital monitoring room — every patient (agent conversation) has vital signs displayed on screens. Heart rate = token usage, blood pressure = response latency, temperature = error rate. When something goes wrong, alarms fire and nurses (content safety, permissions) intervene. The meta-harness is like the hospital administrator who reviews outcomes and updates treatment protocols.
- **Opening hook:** "AI agents are black boxes by default. A conversation goes sideways and you have no idea why — was it the prompt? A bad tool result? A context overflow? The harness solves this by instrumenting *everything* with OpenTelemetry, so you can trace any conversation turn-by-turn, tool call by tool call."
- **Key insight:** Observability isn't optional for production agents. Without it, debugging is guesswork. The harness creates a trace for every turn, tags it with agent name, turn index, and tool calls, and exports it to Jaeger/Prometheus. The meta-harness goes further — it uses these traces to automatically propose improvements to the agent's own skill files.
- **"Why should I care?":** When you tell your AI coding tool "my agent is giving bad answers," the first question should be "what do the traces show?" Understanding observability means you can pinpoint *exactly* where in the pipeline things went wrong, instead of guessing and restarting.

## Screens (6)

### Screen 1: The Observability Stack (Pattern Cards)
Show the three pillars as cards:
- Traces (Jaeger) — follow one conversation from start to finish
- Metrics (Prometheus) — aggregate numbers: avg tokens/turn, tool call frequency, error rates
- Logs (structured JSON) — detailed event-level records

Plus the custom LLM span processor that enriches traces with agentic context.

### Screen 2: Content Safety & Permissions (Flow Animation)
Flow animation showing a message that triggers content safety:
1. User sends message → Pipeline starts
2. ContentSafetyBehavior screens the input → "Blocked: Violence scored at or above BlockAtSeverity" (Azure provider; the default AllowAll provider passes everything)
3. Pipeline halts → Agent never sees the message; `agent.safety.blocks` is incremented with the category tag
4. Then a clean message goes through → ContentSafety approves → continues to handler

Prompt injection is not content safety's job: `PromptInjectionBehavior` scans for it just before this step. Content safety scores only the four harm categories (Hate, SelfHarm, Sexual, Violence).

### Screen 2b: Tool Output Compression (Visual)
Large tool outputs (file reads, API responses, search results) can flood the context window. The harness includes a **ToolOutputCompressionBehavior** — a MediatR pipeline behavior that automatically compresses oversized tool results before they reach the agent. Compression strategies are selected by content type (structured data, free text, code, etc.) and applied transparently. This prevents context overflow and keeps the agent focused on relevant information rather than drowning in raw output.

Visual: a funnel where raw tool output (large block) enters the top and a compressed summary exits the bottom, with a label showing "ToolOutputCompressionBehavior" on the funnel. Content-type badges (JSON, text, code) route to different compression strategies.

### Screen 3: The Permission System (Code Translation)
Code↔English of the 3-phase permission resolution: Deny gates → Ask rules → Allow rules. Show SafetyGate and ToolPermissionRule. Now includes `PluginDeclaration` as a rule source — plugins can emit bypass-immune deny rules at high priority, enforcing hard governance boundaries that no user confirmation can override.

### Screen 4: Meta-Harness — The Agent That Improves Itself
Explanation of the optimization loop: trace history → proposer agent reads traces → proposes skill file changes → evaluator runs tasks → scores → keeps best candidate. This is the "whoa" moment of the course.

### Screen 5: The Full Picture + Final Quiz
Tie everything together — the harness as a complete system. Then a comprehensive final quiz covering concepts from all 6 modules.

## Code Snippets

### Snippet 1: ContentSafetyBehavior
Simplified from `src/Content/Application/Application.AI.Common/MediatRBehaviors/ContentSafetyBehavior.cs`.
```csharp
public sealed class ContentSafetyBehavior<TRequest, TResponse>
    : IPipelineBehavior<TRequest, TResponse> where TRequest : notnull
{
    // ctor: ITextContentSafetyService, IObservabilityStore, IOptions<AppConfig> (no logger)

    public async Task<TResponse> Handle(
        TRequest request, RequestHandlerDelegate<TResponse> next,
        CancellationToken cancellationToken)
    {
        if (request is not IContentScreenable { ScreeningTarget: ContentScreeningTarget.Input or ContentScreeningTarget.Both } screenable)
            return await next();

        ContentSafetyResult result;
        try
        {
            result = await _safetyService.ScreenAsync(screenable.ContentToScreen, cancellationToken);
        }
        catch (ContentSafetyUnavailableException)
        {
            // Recorded as "unscreened"; refused as a service failure unless BlockWhenUnavailable is false.
            await RecordAsync(request, SafetyConventions.OutcomeValues.Unscreened, category: null, cancellationToken);
            if (_config.Value.AI.ContentSafety.BlockWhenUnavailable)
                throw;
            return await next();
        }

        // agent.safety.evaluations (outcome tag) + the session's safety event
        await RecordAsync(request,
            result.IsBlocked ? SafetyConventions.OutcomeValues.Block : SafetyConventions.OutcomeValues.Pass,
            result.Category, cancellationToken);

        if (!result.IsBlocked)
            return await next();

        // agent.safety.blocks, tagged with phase, filter and category
        ContentSafetyMetrics.Blocks.Add(1, /* phase and filter tags, then */
            new KeyValuePair<string, object?>(SafetyConventions.Category, result.Category ?? "unknown"));

        var reason = result.BlockReason ?? "Content blocked by safety policy.";
        if (ResultHelper.TryCreateFailure<TResponse>(nameof(Result.ContentBlocked), reason, out var blockedResult))
            return blockedResult;
        throw new ContentSafetyException(reason, result.Category);
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
    public required PermissionRuleSource Source { get; init; }  // Now includes PluginDeclaration
    public int Priority { get; init; }
    public bool IsBypassImmune { get; init; }  // Plugin deny rules use this
}
```

Note: `PermissionRuleSource` now includes `PluginDeclaration` — plugins emit deny rules at high priority with `IsBypassImmune = true`, meaning no override or user confirmation can bypass them. This is how plugin-boundary governance enforces hard limits on what plugin tools can do.

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
- [x] **Quiz** — 7 questions (final quiz): (1) Scenario: agent response is slow — which observability tool do you check first? (2) What happens when ContentSafety flags a message? (3) What are safety gates and why are they bypass-immune? (4) How does the meta-harness decide if a proposed change is better? (5) Trace a message through all 6 modules — put the pipeline steps in order (drag-and-drop) (6) A plugin declares `DeniedTools: ["bash"]` — can a user override this in auto-approve mode? (answer: No — plugin deny rules are bypass-immune) (7) What does ToolOutputCompressionBehavior do? (answer: detects content type, applies strategy-specific compression to large tool outputs, prevents context overflow)
- [x] **Glossary tooltips** — OpenTelemetry, trace, span, metric, histogram, counter, Jaeger, Prometheus, content safety, prompt injection, safety gate, bypass-immune, meta-harness, proposer, evaluator, benchmark, causal trace, regression suite, plugin permission rules, PluginDeclaration source, tool output compression, AutonomyLevel

## Reference Files to Read
- `references/content-philosophy.md` → all sections
- `references/gotchas.md` → all sections
- `references/interactive-elements.md` → "Pattern/Feature Cards", "Message Flow / Data Flow Animation", "Code ↔ English Translation Blocks", "Multiple-Choice Quizzes", "Drag-and-Drop Matching", "Callout Boxes", "Glossary Tooltips"

## Connections
- **Previous module:** "Tools & The Outside World" — introduced tool permissions briefly. This module goes deeper into the full safety and observability picture.
- **Next module:** None — this is the finale. End with a "you now understand the full system" wrap-up and a callout encouraging them to explore the actual codebase.
- **Tone/style notes:** The hospital monitoring metaphor should ground the observability section. The meta-harness section should feel like a "whoa" reveal — this is the most mind-bending concept in the whole course. End on a high note.
