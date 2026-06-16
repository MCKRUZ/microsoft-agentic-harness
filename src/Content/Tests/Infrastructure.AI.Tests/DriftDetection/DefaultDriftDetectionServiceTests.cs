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

public sealed class DefaultDriftDetectionServiceTests
{
    private readonly Mock<IDriftScorer> _scorerMock = new();
    private readonly Mock<IDriftBaselineStore> _baselineStoreMock = new();
    private readonly Mock<IDriftAuditStore> _auditStoreMock = new();
    private readonly Mock<IDriftNotifier> _notifierMock = new();
    private readonly Mock<IEscalationService> _escalationMock = new();
    private readonly Mock<IKnowledgeGraphStore> _graphStoreMock = new();
    private readonly Mock<ILogger<DefaultDriftDetectionService>> _loggerMock = new();
    private readonly FakeTimeProvider _timeProvider = new();

    private DefaultDriftDetectionService CreateService(
        bool enabled = true, bool escalationEnabled = true,
        double warnThreshold = 1.5, double alertThreshold = 2.5, double escalateThreshold = 3.0,
        int minSamples = 20, int baselineWindowDays = 7)
    {
        var appConfig = new AppConfig
        {
            AI = new AIConfig
            {
                DriftDetection = new DriftDetectionConfig
                {
                    Enabled = enabled,
                    EscalationEnabled = escalationEnabled,
                    EwmaLambda = 0.2,
                    ControlLimitWidth = 3.0,
                    WarnThresholdSigma = warnThreshold,
                    AlertThresholdSigma = alertThreshold,
                    EscalateThresholdSigma = escalateThreshold,
                    MinSamplesForBaseline = minSamples,
                    BaselineWindowDays = baselineWindowDays
                },
                // Drift-triggered escalations source their approver roster from here; without it
                // the service refuses to queue a phantom no-approver escalation (fixed finding).
                Changes = new ChangesConfig { DefaultApprovers = ["drift-approver"] }
            }
        };

        var optionsMonitor = Mock.Of<IOptionsMonitor<AppConfig>>(
            o => o.CurrentValue == appConfig);

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

    private static DriftBaseline CreateTestBaseline(
        DriftScope scope = DriftScope.Skill, string identifier = "code_review",
        Dictionary<DriftDimension, double>? means = null,
        Dictionary<DriftDimension, double>? sigmas = null)
    {
        means ??= new Dictionary<DriftDimension, double>
        {
            [DriftDimension.Faithfulness] = 0.8,
            [DriftDimension.Relevance] = 0.85,
            [DriftDimension.Coherence] = 0.9
        };
        sigmas ??= means.ToDictionary(kv => kv.Key, _ => 0.1);

        return new DriftBaseline
        {
            BaselineId = Guid.NewGuid(),
            Scope = scope,
            ScopeIdentifier = identifier,
            Dimensions = means.AsReadOnly(),
            DimensionSigmas = sigmas.AsReadOnly(),
            SampleCount = 30,
            WindowStart = DateTimeOffset.UtcNow.AddDays(-7),
            WindowEnd = DateTimeOffset.UtcNow,
            CreatedAt = DateTimeOffset.UtcNow
        };
    }

    private static DriftEvaluationRequest CreateTestRequest(
        DriftScope scope = DriftScope.Skill, string identifier = "code_review",
        Dictionary<DriftDimension, double>? dimensions = null)
    {
        dimensions ??= new Dictionary<DriftDimension, double>
        {
            [DriftDimension.Faithfulness] = 0.75,
            [DriftDimension.Relevance] = 0.8,
            [DriftDimension.Coherence] = 0.85
        };

        return new DriftEvaluationRequest
        {
            Scope = scope,
            ScopeIdentifier = identifier,
            Dimensions = dimensions.AsReadOnly()
        };
    }

    private void SetupBaselineReturn(DriftScope scope, string identifier, DriftBaseline? baseline)
    {
        _baselineStoreMock
            .Setup(b => b.GetBaselineAsync(scope, identifier, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<DriftBaseline?>.Success(baseline));
    }

    private void SetupScorerReturns(double deviation)
    {
        _scorerMock
            .Setup(s => s.ScoreDimensionAsync(
                It.IsAny<DriftDimension>(), It.IsAny<double>(),
                It.IsAny<DriftBaseline>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((DriftDimension _, double currentValue, DriftBaseline baseline, CancellationToken _) =>
                Result<DriftDimensionScore>.Success(new DriftDimensionScore
                {
                    CurrentValue = currentValue,
                    BaselineValue = 0.8,
                    EwmaValue = 0.75,
                    Deviation = deviation
                }));
    }

    private void SetupAuditSuccess()
    {
        _auditStoreMock
            .Setup(a => a.RecordAsync(It.IsAny<DriftAuditRecord>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success());
    }

    private void SetupGraphSuccess()
    {
        _graphStoreMock
            .Setup(g => g.AddNodesAsync(It.IsAny<IReadOnlyList<GraphNode>>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _graphStoreMock
            .Setup(g => g.AddEdgesAsync(It.IsAny<IReadOnlyList<GraphEdge>>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
    }

    // ===== EvaluateDriftAsync Tests =====

    [Fact]
    public async Task EvaluateDrift_WithBaseline_ScoresAllDimensions()
    {
        var baseline = CreateTestBaseline();
        SetupBaselineReturn(DriftScope.Skill, "code_review", baseline);
        SetupScorerReturns(deviation: 1.0);
        SetupAuditSuccess();
        var service = CreateService();

        var result = await service.EvaluateDriftAsync(CreateTestRequest(), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value!.Dimensions.Should().HaveCount(3);
        _scorerMock.Verify(s => s.ScoreDimensionAsync(
            It.IsAny<DriftDimension>(), It.IsAny<double>(),
            baseline, It.IsAny<CancellationToken>()), Times.Exactly(3));
    }

    [Fact]
    public async Task EvaluateDrift_NoBaseline_ReturnsFailure()
    {
        SetupBaselineReturn(DriftScope.Skill, "code_review", null);
        SetupBaselineReturn(DriftScope.Agent, "code_review", null);
        var service = CreateService();

        var result = await service.EvaluateDriftAsync(CreateTestRequest(), CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Errors.Should().Contain(e => e.Contains("No baseline"));
    }

    [Fact]
    public async Task EvaluateDrift_BaselineFallback_SkillToAgent()
    {
        var agentBaseline = CreateTestBaseline(DriftScope.Agent, "code_review");
        SetupBaselineReturn(DriftScope.Skill, "code_review", null);
        SetupBaselineReturn(DriftScope.Agent, "code_review", agentBaseline);
        SetupScorerReturns(deviation: 1.0);
        SetupAuditSuccess();
        var service = CreateService();

        var result = await service.EvaluateDriftAsync(CreateTestRequest(), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        _baselineStoreMock.Verify(b => b.GetBaselineAsync(DriftScope.Skill, "code_review", It.IsAny<CancellationToken>()), Times.Once);
        _baselineStoreMock.Verify(b => b.GetBaselineAsync(DriftScope.Agent, "code_review", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task EvaluateDrift_SeverityWarn_EmitsNotification_NoEscalation()
    {
        SetupBaselineReturn(DriftScope.Skill, "code_review", CreateTestBaseline());
        SetupScorerReturns(deviation: 2.0);
        SetupAuditSuccess();
        SetupGraphSuccess();
        var service = CreateService();

        var result = await service.EvaluateDriftAsync(CreateTestRequest(), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value!.Severity.Should().Be(DriftSeverity.Warn);
        _notifierMock.Verify(n => n.NotifyDriftDetectedAsync(
            It.IsAny<DriftScore>(), It.IsAny<CancellationToken>()), Times.Once);
        _escalationMock.Verify(e => e.QueueEscalationAsync(
            It.IsAny<EscalationRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task EvaluateDrift_SeverityEscalate_TriggersEscalationService()
    {
        SetupBaselineReturn(DriftScope.Skill, "code_review", CreateTestBaseline());
        SetupScorerReturns(deviation: 3.5);
        SetupAuditSuccess();
        SetupGraphSuccess();
        _escalationMock
            .Setup(e => e.QueueEscalationAsync(It.IsAny<EscalationRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Guid.NewGuid());
        var service = CreateService();

        var result = await service.EvaluateDriftAsync(CreateTestRequest(), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value!.Severity.Should().Be(DriftSeverity.Escalate);
        _escalationMock.Verify(e => e.QueueEscalationAsync(
            It.Is<EscalationRequest>(r =>
                r.ToolName == "drift_detection" &&
                r.RiskLevel == RiskLevel.High),
            It.IsAny<CancellationToken>()), Times.Once);
        _notifierMock.Verify(n => n.NotifyDriftDetectedAsync(
            It.IsAny<DriftScore>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task EvaluateDrift_SeverityEscalate_EscalationDisabled_SkipsEscalation()
    {
        SetupBaselineReturn(DriftScope.Skill, "code_review", CreateTestBaseline());
        SetupScorerReturns(deviation: 3.5);
        SetupAuditSuccess();
        SetupGraphSuccess();
        var service = CreateService(escalationEnabled: false);

        var result = await service.EvaluateDriftAsync(CreateTestRequest(), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value!.Severity.Should().Be(DriftSeverity.Escalate);
        _escalationMock.Verify(e => e.QueueEscalationAsync(
            It.IsAny<EscalationRequest>(), It.IsAny<CancellationToken>()), Times.Never);
        _notifierMock.Verify(n => n.NotifyDriftDetectedAsync(
            It.IsAny<DriftScore>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task EvaluateDrift_RecordsAuditEntry()
    {
        SetupBaselineReturn(DriftScope.Skill, "code_review", CreateTestBaseline());
        SetupScorerReturns(deviation: 2.0);
        SetupAuditSuccess();
        SetupGraphSuccess();
        var service = CreateService();

        await service.EvaluateDriftAsync(CreateTestRequest(), CancellationToken.None);

        _auditStoreMock.Verify(a => a.RecordAsync(
            It.Is<DriftAuditRecord>(r => r.RecordType == DriftAuditRecordType.Detected),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task EvaluateDrift_SeverityWarn_CreatesGraphNode()
    {
        SetupBaselineReturn(DriftScope.Skill, "code_review", CreateTestBaseline());
        SetupScorerReturns(deviation: 2.0);
        SetupAuditSuccess();
        SetupGraphSuccess();
        var service = CreateService();

        await service.EvaluateDriftAsync(CreateTestRequest(), CancellationToken.None);

        _graphStoreMock.Verify(g => g.AddNodesAsync(
            It.Is<IReadOnlyList<GraphNode>>(nodes =>
                nodes.Count == 1 && nodes[0].Type == "DriftEvent"),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task EvaluateDrift_OverallDrift_IsMaxDeviation()
    {
        var baseline = CreateTestBaseline();
        SetupBaselineReturn(DriftScope.Skill, "code_review", baseline);
        SetupAuditSuccess();

        var callCount = 0;
        double[] deviations = [1.0, 2.5, 1.8];
        _scorerMock
            .Setup(s => s.ScoreDimensionAsync(
                It.IsAny<DriftDimension>(), It.IsAny<double>(),
                It.IsAny<DriftBaseline>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                var idx = Interlocked.Increment(ref callCount) - 1;
                var dev = deviations[idx % deviations.Length];
                return Result<DriftDimensionScore>.Success(new DriftDimensionScore
                {
                    CurrentValue = 0.7,
                    BaselineValue = 0.8,
                    EwmaValue = 0.75,
                    Deviation = dev
                });
            });
        SetupGraphSuccess();

        var service = CreateService();
        var result = await service.EvaluateDriftAsync(CreateTestRequest(), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value!.OverallDrift.Should().Be(2.5);
    }

    [Fact]
    public async Task EvaluateDrift_Disabled_ReturnsSuccessNoOp()
    {
        var service = CreateService(enabled: false);

        var result = await service.EvaluateDriftAsync(CreateTestRequest(), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value!.Severity.Should().Be(DriftSeverity.None);
        _scorerMock.Verify(s => s.ScoreDimensionAsync(
            It.IsAny<DriftDimension>(), It.IsAny<double>(),
            It.IsAny<DriftBaseline>(), It.IsAny<CancellationToken>()), Times.Never);
        _notifierMock.Verify(n => n.NotifyDriftDetectedAsync(
            It.IsAny<DriftScore>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task EvaluateDrift_SeverityNone_NoNotification_PersistsGraphNode()
    {
        SetupBaselineReturn(DriftScope.Skill, "code_review", CreateTestBaseline());
        SetupScorerReturns(deviation: 0.5);
        SetupAuditSuccess();
        SetupGraphSuccess();
        var service = CreateService();

        var result = await service.EvaluateDriftAsync(CreateTestRequest(), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value!.Severity.Should().Be(DriftSeverity.None);
        // A healthy (severity-None) evaluation must NOT notify, but it MUST persist a DriftEvent
        // node: the baseline is recomputed from this history, so healthy samples have to be
        // recorded or the rolling baseline starves (the fixed solution-review finding).
        _notifierMock.Verify(n => n.NotifyDriftDetectedAsync(
            It.IsAny<DriftScore>(), It.IsAny<CancellationToken>()), Times.Never);
        _graphStoreMock.Verify(g => g.AddNodesAsync(
            It.IsAny<IReadOnlyList<GraphNode>>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task EvaluateDrift_AllDimensionsFail_ReturnsFailure()
    {
        SetupBaselineReturn(DriftScope.Skill, "code_review", CreateTestBaseline());
        _scorerMock
            .Setup(s => s.ScoreDimensionAsync(
                It.IsAny<DriftDimension>(), It.IsAny<double>(),
                It.IsAny<DriftBaseline>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<DriftDimensionScore>.Fail("scorer error"));
        var service = CreateService();

        var result = await service.EvaluateDriftAsync(CreateTestRequest(), CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Errors.Should().Contain(e => e.Contains("All dimensions failed"));
    }

    [Fact]
    public async Task EvaluateDrift_BaselineFallback_TaskTypeToSkillToAgent()
    {
        var agentBaseline = CreateTestBaseline(DriftScope.Agent, "code_review");
        SetupBaselineReturn(DriftScope.TaskType, "code_review", null);
        SetupBaselineReturn(DriftScope.Skill, "code_review", null);
        SetupBaselineReturn(DriftScope.Agent, "code_review", agentBaseline);
        SetupScorerReturns(deviation: 1.0);
        SetupAuditSuccess();
        var service = CreateService();

        var request = CreateTestRequest(scope: DriftScope.TaskType, identifier: "code_review");
        var result = await service.EvaluateDriftAsync(request, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        _baselineStoreMock.Verify(b => b.GetBaselineAsync(DriftScope.TaskType, "code_review", It.IsAny<CancellationToken>()), Times.Once);
        _baselineStoreMock.Verify(b => b.GetBaselineAsync(DriftScope.Skill, "code_review", It.IsAny<CancellationToken>()), Times.Once);
        _baselineStoreMock.Verify(b => b.GetBaselineAsync(DriftScope.Agent, "code_review", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task EvaluateDrift_GraphNodeHasOwnerIdAndBaselineId()
    {
        var baseline = CreateTestBaseline();
        SetupBaselineReturn(DriftScope.Skill, "code_review", baseline);
        SetupScorerReturns(deviation: 2.0);
        SetupAuditSuccess();
        SetupGraphSuccess();
        var service = CreateService();

        await service.EvaluateDriftAsync(CreateTestRequest(), CancellationToken.None);

        _graphStoreMock.Verify(g => g.AddNodesAsync(
            It.Is<IReadOnlyList<GraphNode>>(nodes =>
                nodes.Count == 1 &&
                nodes[0].OwnerId == "Skill:code_review" &&
                nodes[0].Properties.ContainsKey("BaselineId")),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    // ===== UpdateBaselineAsync =====

    [Fact]
    public async Task UpdateBaseline_InsufficientSamples_ReturnsFailure()
    {
        SetupGraphSuccess();
        _graphStoreMock
            .Setup(g => g.GetNodesByOwnerAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<GraphNode>().AsReadOnly());
        var service = CreateService(minSamples: 20);

        var request = new DriftBaselineUpdateRequest
        {
            Scope = DriftScope.Skill,
            ScopeIdentifier = "code_review"
        };

        var result = await service.UpdateBaselineAsync(request, CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Errors.Should().Contain(e => e.Contains("Insufficient samples"));
    }

    // ===== GetBaselineAsync =====

    [Fact]
    public async Task GetBaseline_DelegatesToStore()
    {
        var baseline = CreateTestBaseline();
        SetupBaselineReturn(DriftScope.Skill, "code_review", baseline);
        var service = CreateService();

        var result = await service.GetBaselineAsync(DriftScope.Skill, "code_review", CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Be(baseline);
    }

    // ===== GetDriftHistoryAsync =====

    [Fact]
    public async Task GetDriftHistory_UsesGetNodesByOwnerAsync()
    {
        _graphStoreMock
            .Setup(g => g.GetNodesByOwnerAsync("Skill:code_review", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<GraphNode>().AsReadOnly());
        var service = CreateService();

        var query = new DriftHistoryQuery
        {
            Scope = DriftScope.Skill,
            ScopeIdentifier = "code_review",
            Start = DateTimeOffset.UtcNow.AddDays(-7),
            End = DateTimeOffset.UtcNow
        };

        await service.GetDriftHistoryAsync(query, CancellationToken.None);

        _graphStoreMock.Verify(g => g.GetNodesByOwnerAsync("Skill:code_review", It.IsAny<CancellationToken>()), Times.Once);
        _graphStoreMock.Verify(g => g.GetAllNodesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }
}
