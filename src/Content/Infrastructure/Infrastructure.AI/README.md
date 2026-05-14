# Infrastructure.AI

## What This Project Is

Infrastructure.AI is the core implementation layer of the agentic harness. It takes every abstract interface defined in Application.AI.Common and turns it into working code: the permission system that decides whether an agent can use a tool, the compaction engine that prevents conversations from exceeding context limits, the prompt composer that assembles system prompts from multiple sections, the hook system that fires lifecycle events, and the chat client factory that connects to Azure OpenAI, OpenAI, Anthropic, or AI Foundry. Think of Application.AI.Common as the contract and this project as the contractor that builds the house.

This project sits at the center of the Infrastructure layer dependency graph. It is referenced by all Presentation hosts (ConsoleUI, AgentHub, WebUI, MCPServer) and depends on Application.AI.Common for interfaces, Application.Core for MediatR commands, Domain.Common for configuration models, and Infrastructure.AI.RAG for the retrieval pipeline.

## Architecture Context

```
Application.AI.Common (Interfaces)           Domain.Common (Config)
         |                                          |
         v                                          v
+-----------------------------------------------------------+
|                   Infrastructure.AI                         |
|  Implements: IChatClientFactory, IToolPermissionService,   |
|  IContextCompactionService, ISystemPromptComposer,         |
|  IHookExecutor, IToolExecutionStrategy, ISkillMetadata-    |
|  Registry, IAgentMetadataRegistry, IA2AAgentHost,          |
|  IHarnessCandidateRepository, IExecutionTraceStore, ...    |
+-----------------------------------------------------------+
         ^                        ^
         |                        |
  Presentation.AgentHub    Infrastructure.AI.RAG
  Presentation.ConsoleUI   (for DocumentSearchTool)
  Presentation.WebUI
```

Registration is done through a single extension method called from the Presentation composition root:

```csharp
services.AddInfrastructureAIDependencies(appConfig);
```

## Key Concepts

### Chat Client Factory

**What it is:** A multi-provider factory that creates `IChatClient` instances for any supported LLM backend.

**Why it exists:** The harness must support multiple AI providers (Azure OpenAI, OpenAI, Azure AI Inference for Claude/Mistral, Anthropic via Azure Foundry, AI Foundry Persistent Agents, and an Echo client for testing) without coupling the orchestration layer to any specific SDK.

**How it works:**
1. On startup, `RegisterAIClients` in DependencyInjection.cs reads `AppConfig.AI.AgentFramework` and registers the appropriate SDK client (AzureOpenAIClient or OpenAIClient) into DI.
2. When an agent needs a chat client, it calls `IChatClientFactory.GetChatClientAsync(clientType, deploymentOrModelId)`.
3. The factory resolves the correct SDK, creates or retrieves a cached `IChatClient`, and returns it.
4. For Azure AI Inference (Claude, Mistral on Azure), a `ChatCompletionsClient` is created with `api-key` header auth.
5. For Anthropic, an `AzureFoundryRewritingHandler` intercepts SDK requests and rewrites URLs from `api.anthropic.com` to the Foundry endpoint.

```csharp
// Usage in the orchestration layer:
var chatClient = await _chatClientFactory.GetChatClientAsync(
    AIAgentFrameworkClientType.AzureOpenAI,
    "gpt-4o",
    cancellationToken);
```

### Three-Phase Permission Resolver

**What it is:** The runtime engine that decides whether an agent is allowed to invoke a specific tool.

**Why it exists:** AI agents must have guardrails. Without permissions, an agent could delete files, modify production databases, or execute arbitrary commands. The permission system provides a layered defense: explicit deny rules, safety gates for dangerous paths, rate-limiting for repeated denials, and a default-to-ask posture for unknown tools.

**How it works:**
1. **Phase 0 (Rate Limit):** Check if the tool has been denied too many times (configurable threshold). If so, auto-deny without user prompting.
2. **Phase 1a (Safety Gates):** Check bypass-immune safety gates (e.g., `.git/`, `.ssh/`, system dirs). These cannot be overridden by rules.
3. **Phase 1b (Deny Rules):** Evaluate deny rules sorted by priority. First match wins.
4. **Phase 2 (Ask Rules):** Evaluate ask rules. If matched, require user confirmation.
5. **Phase 3 (Allow Rules):** Evaluate allow rules. If no rule matches at any phase, default to Ask.

```csharp
var decision = await _permissionService.ResolvePermissionAsync(
    agentId: "main-agent",
    toolName: "file_system",
    operation: "write",
    parameters: new Dictionary<string, object?> { ["path"] = "/etc/hosts" });

// decision.Behavior is Allow, Deny, or Ask
```

### Context Compaction

**What it is:** A service that reduces conversation history size when approaching the LLM's context window limit.

**Why it exists:** Long agent conversations accumulate tool results, file contents, and multi-turn reasoning. Without compaction, the agent eventually hits the context limit and cannot continue. Compaction intelligently reduces the history while preserving essential context.

**How it works:**
1. `ShouldAutoCompact()` checks current token count against a configurable threshold ratio (e.g., 80% of max).
2. If triggered, `CompactAsync()` selects a strategy based on urgency:
   - **Full:** Summarizes the entire conversation via LLM. Most thorough but costs an API call.
   - **Partial:** Summarizes older half, keeps recent half intact. Balances reduction with recency.
   - **Micro:** Abbreviates large tool results (file reads, grep output) without an LLM call.
3. Fires PreCompact/PostCompact hooks for extension points.
4. Invalidates the prompt cache so the next turn rebuilds with compacted history.
5. Records success/failure in the circuit breaker to prevent compaction storms.

### Hook System

**What it is:** A lifecycle interception mechanism that executes user-defined actions at specific points in the agent loop.

**Why it exists:** Agents need extensibility without modifying core code. Hooks allow external scripts, webhooks, or middleware to observe and react to events like PreToolUse, PostToolUse, PreCompact, PostCompact, and more.

**How it works:**
- `InMemoryHookRegistry` stores hooks keyed by event type, sorted by priority.
- `CompositeHookExecutor` iterates registered hooks for an event and executes them via one of four mechanisms: Command (shell), Prompt (LLM), Http (webhook), or Middleware (in-process).
- Hooks can match specific tool names using glob patterns.

### System Prompt Composition

**What it is:** A section-based system prompt builder that assembles the agent's instructions from multiple providers.

**Why it exists:** System prompts are complex. They include agent identity, tool schemas, permission rules, skill instructions, and session state. Each section changes at different rates (tool schemas change when MCP servers connect; session state changes every turn). Section-based caching avoids regenerating the entire prompt when only one part changes.

**How it works:**
- `MemoizedPromptComposer` iterates `IPromptSectionProvider` instances, each contributing a section.
- `InMemoryPromptSectionCache` caches each section independently.
- `Sha256PromptCacheTracker` detects which section caused a prompt cache break between turns.

Four built-in section providers:
1. `AgentIdentitySectionProvider` -- agent name, role, capabilities
2. `ToolSchemasSectionProvider` -- JSON schemas of available tools
3. `PermissionRulesSectionProvider` -- permission rules in natural language
4. `SessionStateSectionProvider` -- current workflow state

### Subagent Orchestration

**What it is:** Infrastructure for spawning and coordinating child agents with scoped tool access.

**Why it exists:** Complex tasks benefit from specialized sub-agents (research, planning, verification, execution). Each needs different tool permissions and capabilities.

**Key types:**
- `BuiltInSubagentProfiles` -- preset agent archetypes with tool allowlists and max turn limits
- `SubagentToolResolver` -- filters parent tools through a subagent's allow/deny lists
- `InMemoryAgentMailbox` -- async message passing between agents via `Channel<T>`

### Batched Tool Execution

**What it is:** A strategy that executes tool calls with concurrency awareness.

**Why it exists:** LLMs often request multiple tools in parallel. Read-only tools (grep, file read) can safely run concurrently, but write tools (file write, git commit) must run serially to avoid conflicts.

**How it works:**
- `ToolConcurrencyClassifier` categorizes each tool as ReadOnly or WriteSerial.
- `BatchedToolExecutionStrategy` partitions calls by classification, runs reads in parallel (bounded by semaphore), writes sequentially, then returns results in original request order.

## Data Flow

```
Agent Turn Request
       |
       v
[Permission Check] --deny--> Return denial
       |allow/ask
       v
[Tool Execution Strategy]
       |
  +----+----+
  |         |
  v         v
[Parallel  [Serial
 Reads]     Writes]
  |         |
  +----+----+
       |
       v
[Hook Execution: PostToolUse]
       |
       v
[Context Size Check]
       |
  (if over threshold)
       v
[Compaction Service]
       |
       v
[Prompt Recomposition]
       |
       v
[Chat Client Factory] --> LLM Provider
```

## Project Structure

```
Infrastructure.AI/
├── A2A/                        Agent-to-Agent protocol host
├── Agents/                     Subagent profiles, mailbox, tool resolver
├── Audit/                      StructuredLogAuditSink
├── Clients/                    EchoChatClient (testing)
├── Compaction/
│   ├── AutoCompactStateMachine.cs     Circuit breaker for compaction storms
│   ├── ContextCompactionService.cs    Orchestrates strategy selection + hooks
│   └── Strategies/                    Full, Partial, Micro implementations
├── Config/                     DirectoryWalkConfigDiscovery (@include support)
├── ContentSafety/              StructuredLogContentSafetyService
├── Context/                    FileSystemToolResultStore
├── Factories/                  ChatClientFactory (6 providers)
├── Generators/                 StateMarkdownGenerator
├── Helpers/                    AgentFrameworkHelper (SDK client options)
├── Hooks/                      CompositeHookExecutor, InMemoryHookRegistry
├── Memory/                     JsonlAgentHistoryStore
├── MetaHarness/                Proposer, evaluator, regression suite, snapshot builder
├── Permissions/
│   ├── ThreePhasePermissionResolver.cs
│   ├── ConfigBasedRuleProvider.cs
│   ├── GlobPatternMatcher.cs
│   ├── InMemoryDenialTracker.cs
│   └── SafetyGateRegistry.cs
├── Prompts/
│   ├── MemoizedPromptComposer.cs
│   ├── InMemoryPromptSectionCache.cs
│   ├── Sha256PromptCacheTracker.cs
│   └── Sections/               4 section providers
├── Security/                   PatternSecretRedactor
├── Skills/                     SkillMetadataParser, SkillMetadataRegistry, content providers
├── StateManagement/
│   ├── CompositeStateManager.cs
│   ├── MarkdownCheckpointDecorator.cs
│   └── Checkpoints/            JsonCheckpointStateManager
├── Tools/
│   ├── BatchedToolExecutionStrategy.cs
│   ├── FileSystemService.cs / FileSystemTool.cs
│   ├── DocumentSearchTool.cs / DocumentIngestTool.cs
│   ├── EchoLookupTool.cs / EchoCalculateTool.cs
│   ├── RestrictedSearchTool.cs
│   ├── ReadHistoryTool.cs
│   ├── ToolConcurrencyClassifier.cs
│   └── ToolErrorClassifier.cs
├── Traces/                     FileSystemExecutionTraceStore
└── DependencyInjection.cs      Central registration (230+ lines)
```

## Key Types Reference

| Type | Purpose | Implements | Lifetime |
|------|---------|-----------|----------|
| `ChatClientFactory` | Multi-provider LLM client creation | `IChatClientFactory` | Singleton |
| `ThreePhasePermissionResolver` | Tool permission evaluation | `IToolPermissionService` | Singleton |
| `ContextCompactionService` | Context reduction orchestration | `IContextCompactionService` | Singleton |
| `MemoizedPromptComposer` | Section-based prompt assembly | `ISystemPromptComposer` | Singleton |
| `CompositeHookExecutor` | Lifecycle hook execution | `IHookExecutor` | Transient |
| `BatchedToolExecutionStrategy` | Concurrent/serial tool dispatch | `IToolExecutionStrategy` | Transient |
| `FileSystemService` | Sandboxed file I/O | `IFileSystemService` | Singleton |
| `FileSystemTool` | File ops as agent tool | `ITool` (keyed: "file_system") | Singleton |
| `DocumentSearchTool` | RAG search as agent tool | `ITool` (keyed: "document_search") | Singleton |
| `SkillMetadataRegistry` | Skill catalog from filesystem | `ISkillMetadataRegistry` | Singleton |
| `AgentMetadataRegistry` | Agent manifest catalog | `IAgentMetadataRegistry` | Singleton |
| `SubagentToolResolver` | Tool scoping for child agents | `ISubagentToolResolver` | Singleton |
| `InMemoryAgentMailbox` | Inter-agent messaging | `IAgentMailbox` | Singleton |
| `AutoCompactStateMachine` | Compaction circuit breaker | `IAutoCompactStateMachine` | Singleton |
| `PatternSecretRedactor` | Regex-based secret masking | `ISecretRedactor` | Singleton |

## Configuration

The project reads configuration from `AppConfig` bound via the Options pattern. Key sections:

```jsonc
{
  "AppConfig": {
    "AI": {
      "AgentFramework": {
        "ClientType": "AzureOpenAI",       // AzureOpenAI | OpenAI | AzureAIInference | Anthropic | PersistentAgents | Echo
        "Endpoint": "https://...",          // Required for AzureOpenAI, AzureAIInference, Anthropic
        "ApiKey": "...",                    // Required for all except Echo
        "DefaultDeployment": "gpt-4o"      // Model/deployment name
      },
      "Permissions": {
        "DenialRateLimitThreshold": 3      // Auto-deny after N denials
      },
      "ContextManagement": {
        "Compaction": {
          "AutoCompactThresholdRatio": 0.8 // Trigger at 80% of max context
        }
      }
    },
    "Infrastructure": {
      "FileSystem": {
        "AllowedBasePaths": ["../../../.."] // Sandboxed directories for file tools
      }
    }
  }
}
```

## Common Tasks

### How to Add a New Tool

1. Create a class implementing `ITool` in `Tools/`:
```csharp
public sealed class MyCustomTool : ITool
{
    public const string ToolName = "my_custom_tool";
    public string Name => ToolName;
    public string Description => "Does something useful for the agent.";
    // Implement ExecuteAsync...
}
```

2. Register with keyed DI in `DependencyInjection.cs`:
```csharp
services.AddKeyedSingleton<ITool>(MyCustomTool.ToolName, (sp, _) =>
    new MyCustomTool(sp.GetRequiredService<IMyDependency>()));
```

3. Add the tool name to the agent's skill `AllowedTools` list in its SKILL.md.

### How to Add a New Prompt Section

1. Create a class implementing `IPromptSectionProvider` in `Prompts/Sections/`.
2. Register as transient in DI: `services.AddTransient<IPromptSectionProvider, MySectionProvider>()`.
3. The composer will automatically include it in the next prompt assembly.

### How to Debug Permission Denials

Check structured logs for entries with `Permission resolved for agent {AgentId}, tool {ToolName}: {Decision}`. The `ThreePhasePermissionResolver` logs at Debug level with the full decision chain. OpenTelemetry spans also carry `permission.tool_name`, `permission.decision`, and `permission.rule_source` tags.

## Dependencies

**Project References:**
- `Application.AI.Common` -- All interfaces this project implements
- `Application.Core` -- MediatR for DocumentIngestTool command dispatch
- `Domain.Common` -- AppConfig, AIConfig, permission models
- `Infrastructure.AI.RAG` -- IRagOrchestrator for DocumentSearchTool

**NuGet Packages:**
- `Anthropic.SDK` -- Anthropic Messages API client
- `Azure.AI.Agents.Persistent` -- AI Foundry persistent agent CRUD
- `Azure.AI.Inference` -- Azure AI Inference (ChatCompletionsClient)
- `Azure.AI.OpenAI` -- Azure OpenAI SDK
- `Microsoft.Agents.AI.OpenAI` -- Microsoft Agent Framework adapters
- `Microsoft.Extensions.AI.AzureAIInference` -- IChatClient bridge for Inference
- `Microsoft.Extensions.Caching.Memory` -- Client caching in ChatClientFactory
- `Microsoft.Extensions.Http` -- IHttpClientFactory for hooks
- `Microsoft.Extensions.Logging.Abstractions` -- Logging
- `Microsoft.Extensions.Options` -- IOptionsMonitor pattern

## Testing

- **Test project:** `Infrastructure.AI.Tests` (xUnit)
- **Run:** `dotnet test --filter "FullyQualifiedName~Infrastructure.AI.Tests"`
- **Mock guidance:** Mock `IChatClient` for LLM calls, use real implementations for `GlobPatternMatcher`, `ToolConcurrencyClassifier`, and in-memory stores. Use `EchoChatClient` for integration tests that need a chat client without real API calls.
