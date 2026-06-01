using Application.AI.Common.CQRS.Evaluation.GetPromptVersionComparison;
using Application.AI.Common.Evaluation.Interfaces;
using Application.AI.Common.Prompts.Interfaces;
using Application.AI.Common.Prompts.Models;
using Domain.AI.Prompts;
using Domain.Common;
using FluentAssertions;
using FluentValidation.TestHelper;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Application.AI.Common.Tests.CQRS.Evaluation;

public sealed class GetPromptVersionComparisonQueryHandlerTests
{
    private readonly Mock<IPromptUsageStore> _usageStore = new();
    private readonly Mock<IEvalRunStore> _evalStore = new();
    private readonly GetPromptVersionComparisonQueryHandler _sut;

    public GetPromptVersionComparisonQueryHandlerTests()
    {
        _sut = new GetPromptVersionComparisonQueryHandler(
            _usageStore.Object,
            _evalStore.Object,
            NullLogger<GetPromptVersionComparisonQueryHandler>.Instance);
    }

    private static PromptUsageRecord UsageRow(
        PromptVersion version,
        string caseId,
        string metricKey = "faithfulness") => new()
    {
        Descriptor = new PromptDescriptor
        {
            Name = "faithfulness-judge",
            Version = version,
            ContentHash = "h",
            Body = string.Empty,
        },
        CaseId = caseId,
        MetricKey = metricKey,
        TraceId = "t",
        SpanId = "s",
        RecordedAtUtc = DateTimeOffset.UtcNow,
    };

    [Fact]
    public async Task Aggregates_score_per_version_and_metric_key()
    {
        _usageStore.Setup(s => s.QueryByPromptNameAsync("faithfulness-judge", It.IsAny<CancellationToken>()))
            .ReturnsAsync([
                UsageRow(new PromptVersion(1, 0), "case-a"),
                UsageRow(new PromptVersion(1, 0), "case-b"),
                UsageRow(new PromptVersion(2, 0), "case-a"),
                UsageRow(new PromptVersion(2, 0), "case-b"),
            ]);

        _evalStore.Setup(s => s.GetLatestAggregatedScoresAsync(
                It.Is<IReadOnlyCollection<string>>(c => c.Contains("case-a") && c.Contains("case-b")),
                "faithfulness",
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<string, double>(StringComparer.Ordinal)
            {
                ["case-a"] = 0.8,
                ["case-b"] = 0.6,
            });

        var result = await _sut.Handle(
            new GetPromptVersionComparisonQuery { PromptName = "faithfulness-judge" },
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().HaveCount(2);

        // Newest version first per the ordering invariant.
        result.Value[0].Version.Should().Be(new PromptVersion(2, 0));
        result.Value[0].AverageScore.Should().BeApproximately(0.7, 0.0001);
        result.Value[0].SampleSize.Should().Be(2);

        result.Value[1].Version.Should().Be(new PromptVersion(1, 0));
        result.Value[1].AverageScore.Should().BeApproximately(0.7, 0.0001);
    }

    [Fact]
    public async Task Skips_usage_rows_without_metric_key()
    {
        _usageStore.Setup(s => s.QueryByPromptNameAsync("p", It.IsAny<CancellationToken>()))
            .ReturnsAsync([
                UsageRow(new PromptVersion(1, 0), "case-x") with { MetricKey = null },
            ]);

        var result = await _sut.Handle(
            new GetPromptVersionComparisonQuery { PromptName = "p" },
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().BeEmpty();
        _evalStore.Verify(s => s.GetLatestAggregatedScoresAsync(
            It.IsAny<IReadOnlyCollection<string>>(),
            It.IsAny<string>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Emits_zero_sample_row_when_no_scores_match()
    {
        _usageStore.Setup(s => s.QueryByPromptNameAsync("p", It.IsAny<CancellationToken>()))
            .ReturnsAsync([UsageRow(new PromptVersion(1, 0), "case-a")]);

        _evalStore.Setup(s => s.GetLatestAggregatedScoresAsync(
                It.IsAny<IReadOnlyCollection<string>>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<string, double>(StringComparer.Ordinal));

        var result = await _sut.Handle(
            new GetPromptVersionComparisonQuery { PromptName = "p" },
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().ContainSingle();
        result.Value[0].SampleSize.Should().Be(0);
        result.Value[0].AverageScore.Should().Be(0.0);
    }

    [Fact]
    public async Task Translates_usage_store_failure_to_Fail()
    {
        _usageStore.Setup(s => s.QueryByPromptNameAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("boom"));

        var result = await _sut.Handle(
            new GetPromptVersionComparisonQuery { PromptName = "p" },
            CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.FailureType.Should().Be(ResultFailureType.General);
    }
}

public sealed class GetPromptVersionComparisonQueryValidatorTests
{
    private readonly GetPromptVersionComparisonQueryValidator _sut = new();

    [Fact]
    public void Valid_query_has_no_errors()
        => _sut.TestValidate(new GetPromptVersionComparisonQuery { PromptName = "p" })
            .ShouldNotHaveAnyValidationErrors();

    [Fact]
    public void Empty_PromptName_fails()
        => _sut.TestValidate(new GetPromptVersionComparisonQuery { PromptName = "" })
            .ShouldHaveValidationErrorFor(q => q.PromptName);
}
