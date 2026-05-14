# Infrastructure.AI.Tests

## What This Tests

Unit and integration tests for the **Infrastructure.AI** project — the core AI infrastructure layer implementing agents, compaction strategies, hooks, permissions, prompt composition, skill parsing, state management, tools, MCP resource providers, memory persistence, security, and meta-harness services. This is the largest test project, covering all concrete implementations of the application-layer interfaces.

## Test Organization

Files mirror the infrastructure namespace structure: `Agents/`, `Compaction/Strategies/`, `Config/`, `Connectivity/`, `ContentSafety/`, `Context/`, `Factories/`, `Generators/`, `Helpers/`, `Hooks/`, `MCP/`, `Memory/`, `MetaHarness/`, `Permissions/`, `Pipeline/`, `Prompts/Sections/`, `Security/`, `Skills/`, `StateManagement/`, `Tools/`, `Traces/`, and `A2A/`. Naming convention: `MethodName_Scenario_ExpectedResult`.

## All Test Classes

| Test Class | What It Tests | Approx Test Count | Unit/Integration |
|------------|---------------|-------------------|------------------|
| `A2AAgentHostTests` | A2A agent hosting | 4 | Unit |
| `AgentMetadataParserTests` | AGENT.md manifest parsing | 5 | Unit |
| `AgentMetadataRegistryTests` | Agent registry lookup/caching | 4 | Unit |
| `BuiltInSubagentProfilesTests` | Built-in subagent profile definitions | 3 | Unit |
| `InMemoryAgentMailboxTests` | Agent mailbox message passing | 5 | Unit |
| `InMemoryAgentMailboxIntegrationTests` | Mailbox concurrent delivery | 4 | Integration |
| `SubagentToolResolverTests` | Tool resolution for subagents | 4 | Unit |
| `StructuredLogAuditSinkTests` | Audit event structured logging | 3 | Unit |
| `AutoCompactStateMachineTests` | Auto-compaction state transitions | 5 | Unit |
| `ContextCompactionServiceTests` | Compaction orchestration | 5 | Unit |
| `FullCompactionStrategyTests` | Full context compaction | 4 | Unit |
| `MicroCompactionStrategyTests` | Micro compaction (recent turns) | 4 | Unit |
| `MicroCompactionStrategyClassificationTests` | Message classification for micro | 3 | Unit |
| `PartialCompactionStrategyTests` | Partial compaction (sliding window) | 4 | Unit |
| `DirectoryWalkConfigDiscoveryTests` | Directory-based config file discovery | 4 | Unit |
| `AzureAIConnectivityTests` | Azure AI endpoint connectivity | 3 | Unit |
| `StructuredLogContentSafetyServiceTests` | Content safety via structured logs | 4 | Unit |
| `FileSystemToolResultStoreTests` | Tool result persistence to disk | 4 | Unit |
| `ChatClientFactoryTests` | Chat client construction routing | 6 | Unit |
| `ChatClientFactoryAvailabilityTests` | Provider availability checks | 4 | Unit |
| `ChatClientFactoryEndpointTests` | Endpoint URL/key resolution | 4 | Unit |
| `StateMarkdownGeneratorTests` | State-to-markdown generation | 3 | Unit |
| `AgentFrameworkHelperTests` | Agent framework utility methods | 3 | Unit |
| `CompositeHookExecutorTests` | Hook composition and ordering | 5 | Unit |
| `CompositeHookExecutorIntegrationTests` | Multi-hook pipeline execution | 4 | Integration |
| `InMemoryHookRegistryTests` | Hook registration and lookup | 4 | Unit |
| `TraceResourceProviderTests` | MCP resource provider for traces | 3 | Unit |
| `JsonlAgentHistoryStoreTests` | JSONL history file persistence | 4 | Unit |
| `ReadHistoryToolTests` | Tool for reading agent history | 3 | Unit |
| `ActiveConfigSnapshotBuilderTests` | MetaHarness config snapshot | 3 | Unit |
| `AgentEvaluationServiceTests` | Agent performance evaluation | 4 | Unit |
| `EvalTaskLoaderTests` | Evaluation task loading from disk | 3 | Unit |
| `FileSystemHarnessCandidateRepositoryTests` | Candidate CRUD via filesystem | 4 | Unit |
| `FileSystemHarnessCandidateRepositoryIntegrationTests` | Multi-candidate file operations | 3 | Integration |
| `FileSystemRegressionSuiteServiceTests` | Regression suite persistence | 4 | Unit |
| `OrchestratedHarnessProposerTests` | MetaHarness proposal generation | 5 | Unit |
| `OrchestratedHarnessProposer_LearningsTests` | Learning extraction from proposals | 4 | Unit |
| `ConfigBasedRuleProviderTests` | Config-driven permission rules | 4 | Unit |
| `GlobPatternMatcherTests` | Glob pattern matching (tools, paths) | 5 | Unit |
| `InMemoryDenialTrackerTests` | Denial rate-limit tracking | 4 | Unit |
| `SafetyGateRegistryTests` | Safety gate lookup and matching | 4 | Unit |
| `ThreePhasePermissionResolverTests` | 3-phase Deny>Ask>Allow resolution | 8 | Unit |
| `ThreePhasePermissionResolverWithDenialTests` | Resolver with denial rate limiting | 4 | Unit |
| `MeAiPipelineCompatibilityTests` | Microsoft.Extensions.AI pipeline compat | 3 | Unit |
| `InMemoryPromptSectionCacheTests` | Prompt section caching | 4 | Unit |
| `MemoizedPromptComposerTests` | Memoized prompt composition | 4 | Unit |
| `AgentIdentitySectionProviderTests` | Agent identity prompt section | 3 | Unit |
| `PermissionRulesSectionProviderTests` | Permission rules prompt section | 3 | Unit |
| `SessionStateSectionProviderTests` | Session state prompt section | 3 | Unit |
| `ToolSchemasSectionProviderTests` | Tool schema prompt section | 3 | Unit |
| `Sha256PromptCacheTrackerTests` | SHA-256 prompt change detection | 4 | Unit |
| `PatternSecretRedactorTests` | Secret pattern redaction | 4 | Unit |
| `CandidateSkillContentProviderTests` | Candidate skill content loading | 3 | Unit |
| `SkillContentProviderTests` | Skill SKILL.md content loading | 4 | Unit |
| `SkillMetadataParserTests` | SKILL.md YAML+markdown parsing | 5 | Unit |
| `SkillMetadataRegistryTests` | Skill registry discovery and caching | 5 | Unit |
| `SkillParserExtensionTests` | Parser extension points | 3 | Unit |
| `CompositeStateManagerTests` | JSON+Markdown dual state | 5 | Unit |
| `CompositeStateManagerIntegrationTests` | End-to-end state lifecycle | 4 | Integration |
| `JsonCheckpointStateManagerTests` | JSON checkpoint persistence | 5 | Unit |
| `JsonCheckpointStateManagerIntegrationTests` | Checkpoint file round-trip | 3 | Integration |
| `MarkdownCheckpointDecoratorTests` | Markdown decorator on state | 4 | Unit |
| `StateMarkdownGeneratorInterfaceTests` | Generator interface contract | 3 | Unit |
| `BatchedToolExecutionStrategyTests` | Parallel/batched tool execution | 4 | Unit |
| `FileSystemServiceTests` | File system operations | 5 | Unit |
| `FileSystemServiceIntegrationTests` | Real filesystem read/write | 4 | Integration |
| `FileSystemToolTests` | File system AITool | 4 | Unit |
| `ReadHistoryToolTests` | History reading tool | 3 | Unit |
| `RestrictedSearchToolTests` | Bounded file search tool | 4 | Unit |
| `RestrictedSearchToolSecurityTests` | Path traversal prevention | 4 | Unit |
| `ToolConcurrencyClassifierTests` | Tool concurrency classification | 3 | Unit |
| `ToolErrorClassifierTests` | Tool error categorization | 3 | Unit |
| `FileSystemExecutionTraceStoreTests` | Trace persistence to disk | 4 | Unit |
| `FileSystemExecutionTraceStoreIntegrationTests` | Trace file round-trip | 3 | Integration |
| `DependencyInjectionTests` | Service registration completeness | 3 | Unit |
| `_SmokeTests` | Assembly load verification | 2 | Unit |

## Testing Patterns and Example

Tests use Moq extensively for application-layer interfaces, `IOptionsMonitor<AppConfig>` for configuration, and real file-system temp directories for integration tests.

```csharp
[Fact]
public async Task DenyRule_TakesPrecedence_OverAllowAndAsk()
{
    // Arrange — rules with all three behaviors for the same tool
    var rules = new[]
    {
        new ToolPermissionRule("bash", null, PermissionBehaviorType.Allow, PermissionRuleSource.ProjectSettings, 10),
        new ToolPermissionRule("bash", null, PermissionBehaviorType.Ask, PermissionRuleSource.UserSettings, 5),
        new ToolPermissionRule("bash", null, PermissionBehaviorType.Deny, PermissionRuleSource.PolicySettings, 1)
    };

    var resolver = CreateResolver(rules);

    // Act — resolve permission for the tool
    var decision = await resolver.ResolvePermissionAsync("agent-1", "bash");

    // Assert — Deny always wins regardless of priority
    decision.Behavior.Should().Be(PermissionBehaviorType.Deny);
    decision.MatchedRule.Should().NotBeNull();
    decision.MatchedRule!.Source.Should().Be(PermissionRuleSource.PolicySettings);
}
```

**Mocking pattern**: `Mock<IOptionsMonitor<AppConfig>>` for configuration injection. `Mock<IPermissionRuleProvider>` for rule providers. Helper methods like `CreateResolver()` wire up the full SUT with mock dependencies. Integration tests use real temp directories cleaned up after each test.

## How to Run

```bash
# All tests in this project
dotnet test src/Content/Tests/Infrastructure.AI.Tests/Infrastructure.AI.Tests.csproj

# With coverage
dotnet test src/Content/Tests/Infrastructure.AI.Tests/Infrastructure.AI.Tests.csproj --collect:"XPlat Code Coverage"

# Single class
dotnet test --filter "FullyQualifiedName~ThreePhasePermissionResolverTests"

# Single test
dotnet test --filter "FullyQualifiedName~ThreePhasePermissionResolverTests.DenyRule_TakesPrecedence_OverAllowAndAsk"
```

## How to Add a New Test

1. Identify the infrastructure class under `Infrastructure.AI` to test.
2. Create a file in the matching subfolder (e.g., `Permissions/NewResolverTests.cs`).
3. Name: `{ClassName}Tests.cs`. Use `sealed class` for leaf test classes.
4. Skeleton:

```csharp
using FluentAssertions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Infrastructure.AI.Tests.Permissions;

public sealed class NewResolverTests
{
    private readonly Mock<IOptionsMonitor<AppConfig>> _config = new();

    private NewResolver CreateResolver() => new(_config.Object);

    [Fact]
    public async Task Resolve_Scenario_ExpectedBehavior()
    {
        // Arrange
        _config.Setup(c => c.CurrentValue).Returns(new AppConfig { ... });
        var resolver = CreateResolver();

        // Act
        var result = await resolver.ResolveAsync("input");

        // Assert
        result.Should().Be(expected);
    }
}
```

5. Run: `dotnet test --filter "FullyQualifiedName~NewResolverTests"`

## Shared Helpers and Fixtures

| Helper | Location | Purpose |
|--------|----------|---------|
| `TestableAIAgent` | `Helpers/TestableAIAgent.cs` | Configurable AIAgent double for handler tests |
| `TestableAgentSession` | `Helpers/TestableAgentSession.cs` | Minimal AgentSession stub |

## Dependencies

| Package | Purpose |
|---------|---------|
| xunit | Test framework |
| Moq | Mocking |
| FluentAssertions | Fluent assertion syntax |
| coverlet.collector | Code coverage |
| Microsoft.NET.Test.Sdk | VS Test platform |
| xunit.runner.visualstudio | IDE test discovery |
| Anthropic.SDK | Anthropic client types for factory tests |
| Microsoft.Extensions.Configuration.UserSecrets | User secrets for integration tests |
