# Module 4: Skills — What Agents Know

## Teaching Arc
- **Metaphor:** A spy's briefcase with compartments — Compartment 1 (the label on the outside) tells you what mission this is. Compartment 2 (the main folder) has the full briefing. Compartment 3 (the hidden bottom) has the reference materials, templates, and scripts you only pull out when you're actually in the field. You never open Compartment 3 during planning — that would overwhelm you.
- **Opening hook:** "An LLM has a finite memory — its context window. Dump everything into it and the agent drowns. Load too little and it doesn't know how to help. The skills system solves this with a simple trick: give the agent a table of contents first, and only load the full chapter when it's needed."
- **Key insight:** Progressive disclosure is the idea that you only show information when it becomes relevant. This is the same principle behind how a good GPS works — it doesn't show you the entire route at once, it gives you the next turn. The 3-tier system (Index Card → Folder → Filing Cabinet) is how the harness applies this to AI context management.
- **"Why should I care?":** Token budgets are real money. Loading unnecessary context wastes tokens (= costs) and can actually make the agent *worse* by drowning signal in noise. Understanding progressive disclosure helps you write better skills and debug context overflow issues.

## Screens (5)

### Screen 1: The Context Window Problem (Visual)
Show a visual of a context window (like a glass jar) with tokens as colored marbles. System prompt = red marbles, skills = blue, tools = green, conversation = yellow. When the jar overflows, things get lost. The ContextBudgetTracker is the person watching the jar.

### Screen 2: Three Tiers — Progressive Disclosure (Layer Toggle)
Interactive toggle showing the same skill at all 3 tiers:
- Tier 1 (Index Card): just name, description, tags (~100 tokens)
- Tier 2 (Folder): full instructions, tool declarations (~5,000 tokens)  
- Tier 3 (Filing Cabinet): templates, references, scripts (unbounded, never loaded into context)

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
    #region Level 1: Index Card (Metadata — Always Loaded)

    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public IList<string> Tags { get; set; } = new List<string>();

    #endregion

    #region Level 2: Folder (Instructions — On Demand)

    public string Instructions { get; set; } = string.Empty;
    public IList<ToolDeclaration> ToolDeclarations { get; set; } = new List<ToolDeclaration>();
    public IList<string> AllowedTools { get; set; } = new List<string>();

    #endregion

    #region Level 3: Filing Cabinet (Resources — On Demand)

    public IList<SkillResource> Templates { get; set; } = new List<SkillResource>();
    public IList<SkillResource> References { get; set; } = new List<SkillResource>();
    public IList<SkillResource> Scripts { get; set; } = new List<SkillResource>();

    #endregion
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

### Snippet 3: TieredContextAssembler
```csharp
public sealed class TieredContextAssembler : ITieredContextAssembler
{
    private const int DefaultTier1MaxTokens = 3000;
    private const int DefaultTier2MaxTokens = 8000;

    public async Task<TieredContextResult> AssembleAsync(
        SkillDefinition skill, TierLoadingOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var result = new TieredContextResult { SkillId = skill.Id };

        // Tier 1: Always loaded — lightweight metadata
        var tier1Content = BuildTier1Content(skill);
        var tier1Tokens = TokenEstimationHelper.EstimateTokens(tier1Content);
        _budgetTracker.RecordAllocation("skill-tier1", skill.Id, tier1Tokens);

        // Tier 2: On-demand — full instructions
        if (options?.LoadTier2 == true)
        {
            var tier2Content = BuildTier2Content(skill);
            var tier2Tokens = TokenEstimationHelper.EstimateTokens(tier2Content);
            _budgetTracker.RecordAllocation("skill-tier2", skill.Id, tier2Tokens);
        }

        // Tier 3: Never loaded into context — paths exposed for on-demand access
        result.Tier3ResourcePaths = skill.References
            .Concat(skill.Templates)
            .Select(r => r.Path).ToList();
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
- [x] **Quiz** — 4 questions: (1) Why doesn't the agent load all skills at Tier 3 immediately? (2) Scenario: your agent keeps forgetting earlier instructions mid-conversation — what might be happening? (3) What triggers compaction? (4) Match the tier to its description (drag-and-drop)
- [x] **Glossary tooltips** — context window, token, progressive disclosure, compaction, frontmatter, YAML, metadata, budget, allocation, estimation, micro-compaction, boundary message

## Reference Files to Read
- `references/content-philosophy.md` → all sections
- `references/gotchas.md` → all sections
- `references/interactive-elements.md` → "Code ↔ English Translation Blocks", "Group Chat Animation", "Multiple-Choice Quizzes", "Drag-and-Drop Matching", "Glossary Tooltips", "Callout Boxes"

## Connections
- **Previous module:** "The Conversation Loop" — showed the orchestration loop. This module explains how the agent knows what to do within each turn (skills) and how it manages its memory.
- **Next module:** "Tools & The Outside World" — will cover what the agent can *do* (tools, MCP, A2A) vs. what it *knows* (skills, covered here).
- **Tone/style notes:** The spy briefcase metaphor should be introduced in Screen 1 and referenced again in Screen 2. The glass jar visual is the hero element for Screen 1. Keep compaction brief — it's complex and one screen is enough.
