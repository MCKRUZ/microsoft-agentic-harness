using Application.AI.Common.Interfaces.DriftDetection;
using Application.AI.Common.Interfaces.Escalation;
using Application.AI.Common.Interfaces.KnowledgeGraph;
using Domain.AI.DriftDetection;
using Domain.AI.Escalation;
using Domain.AI.KnowledgeGraph.Models;
using Domain.Common;
using Domain.Common.Config;
using Domain.Common.Config.AI;
using Domain.Common.Config.AI.DriftDetection;
using FluentAssertions;
using Infrastructure.AI.DriftDetection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Moq;
using Xunit;

namespace Infrastructure.AI.Tests.DriftDetection;

/// <summary>
/// Regression tests for the 2026-06-11 solution review fixes in
/// <see cref="DefaultDriftDetectionService"/>:
/// <list type="bullet">
///   <item>Finding 6 — healthy (severity None) evaluations must be persisted to the
///   drift-event graph so the rolling baseline is computed from a representative,
///   unbiased sample distribution rather than from anomalous samples only.</item>
///   <item>Finding 36 — a drift escalation must not be queued with an empty Approvers
///   list (which silently auto-denies after timeout); when approvers are configured
///   they must flow onto the queued <see cref="EscalationRequest"/>.</item>
/// </list>
/// </summary>
public sealed class DefaultDriftDetectionServiceSolutionReviewFixTests
{
    private readonly Mock<IDriftScorer> _scorerMock = new();
    private readonly Mock<IDriftBaselineStore> _baselineStoreMock = new();
    private readonly Mock<IDriftAuditStore> _auditStoreMock = new();
    private readonly Mock<IDriftNotifier> _notifierMock = new();
    private readonly Mock<IEscalationService> _escalationMock = new();
    private readonly Mock<IKnowledgeGraphStore> _graphStoreMock = new();
    private readonly Mock<ILogger<DefaultDriftDetectionService>> _loggerMock = new();
    private readonly FakeTimeProvider _timeProvider = new();

    private DefaultDriftDetectionService CreateService(IReadOnlyList<string>? approvers = null)
    {
        var appConfig = new AppConfig
        {
            AI = new AIConfig
            {
                DriftDetection = new DriftDetectionConfig
                {
                    Enabled = true,
                    EscalationEnabled = true,
                    EwmaLambda = 0.2,
                    ControlLimitWidth = 3.0,
                    WarnThresholdSigma = 1.5,
                    AlertThresholdSigma = 2.5,
                    EscalateThresholdSigma = 3.0,
                    MinSamplesForBaseline = 20,
                    BaselineWindowDays = 7
                }
            }
        };

        if (approvers is not null)
            appConfig.AI.Changes.DefaultApprovers = [.. approvers];

        var optionsMonitor = Mock.Of<IOptionsMonitor<AppConfig>>(o => o.CurrentValue == appConfig);

        return new DefaultDriftDetectionService(
            _scorerMock.Object,
            _baselineStoreMock.Object,
            _auditStoreMock.Object,
            _notifierMock.Object,
            _escalationMock.Object,
            _graphStoreMock.Object,
            optionsMonitor,
            _timeProvider,
            _loggerMock.Object);
    }

    private static DriftBaseline CreateBaseline()
    {
        var means = new Dictionary<DriftDimension, double>
        {
            [DriftDimension.Faithfulness] = 0.8,
            [DriftDimension.Relevance] = 0.85,
            [DriftDimension.Coherence] = 0.9
        };
        return new DriftBaseline
        {
            BaselineId = Guid.NewGuid(),
            Scope = DriftScope.Skill,
            ScopeIdentifier = "code_review",
            Dimensions = means.AsReadOnly(),
            DimensionSigmas = means.ToDictionary(kv => kv.Key, _ => 0.1).AsReadOnly(),
            SampleCount = 30,
            WindowStart = DateTimeOffset.UtcNow.AddDays(-7),
            WindowEnd = DateTimeOffset.UtcNow,
            CreatedAt = DateTimeOffset.UtcNow
        };
    }

    private static DriftEvaluationRequest CreateRequest()
    {
        var dimensions = new Dictionary<DriftDimension, double>
        {
            [DriftDimension.Faithfulness] = 0.79,
            [DriftDimension.Relevance] = 0.84,
            [DriftDimension.Coherence] = 0.89
        };
        return new DriftEvaluationRequest
        {
            Scope = DriftScope.Skill,
            ScopeIdentifier = "code_review",
            Dimensions = dimensions.AsReadOnly()
        };
    }

    private void SetupScorer(double deviation) =>
        _scorerMock
            .Setup(s => s.ScoreDimensionAsync(
                It.IsAny<DriftDimension>(), It.IsAny<double>(),
                It.IsAny<DriftBaseline>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((DriftDimension _, double currentValue, DriftBaseline _, CancellationToken _) =>
                Result<DriftDimensionScore>.Success(new DriftDimensionScore
                {
                    CurrentValue = currentValue,
                    BaselineValue = 0.8,
                    EwmaValue = 0.79,
                    Deviation = deviation
                }));

    private void SetupCommon()
    {
        _baselineStoreMock
            .Setup(b => b.GetBaselineAsync(DriftScope.Skill, "code_review", It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<DriftBaseline?>.Success(CreateBaseline()));
        _auditStoreMock
            .Setup(a => a.RecordAsync(It.IsAny<DriftAuditRecord>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success());
        _graphStoreMock
            .Setup(g => g.AddNodesAsync(It.IsAny<IReadOnlyList<GraphNode>>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
    }

    // ===== Finding 6: healthy evaluations are persisted to the graph =====

    [Fact]
    public async Task EvaluateDrift_SeverityNone_PersistsDriftEventNode()
    {
        SetupCommon();
        SetupScorer(deviation: 0.4); // below WarnThresholdSigma → severity None
        var service = CreateService();

        var result = await service.EvaluateDriftAsync(CreateRequest(), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value!.Severity.Should().Be(DriftSeverity.None);
        _graphStoreMock.Verify(g => g.AddNodesAsync(
            It.Is<IReadOnlyList<GraphNode>>(nodes =>
                nodes.Count == 1 &&
                nodes[0].Type == "DriftEvent" &&
                nodes[0].Properties["Severity"] == DriftSeverity.None.ToString()),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task EvaluateDrift_SeverityNone_DoesNotNotifyOrEscalate()
    {
        SetupCommon();
        SetupScorer(deviation: 0.4);
        var service = CreateService(approvers: ["lead@example.com"]);

        await service.EvaluateDriftAsync(CreateRequest(), CancellationToken.None);

        _notifierMock.Verify(n => n.NotifyDriftDetectedAsync(
            It.IsAny<DriftScore>(), It.IsAny<CancellationToken>()), Times.Never);
        _escalationMock.Verify(e => e.QueueEscalationAsync(
            It.IsAny<EscalationRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ===== Finding 36: escalation approvers =====

    [Fact]
    public async Task EvaluateDrift_SeverityEscalate_NoApproversConfigured_DoesNotQueueEscalation()
    {
        SetupCommon();
        SetupScorer(deviation: 3.5); // above EscalateThresholdSigma
        var service = CreateService(approvers: []); // empty roster

        var result = await service.EvaluateDriftAsync(CreateRequest(), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value!.Severity.Should().Be(DriftSeverity.Escalate);
        _escalationMock.Verify(e => e.QueueEscalationAsync(
            It.IsAny<EscalationRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task EvaluateDrift_SeverityEscalate_ApproversConfigured_QueuesWithApprovers()
    {
        SetupCommon();
        SetupScorer(deviation: 3.5);
        _escalationMock
            .Setup(e => e.QueueEscalationAsync(It.IsAny<EscalationRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Guid.NewGuid());
        var service = CreateService(approvers: ["lead@example.com", "oncall@example.com"]);

        var result = await service.EvaluateDriftAsync(CreateRequest(), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        _escalationMock.Verify(e => e.QueueEscalationAsync(
            It.Is<EscalationRequest>(r =>
                r.ToolName == "drift_detection" &&
                r.Approvers.Count == 2 &&
                r.Approvers.Contains("lead@example.com") &&
                r.Approvers.Contains("oncall@example.com")),
            It.IsAny<CancellationToken>()), Times.Once);
    }
}
