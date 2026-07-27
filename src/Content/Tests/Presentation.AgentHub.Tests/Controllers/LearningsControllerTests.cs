using Application.Core.CQRS.Learnings;
using Domain.AI.Learnings;
using Domain.Common;
using FluentAssertions;
using MediatR;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;
using Presentation.AgentHub.Controllers;
using Xunit;

namespace Presentation.AgentHub.Tests.Controllers;

/// <summary>
/// Direct controller unit tests — no WebApplicationFactory. Verifies the
/// <see cref="LearningsController"/>'s <c>Result</c> → MVC status-code mapping, the wire-safe
/// projection to <see cref="LearningRecallEntryDto"/> (internal and cross-user-identifying
/// fields must never reach the wire), and that the endpoint dispatches the correct query
/// through MediatR.
/// </summary>
/// <remarks>
/// Wire-level (auth, routing, role gate) coverage lives in
/// <see cref="LearningsControllerAuthorizationTests"/>; handler behavior is covered by the
/// Application.Core.Tests handler suite.
/// </remarks>
public sealed class LearningsControllerTests
{
    private readonly Mock<IMediator> _mediator = new();
    private readonly LearningsController _sut;

    public LearningsControllerTests()
    {
        _sut = new LearningsController(_mediator.Object);
    }

    [Fact]
    public async Task Recall_MatchingLearnings_ReturnsOkWithWireSafeProjectionAndPassesParameters()
    {
        var weighted = CreateWeighted();
        RecallLearningsQuery? captured = null;
        _mediator.Setup(m => m.Send(It.IsAny<RecallLearningsQuery>(), It.IsAny<CancellationToken>()))
            .Callback<IRequest<Result<IReadOnlyList<WeightedLearning>>>, CancellationToken>(
                (q, _) => captured = (RecallLearningsQuery)q)
            .ReturnsAsync(Result<IReadOnlyList<WeightedLearning>>.Success([weighted]));

        var result = await _sut.Recall("error handling", 7, CancellationToken.None);

        captured!.Context.Should().Be("error handling");
        captured.MaxResults.Should().Be(7);

        var ok = result.Should().BeOfType<OkObjectResult>().Subject;
        var entries = ok.Value.Should().BeAssignableTo<IReadOnlyList<LearningRecallEntryDto>>().Subject;
        entries.Should().HaveCount(1);

        var dto = entries[0];
        dto.LearningId.Should().Be(weighted.Learning.LearningId);
        dto.Content.Should().Be(weighted.Learning.Content);
        dto.Category.Should().Be("DomainKnowledge");
        dto.DecayClass.Should().Be("Stable");
        dto.Scope.IsGlobal.Should().BeTrue();
        dto.Scope.AgentId.Should().BeNull();
        dto.Scope.TeamId.Should().BeNull();
        dto.RelevanceScore.Should().Be(weighted.RelevanceScore);
        dto.FeedbackScore.Should().Be(weighted.FeedbackScore);
        dto.FreshnessScore.Should().Be(weighted.FreshnessScore);
        dto.FinalScore.Should().Be(weighted.FinalScore);
        dto.CreatedAt.Should().Be(weighted.Learning.CreatedAt);
        dto.LastReinforcedAt.Should().Be(weighted.Learning.LastReinforcedAt);
    }

    [Fact]
    public async Task Recall_NullContext_DispatchesEmptyContextWithDefaultMaxResults()
    {
        RecallLearningsQuery? captured = null;
        _mediator.Setup(m => m.Send(It.IsAny<RecallLearningsQuery>(), It.IsAny<CancellationToken>()))
            .Callback<IRequest<Result<IReadOnlyList<WeightedLearning>>>, CancellationToken>(
                (q, _) => captured = (RecallLearningsQuery)q)
            .ReturnsAsync(Result<IReadOnlyList<WeightedLearning>>.ValidationFailure(
                ["Context must not be empty."]));

        await _sut.Recall(null, cancellationToken: CancellationToken.None);

        captured!.Context.Should().Be(string.Empty,
            "a missing query parameter must reach the validator as an empty string, not crash binding");
        captured.MaxResults.Should().Be(10);
    }

    [Fact]
    public async Task Recall_ValidationFailure_Returns400()
    {
        _mediator.Setup(m => m.Send(It.IsAny<RecallLearningsQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<IReadOnlyList<WeightedLearning>>.ValidationFailure(
                ["Context must not be empty."]));

        var result = await _sut.Recall("", 10, CancellationToken.None);

        var problem = result.Should().BeOfType<ObjectResult>().Subject;
        problem.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
    }

    [Fact]
    public async Task Recall_GeneralFailure_Returns500WithGenericBody()
    {
        _mediator.Setup(m => m.Send(It.IsAny<RecallLearningsQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<IReadOnlyList<WeightedLearning>>.Fail(
                "Neo4j connection refused at 10.0.0.5"));

        var result = await _sut.Recall("anything", 10, CancellationToken.None);

        // Security guard: store exceptions can contain connection strings or internal endpoints.
        // Per the harness security rules, General failures must map to a generic body.
        var problem = result.Should().BeOfType<ObjectResult>().Subject;
        problem.StatusCode.Should().Be(StatusCodes.Status500InternalServerError);
        var details = problem.Value.Should().BeAssignableTo<ProblemDetails>().Subject;
        details.Detail.Should().NotContain("Neo4j",
            "raw store errors must not leak through FailureResponse on General failures");
        details.Detail.Should().NotContain("10.0.0.5",
            "internal endpoints must not leak through FailureResponse on General failures");
    }

    [Fact]
    public void WireShape_LearningRecallEntryDto_ExcludesInternalAndCrossUserFields()
    {
        // Pins the exclusion set documented on LearningRecallEntryDto: Source.SourceId can carry
        // a user session identifier, Provenance is internal pipeline metadata, and the remaining
        // fields are internal bookkeeping. If a property creeps back in, this test names it.
        var propertyNames = typeof(LearningRecallEntryDto)
            .GetProperties()
            .Select(p => p.Name);

        propertyNames.Should().BeEquivalentTo(
        [
            nameof(LearningRecallEntryDto.LearningId),
            nameof(LearningRecallEntryDto.Content),
            nameof(LearningRecallEntryDto.Category),
            nameof(LearningRecallEntryDto.DecayClass),
            nameof(LearningRecallEntryDto.Scope),
            nameof(LearningRecallEntryDto.RelevanceScore),
            nameof(LearningRecallEntryDto.FeedbackScore),
            nameof(LearningRecallEntryDto.FreshnessScore),
            nameof(LearningRecallEntryDto.FinalScore),
            nameof(LearningRecallEntryDto.CreatedAt),
            nameof(LearningRecallEntryDto.LastReinforcedAt),
        ]);

        // Recurse into the nested scope DTO: a field added there also reaches the wire, so it
        // gets the same pinning as the top-level shape.
        typeof(LearningScopeDto)
            .GetProperties()
            .Select(p => p.Name)
            .Should().BeEquivalentTo(
            [
                nameof(LearningScopeDto.AgentId),
                nameof(LearningScopeDto.TeamId),
                nameof(LearningScopeDto.IsGlobal),
            ]);

        // And pin that the wire shape's property types introduce no other DTO nesting — if a
        // future field embeds a new complex type, this fails and demands the same treatment.
        typeof(LearningRecallEntryDto)
            .GetProperties()
            .Select(p => Nullable.GetUnderlyingType(p.PropertyType) ?? p.PropertyType)
            .Where(t => !t.IsPrimitive && t != typeof(string) && t != typeof(Guid)
                        && t != typeof(DateTimeOffset) && t != typeof(decimal))
            .Should().BeEquivalentTo([typeof(LearningScopeDto)],
                "every nested wire type must be explicitly pinned by this guard");
    }

    private static WeightedLearning CreateWeighted() => new()
    {
        Learning = new LearningEntry
        {
            LearningId = Guid.NewGuid(),
            Category = LearningCategory.DomainKnowledge,
            DecayClass = DecayClass.Stable,
            Scope = new LearningScope { IsGlobal = true },
            Content = "Validate at system boundaries, trust internal code",
            Source = new LearningSource
            {
                SourceType = LearningSourceType.HumanCorrection,
                SourceId = "session-8f3a-user-identifying",
                SourceDescription = "User corrected validation approach"
            },
            Provenance = new LearningProvenance
            {
                OriginPipeline = "escalation_resolution",
                OriginTask = "human_review",
                OriginTimestamp = DateTimeOffset.UtcNow.AddDays(-3),
                Confidence = 0.9
            },
            CreatedAt = DateTimeOffset.UtcNow.AddDays(-3),
            LastReinforcedAt = DateTimeOffset.UtcNow.AddDays(-1)
        },
        RelevanceScore = 0.82,
        FeedbackScore = 1.15,
        FreshnessScore = 0.94,
        FinalScore = 0.87
    };
}
