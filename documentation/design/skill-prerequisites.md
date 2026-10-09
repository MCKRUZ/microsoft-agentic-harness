# Skill Prerequisites

> **Status: Implemented.** `SkillPrerequisiteResolver` (`Application.AI.Common/Services/Skills/`) builds the prerequisite map; `SkillPrerequisiteMiddleware` (`Application.AI.Common/Middleware/`) filters tools per LLM call. Both are covered by `SkillPrerequisiteResolverTests` and `SkillPrerequisiteMiddlewareTests`. This doc is the design of record for the shipped behavior.

## Problem

Skills in a multi-skill agent have no ordering constraints. An agent with `[validate, deploy]` skills exposes all tools from both immediately. Nothing prevents the LLM from calling deploy tools before validation has run. For enterprise workloads (validate-before-deploy, research-before-present), this is a gap.

## Goals

1. Skills declare prerequisites and a completion signal in SKILL.md frontmatter
2. Tools from prerequisite-blocked skills are withheld until prerequisites complete
3. Completion is detected when a named tool is invoked without error
4. Prerequisite cycles are detected at agent creation time (fail-fast)
5. Instructions for locked skills are included so the LLM knows what's coming
6. Backward compatible — skills without prerequisites work identically

## Non-Goals

- Harness-controlled skill switching (LLM self-orchestrates)
- Cross-conversation prerequisite state (scoped to conversation)
- Transitive prerequisite chains deeper than the declared graph

---

## Design

### Declaration in SKILL.md

```yaml
---
name: deploy-infra
prerequisites: [validate-infra]
completion_tool: deploy_execute
---
```

- `prerequisites`: list of skill IDs that must complete before this skill's tools unlock.
- `completion_tool`: the tool name that signals this skill is complete. When invoked without error, the skill is marked done.
- If `completion_tool` is omitted, the skill is always considered complete (no gate).
- If `prerequisites` is omitted, the skill has no dependencies (always unlocked).

### Domain Model

```csharp
// SkillDefinition gains:
public IList<string> Prerequisites { get; set; } = new List<string>();
public string? CompletionTool { get; set; }

// Computed:
public bool HasPrerequisites => Prerequisites.Count > 0;
public bool HasCompletionTool => !string.IsNullOrEmpty(CompletionTool);
```

### Completion Tracking

```csharp
public interface ISkillCompletionTracker
{
    void MarkCompleted(string conversationId, string skillId);
    bool IsCompleted(string conversationId, string skillId);
    IReadOnlySet<string> GetCompletedSkills(string conversationId);
    void ClearConversation(string conversationId);
}
```

In-memory implementation using `ConcurrentDictionary<string, HashSet<string>>`. Conversation-scoped lifetime — entries are cleared by `IAgentConversationCache.Evict` when the conversation ends or is reassigned to another agent. They are kept across `Invalidate` and across a rebuild triggered by changed skills or options, so a turn that only changes settings does not re-lock unlocked skills.

### Prerequisite Metadata

Computed during `MapToAgentContextAsync` and stashed in `AgentExecutionContext.AdditionalProperties`:

```csharp
public class SkillPrerequisiteMap
{
    public IReadOnlyDictionary<string, SkillPrerequisiteEntry> Skills { get; init; }
}

public class SkillPrerequisiteEntry
{
    public required string SkillId { get; init; }
    public required IReadOnlyList<string> Prerequisites { get; init; }
    public string? CompletionTool { get; init; }
    public required IReadOnlyList<string> ToolNames { get; init; }
}
```

### Cycle Detection

In `AgentFactory.CreateAgentFromSkillsAsync`, after resolving all skills:
1. Build a directed graph: skill → prerequisites
2. Topological sort (Kahn's algorithm)
3. If sort fails (cycle detected), throw `InvalidOperationException` naming the cycle

### Per-Turn Tool Filtering

A `DelegatingChatClient` middleware (`SkillPrerequisiteMiddleware`) sits in the chat client pipeline:

```
ChatClientAgent
  → OpenTelemetry
  → FunctionInvocation
  → ObservabilityMiddleware
  → SkillPrerequisiteMiddleware   ← NEW
  → ToolDiagnosticsMiddleware
  → DistributedCache
  → Inner ChatClient (API)
```

On each LLM call:
1. Read `SkillPrerequisiteMap` from context
2. For each skill with prerequisites, check if all prerequisites are completed via `ISkillCompletionTracker`
3. Build a set of blocked tool names (tools belonging to skills whose prerequisites aren't met)
4. Clone `ChatOptions`, filter `Tools` to exclude blocked tools
5. Forward to inner client
6. After response, scan `FunctionCallContent` in response messages for completion tool matches
7. If a completion tool was invoked, mark the skill as completed

### Why not a MediatR behavior?

MediatR behaviors wrap the entire `ExecuteAgentTurnCommand` handler — they run before/after the full turn, not individual tool invocations within the turn. The `ChatClientAgent.RunAsync` internally loops through multiple LLM calls (tool call → result → next call). A DelegatingChatClient middleware intercepts each individual LLM call, which is the right granularity for:
- Filtering tools dynamically as prerequisites complete mid-turn
- Detecting completion signals immediately after each tool invocation

---

## What Doesn't Change

| Component | Why unchanged |
|---|---|
| `AgentExecutionContext` | Prerequisite map goes in `AdditionalProperties` |
| `AIAgent.RunAsync()` | Sees filtered tools, doesn't know about prerequisites |
| `ISkillMetadataRegistry` | Read-only, no prerequisite logic |
| `AgentConversationCache` | Cache key stays `conversationId` |

---

## Backward Compatibility

- Skills without `prerequisites` or `completion_tool`: no filtering, no tracking, identical behavior
- Single-skill agents: no prerequisites possible, no middleware effect
- `SkillPrerequisiteMiddleware` is only added when the prerequisite map is non-empty

---

## Test Plan

1. Skill without prerequisites has all tools available immediately
2. Skill with unmet prerequisites has its tools withheld
3. Completion tool invocation marks skill as completed
4. Prerequisites met → tools become available on next LLM call
5. Cycle in prerequisites fails agent creation with clear error
6. Transitive prerequisites (A→B→C) resolved correctly
7. Skills without `completion_tool` are always considered complete
8. Single-skill agent works identically (backward compat)
9. `ISkillCompletionTracker` scopes state per conversation
10. Middleware passes through when no prerequisite map exists

---

## Decisions

1. **Explicit completion signal** — `completion_tool` in frontmatter, not "any tool invocation"
2. **Per-turn tool filtering via DelegatingChatClient** — not MediatR behavior (wrong granularity)
3. **In-memory tracking** — conversation-scoped, no persistence needed
4. **Instructions included for locked skills** — LLM sees the plan, just can't execute yet
5. **Cycle detection at creation** — fail-fast via topological sort
6. **No completion_tool = always complete** — backward compatible, handles instructional skills
