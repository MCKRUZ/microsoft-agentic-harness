# Domain.AI.Tests

## What This Tests

Unit tests for the **Domain.AI** project — pure domain models for the AI agent system including agent definitions, skill definitions, hook events, tool permissions, MCP entities, knowledge graph nodes, telemetry conventions, compaction models, context budgets, and governance decisions. These tests validate record construction, computed properties, default values, and enum completeness with zero external dependencies.

## Test Organization

Files mirror the domain model namespace structure: `Agents/`, `Skills/`, `Hooks/`, `Permissions/`, `Tools/`, `MCP/`, `KnowledgeGraph/`, `Telemetry/`, `Context/`, `Compaction/`, `Config/`, `Enums/`, `Governance/`, `Models/`, `Prompts/`, `A2A/`. Naming convention: `PropertyName_Scenario_ExpectedResult` for property tests, `MethodName_Scenario_ExpectedResult` for computed members.

## All Test Classes

| Test Class | What It Tests | Approx Test Count | Unit/Integration |
|------------|---------------|-------------------|------------------|
| `AgentCardTests` | A2A agent card model | 4 | Unit |
| `AgentDefinitionTests` | Agent definition record | 5 | Unit |
| `AgentManifestTests` | Agent manifest parsing/validation | 5 | Unit |
| `AgentMessageTests` | Agent message record properties | 4 | Unit |
| `AgentMessageTypeTests` | Message type enum completeness | 3 | Unit |
| `SkillReferenceTests` | Skill reference value object | 3 | Unit |
| `SubagentDefinitionTests` | Subagent definition model | 4 | Unit |
| `SubagentTypeTests` | Subagent type enum values | 2 | Unit |
| `CompactionBoundaryMessageTests` | Compaction boundary markers | 3 | Unit |
| `CompactionEnumTests` | Compaction strategy enums | 2 | Unit |
| `CompactionResultTests` | Compaction result model | 4 | Unit |
| `ConfigScopeTests` | Config scope boundaries | 3 | Unit |
| `DiscoveredConfigFileTests` | Config file discovery model | 3 | Unit |
| `BudgetAssessmentTests` | Context budget assessment | 4 | Unit |
| `ToolResultReferenceTests` | Tool result reference model | 3 | Unit |
| `GovernanceDecisionTests` | Governance decision record | 5 | Unit |
| `HookDefinitionTests` | Hook definition model | 4 | Unit |
| `HookEventTests` | Hook event types | 3 | Unit |
| `HookExecutionContextTests` | Hook execution context | 4 | Unit |
| `HookResultTests` | Hook result model | 3 | Unit |
| `HookTypeTests` | Hook type enum | 2 | Unit |
| `GraphNodeTests` | Knowledge graph node model | 4 | Unit |
| `KnowledgeScopeDescriptorTests` | Knowledge scope boundaries | 3 | Unit |
| `ProvenanceStampTests` | Provenance stamping model | 4 | Unit |
| `McpPromptTests` | MCP prompt model | 3 | Unit |
| `McpRequestContextTests` | MCP request context | 3 | Unit |
| `McpResourceTests` | MCP resource model | 3 | Unit |
| `AgentRunManifestTests` | Run manifest record | 4 | Unit |
| `ContentSafetyResultTests` | Content safety evaluation result | 4 | Unit |
| `FileSearchResultTests` | File search result model | 3 | Unit |
| `ToolResultTests` | Tool execution result model | 4 | Unit |
| `DenialRecordTests` | Permission denial tracking | 3 | Unit |
| `PermissionDecisionTests` | Allow/Deny/Ask decisions | 5 | Unit |
| `PermissionEnumTests` | Permission behavior type enum | 3 | Unit |
| `SafetyGateTests` | Safety gate model | 3 | Unit |
| `ToolPermissionRuleTests` | Rule construction and matching | 5 | Unit |
| `PromptCacheBreakReportTests` | Cache break report model | 3 | Unit |
| `PromptHashSnapshotTests` | Prompt hash tracking | 3 | Unit |
| `SystemPromptSectionTests` | System prompt section model | 4 | Unit |
| `SkillCacheStatisticsTests` | Cache hit/miss statistics | 3 | Unit |
| `SkillChangedEventArgsTests` | Skill change notification | 3 | Unit |
| `SkillDefinitionTests` | Skill defaults, computed properties | 20 | Unit |
| `SkillResourceTests` | Skill resource model | 3 | Unit |
| `SkillSourcesTests` | Skill source types | 3 | Unit |
| `TelemetryConventionsTests` | OTel attribute naming constants | 5 | Unit |
| `TokenAndToolConventionsTests` | Token/tool telemetry conventions | 4 | Unit |
| `ToolConcurrencyClassificationTests` | Tool concurrency classification | 3 | Unit |
| `ToolDeclarationTests` | Tool declaration model | 4 | Unit |
| `_SmokeTests` | Assembly load verification | 2 | Unit |

## Testing Patterns and Example

Domain tests are pure unit tests with no mocking — they instantiate domain records/models directly and assert property values, computed members, and factory method results.

```csharp
[Fact]
public void HasObjectives_WithContent_ReturnsTrue()
{
    // Arrange + Act — construct domain model with property set
    var skill = new SkillDefinition { Objectives = "Objective 1" };

    // Assert — verify computed property
    skill.HasObjectives.Should().BeTrue();
}

[Fact]
public void Defaults_Collections_AreEmpty()
{
    // Arrange + Act — construct with defaults
    var skill = new SkillDefinition();

    // Assert — all collections initialized empty (not null)
    skill.Tags.Should().BeEmpty();
    skill.Templates.Should().BeEmpty();
    skill.References.Should().BeEmpty();
    skill.Scripts.Should().BeEmpty();
    skill.Assets.Should().BeEmpty();
}
```

**Pattern**: No mocks needed — domain models are pure value objects. Tests validate defaults, computed properties (e.g., `HasObjectives`, `IsChild`, `HasToolRestrictions`), and factory methods (e.g., `PermissionDecision.Allow()`).

## How to Run

```bash
# All tests in this project
dotnet test src/Content/Tests/Domain.AI.Tests/Domain.AI.Tests.csproj

# With coverage
dotnet test src/Content/Tests/Domain.AI.Tests/Domain.AI.Tests.csproj --collect:"XPlat Code Coverage"

# Single class
dotnet test --filter "FullyQualifiedName~SkillDefinitionTests"

# Single test
dotnet test --filter "FullyQualifiedName~SkillDefinitionTests.HasObjectives_WithContent_ReturnsTrue"
```

## How to Add a New Test

1. Identify the domain model in `Domain.AI` to test.
2. Create a file in the matching subfolder (e.g., `Skills/NewModelTests.cs`).
3. Name: `{DomainModelName}Tests.cs`.
4. Skeleton:

```csharp
using Domain.AI.Skills;
using FluentAssertions;
using Xunit;

namespace Domain.AI.Tests.Skills;

public sealed class NewModelTests
{
    [Fact]
    public void PropertyName_DefaultValue_IsExpected()
    {
        var model = new NewModel();

        model.PropertyName.Should().BeNull();
    }

    [Fact]
    public void ComputedProperty_WithData_ReturnsTrue()
    {
        var model = new NewModel { Data = "value" };

        model.HasData.Should().BeTrue();
    }
}
```

5. Run: `dotnet test --filter "FullyQualifiedName~NewModelTests"`

## Shared Helpers and Fixtures

None — domain tests are fully self-contained. Each test class creates its own model instances inline.

## Dependencies

| Package | Purpose |
|---------|---------|
| xunit | Test framework |
| Moq | Mocking (available but rarely used in domain tests) |
| FluentAssertions | Fluent assertion syntax |
| coverlet.collector | Code coverage |
| Microsoft.NET.Test.Sdk | VS Test platform |
| xunit.runner.visualstudio | IDE test discovery |
