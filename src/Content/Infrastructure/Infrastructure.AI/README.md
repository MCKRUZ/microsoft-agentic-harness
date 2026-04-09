# Infrastructure.AI

This is the largest project in the solution, and for good reason — it's where abstractions become reality. Every interface defined in Application.AI.Common has a concrete implementation here: the permission resolver that actually evaluates rules, the compaction service that actually summarizes conversations, the hook executor that actually runs shell commands, the prompt composer that actually assembles system prompts.

If Application.AI.Common is the blueprint, Infrastructure.AI is the building.

---

## Permissions: The Three-Phase Resolver

`ThreePhasePermissionResolver` is the concrete implementation of the tool permission system. When a tool request arrives, it runs three checks in sequence:

**Phase 1 — Rule Matching.** The `ConfigBasedRuleProvider` loads permission rules from AppConfig. The `GlobPatternMatcher` matches tool names against rule patterns (exact match, prefix wildcard `*`, full wildcard). If a rule matches with `Allow` or `Deny`, that's the answer.

**Phase 2 — Safety Gates.** The `SafetyGateRegistry` defines paths that are always dangerous — `.git/`, `.ssh/`, system directories. Even if Phase 1 said "allow," a safety gate can override it to "ask the user."

**Phase 3 — Denial Rate Limiting.** The `InMemoryDenialTracker` tracks how often each tool has been denied per agent. After a configurable threshold (default 3), the tool is auto-denied without prompting. This prevents the agent from repeatedly asking the user about a tool they've already rejected.

## Compaction: Three Strategies

When the context window fills up, `ContextCompactionService` orchestrates the reduction. It selects a strategy, fires lifecycle hooks (PreCompact/PostCompact), manages the `AutoCompactStateMachine` circuit breaker, and invalidates prompt caches.

**FullCompactionStrategy** sends the entire conversation to the LLM with a summarization prompt. Most thorough, but costs an API call. The result is a single `CompactionBoundaryMessage` that replaces the full history.

**PartialCompactionStrategy** splits the conversation in half. The older half gets summarized; the recent half stays intact. Balances context reduction with recency preservation.

**MicroCompactionStrategy** doesn't touch the conversation structure at all. Instead, it finds large tool results (file reads, grep output, HTTP responses) and replaces them with abbreviated summaries. No LLM call required — it's pure string processing.

The `AutoCompactStateMachine` prevents compaction storms. If compaction fails repeatedly (LLM timeout, budget still exceeded after summary), the circuit breaker trips and blocks further attempts until a cooldown period passes.

## Hooks: Lifecycle Interception

`CompositeHookExecutor` runs registered hooks by event type. Four execution mechanisms are supported:

- **Command** — Runs a shell command with hook context as environment variables
- **Prompt** — Sends a prompt to the LLM (for hooks that need AI judgment)
- **Http** — Calls a webhook URL with hook context as JSON payload
- **Middleware** — Invokes registered middleware components (for in-process hooks)

`InMemoryHookRegistry` stores hooks in a `ConcurrentDictionary`, sorted by priority. Hooks can optionally match specific tool names via the same glob pattern matcher used by permissions.

## Prompts: Composable System Prompts

`MemoizedPromptComposer` assembles the system prompt from five section providers, each responsible for one concern:

- **AgentIdentitySectionProvider** — Agent name, role, capabilities, constraints
- **ToolSchemasSectionProvider** — JSON schemas of available tools
- **PermissionRulesSectionProvider** — Permission rules formatted as natural language constraints
- **SkillInstructionsSectionProvider** — Loaded skill instructions
- **SessionStateSectionProvider** — Current workflow/session state

Sections are cached by `InMemoryPromptSectionCache` and invalidated selectively — a permission change only invalidates the permission section, not the entire prompt.

`Sha256PromptCacheTracker` hashes the system prompt and individual tool schemas after each turn. On the next turn, it compares hashes to detect which section caused a cache break — useful for diagnosing why the LLM's prompt cache miss rate is high.

## Subagent Management

**BuiltInSubagentProfiles** defines preset agent types: Explore, Plan, Verify, Execute. Each profile specifies a tool allowlist, max turns, and permission mode.

**SubagentToolResolver** filters the parent agent's tool pool through a subagent's allowlist and denylist. A research subagent gets read-only tools; an execution subagent gets write tools; neither gets tools outside their profile.

**InMemoryAgentMailbox** provides async message passing between agents using `Channel<T>`. Unbounded FIFO delivery, suitable for single-process orchestration.

## Tool Execution

**BatchedToolExecutionStrategy** partitions tool calls by concurrency classification: read-only tools run in parallel (bounded by `SemaphoreSlim`), write-serial tools run one at a time. Results are returned in the original request order regardless of execution order.

**FileSystemService** implements sandboxed file operations. Every read, write, and search is restricted to explicitly configured base paths. Path traversal attempts are caught and rejected.

**FileSystemTool** wraps `FileSystemService` as an `ITool` with a name, description, and operation schema — making it available to agents through the tool pipeline.

## State Management

**JsonCheckpointStateManager** persists workflow state as JSON files, using temp-file-then-rename for atomic writes.

**MarkdownCheckpointDecorator** wraps any state manager to also generate human-readable markdown checkpoints with YAML frontmatter — useful for debugging workflow state without parsing JSON.

**CompositeStateManager** composes multiple managers (JSON + Markdown) so both formats stay in sync.

## Configuration Discovery

**DirectoryWalkConfigDiscovery** walks the directory tree upward from a starting point, discovering `AGENT.md`, `SKILL.md`, `CLAUDE.md`, `CLAUDE.local.md`, and `.claude/rules/*.md` files. It supports `@include` directives for composing config from multiple files and YAML frontmatter path globs for scoping rules to specific directories.

---

## Project Structure

```
Infrastructure.AI/
├── A2A/                          A2AAgentHost (agent discovery + delegation)
├── Agents/                       BuiltInSubagentProfiles, InMemoryAgentMailbox, SubagentToolResolver
├── Compaction/
│   ├── AutoCompactStateMachine.cs
│   ├── ContextCompactionService.cs
│   └── Strategies/               Full, Partial, Micro compaction
├── Config/                       DirectoryWalkConfigDiscovery
├── Context/                      FileSystemToolResultStore
├── Factories/                    ChatClientFactory (Azure OpenAI, OpenAI, AI Foundry)
├── Generators/                   StateMarkdownGenerator
├── Helpers/                      AgentFrameworkHelper
├── Hooks/                        CompositeHookExecutor, InMemoryHookRegistry
├── Permissions/                  ThreePhasePermissionResolver, GlobPatternMatcher,
│                                 ConfigBasedRuleProvider, InMemoryDenialTracker, SafetyGateRegistry
├── Prompts/
│   ├── InMemoryPromptSectionCache.cs
│   ├── MemoizedPromptComposer.cs
│   ├── Sha256PromptCacheTracker.cs
│   └── Sections/                 5 section providers (identity, tools, permissions, skills, session)
├── StateManagement/
│   ├── CompositeStateManager.cs
│   ├── MarkdownCheckpointDecorator.cs
│   └── Checkpoints/              JsonCheckpointStateManager
├── Tools/                        BatchedToolExecutionStrategy, FileSystemService/Tool,
│                                 ToolConcurrencyClassifier, ToolErrorClassifier
└── DependencyInjection.cs        Registers everything
```

## Dependencies

- **Application.AI.Common** — All 41 interfaces this project implements
- **Application.Common** — Pipeline behaviors, logging, helpers
- **Domain.AI** / **Domain.Common** — Domain models, config POCOs
- **Azure.AI.Agents.Persistent** — Persistent agent client
- **Azure.AI.OpenAI** — Azure OpenAI chat client
- **Microsoft.Agents.AI** — Agent framework
