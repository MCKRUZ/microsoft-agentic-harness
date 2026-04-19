

I now have comprehensive understanding of the entire codebase. Let me construct the implementation plan.

---

# Implementation Plan: Claude Code 10 Patterns for Microsoft Agentic Harness

## Overview

This plan implements 10 architectural patterns identified in the Claude Code deep dive analysis, organized across 4 phases of work. Each pattern is decomposed into domain models, application interfaces, infrastructure implementations, pipeline behaviors, DI registration, config POCOs, telemetry conventions, and tests. The plan respects the existing Clean Architecture layering, keyed DI patterns, MediatR pipeline behavior ordering, Result<T> error handling, and IOptionsMonitor<T> config patterns already established in the harness.

## Baseline Inventory (What Exists Today)

| Component | Status | File |
|-----------|--------|------|
| `IToolPermissionService` | Interface only, returns `bool` | `Application.AI.Common/Interfaces/Agent/IToolPermissionService.cs` |
| `ToolPermissionBehavior` | Wired, calls `IsToolAllowedAsync` | `Application.AI.Common/MediatRBehaviors/ToolPermissionBehavior.cs` |
| `IContextBudgetTracker` | Implemented, per-agent token tracking | `Application.AI.Common/Interfaces/Context/IContextBudgetTracker.cs` |
| `ContextBudgetTracker` | ConcurrentDictionary-based, 80% warning | `Application.AI.Common/Services/Context/ContextBudgetTracker.cs` |
| `ITieredContextAssembler` | Implemented, 3-tier skill loading | `Application.AI.Common/Interfaces/Context/ITieredContextAssembler.cs` |
| `ITool` | Interface with Name, Description, SupportedOperations, ExecuteAsync | `Application.AI.Common/Interfaces/Tools/ITool.cs` |
| `ToolResult` | Domain record with Ok/Fail factories | `Domain.AI/Models/ToolResult.cs` |
| `ExecuteAgentTurnCommand` | Single-turn agent execution | `Application.Core/CQRS/Agents/ExecuteAgentTurn/` |
| `RunConversationCommand` | Multi-turn sequential conversation | `Application.Core/CQRS/Agents/RunConversation/` |
| `RunOrchestratedTaskCommand` | Multi-agent orchestration | `Application.Core/CQRS/Agents/RunOrchestratedTask/` |
| `AgentFactory` | Full agent creation pipeline with middleware | `Application.AI.Common/Factories/AgentFactory.cs` |
| Hook/Event system | **Nothing** | -- |
| System prompt composer | **Static assembly in AgentFactory** | -- |
| Compaction | **Nothing** | -- |
| Tool result storage | **Nothing** | -- |
| Streaming tool execution | **Sequential only** | -- |
| Subagent tool scoping | **No tool filtering per subagent** | -- |
| Test projects | **None** | -- |

## Conventions This Plan Follows

- **Namespace pattern**: `{Layer}.{Sublayer}.{Concern}` (e.g., `Domain.AI.Compaction`, `Application.AI.Common.Interfaces.Hooks`)
- **Result<T>**: Used for all command return types. `Result<T>.Success(value)`, `Result<T>.Fail(message)`. `ResultFailureType` enum for categorization.
- **Pipeline behavior ordering**: Outermost to innermost: UnhandledException -> AgentContext -> Audit -> ContentSafety -> ToolPermission -> Validation -> (new behaviors here) -> Handler
- **Config pattern**: POCOs in `Domain.Common/Config/AI/`, bound via `IOptionsMonitor<AppConfig>`, accessed as `appConfig.AI.{Section}`
- **Telemetry**: Constants in `Domain.AI/Telemetry/Conventions/`, metrics in `Application.AI.Common/OpenTelemetry/Metrics/`
- **DI**: Each layer has `DependencyInjection.cs` with `Add{Layer}Dependencies()` extension
- **Immutability**: Records for DTOs/models, `IReadOnlyList<T>` for public surfaces, `init`-only properties
- **XML docs**: Full documentation on all public types and members

## Root Path Reference

All paths below are relative to:
`C:\Users\kruz7\OneDrive\Documents\Code Repos\MCKRUZ\microsoft-agentic-harness\src\Content\`

---

## Phase 0: Foundation (Test Infrastructure + Config Expansion)

Before implementing any pattern, we need test projects and the config POCOs that all patterns share.

### Step 0.1 -- Create Test Projects

Create xUnit test projects for each layer that will receive new code:

**New files:**
- `Tests/Domain.AI.Tests/Domain.AI.Tests.csproj`
- `Tests/Application.AI.Common.Tests/Application.AI.Common.Tests.csproj`
- `Tests/Application.Core.Tests/Application.Core.Tests.csproj`
- `Tests/Infrastructure.AI.Tests/Infrastructure.AI.Tests.csproj`

**Modified files:**
- `../AgenticHarness.slnx` -- add Test projects to a `/Tests/` solution folder
- `../Directory.Packages.props` -- add xUnit, Moq, coverlet, FluentAssertions package versions

Each test project references `xunit`, `xunit.runner.visualstudio`, `coverlet.collector`, `Moq`, `FluentAssertions`, and the project under test.

**Dependencies:** None
**Risk:** Low
**Commit:** `chore: add xUnit test projects and testing infrastructure`

### Step 0.2 -- Config POCOs for All 10 Patterns

Add configuration sections under `AppConfig.AI` for the new pattern systems.

**New files:**
- `Domain/Domain.Common/Config/AI/ContextManagement/ContextManagementConfig.cs` -- root section for compaction + budget + result storage
- `Domain/Domain.Common/Config/AI/ContextManagement/CompactionConfig.cs` -- auto-compact trigger threshold, circuit breaker max failures, micro-compact staleness, full compact preserved segment limits
- `Domain/Domain.Common/Config/AI/ContextManagement/ToolResultStorageConfig.cs` -- per-result char limit (50K default), aggregate per-message limit (200K), preview size (2000 chars), storage path
- `Domain/Domain.Common/Config/AI/ContextManagement/BudgetConfig.cs` -- diminishing returns continuation threshold, min delta, max continuation count, 90% completion threshold
- `Domain/Domain.Common/Config/AI/Permissions/PermissionsConfig.cs` -- safety gate paths, default deny patterns, max subcommand limit for pattern matching, denial rate limit threshold
- `Domain/Domain.Common/Config/AI/Hooks/HooksConfig.cs` -- default timeout, max parallel hooks, enabled flag
- `Domain/Domain.Common/Config/AI/Orchestration/SubagentConfig.cs` -- max concurrent subagents, default max turns per subagent, mailbox storage path
- `Domain/Domain.Common/Config/AI/Orchestration/StreamingExecutionConfig.cs` -- parallel batch size, progress callback interval

**Modified files:**
- `Domain/Domain.Common/Config/AI/AIConfig.cs` -- add `ContextManagement`, `Permissions`, `Hooks`, `Orchestration` properties

**Dependencies:** None
**Risk:** Low
**Commit:** `feat: add config POCOs for all 10 Claude Code patterns`

---

## Phase 1: Critical Path (Patterns 1-4)

These four patterns are load-bearing for the agent loop. They depend on each other:
- Compaction needs budget tracking (exists) + system prompt awareness (Pattern 4)
- Permissions system is standalone
- Hook system is standalone but will be used by compaction and tool execution
- System prompt composer is standalone but consumed by compaction

**Implementation order:** Permissions (standalone, simplest) -> Hooks (standalone) -> System Prompt Composer (standalone) -> Context Compaction (depends on budget + composer)

---

### Pattern 2: Tool Permission System (3-Phase Resolution)

#### Step 1.1 -- Domain Models

**New files:**
- `Domain/Domain.AI/Permissions/PermissionBehavior.cs`
  ```csharp
  public enum PermissionBehavior { Allow, Deny, Ask }
  ```
- `Domain/Domain.AI/Permissions/PermissionRuleSource.cs`
  ```csharp
  public enum PermissionRuleSource { AgentManifest, SkillDefinition, UserSettings, ProjectSettings, LocalSettings, SessionOverride, PolicySettings, CliArgument }
  ```
- `Domain/Domain.AI/Permissions/ToolPermissionRule.cs` -- record with `ToolPattern` (string), `OperationPattern` (string?), `Behavior` (PermissionBehavior), `Source` (PermissionRuleSource), `Priority` (int), `IsBypassImmune` (bool)
- `Domain/Domain.AI/Permissions/PermissionDecision.cs` -- record with `Behavior` (PermissionBehavior), `Reason` (string), `MatchedRule` (ToolPermissionRule?), `Source` (PermissionRuleSource?)
- `Domain/Domain.AI/Permissions/SafetyGate.cs` -- record with `PathPattern` (string), `Description` (string), `IsBypassImmune` (bool = true). For paths like `.git/`, `.claude/`, shell configs that always require approval.

**Dependencies:** None
**Risk:** Low

#### Step 1.2 -- Application Interfaces

**Modified files:**
- `Application/Application.AI.Common/Interfaces/Agent/IToolPermissionService.cs` -- **Replace** the existing simple `bool` interface with a richer one:

  New signature:
  ```csharp
  public interface IToolPermissionService
  {
      ValueTask<PermissionDecision> ResolvePermissionAsync(
          string agentId, string toolName, string? operation = null,
          IReadOnlyDictionary<string, object?>? parameters = null,
          CancellationToken cancellationToken = default);
  
      ValueTask<bool> IsToolAllowedAsync(string agentId, string toolName,
          CancellationToken cancellationToken); // backward compat
  }
  ```

**New files:**
- `Application/Application.AI.Common/Interfaces/Permissions/IPermissionRuleProvider.cs` -- loads rules from a specific source (manifest, config, session). Returns `IReadOnlyList<ToolPermissionRule>`.
- `Application/Application.AI.Common/Interfaces/Permissions/ISafetyGateRegistry.cs` -- registers and checks safety gates. `bool IsSafetyGated(string toolName, IReadOnlyDictionary<string, object?>? parameters)`.
- `Application/Application.AI.Common/Interfaces/Permissions/IPatternMatcher.cs` -- matches tool names/operations against rules. Supports exact, prefix (`git:*`), and wildcard patterns.

**Modified files:**
- `Application/Application.AI.Common/MediatRBehaviors/ToolPermissionBehavior.cs` -- update to use `ResolvePermissionAsync`, handle `Ask` decision (return a new `ResultFailureType.PermissionRequired`)

**Modified files (Domain):**
- `Domain/Domain.Common/Result.cs` -- add `PermissionRequired` to `ResultFailureType` enum and add `static Result<T> PermissionRequired(string reason)` factory

**Dependencies:** Step 1.1
**Risk:** Medium -- changes existing interface signature. Must update all existing consumers (currently only `ToolPermissionBehavior`).

#### Step 1.3 -- Infrastructure Implementation

**New files:**
- `Infrastructure/Infrastructure.AI/Permissions/ThreePhasePermissionResolver.cs` -- implements `IToolPermissionService`. Three-phase algorithm:
  1. Check deny rules -> safety gates -> if match, return Deny
  2. Check ask rules -> if match, return Ask
  3. Check allow rules -> bypass mode -> return Allow or default Ask
  First-match-wins across source priority order.
- `Infrastructure/Infrastructure.AI/Permissions/ConfigBasedRuleProvider.cs` -- implements `IPermissionRuleProvider`, loads rules from `AppConfig.AI.Permissions`
- `Infrastructure/Infrastructure.AI/Permissions/ManifestRuleProvider.cs` -- implements `IPermissionRuleProvider`, extracts rules from `AgentManifest.AllowedTools` and `SkillDefinition.AllowedTools`
- `Infrastructure/Infrastructure.AI/Permissions/GlobPatternMatcher.cs` -- implements `IPatternMatcher` with exact, prefix, and glob matching. Max 50 subcommand limit for ReDoS prevention.
- `Infrastructure/Infrastructure.AI/Permissions/SafetyGateRegistry.cs` -- implements `ISafetyGateRegistry` with default gates for `.git/`, `.claude/`, shell config paths

**Modified files:**
- `Infrastructure/Infrastructure.AI/DependencyInjection.cs` -- register `IToolPermissionService` -> `ThreePhasePermissionResolver`, `IPatternMatcher` -> `GlobPatternMatcher`, `ISafetyGateRegistry` -> `SafetyGateRegistry`

**Dependencies:** Steps 1.1, 1.2
**Risk:** Medium

#### Step 1.4 -- Telemetry

**New files:**
- `Domain/Domain.AI/Telemetry/Conventions/PermissionConventions.cs` -- `agent.permission.decision`, `agent.permission.source`, `agent.permission.tool`, `agent.permission.denials`
- `Application/Application.AI.Common/OpenTelemetry/Metrics/PermissionMetrics.cs` -- counter for permission decisions by type, histogram for resolution latency

**Dependencies:** Step 1.3
**Risk:** Low

#### Step 1.5 -- Tests

**New files:**
- `Tests/Domain.AI.Tests/Permissions/PermissionDecisionTests.cs`
- `Tests/Application.AI.Common.Tests/Behaviors/ToolPermissionBehaviorTests.cs`
- `Tests/Infrastructure.AI.Tests/Permissions/ThreePhasePermissionResolverTests.cs`
- `Tests/Infrastructure.AI.Tests/Permissions/GlobPatternMatcherTests.cs`
- `Tests/Infrastructure.AI.Tests/Permissions/SafetyGateRegistryTests.cs`

Test cases: deny-before-ask-before-allow ordering, safety gate bypass immunity, pattern matching (exact, prefix, glob), ReDoS prevention with max subcommand, backward compat `IsToolAllowedAsync`, rule source priority.

**Dependencies:** Steps 1.1-1.3
**Risk:** Low
**Commit:** `feat: 3-phase tool permission system with safety gates and pattern matching`

---

### Pattern 3: Hook/Event System

#### Step 2.1 -- Domain Models

**New files:**
- `Domain/Domain.AI/Hooks/HookEvent.cs`
  ```csharp
  public enum HookEvent
  {
      // Tool lifecycle
      PreToolUse, PostToolUse,
      // Session lifecycle
      SessionStart, SessionEnd,
      // Compaction lifecycle
      PreCompact, PostCompact,
      // Agent lifecycle
      SubagentStart, SubagentStop,
      // Context changes
      SkillLoaded, SkillUnloaded,
      ToolRegistered, ToolUnregistered,
      // Conversation
      PreTurn, PostTurn,
      // System
      BudgetWarning, BudgetExceeded
  }
  ```
- `Domain/Domain.AI/Hooks/HookType.cs` -- `enum HookType { Command, Prompt, Middleware, Http }`
- `Domain/Domain.AI/Hooks/HookDefinition.cs` -- record with `Id` (string), `Event` (HookEvent), `Type` (HookType), `Matcher` (string? -- tool name glob for tool events), `TimeoutMs` (int, default 5000), `RunOnce` (bool), `Priority` (int), `CommandLine` (string? -- for Command type), `PromptTemplate` (string? -- for Prompt type), `MiddlewareType` (Type? -- for Middleware type), `WebhookUrl` (string? -- for Http type)
- `Domain/Domain.AI/Hooks/HookResult.cs` -- record with `Continue` (bool, default true), `SuppressOutput` (bool), `ModifiedInput` (IReadOnlyDictionary<string, object?>?), `ModifiedOutput` (string?), `AdditionalContext` (string?), `Decision` (PermissionBehavior? -- for PreToolUse hooks that want to approve/block)
- `Domain/Domain.AI/Hooks/HookExecutionContext.cs` -- record with `Event` (HookEvent), `AgentId` (string?), `ToolName` (string?), `ToolParameters` (IReadOnlyDictionary<string, object?>?), `ToolResult` (string?), `TurnNumber` (int?), `ConversationId` (string?)

**Dependencies:** None
**Risk:** Low

#### Step 2.2 -- Application Interfaces

**New files:**
- `Application/Application.AI.Common/Interfaces/Hooks/IHookRegistry.cs` -- `Register(HookDefinition)`, `Unregister(string hookId)`, `GetHooksForEvent(HookEvent, string? toolName)` returning `IReadOnlyList<HookDefinition>`
- `Application/Application.AI.Common/Interfaces/Hooks/IHookExecutor.cs` -- `Task<IReadOnlyList<HookResult>> ExecuteHooksAsync(HookEvent, HookExecutionContext, CancellationToken)`. Executes matching hooks in parallel with timeout. Aggregates results.
- `Application/Application.AI.Common/MediatRBehaviors/HookBehavior.cs` -- new pipeline behavior. For `IToolRequest`: fires `PreToolUse` before handler, `PostToolUse` after. For `IAgentScopedRequest`: fires `PreTurn`/`PostTurn`. Checks `HookResult.Continue` to short-circuit. Applies `ModifiedInput` if present.

Pipeline position: between ToolPermission (5) and Validation (6) -- hooks can modify inputs before validation.

**Modified files:**
- `Application/Application.AI.Common/DependencyInjection.cs` -- register `HookBehavior` in the pipeline

**Dependencies:** Step 2.1
**Risk:** Medium -- new pipeline behavior insertion requires careful ordering

#### Step 2.3 -- Infrastructure Implementation

**New files:**
- `Infrastructure/Infrastructure.AI/Hooks/InMemoryHookRegistry.cs` -- implements `IHookRegistry`. ConcurrentDictionary keyed by `HookEvent`. Glob matching on tool name for tool lifecycle events. Removes `RunOnce` hooks after first execution.
- `Infrastructure/Infrastructure.AI/Hooks/CompositeHookExecutor.cs` -- implements `IHookExecutor`. Dispatches to type-specific executors. Parallel execution with `Task.WhenAll` + per-hook timeout via `CancellationTokenSource.CreateLinkedTokenSource`. Error isolation (one hook failure doesn't block others). Telemetry span per hook execution.
- `Infrastructure/Infrastructure.AI/Hooks/Executors/PromptHookExecutor.cs` -- for `HookType.Prompt`: evaluates template against context, returns `AdditionalContext`
- `Infrastructure/Infrastructure.AI/Hooks/Executors/MiddlewareHookExecutor.cs` -- for `HookType.Middleware`: resolves `Type` from DI, invokes
- `Infrastructure/Infrastructure.AI/Hooks/Executors/HttpHookExecutor.cs` -- for `HookType.Http`: POSTs JSON to webhook URL, parses `HookResult` from response

Note: `HookType.Command` (shell execution) is deferred -- too much security surface for a POC. Register a no-op executor that logs a warning.

**Modified files:**
- `Infrastructure/Infrastructure.AI/DependencyInjection.cs` -- register `IHookRegistry` -> `InMemoryHookRegistry` (singleton), `IHookExecutor` -> `CompositeHookExecutor` (transient)

**Dependencies:** Steps 2.1, 2.2
**Risk:** Medium -- parallel hook execution with timeouts needs careful cancellation handling

#### Step 2.4 -- Telemetry

**New files:**
- `Domain/Domain.AI/Telemetry/Conventions/HookConventions.cs` -- `agent.hook.event`, `agent.hook.type`, `agent.hook.id`, `agent.hook.duration`, `agent.hook.result`
- `Application/Application.AI.Common/OpenTelemetry/Metrics/HookMetrics.cs` -- counter for hooks fired by event type, histogram for hook execution duration, counter for hook failures

**Dependencies:** Step 2.3
**Risk:** Low

#### Step 2.5 -- Tests

**New files:**
- `Tests/Domain.AI.Tests/Hooks/HookDefinitionTests.cs`
- `Tests/Application.AI.Common.Tests/Behaviors/HookBehaviorTests.cs`
- `Tests/Infrastructure.AI.Tests/Hooks/InMemoryHookRegistryTests.cs`
- `Tests/Infrastructure.AI.Tests/Hooks/CompositeHookExecutorTests.cs`

Test cases: hook registration/unregister, event matching with glob, RunOnce removal, parallel execution with timeout, one-hook-failure isolation, PreToolUse Continue=false short-circuits, ModifiedInput flows to handler.

**Dependencies:** Steps 2.1-2.3
**Risk:** Low
**Commit:** `feat: hook/event system with pre/post tool interception and parallel execution`

---

### Pattern 4: System Prompt Composer

#### Step 3.1 -- Domain Models

**New files:**
- `Domain/Domain.AI/Prompts/SystemPromptSectionType.cs`
  ```csharp
  public enum SystemPromptSectionType
  {
      AgentIdentity, SkillInstructions, ToolSchemas, PermissionRules,
      GitContext, UserContext, SessionState, ActiveHooks, CustomSection
  }
  ```
- `Domain/Domain.AI/Prompts/SystemPromptSection.cs` -- record with `Name` (string), `Type` (SystemPromptSectionType), `Priority` (int -- lower = earlier in prompt), `IsCacheable` (bool, default true), `EstimatedTokens` (int), `Content` (string)

**Dependencies:** None
**Risk:** Low

#### Step 3.2 -- Application Interfaces

**New files:**
- `Application/Application.AI.Common/Interfaces/Prompts/ISystemPromptComposer.cs`
  ```csharp
  public interface ISystemPromptComposer
  {
      Task<string> ComposeAsync(string agentId, int tokenBudget, CancellationToken ct = default);
      void InvalidateSection(SystemPromptSectionType type);
      void InvalidateAll();
  }
  ```
- `Application/Application.AI.Common/Interfaces/Prompts/IPromptSectionProvider.cs` -- registered per section type. `Task<SystemPromptSection> GetSectionAsync(string agentId, CancellationToken ct)`. Multiple implementations via keyed DI.
- `Application/Application.AI.Common/Interfaces/Prompts/IPromptSectionCache.cs` -- `TryGet(string agentId, SystemPromptSectionType, out SystemPromptSection?)`, `Set(string agentId, SystemPromptSection)`, `Invalidate(SystemPromptSectionType)`, `InvalidateAll()`

**Dependencies:** Step 3.1
**Risk:** Low

#### Step 3.3 -- Infrastructure Implementation

**New files:**
- `Infrastructure/Infrastructure.AI/Prompts/MemoizedPromptComposer.cs` -- implements `ISystemPromptComposer`. Resolves all registered `IPromptSectionProvider` via keyed DI. Checks cache for each cacheable section. Computes missing sections via `Task.WhenAll`. Sorts by priority. Concatenates with budget awareness (drops lowest-priority sections if over budget). Records allocations to `IContextBudgetTracker`.
- `Infrastructure/Infrastructure.AI/Prompts/InMemoryPromptSectionCache.cs` -- implements `IPromptSectionCache`. ConcurrentDictionary keyed by `(agentId, sectionType)`. Invalidation clears matching entries.
- `Infrastructure/Infrastructure.AI/Prompts/Sections/AgentIdentitySectionProvider.cs` -- provides agent name, description, role instructions
- `Infrastructure/Infrastructure.AI/Prompts/Sections/SkillInstructionsSectionProvider.cs` -- delegates to `ITieredContextAssembler` for skill context
- `Infrastructure/Infrastructure.AI/Prompts/Sections/ToolSchemasSectionProvider.cs` -- lists available tool names and descriptions (not full schemas -- those go to `ChatOptions.Tools`)
- `Infrastructure/Infrastructure.AI/Prompts/Sections/PermissionRulesSectionProvider.cs` -- injects active permission rules as natural language constraints
- `Infrastructure/Infrastructure.AI/Prompts/Sections/SessionStateSectionProvider.cs` -- injects conversation turn count, budget remaining, active skills

**Modified files:**
- `Infrastructure/Infrastructure.AI/DependencyInjection.cs` -- register composer, cache, section providers with keyed DI by `SystemPromptSectionType`
- `Application/Application.AI.Common/Factories/AgentFactory.cs` -- replace static prompt assembly with `ISystemPromptComposer.ComposeAsync()` call in `CreateAgentFromSkillAsync`

**Dependencies:** Steps 3.1, 3.2, existing `ITieredContextAssembler`, `IContextBudgetTracker`
**Risk:** Medium -- modifying `AgentFactory` prompt assembly is central to the system

#### Step 3.4 -- Tests

**New files:**
- `Tests/Infrastructure.AI.Tests/Prompts/MemoizedPromptComposerTests.cs` -- cache hit/miss, budget truncation, section ordering, invalidation
- `Tests/Infrastructure.AI.Tests/Prompts/InMemoryPromptSectionCacheTests.cs`
- `Tests/Infrastructure.AI.Tests/Prompts/Sections/AgentIdentitySectionProviderTests.cs`

Test cases: memoization (same section not recomputed), invalidation clears cache, budget overflow drops low-priority sections, parallel section computation, section ordering by priority.

**Dependencies:** Steps 3.1-3.3
**Risk:** Low
**Commit:** `feat: memoized system prompt composer with section-based assembly and cache invalidation`

---

### Pattern 1: Context Compaction System

#### Step 4.1 -- Domain Models

**New files:**
- `Domain/Domain.AI/Compaction/CompactionStrategy.cs` -- `enum CompactionStrategy { Full, Partial, Micro }`
- `Domain/Domain.AI/Compaction/CompactionTrigger.cs` -- `enum CompactionTrigger { AutoBudget, Manual, CircuitBreaker }`
- `Domain/Domain.AI/Compaction/CompactionBoundaryMessage.cs` -- record with `Id` (string), `Trigger` (CompactionTrigger), `Strategy` (CompactionStrategy), `PreCompactionTokens` (int), `PostCompactionTokens` (int), `TokensSaved` (int), `PreservedSegmentIds` (IReadOnlyList<string>), `Timestamp` (DateTimeOffset), `Summary` (string)
- `Domain/Domain.AI/Compaction/CompactionResult.cs` -- record with `Success` (bool), `Boundary` (CompactionBoundaryMessage?), `Error` (string?), `NewMessageHistory` (IReadOnlyList<ChatMessage>)
- `Domain/Domain.AI/Compaction/MicroCompactTarget.cs` -- `enum MicroCompactTarget { FileRead, ShellOutput, GrepResult, GlobResult, WebFetch, LargeToolResult }`

**Dependencies:** None
**Risk:** Low

#### Step 4.2 -- Application Interfaces

**New files:**
- `Application/Application.AI.Common/Interfaces/Compaction/IContextCompactionService.cs`
  ```csharp
  public interface IContextCompactionService
  {
      Task<CompactionResult> CompactAsync(
          string agentId,
          IReadOnlyList<ChatMessage> messages,
          CompactionStrategy strategy,
          CancellationToken ct = default);
      
      bool ShouldAutoCompact(string agentId, int currentTokens, int maxTokens);
  }
  ```
- `Application/Application.AI.Common/Interfaces/Compaction/ICompactionStrategy.cs` -- strategy pattern interface. `Task<CompactionResult> ExecuteAsync(string agentId, IReadOnlyList<ChatMessage> messages, CompactionOptions options, CancellationToken ct)`. Implemented per strategy.
- `Application/Application.AI.Common/Interfaces/Compaction/IAutoCompactStateMachine.cs` -- tracks compaction state per agent. `CompactionState GetState(string agentId)`, `void RecordSuccess(string agentId)`, `void RecordFailure(string agentId)`, `bool IsCircuitBroken(string agentId)`. Circuit breaker after 3 consecutive failures.

**New files (MediatR):**
- `Application/Application.AI.Common/MediatRBehaviors/AutoCompactBehavior.cs` -- pipeline behavior that wraps `ExecuteAgentTurnCommand` responses. After the handler returns, checks `ShouldAutoCompact()`. If yes, fires `PreCompact` hook, compacts, fires `PostCompact` hook, updates conversation history on the result. **Only applies to `IAgentScopedRequest` types.**

Pipeline position: outermost agent-specific behavior (before UnhandledException) so it can modify the returned history after the full pipeline completes.

**Modified files:**
- `Application/Application.AI.Common/DependencyInjection.cs` -- register `AutoCompactBehavior` as first in the agent pipeline

**Dependencies:** Steps 4.1, Pattern 3 (composer invalidation on compact), Pattern 3 hooks (PreCompact/PostCompact)
**Risk:** High -- modifying conversation history post-handler is architecturally significant

#### Step 4.3 -- Infrastructure Implementation

**New files:**
- `Infrastructure/Infrastructure.AI/Compaction/ContextCompactionService.cs` -- implements `IContextCompactionService`. Selects strategy based on token usage: micro if < 70% used and stale results detected, partial if specific segment is large, full otherwise. Delegates to strategy implementations.
- `Infrastructure/Infrastructure.AI/Compaction/Strategies/FullCompactionStrategy.cs` -- sends entire message history to LLM for summarization. Preserves plan files (5 max, 5K tokens each), invoked skill instructions (25K budget). Creates `CompactionBoundaryMessage`. Invalidates prompt section cache.
- `Infrastructure/Infrastructure.AI/Compaction/Strategies/PartialCompactionStrategy.cs` -- two modes: `up_to` (compact before pivot message) and `from` (compact after pivot). Enables surgical compaction of just old or just recent content.
- `Infrastructure/Infrastructure.AI/Compaction/Strategies/MicroCompactionStrategy.cs` -- lightweight, no LLM call. Targets specific tool result types by examining `ToolResult.Output` patterns. Replaces stale file reads with "[file previously read: {path}]", large outputs with truncated previews. Uses `MicroCompactTarget` enum for classification.
- `Infrastructure/Infrastructure.AI/Compaction/AutoCompactStateMachine.cs` -- implements `IAutoCompactStateMachine`. ConcurrentDictionary per agent. Tracks consecutive failure count. Circuit breaker at 3 failures (configurable via `CompactionConfig.CircuitBreakerMaxFailures`). Auto-reset after configurable cooldown.

**Modified files:**
- `Infrastructure/Infrastructure.AI/DependencyInjection.cs` -- register compaction service, strategies (keyed by `CompactionStrategy` enum), state machine
- `Application/Application.AI.Common/Interfaces/Prompts/IPromptSectionCache.cs` -- ensure `InvalidateAll()` is called by compaction

**Dependencies:** Steps 4.1, 4.2, `IChatClientFactory` (for full compaction LLM call), `IHookExecutor` (for Pre/PostCompact), `ISystemPromptComposer` (invalidation)
**Risk:** High -- compaction directly modifies conversation state

#### Step 4.4 -- Telemetry

**Modified files:**
- `Domain/Domain.AI/Telemetry/Conventions/ContextConventions.cs` -- add `CompactionStrategy`, `CompactionTokensSaved`, `CompactionDuration`, `CompactionCircuitBroken`
- `Application/Application.AI.Common/OpenTelemetry/Metrics/ContextBudgetMetrics.cs` -- add compaction counters and histograms

**Dependencies:** Step 4.3
**Risk:** Low

#### Step 4.5 -- Tests

**New files:**
- `Tests/Domain.AI.Tests/Compaction/CompactionBoundaryMessageTests.cs`
- `Tests/Infrastructure.AI.Tests/Compaction/ContextCompactionServiceTests.cs` -- strategy selection logic
- `Tests/Infrastructure.AI.Tests/Compaction/Strategies/FullCompactionStrategyTests.cs` -- mock LLM, verify boundary creation
- `Tests/Infrastructure.AI.Tests/Compaction/Strategies/MicroCompactionStrategyTests.cs` -- stale result replacement
- `Tests/Infrastructure.AI.Tests/Compaction/AutoCompactStateMachineTests.cs` -- circuit breaker at 3 failures, cooldown reset
- `Tests/Application.AI.Common.Tests/Behaviors/AutoCompactBehaviorTests.cs` -- behavior fires when threshold exceeded, skips when circuit broken

**Dependencies:** Steps 4.1-4.3
**Risk:** Low
**Commit:** `feat: context compaction system with full/partial/micro strategies and auto-compact state machine`

---

## Phase 2: Orchestration (Patterns 5-7)

These patterns depend on the Phase 1 foundation:
- Subagent orchestration needs permissions (tool scoping) and hooks (subagent lifecycle)
- Tool result storage needs budget tracker enhancements
- Streaming execution needs hook system (progress) and permissions (safety classification)

---

### Pattern 6: Tool Result Storage & Budget Management

#### Step 5.1 -- Domain Models

**New files:**
- `Domain/Domain.AI/Context/ToolResultReference.cs` -- record with `ResultId` (string), `ToolName` (string), `Operation` (string?), `PreviewContent` (string), `FullContentPath` (string?), `SizeChars` (int), `SizeTokens` (int), `IsPersistedToDisk` (bool), `Timestamp` (DateTimeOffset)
- `Domain/Domain.AI/Context/TokenBudgetDecision.cs` -- `enum TokenBudgetDecision { Continue, Stop, Nudge }` with companion record `BudgetAssessment` containing `Decision`, `Reason` (string), `ContinuationCount` (int), `CompletionPercentage` (double)

**Dependencies:** None
**Risk:** Low

#### Step 5.2 -- Application Interfaces

**New files:**
- `Application/Application.AI.Common/Interfaces/Context/IToolResultStore.cs`
  ```csharp
  public interface IToolResultStore
  {
      Task<ToolResultReference> StoreIfLargeAsync(
          string sessionId, string toolName, string? operation,
          string fullOutput, CancellationToken ct = default);
      
      Task<string> RetrieveFullContentAsync(string resultId, CancellationToken ct = default);
  }
  ```

**Modified files:**
- `Application/Application.AI.Common/Interfaces/Context/IContextBudgetTracker.cs` -- add new methods:
  ```csharp
  BudgetAssessment AssessContinuation(string agentName, int totalBudget);
  void RecordContinuation(string agentName, int tokensProduced);
  ```
  Keep all existing methods for backward compatibility.

- `Application/Application.AI.Common/Services/Context/ContextBudgetTracker.cs` -- implement the new methods. Track `continuationCount` and `lastTwoDeltas` per agent. Diminishing returns: if `continuationCount >= 3` and last two deltas < 500 tokens, return `Stop`.

**Dependencies:** Step 5.1
**Risk:** Medium -- modifying existing interface and implementation

#### Step 5.3 -- Infrastructure Implementation

**New files:**
- `Infrastructure/Infrastructure.AI/Context/FileSystemToolResultStore.cs` -- implements `IToolResultStore`. Persists to `{configuredPath}/{sessionId}/tool-results/{resultId}.json`. If output exceeds `ToolResultStorageConfig.PerResultCharLimit` (default 50K), writes full content to disk, returns reference with preview (first `PreviewSizeChars`, default 2000). Otherwise returns reference with full content inline (no disk write). Wraps output in `<persisted-output ref="{resultId}">` XML when persisted.

**Modified files:**
- `Infrastructure/Infrastructure.AI/DependencyInjection.cs` -- register `IToolResultStore` -> `FileSystemToolResultStore`
- `Application/Application.AI.Common/Services/Tools/AIToolConverter.cs` -- after tool execution, pass result through `IToolResultStore.StoreIfLargeAsync` before returning to LLM

**Dependencies:** Steps 5.1, 5.2
**Risk:** Medium

#### Step 5.4 -- Tests

**New files:**
- `Tests/Application.AI.Common.Tests/Services/ContextBudgetTrackerTests.cs` -- diminishing returns detection, continuation counting
- `Tests/Infrastructure.AI.Tests/Context/FileSystemToolResultStoreTests.cs` -- small result passthrough, large result persistence, retrieval

**Dependencies:** Steps 5.1-5.3
**Risk:** Low
**Commit:** `feat: tool result storage with disk persistence and diminishing returns budget detection`

---

### Pattern 7: Streaming Tool Execution

#### Step 6.1 -- Application Interfaces and Domain Models

**New files:**
- `Domain/Domain.AI/Tools/ToolConcurrencyClassification.cs` -- `enum ToolConcurrencyClassification { ReadOnly, WriteSerial, Unknown }`
- `Application/Application.AI.Common/Interfaces/Tools/IToolExecutionStrategy.cs`
  ```csharp
  public interface IToolExecutionStrategy
  {
      Task<IReadOnlyList<ToolResult>> ExecuteBatchAsync(
          IReadOnlyList<ToolExecutionRequest> requests,
          IProgress<ToolExecutionProgress>? progress = null,
          CancellationToken ct = default);
  }
  ```
- `Application/Application.AI.Common/Interfaces/Tools/IToolConcurrencyClassifier.cs` -- `ToolConcurrencyClassification Classify(ITool tool)`
- `Application/Application.AI.Common/Models/Tools/ToolExecutionRequest.cs` -- record with `Tool` (ITool), `Operation` (string), `Parameters`, `CallId` (string)
- `Application/Application.AI.Common/Models/Tools/ToolExecutionProgress.cs` -- record with `CallId`, `Status` (string), `PercentComplete` (double?)

**Modified files:**
- `Application/Application.AI.Common/Interfaces/Tools/ITool.cs` -- add optional properties:
  ```csharp
  bool IsReadOnly => false;
  bool IsConcurrencySafe => false;
  ```
  Default interface implementations preserve backward compatibility.

**Dependencies:** None
**Risk:** Medium -- modifying `ITool` interface (mitigated by default implementations)

#### Step 6.2 -- Infrastructure Implementation

**New files:**
- `Infrastructure/Infrastructure.AI/Tools/BatchedToolExecutionStrategy.cs` -- implements `IToolExecutionStrategy`. Partitions calls via `IToolConcurrencyClassifier`: read-only tools run in parallel via `Task.WhenAll`, write tools run serially. Reports progress via `IProgress<T>`. Error classification per tool for telemetry.
- `Infrastructure/Infrastructure.AI/Tools/ToolConcurrencyClassifier.cs` -- implements `IToolConcurrencyClassifier`. Reads `ITool.IsReadOnly` and `ITool.IsConcurrencySafe`. Fail-closed: unknown tools default to `WriteSerial`.
- `Infrastructure/Infrastructure.AI/Tools/ToolErrorClassifier.cs` -- static utility. Classifies exceptions into telemetry-safe strings: `timeout`, `permission_denied`, `not_found`, `invalid_input`, `internal_error`.

**Modified files:**
- `Infrastructure/Infrastructure.AI/DependencyInjection.cs` -- register `IToolExecutionStrategy` -> `BatchedToolExecutionStrategy`, `IToolConcurrencyClassifier` -> `ToolConcurrencyClassifier`
- `Infrastructure/Infrastructure.AI/Tools/FileSystemTool.cs` -- set `IsReadOnly` based on operation (read = true, write = false)

**Dependencies:** Step 6.1
**Risk:** Medium

#### Step 6.3 -- Tests

**New files:**
- `Tests/Infrastructure.AI.Tests/Tools/BatchedToolExecutionStrategyTests.cs` -- read-only parallel, write serial, mixed batch ordering, error isolation
- `Tests/Infrastructure.AI.Tests/Tools/ToolConcurrencyClassifierTests.cs` -- fail-closed default, read-only detection

**Dependencies:** Steps 6.1-6.2
**Risk:** Low
**Commit:** `feat: batched tool execution with parallel reads and serial writes`

---

### Pattern 5: Subagent Orchestration

#### Step 7.1 -- Domain Models

**New files:**
- `Domain/Domain.AI/Agents/SubagentDefinition.cs` -- record with `AgentType` (SubagentType enum), `ToolAllowlist` (IReadOnlyList<string>?), `ToolDenylist` (IReadOnlyList<string>?), `PermissionMode` (PermissionBehavior, default Ask), `MaxTurns` (int), `ModelOverride` (string?), `SystemPromptOverride` (string?), `InheritParentTools` (bool, default true)
- `Domain/Domain.AI/Agents/SubagentType.cs` -- `enum SubagentType { Explore, Plan, Verify, Execute, General }`
- `Domain/Domain.AI/Agents/AgentMessage.cs` -- record with `FromAgentId` (string), `ToAgentId` (string), `Content` (string), `MessageType` (AgentMessageType enum: Task, Result, Notification, Error), `Timestamp` (DateTimeOffset), `CorrelationId` (string)

**Dependencies:** Pattern 2 (PermissionBehavior enum)
**Risk:** Low

#### Step 7.2 -- Application Interfaces

**New files:**
- `Application/Application.AI.Common/Interfaces/Agents/ISubagentToolResolver.cs`
  ```csharp
  public interface ISubagentToolResolver
  {
      IReadOnlyList<AITool> ResolveToolsForSubagent(
          SubagentDefinition definition,
          IReadOnlyList<AITool> parentTools);
  }
  ```
- `Application/Application.AI.Common/Interfaces/Agents/IAgentMailbox.cs`
  ```csharp
  public interface IAgentMailbox
  {
      Task SendAsync(AgentMessage message, CancellationToken ct = default);
      Task<IReadOnlyList<AgentMessage>> ReceiveAsync(string agentId, CancellationToken ct = default);
      Task<IReadOnlyList<AgentMessage>> WaitForResponseAsync(string agentId, string correlationId, TimeSpan timeout, CancellationToken ct = default);
  }
  ```
- `Application/Application.AI.Common/Interfaces/Agents/ISubagentProfileRegistry.cs` -- registers built-in profiles (Explore, Plan, Verify, Execute). `SubagentDefinition GetProfile(SubagentType type)`.

**Dependencies:** Step 7.1
**Risk:** Low

#### Step 7.3 -- Infrastructure Implementation

**New files:**
- `Infrastructure/Infrastructure.AI/Agents/SubagentToolResolver.cs` -- implements `ISubagentToolResolver`. Pipeline: start with parent tools -> apply allowlist filter -> apply denylist filter -> if not `InheritParentTools`, start from empty.
- `Infrastructure/Infrastructure.AI/Agents/InMemoryAgentMailbox.cs` -- implements `IAgentMailbox`. `ConcurrentDictionary<string, Channel<AgentMessage>>` per agent. `WaitForResponseAsync` uses `ChannelReader.ReadAsync` with timeout.
- `Infrastructure/Infrastructure.AI/Agents/BuiltInSubagentProfiles.cs` -- implements `ISubagentProfileRegistry`. Predefined profiles:
  - `Explore`: read-only tools only, max 10 turns
  - `Plan`: no tools, max 3 turns (planning only)
  - `Verify`: read-only tools, max 5 turns
  - `Execute`: all tools, max 15 turns

**Modified files:**
- `Application/Application.Core/CQRS/Agents/RunOrchestratedTask/RunOrchestratedTaskCommandHandler.cs` -- refactor to use `ISubagentToolResolver` for tool scoping, `IAgentMailbox` for message passing, `IHookExecutor` for SubagentStart/Stop events
- `Infrastructure/Infrastructure.AI/DependencyInjection.cs` -- register new services

**Dependencies:** Steps 7.1, 7.2, Pattern 2 (permissions for tool filtering), Pattern 3 (hooks for subagent lifecycle)
**Risk:** Medium -- refactoring existing orchestration handler

#### Step 7.4 -- Tests

**New files:**
- `Tests/Infrastructure.AI.Tests/Agents/SubagentToolResolverTests.cs` -- allowlist/denylist filtering, inherit mode
- `Tests/Infrastructure.AI.Tests/Agents/InMemoryAgentMailboxTests.cs` -- send/receive, wait with timeout, concurrent access
- `Tests/Infrastructure.AI.Tests/Agents/BuiltInSubagentProfilesTests.cs` -- profile lookup, default tool restrictions

**Dependencies:** Steps 7.1-7.3
**Risk:** Low
**Commit:** `feat: subagent orchestration with tool scoping, mailbox messaging, and built-in profiles`

---

## Phase 3: Polish (Patterns 8-10)

These are lower-priority patterns. Each is self-contained.

---

### Pattern 8: Prompt Cache Break Detection

#### Step 8.1 -- Implementation

**New files:**
- `Domain/Domain.AI/Prompts/PromptHashSnapshot.cs` -- record with `SystemHash` (string), `ToolsHash` (string), `PerToolHashes` (IReadOnlyDictionary<string, string>), `Timestamp` (DateTimeOffset)
- `Application/Application.AI.Common/Interfaces/Prompts/IPromptCacheTracker.cs` -- `PromptHashSnapshot TakeSnapshot(string systemPrompt, IReadOnlyList<AITool> tools)`, `PromptCacheBreakReport? Compare(PromptHashSnapshot previous, PromptHashSnapshot current)`
- `Domain/Domain.AI/Prompts/PromptCacheBreakReport.cs` -- record with `SystemChanged` (bool), `ToolsChanged` (bool), `ChangedToolNames` (IReadOnlyList<string>), `PreviousSnapshot`, `CurrentSnapshot`
- `Infrastructure/Infrastructure.AI/Prompts/Sha256PromptCacheTracker.cs` -- implements `IPromptCacheTracker`. SHA256 hashing of system prompt bytes and per-tool JSON schema serialization. Diff generation for debugging.

**Modified files:**
- `Infrastructure/Infrastructure.AI/DependencyInjection.cs` -- register tracker

**Dependencies:** Pattern 4 (composer)
**Risk:** Low
**Commit:** `feat: prompt cache break detection with per-tool hash tracking`

---

### Pattern 9: Config Discovery

#### Step 9.1 -- Implementation

**New files:**
- `Domain/Domain.AI/Config/ConfigDiscoveryRule.cs` -- record with `SourcePath` (string), `Priority` (int), `Scope` (ConfigScope enum: Managed, User, Project, Local), `PathGlobs` (IReadOnlyList<string>? -- frontmatter scoping)
- `Application/Application.AI.Common/Interfaces/Config/IConfigDiscoveryService.cs` -- `Task<IReadOnlyList<ConfigDiscoveryRule>> DiscoverRulesAsync(string cwd, CancellationToken ct)`, resolves files walking upward from CWD.
- `Infrastructure/Infrastructure.AI/Config/DirectoryWalkConfigDiscovery.cs` -- implements `IConfigDiscoveryService`. Walks from CWD to root. Discovers `AGENT.md`, `SKILL.md`, `rules/*.md` files. Parses `@include` directives. Applies frontmatter `paths:` glob scoping. Priority: closer to CWD = higher priority.

**Modified files:**
- `Infrastructure/Infrastructure.AI/DependencyInjection.cs` -- register discovery service

**Dependencies:** Existing `ISkillLoaderService`
**Risk:** Low
**Commit:** `feat: config discovery with directory walk, @include directives, and path scoping`

---

### Pattern 10: Denial Tracking & Rate Limiting

#### Step 10.1 -- Implementation

**New files:**
- `Domain/Domain.AI/Permissions/DenialRecord.cs` -- record with `ToolName` (string), `OperationPattern` (string?), `DenialCount` (int), `FirstDenied` (DateTimeOffset), `LastDenied` (DateTimeOffset)
- `Application/Application.AI.Common/Interfaces/Permissions/IDenialTracker.cs` -- `void RecordDenial(string agentId, string toolName, string? operation)`, `bool IsRateLimited(string agentId, string toolName, string? operation)`, `IReadOnlyList<DenialRecord> GetDenials(string agentId)`
- `Infrastructure/Infrastructure.AI/Permissions/InMemoryDenialTracker.cs` -- implements `IDenialTracker`. ConcurrentDictionary keyed by `(agentId, toolName, operation)`. Auto-escalate to hard deny after N denials (configurable via `PermissionsConfig.DenialRateLimitThreshold`, default 3).

**Modified files:**
- `Infrastructure/Infrastructure.AI/Permissions/ThreePhasePermissionResolver.cs` -- check `IDenialTracker.IsRateLimited` in Phase 1 (deny) before rule matching
- `Application/Application.AI.Common/MediatRBehaviors/ToolPermissionBehavior.cs` -- call `IDenialTracker.RecordDenial` when permission is denied
- `Infrastructure/Infrastructure.AI/DependencyInjection.cs` -- register tracker

**Dependencies:** Pattern 2 (permissions)
**Risk:** Low
**Commit:** `feat: denial tracking with rate limiting for repeated permission denials`

---

## Dependency Graph

```
Phase 0: Test Projects + Config POCOs
    |
    v
Phase 1:
    Pattern 2 (Permissions) ──────────────────┐
    Pattern 3 (Hooks) ────────────────────────┤
    Pattern 4 (System Prompt Composer) ───────┤
    Pattern 1 (Compaction) ───────────────────┘ depends on 2,3,4
    |
    v
Phase 2:
    Pattern 6 (Tool Result Storage) ──────────┐
    Pattern 7 (Streaming Execution) ──────────┤
    Pattern 5 (Subagent Orchestration) ───────┘ depends on 2,3,6,7
    |
    v
Phase 3:
    Pattern 8 (Cache Break Detection) ──── depends on 4
    Pattern 9 (Config Discovery)  ──────── standalone
    Pattern 10 (Denial Tracking) ─────── depends on 2
```

## New File Count Summary

| Layer | New Files | Modified Files |
|-------|-----------|----------------|
| Domain.AI | ~18 | 1 (ContextConventions.cs) |
| Domain.Common | ~7 (config) | 2 (AIConfig.cs, Result.cs) |
| Application.AI.Common | ~16 (interfaces + behaviors) | 5 (DI, ITool, IContextBudgetTracker, ToolPermissionBehavior, AgentFactory) |
| Application.Core | 0 | 1 (RunOrchestratedTaskHandler) |
| Infrastructure.AI | ~25 (implementations) | 3 (DI, FileSystemTool, AIToolConverter) |
| Tests | ~25 | 0 |
| Solution | 0 | 2 (slnx, Directory.Packages.props) |
| **Total** | **~91** | **~14** |

## MediatR Pipeline Behavior Final Order

After all patterns are implemented, the pipeline from outermost to innermost:

1. `AutoCompactBehavior` -- post-handler compaction check (Pattern 1)
2. `UnhandledExceptionBehavior` -- safety net with agent context
3. `AgentContextPropagationBehavior` -- sets scoped agent identity
4. `AuditTrailBehavior` -- records auditable requests
5. `ContentSafetyBehavior` -- screens content
6. `ToolPermissionBehavior` -- 3-phase permission check (Pattern 2, enhanced)
7. `HookBehavior` -- pre/post hooks (Pattern 3)
8. `RequestValidationBehavior` -- FluentValidation
9. `RequestTracingBehavior` -- OTel spans
10. Handler

## Config Structure (appsettings.json)

```json
{
  "AppConfig": {
    "AI": {
      "ContextManagement": {
        "Compaction": {
          "AutoCompactThresholdRatio": 0.85,
          "CircuitBreakerMaxFailures": 3,
          "CircuitBreakerCooldownSec": 60,
          "MicroCompactStalenessMinutes": 5,
          "FullCompactMaxPreservedPlans": 5,
          "FullCompactPlanTokenBudget": 5000,
          "FullCompactSkillTokenBudget": 25000
        },
        "ToolResultStorage": {
          "PerResultCharLimit": 50000,
          "AggregatePerMessageCharLimit": 200000,
          "PreviewSizeChars": 2000,
          "StoragePath": ".agent-sessions"
        },
        "Budget": {
          "DiminishingReturnsContinuationThreshold": 3,
          "DiminishingReturnsMinDelta": 500,
          "CompletionThresholdRatio": 0.90
        }
      },
      "Permissions": {
        "DefaultBehavior": "Ask",
        "DenialRateLimitThreshold": 3,
        "SafetyGatePaths": [".git/", ".claude/", ".ssh/", ".env"],
        "Rules": []
      },
      "Hooks": {
        "Enabled": true,
        "DefaultTimeoutMs": 5000,
        "MaxParallelHooks": 10
      },
      "Orchestration": {
        "MaxConcurrentSubagents": 3,
        "DefaultMaxTurnsPerSubagent": 10,
        "MailboxStoragePath": ".agent-sessions/mailbox"
      }
    }
  }
}
```

## Risks & Mitigations

| Risk | Impact | Mitigation |
|------|--------|------------|
| `IToolPermissionService` interface change breaks consumers | Medium | Only one consumer (`ToolPermissionBehavior`). Keep `IsToolAllowedAsync` as backward-compat wrapper that calls `ResolvePermissionAsync` and returns `decision.Behavior == Allow`. |
| `AutoCompactBehavior` modifying conversation history after handler | High | The behavior only triggers on `AgentTurnResult.UpdatedHistory`. It replaces the history with compacted version. The caller (`RunConversationHandler`) already passes `lastResult.UpdatedHistory` to the next turn, so the compacted history flows naturally. Add integration test verifying multi-turn with auto-compact. |
| Hook parallel execution causing race conditions | Medium | Each hook receives a read-only `HookExecutionContext`. Only `HookResult` values are collected. Aggregation is sequential (after all hooks complete). No shared mutable state. |
| `AgentFactory` prompt assembly change breaking existing agents | Medium | `ISystemPromptComposer.ComposeAsync` is opt-in. If no section providers are registered for a section type, it returns empty. The `AgentIdentitySectionProvider` produces equivalent output to the current static assembly. Add regression test comparing old vs new prompt output. |
| No test projects exist today | High | Phase 0 creates them before any code changes. Every subsequent pattern step includes tests. |
| 91 new files is a large change | Medium | Each commit is self-contained and independently verifiable. The dependency graph enables incremental merging. |

## Success Criteria

- [ ] All 10 patterns have domain models, application interfaces, and infrastructure implementations
- [ ] `dotnet build src/AgenticHarness.slnx` passes with 0 errors after each commit
- [ ] `dotnet test` passes with 80%+ coverage on new code after each commit
- [ ] Existing `ExecuteAgentTurn`, `RunConversation`, `RunOrchestratedTask` commands work without regression
- [ ] MediatR pipeline behavior order is documented and tested
- [ ] All config POCOs are bound through `IOptionsMonitor<AppConfig>` with sensible defaults
- [ ] All public types have XML documentation
- [ ] No function exceeds 50 lines, no nesting exceeds 4 levels
- [ ] All new files follow existing naming and namespace conventions

## Suggested Commit Sequence

1. `chore: add xUnit test projects and testing infrastructure`
2. `feat: add config POCOs for all 10 Claude Code patterns`
3. `feat: 3-phase tool permission system with safety gates and pattern matching`
4. `feat: hook/event system with pre/post tool interception and parallel execution`
5. `feat: memoized system prompt composer with section-based assembly and cache invalidation`
6. `feat: context compaction system with full/partial/micro strategies and auto-compact state machine`
7. `feat: tool result storage with disk persistence and diminishing returns budget detection`
8. `feat: batched tool execution with parallel reads and serial writes`
9. `feat: subagent orchestration with tool scoping, mailbox messaging, and built-in profiles`
10. `feat: prompt cache break detection with per-tool hash tracking`
11. `feat: config discovery with directory walk, @include directives, and path scoping`
12. `feat: denial tracking with rate limiting for repeated permission denials`