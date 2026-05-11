using Domain.AI.DriftDetection;
using FluentAssertions;
using Infrastructure.AI.DriftDetection;
using Xunit;

namespace Infrastructure.AI.Tests.DriftDetection;

public sealed class InMemoryDriftBaselineStoreTests
{
    private static DriftBaseline CreateBaseline(
        DriftScope scope = DriftScope.Skill,
        string scopeIdentifier = "code_review",
        int sampleCount = 10) => new()
    {
        BaselineId = Guid.NewGuid(),
        Scope = scope,
        ScopeIdentifier = scopeIdentifier,
        Dimensions = new Dictionary<DriftDimension, double>
        {
            [DriftDimension.Faithfulness] = 0.85,
            [DriftDimension.Relevance] = 0.90
        }.AsReadOnly(),
        DimensionSigmas = new Dictionary<DriftDimension, double>
        {
            [DriftDimension.Faithfulness] = 0.05,
            [DriftDimension.Relevance] = 0.03
        }.AsReadOnly(),
        SampleCount = sampleCount,
        WindowStart = new DateTimeOffset(2026, 5, 1, 0, 0, 0, TimeSpan.Zero),
        WindowEnd = new DateTimeOffset(2026, 5, 10, 0, 0, 0, TimeSpan.Zero),
        CreatedAt = new DateTimeOffset(2026, 5, 10, 12, 0, 0, TimeSpan.Zero)
    };

    [Fact]
    public async Task SaveAndRetrieve_RoundTrips()
    {
        // Arrange
        var store = new InMemoryDriftBaselineStore();
        var baseline = CreateBaseline();

        // Act
        await store.SaveBaselineAsync(baseline, CancellationToken.None);
        var result = await store.GetBaselineAsync(DriftScope.Skill, "code_review", CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Value.Should().NotBeNull();
        result.Value!.BaselineId.Should().Be(baseline.BaselineId);
        result.Value.Scope.Should().Be(DriftScope.Skill);
        result.Value.ScopeIdentifier.Should().Be("code_review");
        result.Value.Dimensions.Should().BeEquivalentTo(baseline.Dimensions);
        result.Value.DimensionSigmas.Should().BeEquivalentTo(baseline.DimensionSigmas);
        result.Value.SampleCount.Should().Be(10);
    }

    [Fact]
    public async Task OverwriteExisting_ReplacesValue()
    {
        // Arrange
        var store = new InMemoryDriftBaselineStore();
        var v1 = CreateBaseline(sampleCount: 5);
        var v2 = CreateBaseline(sampleCount: 20);

        // Act
        await store.SaveBaselineAsync(v1, CancellationToken.None);
        await store.SaveBaselineAsync(v2, CancellationToken.None);
        var result = await store.GetBaselineAsync(DriftScope.Skill, "code_review", CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Value!.SampleCount.Should().Be(20);
    }

    [Fact]
    public async Task GetBaselines_FiltersByScope()
    {
        // Arrange
        var store = new InMemoryDriftBaselineStore();
        await store.SaveBaselineAsync(CreateBaseline(DriftScope.Skill, "a"), CancellationToken.None);
        await store.SaveBaselineAsync(CreateBaseline(DriftScope.Skill, "b"), CancellationToken.None);
        await store.SaveBaselineAsync(CreateBaseline(DriftScope.Agent, "agent-1"), CancellationToken.None);

        // Act
        var result = await store.GetBaselinesAsync(DriftScope.Skill, CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Value!.Should().HaveCount(2);
        result.Value.Should().OnlyContain(b => b.Scope == DriftScope.Skill);
    }

    [Fact]
    public async Task GetBaseline_NotFound_ReturnsNull()
    {
        // Arrange
        var store = new InMemoryDriftBaselineStore();

        // Act
        var result = await store.GetBaselineAsync(DriftScope.Skill, "nonexistent", CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Value.Should().BeNull();
    }

    [Fact]
    public async Task GetBaselines_NullScope_ReturnsAll()
    {
        // Arrange
        var store = new InMemoryDriftBaselineStore();
        await store.SaveBaselineAsync(CreateBaseline(DriftScope.Skill, "a"), CancellationToken.None);
        await store.SaveBaselineAsync(CreateBaseline(DriftScope.Agent, "agent-1"), CancellationToken.None);
        await store.SaveBaselineAsync(CreateBaseline(DriftScope.TaskType, "summarization"), CancellationToken.None);

        // Act
        var result = await store.GetBaselinesAsync(null, CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Value!.Should().HaveCount(3);
    }
}
