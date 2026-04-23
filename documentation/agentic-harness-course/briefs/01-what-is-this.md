# Module 1: What Is This Thing?

## Teaching Arc
- **Metaphor:** An air traffic control tower — it doesn't fly the planes, but nothing lands or takes off without it coordinating the whole operation. The harness is the control tower for AI agents.
- **Opening hook:** "You've probably chatted with an AI assistant. You type a question, it thinks, it answers. Simple, right? Under the hood, there's a whole orchestration system making that work — and most people have no idea it exists."
- **Key insight:** An AI agent isn't just a model — it's a model + tools + skills + safety rails + a conversation loop that ties them all together. The "harness" is the invisible infrastructure that makes agents useful instead of just chatty.
- **"Why should I care?":** Understanding what the harness does helps you evaluate whether an AI agent is well-built or just a thin wrapper over an API call. When your agent goes off the rails, this knowledge helps you diagnose *where* in the pipeline things broke.

## Screens (4)

### Screen 1: The Elevator Pitch
What this project is and why it exists. Brief plain-English summary of what an "agentic harness" does — with the air traffic control tower metaphor.

### Screen 2: What Happens When You Send a Message (Data Flow Animation)
Trace a user message end-to-end: User → AgentHub (SignalR) → MediatR Pipeline (Validation → Content Safety → Handler) → LLM → Tool Calls → Response back. This is the hero visual.

### Screen 3: It's Not Just a Chatbot (Pattern Cards)
Show the 5 key capabilities that distinguish this from a simple API wrapper:
1. Sandboxed tool execution (files stay in a cage)
2. Progressive skill loading (only loads what's needed)
3. Multi-agent orchestration (agents can delegate to other agents)
4. Context budget management (doesn't drown in tokens)
5. Full observability (every turn traced)

### Screen 4: The Tech Stack at a Glance + Quiz
Visual tech stack overview with icon-label rows, then a 3-question quiz.

## Code Snippets

### Snippet 1: The conversation loop (RunConversationCommandHandler)
File: src/Content/Application/Application.Core/CQRS/Agents/RunConversation/RunConversationCommandHandler.cs (key section)
```csharp
public async Task<ConversationResult> Handle(RunConversationCommand request, CancellationToken cancellationToken)
{
    _logger.LogInformation("Starting conversation with agent {AgentName}, max {MaxTurns} turns",
        request.AgentName, request.MaxTurns);

    var context = await _contextFactory.CreateAsync(request.AgentName, cancellationToken);
    var agent = _agentFactory.CreateAgent(context.Definition);
    var messages = new List<ChatMessage> { new(ChatRole.User, request.UserMessage) };
    var turnCount = 0;

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

## Interactive Elements

- [x] **Data flow animation** — actors: User, AgentHub/SignalR, MediatR Pipeline, LLM (Azure OpenAI), Tool System, Response. Steps: user sends message → SignalR receives → pipeline validates → handler calls LLM → LLM requests tool → tool executes → result feeds back → LLM responds → response streams back.
- [x] **Pattern/Feature cards** — 5 capabilities (sandboxed tools, progressive skills, multi-agent, context budget, observability)
- [x] **Code↔English translation** — RunConversation loop snippet
- [x] **Quiz** — 3 questions: (1) What does the harness do that a raw API call doesn't? (2) Why is sandboxed tool execution important? (3) Scenario: your agent keeps using too many tokens — which component would you investigate?
- [x] **Glossary tooltips** — harness, agent, LLM, API, token, SignalR, MediatR, pipeline, orchestration, CQRS, sandboxed, context window

## Reference Files to Read
- `references/content-philosophy.md` → all sections
- `references/gotchas.md` → all sections
- `references/interactive-elements.md` → "Message Flow / Data Flow Animation", "Pattern/Feature Cards", "Code ↔ English Translation Blocks", "Multiple-Choice Quizzes", "Glossary Tooltips"
- `references/design-system.md` → "Color Palette", "Typography", "Module Structure"

## Connections
- **Previous module:** None — this is the opener
- **Next module:** "Meet the Cast" — introduces each component in detail. This module should end with a teaser: "Now that you've seen the full journey, let's zoom in and meet each of these actors face to face."
- **Tone/style notes:** Accent color is teal (#2A7B9B). Actor colors: User = vermillion (actor-1), AgentHub = teal (actor-2), Pipeline = plum (actor-3), LLM = golden (actor-4), Tools = forest (actor-5). Use these consistently across all modules.
