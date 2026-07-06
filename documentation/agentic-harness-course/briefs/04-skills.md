# Module 4: Skills — What Agents Know

## Teaching Arc
- **Metaphor:** A spy's briefcase with compartments — Compartment 1 (the label on the outside) tells you what mission this is. Compartment 2 (the main folder) has the full briefing. Compartment 3 (the hidden bottom) has the reference materials, templates, and scripts you only pull out when you're actually in the field. You never open Compartment 3 during planning — that would overwhelm you.
- **Opening hook:** "An LLM has a finite memory — its context window. Dump everything into it and the agent drowns. Load too little and it doesn't know how to help. The skills system solves this with a simple trick: give the agent a table of contents first, and only load the full chapter when it's needed."
- **Key insight:** Progressive disclosure is the idea that you only show information when it becomes relevant. This is the same principle behind how a good GPS works — it doesn't show you the entire route at once, it gives you the next turn. The 3-tier system (Index Card → Folder → Filing Cabinet) is how the harness applies this to AI context management.
- **"Why should I care?":** Token budgets are real money. Loading unnecessary context wastes tokens (= costs) and can actually make the agent *worse* by drowning signal in noise. Understanding progressive disclosure helps you write better skills and debug context overflow issues.
- **Additional insight:** Skills can now declare prerequisites (other skills that must complete first) and operate in two modes: **Managed** (harness-native with explicit tool declarations) or **Injected** (plugin-provided, receiving all MCP tools from the plugin's servers). Agents can use multiple skills simultaneously — instructions merge and tool lists combine.

## Screens (6)

### Screen 1: The Context Window Problem (Visual)
Show a visual of a context window (like a glass jar) with tokens as colored marbles. System prompt = red marbles, skills = blue, tools = green, conversation = yellow. When the jar overflows, things get lost. The ContextBudgetTracker is the person watching the jar.

### Screen 2: Three Tiers — Progressive Disclosure (Layer Toggle)
Interactive toggle showing the same skill at all 3 tiers:
- Tier 1 (Index Card): just name, description, tags (~100 tokens)
- Tier 2 (Folder): full instructions, tool declarations (~5,000 tokens)  
- Tier 3 (Filing Cabinet): templates, references, scripts (unbounded, never loaded into context)

### Screen 2b: Skill Modes & Prerequisites (Visual)
Side-by-side comparison of the two skill modes:
- **Managed** (harness-native): skill declares specific tool names in its `AllowedTools` list. The factory resolves only those tools via MCP or keyed DI. You control exactly what the skill can touch.
- **Injected** (plugin-provided): skill comes from a local plugin directory. It gets *all* MCP tools from the plugin's configured servers. Plugin governance filters (AllowedTools/DeniedTools) control what reaches the agent.

Below the comparison, show a prerequisite chain: Skill A (completion_tool: `finish_research`) must complete before Skill B can activate. Visual of a locked padlock on Skill B that opens when Skill A's completion tool fires.

### Screen 3: SKILL.md — How Skills Are Written (Code Translation)
Show a real SKILL.md file with code↔English translation of the YAML frontmatter and markdown body.

### Screen 4: The Budget Tracker (Code Translation)
Code↔English of ContextBudgetTracker — how it tracks token allocations and decides when to compact.

### Screen 5: Compaction — When Memory Gets Full + Quiz
Brief explanation of compaction strategies (boundary messages, micro-compaction) and quiz.

## Code Snippets

### Snippet 1: SkillDefinition tiers
```csharp
public class SkillDefinition
{
    // Tier 1: Always loaded — lightweight metadata
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public IList<string> Tags { get; set; } = new List<string>();

    // Tier 2: On-demand — full instructions and tool access
    public string Instructions { get; set; } = string.Empty;
    public IList<ToolDeclaration> ToolDeclarations { get; set; } = new List<ToolDeclaration>();
    public IList<string> AllowedTools { get; set; } = new List<string>();

    // Skill execution mode and plugin integration
    public string? PluginSource { get; set; }
    public SkillMode Mode => /* Managed or Injected based on PluginSource */;
    public IReadOnlyList<string> Prerequisites { get; set; } = [];
    public string? CompletionTool { get; set; }

    // Tier 3: Resources — loaded only during execution
    public IList<SkillResource> Templates { get; set; } = new List<SkillResource>();
    public IList<SkillResource> References { get; set; } = new List<SkillResource>();
    public IList<SkillResource> Scripts { get; set; } = new List<SkillResource>();
}
```

### Snippet 2: SKILL.md example (Orchestrator)
```markdown
---
name: orchestrator
description: Decomposes complex tasks into subtasks and delegates to specialized sub-agents
allowed-tools: file_system web_search
---

# Your Role

You are a task orchestrator. When given a complex task:
1. Break it into independent subtasks
2. Assign each subtask to the most appropriate agent
3. Synthesize results into a coherent answer

## Task Decomposition Format
- Each subtask must have: description, assigned agent, expected output
- Prefer parallel subtasks over sequential when possible

## Guidelines
- Never execute subtasks yourself — always delegate
- If a subtask fails, retry with a different agent or approach
```

### Snippet 3: ContextBudgetTracker (tiered skill loading)
```csharp
// IContextBudgetTracker records token allocations per agent, by named component
// ("system_prompt", "tool_schemas", "skill_context", "conversation_history"),
// and throws ContextBudgetExceededException before the window overflows.
public interface IContextBudgetTracker
{
    void RecordAllocation(string agentName, string component, int tokens);
    int  GetTotalAllocated(string agentName);
    void EnsureBudget(string agentName, int additionalTokens, int totalBudget);
    IReadOnlyDictionary<string, int> GetBreakdown(string agentName);
}

// Tiered loading in practice: only the tiers that actually enter the window are
// charged against the budget. Tier 3 resource paths are exposed, never loaded.
void LoadSkillContext(string agentName, SkillDefinition skill, int totalBudget)
{
    // Tier 1: always loaded — lightweight metadata (id, name, description, tags)
    int tier1Tokens = EstimateTokens(BuildTier1Metadata(skill));
    tracker.EnsureBudget(agentName, tier1Tokens, totalBudget);
    tracker.RecordAllocation(agentName, "skill_context", tier1Tokens);

    // Tier 2: on-demand — full instructions + tool schemas, only when the skill activates
    if (skill.Mode == SkillMode.Injected)
    {
        int tier2Tokens = EstimateTokens(skill.Instructions);
        tracker.EnsureBudget(agentName, tier2Tokens, totalBudget);
        tracker.RecordAllocation(agentName, "skill_context", tier2Tokens);
    }

    // Tier 3: never loaded into context — paths handed to the agent for on-demand reads
    var tier3Paths = skill.References.Concat(skill.Templates)
        .Select(r => r.Path).ToList();
}
```

### Snippet 4: CompactionBoundaryMessage
```csharp
public sealed record CompactionBoundaryMessage
{
    public required string Id { get; init; }
    public required CompactionTrigger Trigger { get; init; }
    public required CompactionStrategy Strategy { get; init; }
    public required int PreCompactionTokens { get; init; }
    public required int PostCompactionTokens { get; init; }
    public int TokensSaved => PreCompactionTokens - PostCompactionTokens;
    public required string Summary { get; init; }
}
```

## Interactive Elements

- [x] **Visual metaphor** — context window as a glass jar with colored marbles (CSS animation)
- [x] **Code↔English translation** — SkillDefinition (3 tiers) and TieredContextAssembler
- [x] **Group chat animation** — ContextBudgetTracker, TieredContextAssembler, and SkillLoader discussing whether to load Tier 2 for a skill: Budget says "We have 12,000 tokens left," Assembler says "Tier 2 for research-agent is 4,800 tokens," Budget says "That fits — go ahead," then later Budget says "Warning: only 2,100 tokens remain," Assembler says "Compaction needed — summarizing old messages."
- [x] **Quiz** — 6 questions: (1) Why doesn't the agent load all skills at Tier 3 immediately? (2) Scenario: your agent keeps forgetting earlier instructions mid-conversation — what might be happening? (3) What triggers compaction? (4) Match the tier to its description (drag-and-drop) (5) What happens if Skill B lists Skill A as a prerequisite but Skill A hasn't completed yet? (answer: Skill B stays locked — the harness won't activate it until Skill A's completion tool fires) (6) What is the difference between a Managed skill and an Injected skill? (answer: Managed skills declare specific tools and the factory resolves only those; Injected skills come from plugins and receive all MCP tools from the plugin's servers, filtered by plugin governance)
- [x] **Glossary tooltips** — context window, token, progressive disclosure, compaction, frontmatter, YAML, metadata, budget, allocation, estimation, micro-compaction, boundary message, skill mode, managed skill, injected skill, plugin, plugin source, prerequisite, completion tool

## Reference Files to Read
- `references/content-philosophy.md` → all sections
- `references/gotchas.md` → all sections
- `references/interactive-elements.md` → "Code ↔ English Translation Blocks", "Group Chat Animation", "Multiple-Choice Quizzes", "Drag-and-Drop Matching", "Glossary Tooltips", "Callout Boxes"

## Connections
- **Previous module:** "The Conversation Loop" — showed the orchestration loop. This module explains how the agent knows what to do within each turn (skills) and how it manages its memory.
- **Next module:** "Tools & The Outside World" — will cover what the agent can *do* (tools, MCP, A2A) vs. what it *knows* (skills, covered here).
- **Tone/style notes:** The spy briefcase metaphor should be introduced in Screen 1 and referenced again in Screen 2. The glass jar visual is the hero element for Screen 1. Keep compaction brief — it's complex and one screen is enough.
