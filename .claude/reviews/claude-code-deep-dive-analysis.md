# Claude Code Deep Dive — Patterns for Agentic Harness

**Date:** 2026-04-08
**Source:** `C:\Users\kruz7\OneDrive\Documents\Code Repos\yasasbanukaofficial\claude-code`

---

## Executive Summary

After deep analysis of the Claude Code codebase (~70K+ lines of TypeScript), I identified **10 major pattern areas** that map to gaps or improvement opportunities in our Microsoft Agentic Harness. These are organized into 3 tiers by impact and implementation complexity.

---

## Tier 1 — High Impact, Should Build Next

### 1. Context Compaction System

**What Claude Code does:**
- **Auto-Compact State Machine** (`autoCompact.ts`): Monitors token usage per-turn. When usage hits `effectiveWindow - 13K tokens`, triggers automatic compaction. Circuit breaker after 3 consecutive failures.
- **Full Compact** (`compact.ts`): Sends entire message history to Claude for summarization. Creates a `CompactBoundaryMessage` marker. Preserves: plan files (5 max, 5K tokens each), invoked skills (25K budget), deferred tool deltas, MCP instructions.
- **Partial Compact**: Two modes — `up_to` (compact before pivot) and `from` (compact after pivot). Enables surgical compaction of just old or just recent content.
- **Microcompact** (`microCompact.ts`): Lightweight — targets specific tool result types (file reads, shell output, grep, glob, web fetches). Replaces stale content without an LLM call. Two variants: cache-API-based and time-based.
- **Boundary Tracking**: `SystemCompactBoundaryMessage` with metadata (trigger, pre/post token counts, preserved segment UUIDs). Message chain relinking on load.

**What we have:**
- `IContextBudgetTracker` interface defined in `Application.AI.Common`
- No compaction algorithm, no boundary tracking, no auto-trigger

**What to build:**
- `IContextCompactionService` with Full, Partial, and Micro strategies
- `CompactionBoundaryMessage` domain model for message chain tracking
- Auto-compact MediatR behavior that wraps `ExecuteAgentTurn` handler
- Configurable thresholds in `AppConfig.AI.ContextManagement`
- Strategy pattern: `ICompactionStrategy` (Full, Partial, Micro) selected by budget tracker

**Layer placement:**
- Domain: `CompactionBoundary`, `CompactionResult` models
- Application: `IContextCompactionService`, `ICompactionStrategy` interfaces
- Infrastructure.AI: `LlmCompactionStrategy`, `MicroCompactionStrategy`, `AutoCompactStateMachine`

---

### 2. Tool Permission System (3-Phase Resolution)

**What Claude Code does:**
- **Phase 1 — Deny/Ask (Hard Gates)**: Check deny rules → ask rules → tool-specific `checkPermissions()` → safety checks (`.git/`, `.claude/`, shell configs). Safety checks are **bypass-immune**.
- **Phase 2 — Allow/Mode**: Check bypass mode → always-allow rules → convert passthrough to ask.
- **Rule Hierarchy**: 8 sources merged first-match-wins: `userSettings`, `projectSettings`, `localSettings`, `flagSettings`, `policySettings`, `cliArg`, `command`, `session`.
- **Bash Security**: 22 validators via Tree-sitter AST parsing — command substitution, redirection attacks, quote bypass, IFS injection, zsh bypass, Unicode whitespace.
- **Denial Tracking**: Rate-limits repeated denial attempts.
- **Rule Types**: Exact (`rm /path`), Prefix (`git:*`), Wildcard (`rm -rf *`) with max 50 subcommand limit for ReDoS prevention.

**What we have:**
- `IToolPermissionService` interface defined
- No implementation, no rule hierarchy, no AST-based security analysis

**What to build:**
- `ToolPermissionRule` domain model (source, behavior, pattern, matcher)
- `IToolPermissionResolver` with 3-phase algorithm
- Rule sources from `AppConfig.AI.Permissions` (agent-level, skill-level, user-level, session-level)
- Safety gate registry for paths/operations that always require approval
- `PermissionDecision` result type: Allow, Deny, Ask (with reason and source)
- MediatR pipeline behavior for pre-tool permission checks

**Layer placement:**
- Domain: `ToolPermissionRule`, `PermissionDecision`, `SafetyGate` models
- Application: `IToolPermissionResolver`, permission pipeline behavior
- Infrastructure.AI: `ConfigBasedPermissionResolver`, `PatternMatcher`

---

### 3. Hook/Event System (Pre/Post Tool Interception)

**What Claude Code does:**
- **28 event types**: PreToolUse, PostToolUse, SessionStart, SessionEnd, SubagentStart, SubagentStop, PreCompact, PostCompact, CwdChanged, FileChanged, etc.
- **4 hook types**: command (shell), prompt (inject text), agent (delegate), http (webhook)
- **3 registration paths**: settings.json, SDK callbacks, plugin hooks
- **Hook output**: `continue` (proceed?), `suppressOutput`, `stopReason`, `decision` (approve/block), `updatedInput` (modify tool args), `updatedMCPToolOutput` (transform results)
- **Lifecycle**: Trust check → match → parallel execute with timeout → JSON validate → telemetry
- **Error codes**: 0 = success, 2 = blocking (show to model), other = non-blocking (show to user)

**What we have:**
- Nothing. No hook/event system.

**What to build:**
- `IHookRegistry` — register/unregister hooks by event type
- `IHookExecutor` — execute matching hooks with timeout, parallel execution
- `HookEvent` enum covering tool lifecycle, session lifecycle, compaction, agent delegation
- `HookDefinition` model: event, matcher, type (command/prompt/middleware), timeout, once flag
- `HookResult` model: continue, modifiedInput, modifiedOutput, additionalContext
- Pre/Post tool hooks as MediatR pipeline behavior (wraps tool execution)
- Skill-level hooks (skills can declare hooks in their manifest)

**Layer placement:**
- Domain: `HookEvent`, `HookDefinition`, `HookResult` models
- Application: `IHookRegistry`, `IHookExecutor` interfaces, pipeline behavior
- Infrastructure.AI: `ConfigBasedHookRegistry`, `ShellHookExecutor`, `PromptHookExecutor`

---

### 4. System Prompt Composition (Memoized Sections)

**What Claude Code does:**
- System prompt built from composable `SystemPromptSection` objects
- Two caching strategies: `systemPromptSection()` (memoized until /clear or /compact) and `DANGEROUS_uncachedSystemPromptSection()` (recomputes every turn, breaks prompt cache)
- Sections: git status, memory files, permission rules, coordinator mode tools, skills, deferred tools
- `resolveSystemPromptSections()` — `Promise.all` all sections, check cache before computing
- Cache clearing triggered by `/clear`, `/compact`, and specific state changes

**What we have:**
- `ITieredContextAssembler` interface for progressive skill loading
- Static prompt assembly in agent factory

**What to build:**
- `ISystemPromptComposer` — composable section-based prompt builder
- `SystemPromptSection` model: name, computeFn (async), cacheable flag, priority/order
- `ICachedPromptSection` — memoized sections with explicit invalidation
- Section types: AgentIdentity, SkillInstructions, ToolSchemas, PermissionRules, GitContext, UserContext, SessionState
- Invalidation triggers: compaction, skill change, tool change, permission change
- Budget-aware: each section reports token estimate, composer respects total budget

**Layer placement:**
- Domain: `SystemPromptSection` model
- Application: `ISystemPromptComposer`, `ICachedPromptSection` interfaces
- Infrastructure.AI: `MemoizedPromptComposer`, section implementations

---

## Tier 2 — Medium Impact, Build After Tier 1

### 5. Subagent Orchestration (AgentTool Pattern)

**What Claude Code does:**
- `AgentTool` spawns subagents with isolated contexts, scoped tool pools, and optional worktree isolation
- **Tool filtering pipeline**: `ALL_AGENT_DISALLOWED_TOOLS` → `ASYNC_AGENT_ALLOWED_TOOLS` → `IN_PROCESS_TEAMMATE_ALLOWED_TOOLS` — layered allowlists per agent type
- **Built-in agent types**: explore, plan, verification, general-purpose, claude-code-guide — each with predefined tool sets and system prompts
- **Fork agents**: Inherit parent's exact tool pool + system prompt bytes for prompt cache parity
- **Coordinator mode**: Spawns workers via TeamCreate, receives results as `<task-notification>` XML
- **Teammate mailbox**: File-based messaging at `.claude/teams/{team}/inboxes/{agent}.json` with lockfile concurrency
- **Worktree isolation**: Git worktree per agent, symlinked node_modules, branch-scoped changes

**What we have:**
- `RunOrchestratedTask` command/handler
- `IAgentFactory.CreateAgentsByCategory/Tags` for batch creation
- A2A protocol skeleton
- No tool scoping per subagent, no worktree isolation, no mailbox

**What to build:**
- `SubagentDefinition` domain model: agentType, toolAllowlist, toolDenylist, permissionMode, maxTurns, model
- Built-in subagent profiles: Explore, Plan, Verify, Execute (matching our skill categories)
- `ISubagentToolResolver` — filters parent tool pool per subagent definition
- `IAgentMailbox` — async message passing between agents
- `SubagentContext` — isolated execution context with scoped tools and budget
- Enhance `RunOrchestratedTask` to use subagent definitions for delegation

**Layer placement:**
- Domain: `SubagentDefinition`, `AgentMessage` models
- Application: `ISubagentToolResolver`, `IAgentMailbox` interfaces
- Infrastructure.AI: `InMemoryAgentMailbox`, `SubagentToolResolver`

---

### 6. Tool Result Storage & Budget Management

**What Claude Code does:**
- **Persistence thresholds**: Default 50K chars per result, 200K aggregate per message
- Large results persisted to `sessionId/tool-results/` with `<persisted-output>` XML wrapper
- Preview (first 2000 bytes) kept in context, full content on disk
- Per-tool overrides via feature flags
- **Token Budget Tracker**: `BudgetTracker` with continuation counting, diminishing returns detection (`continuationCount >= 3` + last two deltas < 500 tokens = stop), 90% completion threshold
- Agents (with agentId) and disabled budgets skip all checks

**What we have:**
- `IContextBudgetTracker` interface (tracks budget but no persistence or result storage)
- No tool result persistence, no diminishing returns detection

**What to build:**
- `IToolResultStore` — persist large results to disk/blob, return preview + reference
- `ToolResultReference` model: resultId, previewContent, fullContentPath, sizeChars
- Budget tracker enhancements: continuation counting, diminishing returns detection, per-agent budget isolation
- Configurable thresholds in `AppConfig.AI.ContextManagement.ToolResultLimits`
- Token budget decision: Continue, Stop, Nudge (with message)

**Layer placement:**
- Domain: `ToolResultReference`, `TokenBudgetDecision` models
- Application: Enhanced `IContextBudgetTracker`, `IToolResultStore` interface
- Infrastructure.AI: `FileSystemToolResultStore`, `DiminishingReturnsBudgetTracker`

---

### 7. Streaming Tool Execution (Parallel/Serial Batching)

**What Claude Code does:**
- `StreamingToolExecutor` — processes tool calls as they stream from the LLM
- `partitionToolCalls()` — batches by safety: read-only tools run in parallel, write tools run serially
- `isConcurrencySafe()` per tool — opt-in to parallel execution
- Progress reporting via async `onProgress()` callback during streaming
- Error classification via `classifyToolError()` for telemetry-safe error strings

**What we have:**
- Sequential tool execution in `ExecuteAgentTurn` handler
- No concurrency classification, no streaming tool execution

**What to build:**
- `IToolExecutionStrategy` — Serial, Parallel, Batched (read-parallel, write-serial)
- `ToolConcurrencyClassifier` — reads `ITool.IsReadOnly` to partition
- `IToolProgressReporter` — progress callbacks during long-running tools
- Error classification for telemetry: `ToolErrorClassifier`

**Layer placement:**
- Application: `IToolExecutionStrategy`, `IToolProgressReporter` interfaces
- Infrastructure.AI: `BatchedToolExecutionStrategy`, `ToolConcurrencyClassifier`

---

## Tier 3 — Nice to Have, Build When Needed

### 8. Prompt Cache Break Detection

**What Claude Code does:**
- Tracks per-source state hashes: systemHash, toolsHash, cacheControlHash, perToolHashes
- Detects which specific tool description changed (77% of cache breaks are per-tool)
- Creates `.diff` files for debugging cache misses
- Latches for session-stable flags (overage eligibility, cached MC) to prevent spurious breaks

**Relevance to us:** Azure OpenAI doesn't have Anthropic's prompt caching semantics, but the pattern of tracking what changed in the system prompt between turns is useful for debugging and cost optimization. Defer until we need it.

---

### 9. CLAUDE.md Discovery Algorithm

**What Claude Code does:**
- Directory traversal upward from CWD to root
- Priority: managed → user → project → local (closer to CWD = higher priority)
- `@include` directive for cross-referencing
- Frontmatter with `paths:` glob patterns to scope rules to specific files
- Strips HTML comments, truncates MEMORY.md entries

**Relevance to us:** We already have AGENT.md and SKILL.md parsing. The directory traversal + `@include` pattern would be useful for multi-skill scenarios where skills reference shared configuration. Lower priority since our skill loader handles discovery differently.

---

### 10. Denial Tracking & Rate Limiting

**What Claude Code does:**
- `denialTracking.ts` — rate-limits repeated permission denial attempts
- Prevents the agent from asking the same question repeatedly
- Tracks denial count per tool+pattern combination

**What to build (simple):**
- `IDenialTracker` — track denied tool+operation combos per session
- Auto-escalate to hard-deny after N denials for same pattern
- Wire into permission resolver

---

## Implementation Roadmap

```
Phase 1 (Foundation): Context Compaction + Permissions + Hooks
  └─ These three systems are load-bearing for the agent loop
  └─ Compaction enables long conversations
  └─ Permissions enable safe tool use
  └─ Hooks enable extensibility without code changes

Phase 2 (Orchestration): System Prompt Composer + Subagent Orchestration
  └─ Composer makes prompt assembly testable and budget-aware
  └─ Subagent patterns enable delegation and parallelism

Phase 3 (Optimization): Tool Result Storage + Streaming Execution + Budget Enhancement
  └─ These optimize performance and context efficiency
  └─ Build when the basic loop is working end-to-end

Phase 4 (Polish): Cache Detection + Denial Tracking + CLAUDE.md Discovery
  └─ Nice-to-haves for production hardening
```

---

## Pattern Comparison Matrix

| Pattern | Claude Code | Our Harness | Gap |
|---------|------------|-------------|-----|
| Context Compaction | Full + Partial + Micro + Auto | Interface only | **CRITICAL** |
| Tool Permissions | 3-phase, 8 sources, AST bash | Interface only | **CRITICAL** |
| Hook/Event System | 28 events, 4 types, plugin-aware | None | **HIGH** |
| System Prompt Composition | Memoized sections, cache-aware | Static assembly | **HIGH** |
| Subagent Orchestration | Tool scoping, mailbox, worktree | Basic command | **MEDIUM** |
| Tool Result Storage | Disk persist, preview, budget | None | **MEDIUM** |
| Streaming Tool Execution | Parallel read, serial write | Sequential only | **MEDIUM** |
| Prompt Cache Detection | Per-tool hashing, diff files | N/A (Azure) | LOW |
| Config Discovery | Directory walk, @include | Skill loader | LOW |
| Denial Tracking | Rate-limited per pattern | None | LOW |

---

## Key Design Principles Observed in Claude Code

1. **Fail-closed defaults**: `isConcurrencySafe: false`, `isReadOnly: false`, `isDestructive: false` — assume worst case, opt-in to less restrictive
2. **Budget-first thinking**: Everything has token budgets — skills (25K), plans (5K each, 5 max), tool results (50K), system prompt sections
3. **Boundary messages**: Compaction creates explicit boundaries in message history — enables surgical replay and debugging
4. **Feature flags for safety**: Dangerous features behind flags, not config — prevents accidental enablement
5. **Cache-aware architecture**: Prompt caching drives architectural decisions — memoized sections, byte-for-byte fork inheritance, schema caching
6. **Trust but verify**: Internal code trusted, but system boundaries (bash, file system, MCP) always gated
7. **Graceful degradation**: Circuit breakers (auto-compact after 3 failures), diminishing returns detection, timeout fallbacks
