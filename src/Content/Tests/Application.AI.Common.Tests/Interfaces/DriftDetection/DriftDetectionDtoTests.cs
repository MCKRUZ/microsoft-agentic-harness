using Application.AI.Common.Interfaces.DriftDetection;
using Domain.AI.DriftDetection;
using FluentAssertions;
using Xunit;

namespace Application.AI.Common.Tests.Interfaces.DriftDetection;

public sealed class DriftDetectionDtoTests
{
    [Fact]
    public void DriftEvaluationRequest_ValidRequest_ConstructsSuccessfully()
    {
        var request = new DriftEvaluationRequest
        {
            Scope = DriftScope.Skill,
            ScopeIdentifier = "code_review",
            Dimensions = new Dictionary<DriftDimension, double>
            {
                [DriftDimension.Faithfulness] = 0.85,
                [DriftDimension.Relevance] = 0.90
            }.AsReadOnly()
        };

        request.Scope.Should().Be(DriftScope.Skill);
        request.ScopeIdentifier.Should().Be("code_review");
        request.Dimensions.Should().HaveCount(2);
    }

    [Fact]
    public void DriftBaselineUpdateRequest_Construction_SetsProperties()
    {
        var request = new DriftBaselineUpdateRequest
        {
            Scope = DriftScope.Agent,
            ScopeIdentifier = "primary_agent"
        };

        request.Scope.Should().Be(DriftScope.Agent);
        request.ScopeIdentifier.Should().Be("primary_agent");
    }

    [Fact]
    public void EwmaState_Construction_WithScopeDimensionAndInitialValues()
    {
        var state = new EwmaState
        {
            Scope = DriftScope.Skill,
            ScopeIdentifier = "code_review",
            Dimension = DriftDimension.Faithfulness,
            CurrentEwma = 0.85,
            SampleCount = 10,
            LastUpdatedAt = DateTimeOffset.UtcNow
        };

        state.Scope.Should().Be(DriftScope.Skill);
        state.ScopeIdentifier.Should().Be("code_review");
        state.Dimension.Should().Be(DriftDimension.Faithfulness);
        state.CurrentEwma.Should().Be(0.85);
        state.SampleCount.Should().Be(10);
    }

    [Fact]
    public void EwmaState_DeterministicId_MatchesExpectedPattern()
    {
        var state = new EwmaState
        {
            Scope = DriftScope.Skill,
            ScopeIdentifier = "code_review",
            Dimension = DriftDimension.Faithfulness,
            CurrentEwma = 0.85,
            SampleCount = 10,
            LastUpdatedAt = DateTimeOffset.UtcNow
        };

        state.DeterministicId.Should().Be("ewma:Skill:code_review:Faithfulness");
    }

    [Fact]
    public void DriftHistoryQuery_Construction_SetsAllProperties()
    {
        var start = DateTimeOffset.UtcNow.AddDays(-7);
        var end = DateTimeOffset.UtcNow;

        var query = new DriftHistoryQuery
        {
            Scope = DriftScope.TaskType,
            ScopeIdentifier = "summarization",
            Start = start,
            End = end
        };

        query.Scope.Should().Be(DriftScope.TaskType);
        query.ScopeIdentifier.Should().Be("summarization");
        query.Start.Should().Be(start);
        query.End.Should().Be(end);
    }

    [Fact]
    public void DriftAuditQuery_OptionalFields_DefaultToNull()
    {
        var query = new DriftAuditQuery();

        query.Start.Should().BeNull();
        query.End.Should().BeNull();
        query.RecordType.Should().BeNull();
        query.EventId.Should().BeNull();
    }
}
