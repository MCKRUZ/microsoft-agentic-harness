# Infrastructure.AI.KnowledgeGraph.Tests

## What This Tests

Unit tests for the **Infrastructure.AI.KnowledgeGraph** project — the knowledge graph infrastructure implementing in-memory graph storage, feedback-weighted retrieval scoring, cross-session memory (Remember/Recall/Forget), entity provenance stamping, LLM-based feedback detection, and multi-tenant knowledge isolation. Tests validate graph CRUD, score blending math, session cache behavior, scope enforcement, and provenance metadata.

## Test Organization

Files are organized by capability: `InMemory/` (graph store), `Feedback/` (feedback store, LLM detector), `Memory/` (session cache, memory service), `Provenance/` (stamping), `Retrieval/` (feedback-weighted scorer), and `Scoping/` (validator, tenant isolation). Naming convention: `MethodName_Scenario_ExpectedResult`.

## All Test Classes

| Test Class | What It Tests | Approx Test Count | Unit/Integration |
|------------|---------------|-------------------|------------------|
| `InMemoryGraphStoreTests` | Node/edge CRUD, triplet queries, bulk operations | 8 | Unit |
| `GraphFeedbackStoreTests` | Feedback weight storage: record, retrieve batch, decay | 5 | Unit |
| `LlmFeedbackDetectorTests` | LLM output analysis for implicit feedback signals | 4 | Unit |
| `InMemorySessionCacheTests` | Session-local fast cache: put, get, evict, TTL | 5 | Unit |
| `KnowledgeMemoryServiceTests` | Remember/Recall/Forget/Improve operations | 6 | Unit |
| `DefaultProvenanceStamperTests` | Provenance: source pipeline, task ID, timestamps | 4 | Unit |
| `FeedbackWeightedScorerTests` | Score blending: alpha weighting, re-sort, passthrough | 4 | Unit |
| `KnowledgeScopeValidatorTests` | Scope boundary enforcement (user/dataset/owner) | 4 | Unit |
| `TenantIsolatedGraphStoreTests` | Tenant isolation: cross-tenant read prevention | 5 | Unit |

## Testing Patterns and Example

Tests use Moq for graph store and feedback store interfaces, with configuration injected via `Mock<IOptionsMonitor<AppConfig>>`. Mathematical score blending is tested with precise expected values.

```csharp
[Fact]
public async Task BlendFeedback_WithHighWeightNode_BoostsScore()
{
    // Arrange — graph has a node linked to chunk "c1" with weight 1.0
    SetupGraphWithNodeForChunk("c1", "n1", ["c1"]);
    _feedbackStore
        .Setup(f => f.GetNodeWeightsBatchAsync(It.IsAny<IReadOnlyList<string>>(), default))
        .ReturnsAsync(new Dictionary<string, NodeFeedbackWeight>
        {
            ["n1"] = new() { NodeId = "n1", Weight = 1.0, UpdateCount = 5,
                             LastUpdatedAt = DateTimeOffset.UtcNow }
        });

    // Act — blend with alpha=0.3, original score=0.5
    var results = CreateRerankedResults(("c1", 0.5));
    var blended = await _scorer.BlendFeedbackAsync(results, "test");

    // Assert — adjusted = (1-0.3)*0.5 + 0.3*1.0 = 0.65
    blended[0].RerankScore.Should().BeApproximately(0.65, 0.01);
}
```

**Mocking pattern**: `Mock<IKnowledgeGraphStore>` for graph operations, `Mock<IFeedbackStore>` for weight retrieval, `Mock<IOptionsMonitor<AppConfig>>` for alpha/config values. Helper methods like `SetupGraphWithNodeForChunk()`, `SetAlpha()`, and `CreateRerankedResults()` encapsulate repetitive setup. `Mock.Of<ILogger<T>>()` for logging.

## How to Run

```bash
# All tests in this project
dotnet test src/Content/Tests/Infrastructure.AI.KnowledgeGraph.Tests/Infrastructure.AI.KnowledgeGraph.Tests.csproj

# With coverage
dotnet test src/Content/Tests/Infrastructure.AI.KnowledgeGraph.Tests/Infrastructure.AI.KnowledgeGraph.Tests.csproj --collect:"XPlat Code Coverage"

# Single class
dotnet test --filter "FullyQualifiedName~FeedbackWeightedScorerTests"

# Single test
dotnet test --filter "FullyQualifiedName~FeedbackWeightedScorerTests.BlendFeedback_WithHighWeightNode_BoostsScore"
```

## How to Add a New Test

1. Identify the knowledge graph class under `Infrastructure.AI.KnowledgeGraph`.
2. Create a file in the matching subfolder (e.g., `Retrieval/NewScorerTests.cs`).
3. Name: `{ClassName}Tests.cs`.
4. Skeleton:

```csharp
using Domain.AI.KnowledgeGraph.Models;
using FluentAssertions;
using Moq;
using Xunit;

namespace Infrastructure.AI.KnowledgeGraph.Tests.Retrieval;

public sealed class NewScorerTests
{
    private readonly Mock<IKnowledgeGraphStore> _graphStore = new();

    [Fact]
    public async Task Score_WithFeedback_AdjustsRanking()
    {
        // Arrange
        _graphStore
            .Setup(g => g.GetTripletsAsync(It.IsAny<IReadOnlyList<string>>(), default))
            .ReturnsAsync(Array.Empty<GraphTriplet>());

        var scorer = new NewScorer(_graphStore.Object);

        // Act
        var result = await scorer.ScoreAsync(input);

        // Assert
        result.Should().BeApproximately(expected, 0.01);
    }
}
```

5. Run: `dotnet test --filter "FullyQualifiedName~NewScorerTests"`

## Shared Helpers and Fixtures

No shared fixture files. Each test class defines private helper methods inline:
- `SetAlpha(double)` — configures feedback alpha in mocked options
- `SetupGraphWithNodeForChunk(...)` — wires graph store mock for specific chunks
- `CreateRerankedResults(...)` — builds `RerankedResult` lists with specified scores

## Dependencies

| Package | Purpose |
|---------|---------|
| xunit | Test framework |
| Moq | Mocking |
| FluentAssertions | Fluent assertion syntax |
| coverlet.collector | Code coverage |
| Microsoft.NET.Test.Sdk | VS Test platform |
| xunit.runner.visualstudio | IDE test discovery |
