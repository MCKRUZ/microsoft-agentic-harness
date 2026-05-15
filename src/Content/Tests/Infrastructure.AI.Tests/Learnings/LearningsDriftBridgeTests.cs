using System.Text.Json;
using Application.AI.Common.Interfaces.DriftDetection;
using Application.AI.Common.Interfaces.KnowledgeGraph;
using Domain.AI.DriftDetection;
using Domain.AI.KnowledgeGraph.Models;
using Domain.AI.Learnings;
using Domain.Common;
using Domain.Common.Config;
using Domain.Common.Config.AI;
using Domain.Common.Config.AI.DriftDetection;
using Domain.Common.Config.AI.Learnings;
using FluentAssertions;
using Infrastructure.AI.Learnings;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Moq;
using Xunit;

namespace Infrastructure.AI.Tests.Learnings;

/// <summary>
/// Tests for <see cref="LearningsDriftBridge"/>.
/// Verifies that high-confidence learnings originating from drift events trigger
/// baseline adjustment and drift event resolution.
/// </summary>
public sealed class LearningsDriftBridgeTests
{
    private readonly Mock<IDriftDetectionService> _driftService = new();
    private readonly Mock<IDriftAuditStore> _auditStore = new();
    private readonly Mock<IKnowledgeGraphStore> _graphStore = new();
    private readonly FakeTimeProvider _timeProvider = new(new DateTimeOffset(2025, 6, 15, 10, 0, 0, TimeSpan.Zero));

    [Fact]
    public async Task CheckAndAdjustBaselineAsync_HighWeight_DriftSource_TriggersBaselineUpdate()
    {
        var learning = CreateLearning(
            sourceType: LearningSourceType.DriftDetection,
            sourceId: "evt-123",
            feedbackWeight: 0.85);
        SetupDriftEventNode("evt-123", DriftScope.Skill, "code_review");
        _driftService
            .Setup(s => s.UpdateBaselineAsync(It.IsAny<DriftBaselineUpdateRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<DriftBaseline>.Success(CreateBaseline(DriftScope.Skill, "code_review")));
        _auditStore
            .Setup(s => s.RecordAsync(It.IsAny<DriftAuditRecord>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success());
        var bridge = CreateBridge();

        var result = await bridge.CheckAndAdjustBaselineAsync(learning, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        _driftService.Verify(s => s.UpdateBaselineAsync(
            It.Is<DriftBaselineUpdateRequest>(r =>
                r.Scope == DriftScope.Skill &&
                r.ScopeIdentifier == "code_review"),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task CheckAndAdjustBaselineAsync_HighWeight_NonDriftSource_NoBaselineUpdate()
    {
        var learning = CreateLearning(
            sourceType: LearningSourceType.HumanCorrection,
            sourceId: "manual-1",
            feedbackWeight: 0.9);
        var bridge = CreateBridge();

        var result = await bridge.CheckAndAdjustBaselineAsync(learning, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        _driftService.Verify(
            s => s.UpdateBaselineAsync(It.IsAny<DriftBaselineUpdateRequest>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task CheckAndAdjustBaselineAsync_BelowThreshold_NoBaselineUpdate()
    {
        var learning = CreateLearning(
            sourceType: LearningSourceType.DriftDetection,
            sourceId: "evt-200",
            feedbackWeight: 0.5);
        var bridge = CreateBridge();

        var result = await bridge.CheckAndAdjustBaselineAsync(learning, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        _driftService.Verify(
            s => s.UpdateBaselineAsync(It.IsAny<DriftBaselineUpdateRequest>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task CheckAndAdjustBaselineAsync_BaselineAdjusted_RecordsDriftAuditEntry()
    {
        var learning = CreateLearning(
            sourceType: LearningSourceType.DriftDetection,
            sourceId: "evt-456",
            feedbackWeight: 0.85);
        SetupDriftEventNode("evt-456", DriftScope.Agent, "agent-1");
        SetupSuccessfulUpdate();
        var bridge = CreateBridge();

        await bridge.CheckAndAdjustBaselineAsync(learning, CancellationToken.None);

        _auditStore.Verify(s => s.RecordAsync(
            It.Is<DriftAuditRecord>(r =>
                r.RecordType == DriftAuditRecordType.BaselineUpdated &&
                r.RecordedAt == _timeProvider.GetUtcNow()),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task CheckAndAdjustBaselineAsync_BaselineAdjusted_ResolvesDriftEvent()
    {
        var learning = CreateLearning(
            sourceType: LearningSourceType.DriftDetection,
            sourceId: "evt-789",
            feedbackWeight: 0.85);
        SetupDriftEventNode("evt-789", DriftScope.Skill, "summarization");
        SetupSuccessfulUpdate();
        var bridge = CreateBridge();

        await bridge.CheckAndAdjustBaselineAsync(learning, CancellationToken.None);

        _graphStore.Verify(s => s.AddNodesAsync(
            It.Is<IReadOnlyList<GraphNode>>(nodes =>
                nodes.Count == 1 &&
                nodes[0].Properties.ContainsKey("Resolution") &&
                nodes[0].Properties["Resolution"].Contains("BaselineAdjusted")),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task CheckAndAdjustBaselineAsync_DriftDetectionDisabled_NoOp()
    {
        var learning = CreateLearning(
            sourceType: LearningSourceType.DriftDetection,
            sourceId: "evt-100",
            feedbackWeight: 0.85);
        var bridge = CreateBridge(driftEnabled: false);

        var result = await bridge.CheckAndAdjustBaselineAsync(learning, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        _driftService.Verify(
            s => s.UpdateBaselineAsync(It.IsAny<DriftBaselineUpdateRequest>(), It.IsAny<CancellationToken>()),
            Times.Never);
        _graphStore.Verify(
            s => s.GetNodeAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task CheckAndAdjustBaselineAsync_LearningsDisabled_NoOp()
    {
        var learning = CreateLearning(
            sourceType: LearningSourceType.DriftDetection,
            sourceId: "evt-101",
            feedbackWeight: 0.85);
        var bridge = CreateBridge(learningsEnabled: false);

        var result = await bridge.CheckAndAdjustBaselineAsync(learning, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        _driftService.Verify(
            s => s.UpdateBaselineAsync(It.IsAny<DriftBaselineUpdateRequest>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task CheckAndAdjustBaselineAsync_DriftEventNodeNotFound_LogsWarning_ReturnsSuccess()
    {
        var learning = CreateLearning(
            sourceType: LearningSourceType.DriftDetection,
            sourceId: "evt-pruned",
            feedbackWeight: 0.85);
        _graphStore
            .Setup(s => s.GetNodeAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((GraphNode?)null);
        var bridge = CreateBridge();

        var result = await bridge.CheckAndAdjustBaselineAsync(learning, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        _driftService.Verify(
            s => s.UpdateBaselineAsync(It.IsAny<DriftBaselineUpdateRequest>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task CheckAndAdjustBaselineAsync_UpdateBaselineFails_ReturnsFailure()
    {
        var learning = CreateLearning(
            sourceType: LearningSourceType.DriftDetection,
            sourceId: "evt-fail",
            feedbackWeight: 0.85);
        SetupDriftEventNode("evt-fail", DriftScope.TaskType, "task-1");
        _driftService
            .Setup(s => s.UpdateBaselineAsync(It.IsAny<DriftBaselineUpdateRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<DriftBaseline>.Fail("Baseline store unreachable"));
        var bridge = CreateBridge();

        var result = await bridge.CheckAndAdjustBaselineAsync(learning, CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Errors.Should().Contain(e => e.Contains("Baseline store unreachable"));
    }

    [Fact]
    public async Task CheckAndAdjustBaselineAsync_UsesTimeProviderForResolvedAt()
    {
        var learning = CreateLearning(
            sourceType: LearningSourceType.DriftDetection,
            sourceId: "evt-time",
            feedbackWeight: 0.85);
        SetupDriftEventNode("evt-time", DriftScope.Agent, "global-agent");
        SetupSuccessfulUpdate();
        var bridge = CreateBridge();

        await bridge.CheckAndAdjustBaselineAsync(learning, CancellationToken.None);

        _graphStore.Verify(s => s.AddNodesAsync(
            It.Is<IReadOnlyList<GraphNode>>(nodes =>
                nodes.Count == 1 &&
                nodes[0].Properties["Resolution"].Contains("2025-06-15")),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task CheckAndAdjustBaselineAsync_CorruptedNodeScope_ReturnsSuccess()
    {
        var learning = CreateLearning(
            sourceType: LearningSourceType.DriftDetection,
            sourceId: "evt-corrupt",
            feedbackWeight: 0.85);
        var corruptedNode = new GraphNode
        {
            Id = "driftevent:evt-corrupt",
            Name = "DriftEvent:corrupt",
            Type = "DriftEvent",
            Properties = new Dictionary<string, string>
            {
                ["EventId"] = "evt-corrupt",
                ["Scope"] = "INVALID_SCOPE_VALUE",
                ["ScopeIdentifier"] = "something"
            }.AsReadOnly()
        };
        _graphStore
            .Setup(s => s.GetNodeAsync("driftevent:evt-corrupt", It.IsAny<CancellationToken>()))
            .ReturnsAsync(corruptedNode);
        var bridge = CreateBridge();

        var result = await bridge.CheckAndAdjustBaselineAsync(learning, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        _driftService.Verify(
            s => s.UpdateBaselineAsync(It.IsAny<DriftBaselineUpdateRequest>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task CheckAndAdjustBaselineAsync_AlreadyResolved_Skips()
    {
        var learning = CreateLearning(
            sourceType: LearningSourceType.DriftDetection,
            sourceId: "evt-resolved",
            feedbackWeight: 0.9);
        SetupDriftEventNode("evt-resolved", DriftScope.Skill, "code_review", alreadyResolved: true);
        var bridge = CreateBridge();

        var result = await bridge.CheckAndAdjustBaselineAsync(learning, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        _driftService.Verify(
            s => s.UpdateBaselineAsync(It.IsAny<DriftBaselineUpdateRequest>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    private LearningsDriftBridge CreateBridge(
        bool driftEnabled = true,
        bool learningsEnabled = true,
        double threshold = 0.8) => new(
        _driftService.Object,
        _auditStore.Object,
        _graphStore.Object,
        CreateOptions(driftEnabled, learningsEnabled, threshold),
        _timeProvider,
        NullLogger<LearningsDriftBridge>.Instance);

    private void SetupDriftEventNode(
        string eventId,
        DriftScope scope,
        string scopeIdentifier,
        bool alreadyResolved = false)
    {
        var properties = new Dictionary<string, string>
        {
            ["EventId"] = eventId,
            ["ScoreId"] = Guid.NewGuid().ToString(),
            ["BaselineId"] = Guid.NewGuid().ToString(),
            ["Scope"] = scope.ToString(),
            ["ScopeIdentifier"] = scopeIdentifier,
            ["Severity"] = DriftSeverity.Alert.ToString(),
            ["OverallDrift"] = "2.5",
            ["ScoredAt"] = DateTimeOffset.UtcNow.ToString("o"),
            ["DimensionsJson"] = "{}"
        };

        if (alreadyResolved)
        {
            var resolution = new DriftResolution
            {
                ResolvedBy = DriftResolutionType.ManualDismissal,
                ResolutionId = "prior-resolution",
                ResolvedAt = DateTimeOffset.UtcNow.AddHours(-1)
            };
            properties["Resolution"] = JsonSerializer.Serialize(resolution);
        }

        var node = new GraphNode
        {
            Id = $"driftevent:{eventId}",
            Name = $"DriftEvent:{scope}:{scopeIdentifier}",
            Type = "DriftEvent",
            Properties = properties.AsReadOnly()
        };

        _graphStore
            .Setup(s => s.GetNodeAsync($"driftevent:{eventId}", It.IsAny<CancellationToken>()))
            .ReturnsAsync(node);
    }

    private void SetupSuccessfulUpdate()
    {
        _driftService
            .Setup(s => s.UpdateBaselineAsync(It.IsAny<DriftBaselineUpdateRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<DriftBaseline>.Success(CreateBaseline(DriftScope.Skill, "default")));
        _auditStore
            .Setup(s => s.RecordAsync(It.IsAny<DriftAuditRecord>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success());
    }

    private static DriftBaseline CreateBaseline(DriftScope scope, string identifier) => new()
    {
        BaselineId = Guid.NewGuid(),
        Scope = scope,
        ScopeIdentifier = identifier,
        Dimensions = new Dictionary<DriftDimension, double>().AsReadOnly(),
        DimensionSigmas = new Dictionary<DriftDimension, double>().AsReadOnly(),
        SampleCount = 20,
        WindowStart = DateTimeOffset.UtcNow.AddDays(-7),
        WindowEnd = DateTimeOffset.UtcNow,
        CreatedAt = DateTimeOffset.UtcNow
    };

    private static LearningEntry CreateLearning(
        LearningSourceType sourceType = LearningSourceType.DriftDetection,
        string sourceId = "evt-default",
        double feedbackWeight = 1.0) => new()
    {
        LearningId = Guid.NewGuid(),
        Category = LearningCategory.FactualCorrection,
        DecayClass = DecayClass.Permanent,
        Scope = new LearningScope { AgentId = "agent-1" },
        Content = "Test learning content",
        Source = new LearningSource
        {
            SourceType = sourceType,
            SourceId = sourceId,
            SourceDescription = "Test drift source"
        },
        Provenance = new LearningProvenance
        {
            OriginPipeline = "DriftDetection",
            OriginTask = "drift_detection",
            OriginTimestamp = DateTimeOffset.UtcNow,
            Confidence = 0.9
        },
        CreatedAt = DateTimeOffset.UtcNow,
        FeedbackWeight = feedbackWeight,
        UpdateCount = 3
    };

    private static IOptionsMonitor<AppConfig> CreateOptions(
        bool driftEnabled,
        bool learningsEnabled,
        double threshold)
    {
        var appConfig = new AppConfig
        {
            AI = new AIConfig
            {
                DriftDetection = new DriftDetectionConfig { Enabled = driftEnabled },
                Learnings = new LearningsConfig
                {
                    Enabled = learningsEnabled,
                    BaselineAdjustmentThreshold = threshold
                }
            }
        };
        var mock = new Mock<IOptionsMonitor<AppConfig>>();
        mock.Setup(m => m.CurrentValue).Returns(appConfig);
        return mock.Object;
    }
}
