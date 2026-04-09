# Domain.AI

If Domain.Common is the shared language of the application, Domain.AI is the vocabulary of intelligence. This project defines what an agent *is*, what it *knows*, what it *can do*, and how it *behaves* — all as pure domain models with zero infrastructure dependencies.

Every concept that makes the harness an *agentic* system rather than just a chat wrapper lives here: agent manifests, skill definitions, tool declarations, permission rules, hook events, compaction strategies, prompt sections, and the telemetry conventions that make all of it observable.

---

## Agents

An agent in this system is more than a system prompt and an API key. It's a composite of identity, capabilities, and constraints:

- **AgentManifest** — Parsed from `AGENT.md` files. Defines the agent's name, role, orchestration behavior, tool access, workflow state configuration, decision frameworks, and skill references. This is the blueprint.
- **AgentExecutionContext** — The runtime form of a manifest. Carries the concrete instructions, tool instances, middleware pipeline, and deployment config needed to actually run the agent.
- **SubagentDefinition** — Describes a child agent: its type (researcher, analyst, orchestrator, executor, verifier), tool allowlist, permission mode, turn limits, and model overrides.
- **AgentMessage** — Messages passed between agents through the mailbox system, categorized as Task, Result, Notification, or Error.

## Skills: The Knowledge Model

Skills are the harness's answer to the question "how does an agent know what it knows without blowing its context budget?" The domain model captures the three-tier progressive disclosure system:

- **SkillDefinition** — A loaded skill with its ID, name, category, tags, instructions, allowed tools, required resources, and token estimates per tier.
- **ContextLoading** — Configures what loads at each tier: Tier 1 (always loaded, ~100 tokens), Tier 2 (on-demand, ~5K tokens), Tier 3 (active execution only, unbounded).
- **ContextContract** — The skill's input/output contract: what it requires, what it produces, and what it depends on.
- **SkillResource** — Individual artifacts (templates, reference docs, examples) attached to a skill.
- **SkillAgentOptions** — Controls how a skill maps to an agent: which resources to load, deployment overrides, additional tools.

## Tools

- **ToolDeclaration** — What a skill says it needs: tool name, supported operations, fallback behavior, optionality, usage guidance (`WhenToUse`, `WhenNotToUse`).
- **ToolConcurrencyClassification** — Classifies tools as `ReadOnly` (safe for parallel execution), `WriteSerial` (must run sequentially), or `Unknown` (fail-closed to serial).

## Permissions

The permission model is built for defense in depth:

- **ToolPermissionRule** — A single rule: tool pattern, operation pattern, behavior (Allow/Deny/Ask/Bypass), source, priority, and whether it's immune to safety gate bypass.
- **PermissionDecision** — The resolved outcome of evaluating all applicable rules: what was decided, which rule matched, from what source, and why.
- **PermissionRuleSource** — Where a rule came from: AgentManifest, SkillDefinition, UserSettings, ProjectSettings, LocalSettings, SessionOverride, PolicySettings, or CliArgument.
- **SafetyGate** — Paths that are always dangerous (`.git/`, `.ssh/`) and require explicit approval regardless of permission mode.
- **DenialRecord** — Tracks when and why a tool invocation was denied, for rate limiting and audit.

## Hooks

Hooks let external code intercept the agent's lifecycle:

- **HookDefinition** — Subscribes to a lifecycle event with an execution mechanism (Command, Prompt, Middleware, Http), optional tool matcher, timeout, priority, and RunOnce flag.
- **HookEvent** — The 16 lifecycle events: PreToolUse, PostToolUse, SessionStart, SessionEnd, PreCompact, PostCompact, SubagentStart, SubagentStop, SkillLoaded, SkillRemoved, and more.
- **HookExecutionContext** — Runtime context passed to hooks: event type, agent name, tool details, parameters, turn number, conversation ID.
- **HookResult** — What the hook returns: Continue or Block, with optional modified input/output and additional context.

## Context & Compaction

When the context window fills up, the agent needs to compress:

- **CompactionStrategy** — Three algorithms: Full (LLM summarizes everything), Partial (compress old, keep recent), Micro (trim individual tool results).
- **CompactionBoundaryMessage** — Marker inserted where compaction occurred, enabling replay and chain relinking.
- **CompactionResult** — Outcome: success, boundary marker, error, and metrics.
- **TokenBudgetDecision** — When to load skills, trigger compaction, or fall back to Index Cards.
- **ToolResultReference** — Pointer to a previously-executed tool result for deduplication.

## Prompts

The system prompt is assembled from composable sections:

- **SystemPromptSection** — A named section with type, priority, cacheability flag, estimated tokens, and content.
- **SystemPromptSectionType** — Section categories: AgentIdentity, SkillInstructions, ToolSchemas, PermissionRules, GitContext, UserContext, Limitations, TokenBudgetWarning.
- **PromptHashSnapshot** — SHA256 hashes of the system prompt and tool schemas for cache break detection.
- **PromptCacheBreakReport** — Reports which sections changed between turns.

## Configuration Discovery

- **DiscoveredConfigFile** — A config file found during directory walk: path, scope, priority, content, and optional path glob filters.
- **ConfigScope** — Priority levels: Managed (lowest), User, Project, Local (highest).

## Telemetry Conventions

Ten convention classes define semantic attribute names for OpenTelemetry spans and metrics across every subsystem: agents, compaction, context budgets, hooks, MCP, orchestration, permissions, safety, tokens, and tools. These constants ensure consistent, queryable telemetry across the entire harness.

---

## Project Structure

```
Domain.AI/
├── A2A/                         AgentCard (agent discovery protocol)
├── Agents/                      AgentManifest, AgentExecutionContext, SubagentDefinition, AgentMessage
├── Compaction/                  CompactionStrategy, CompactionResult, CompactionBoundaryMessage
├── Config/                      ConfigScope, DiscoveredConfigFile
├── Context/                     TokenBudgetDecision, ToolResultReference
├── Enums/                       AgentDirectory
├── Hooks/                       HookDefinition, HookEvent, HookResult, HookExecutionContext
├── Models/                      AgentRunManifest, ContentSafetyResult, ToolResult, FileSearchResult
├── Permissions/                 ToolPermissionRule, PermissionDecision, SafetyGate, DenialRecord
├── Prompts/                     SystemPromptSection, PromptHashSnapshot, PromptCacheBreakReport
├── Skills/                      SkillDefinition, ContextContract, ContextLoading, SkillResource
├── Telemetry/Conventions/       10 convention classes for OTel semantic attributes
└── Tools/                       ToolDeclaration, ToolConcurrencyClassification
```

## Dependencies

- **Domain.Common** — Result pattern, AppConfig hierarchy
- **Microsoft.Extensions.AI.Abstractions** — `AITool` contract for tool interop

Nothing else. The domain layer stays pure.
