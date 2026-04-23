# Module 2: Meet the Cast

## Teaching Arc
- **Metaphor:** A film production crew — the Director (AgentFactory) assembles the team, the Script Supervisor (SkillLoader) knows the script, the Props Department (Tools) provides the physical things actors need, the Camera Operator (Observability) records everything, and the Producer (MediatR Pipeline) keeps everyone on schedule.
- **Opening hook:** "In the last module, you watched a message travel through the whole system. Now let's zoom in and meet each of the characters who made that journey happen."
- **Key insight:** Clean Architecture means each actor has exactly one job and doesn't peek at anyone else's work. This isn't just neat — it means you can swap out any actor (like changing your LLM provider) without rewriting the entire show.
- **"Why should I care?":** When you tell an AI coding tool "add a new tool to the agent," you need to know *which* layer that tool belongs in. Put it in the wrong place and the whole architecture fights you. Knowing the cast means knowing where things go.

## Screens (5)

### Screen 1: The Cast Lineup (Architecture Diagram)
Visual Clean Architecture diagram with the 4 layers. Each layer is a horizontal band with its key actors shown as character cards with icons.

### Screen 2: Group Chat — How Actors Collaborate
Group chat animation showing what happens when a user asks the agent to read a file:
- User → AgentHub: "Read the README.md file"
- AgentHub → MediatR: "New ExecuteAgentTurn command"
- MediatR → ContentSafety: "Is this request safe?"
- ContentSafety → MediatR: "All clear!"
- MediatR → Handler: "Execute this turn"
- Handler → LLM: "User wants to read a file"
- LLM → Handler: "I'll use the file_system tool"
- Handler → ToolPermission: "Can this agent use file_system?"
- ToolPermission → Handler: "Allowed within /allowed/paths"
- Handler → FileSystemService: "Read /project/README.md"
- FileSystemService → Handler: "Here's the content..."
- Handler → LLM: "Tool returned this content"
- LLM → Handler: "Here's my analysis of the README"

### Screen 3: The Domain Layer (Code Translation)
Code↔English translation of AgentManifest.cs and SkillDefinition.cs — showing how the "blueprints" are defined.

### Screen 4: The Application Layer (Code Translation)
Code↔English translation of AgentFactory — how it assembles an agent from parts.

### Screen 5: Infrastructure & Presentation + Quiz
Brief visual overview of Infrastructure (LLM providers, MCP, state) and Presentation (ConsoleUI, WebUI, AgentHub) layers, then quiz.

## Code Snippets

### Snippet 1: AgentManifest (Domain)
```csharp
public sealed record AgentManifest
{
    public required string Name { get; init; }
    public string? Description { get; init; }
    public string? SystemPrompt { get; init; }
    public IReadOnlyList<SkillReference> Skills { get; init; } = [];
    public IReadOnlyList<SubagentDefinition> Subagents { get; init; } = [];
    public AgentConfig Config { get; init; } = new();
}
```

### Snippet 2: SkillDefinition Level 1 (Domain)
```csharp
public class SkillDefinition
{
    #region Level 1: Index Card (Metadata — Always Loaded)

    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public IList<string> Tags { get; set; } = new List<string>();

    #endregion
```

### Snippet 3: AgentFactory.CreateAgent (Application)
```csharp
public AIAgent CreateAgent(AgentDefinition definition)
{
    Guard.Against.Null(definition, nameof(definition));

    var chatClient = BuildChatClientPipeline(definition);
    var agent = CreateBaseAgent(chatClient, definition);

    _logger.LogInformation(
        "Created agent {AgentName} with {ToolCount} tools, model {Model}",
        definition.Name, definition.Tools?.Count ?? 0, definition.ModelDeployment);

    return agent;
}
```

## Interactive Elements

- [x] **Architecture diagram** — 4-layer Clean Architecture with actor cards in each layer
- [x] **Group chat animation** — 13-message conversation showing file read flow (User, AgentHub, MediatR, ContentSafety, Handler, LLM, ToolPermission, FileSystemService)
- [x] **Code↔English translation** — AgentManifest and SkillDefinition (domain models), AgentFactory.CreateAgent (application factory)
- [x] **Quiz** — 4 questions: (1) Which layer owns the AgentManifest? (2) Scenario: you want to add a new LLM provider — which layer do you modify? (3) Why does the AgentFactory live in Application, not Infrastructure? (4) Drag-and-drop: match actors to their layers
- [x] **Glossary tooltips** — Clean Architecture, Domain layer, Application layer, Infrastructure layer, Presentation layer, factory pattern, manifest, CQRS, dependency injection, interface, record (C#), middleware

## Reference Files to Read
- `references/content-philosophy.md` → all sections
- `references/gotchas.md` → all sections
- `references/interactive-elements.md` → "Group Chat Animation", "Code ↔ English Translation Blocks", "Interactive Architecture Diagram", "Multiple-Choice Quizzes", "Drag-and-Drop Matching", "Glossary Tooltips", "Pattern/Feature Cards"
- `references/design-system.md` → "Color Palette" (actor colors), "Typography"

## Connections
- **Previous module:** "What Is This Thing?" — gave the 10,000-foot view and traced a message end-to-end
- **Next module:** "The Conversation Loop" — will dive deep into the CQRS pipeline and how turns actually execute
- **Tone/style notes:** Actor colors from Module 1 apply. When referring to actors, use the same color coding. The group chat should feel like a lively Slack thread, not a dry sequence diagram.
