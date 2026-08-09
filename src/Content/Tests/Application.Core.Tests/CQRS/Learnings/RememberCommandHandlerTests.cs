using Application.AI.Common.Interfaces.KnowledgeGraph;
using Application.AI.Common.Interfaces.Learnings;
using Application.Core.CQRS.Learnings;
using Domain.AI.KnowledgeGraph.Models;
using Domain.AI.Learnings;
using Domain.Common;
using Domain.Common.Config;
using Domain.Common.Config.AI;
using Domain.Common.Config.AI.Learnings;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Moq;
using Xunit;

namespace Application.Core.Tests.CQRS.Learnings;

/// <summary>
/// Tests for <see cref="RememberCommandHandler"/>.
/// </summary>
public sealed class RememberCommandHandlerTests
{
    private readonly Mock<ILearningsStore> _store = new();
    private readonly Mock<ILearningNotificationChannel> _notifications = new();
    private readonly Mock<IMemoryWriteGate> _writeGate = new();
    private readonly FakeTimeProvider _timeProvider = new(new DateTimeOffset(2026, 5, 14, 12, 0, 0, TimeSpan.Zero));
    private readonly RememberCommandHandler _handler;
    private readonly LearningsConfig _learningsConfig = new();

    public RememberCommandHandlerTests()
    {
        _store.Setup(s => s.SaveAsync(It.IsAny<LearningEntry>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success());

        GateReturns(MemoryWriteDecision.Allow());

        _handler = CreateHandler();
    }

    [Fact]
    public async Task Handle_ValidInput_SavesLearningToStore()
    {
        var command = CreateCommand();
        LearningEntry? savedEntry = null;
        _store.Setup(s => s.SaveAsync(It.IsAny<LearningEntry>(), It.IsAny<CancellationToken>()))
            .Callback<LearningEntry, CancellationToken>((e, _) => savedEntry = e)
            .ReturnsAsync(Result.Success());

        await _handler.Handle(command, CancellationToken.None);

        savedEntry.Should().NotBeNull();
        savedEntry!.Content.Should().Be(command.Content);
        savedEntry.Category.Should().Be(command.Category);
        savedEntry.Scope.Should().Be(command.Scope);
        savedEntry.Source.Should().Be(command.Source);
        savedEntry.Provenance.Should().Be(command.Provenance);
        savedEntry.FeedbackWeight.Should().Be(1.0);
        savedEntry.UpdateCount.Should().Be(0);
    }

    [Fact]
    public async Task Handle_FactualCorrection_SetsPermanentDecay()
    {
        var command = CreateCommand(category: LearningCategory.FactualCorrection);
        LearningEntry? saved = null;
        _store.Setup(s => s.SaveAsync(It.IsAny<LearningEntry>(), It.IsAny<CancellationToken>()))
            .Callback<LearningEntry, CancellationToken>((e, _) => saved = e)
            .ReturnsAsync(Result.Success());

        await _handler.Handle(command, CancellationToken.None);

        saved!.DecayClass.Should().Be(DecayClass.Permanent);
    }

    [Fact]
    public async Task Handle_StylePreference_SetsStableDecay()
    {
        var command = CreateCommand(category: LearningCategory.StylePreference);
        LearningEntry? saved = null;
        _store.Setup(s => s.SaveAsync(It.IsAny<LearningEntry>(), It.IsAny<CancellationToken>()))
            .Callback<LearningEntry, CancellationToken>((e, _) => saved = e)
            .ReturnsAsync(Result.Success());

        await _handler.Handle(command, CancellationToken.None);

        saved!.DecayClass.Should().Be(DecayClass.Stable);
    }

    [Fact]
    public async Task Handle_ExplicitDecayClass_OverridesDefault()
    {
        var command = CreateCommand(category: LearningCategory.FactualCorrection, decayClass: DecayClass.Volatile);
        LearningEntry? saved = null;
        _store.Setup(s => s.SaveAsync(It.IsAny<LearningEntry>(), It.IsAny<CancellationToken>()))
            .Callback<LearningEntry, CancellationToken>((e, _) => saved = e)
            .ReturnsAsync(Result.Success());

        await _handler.Handle(command, CancellationToken.None);

        saved!.DecayClass.Should().Be(DecayClass.Volatile);
    }

    [Fact]
    public async Task Handle_ValidInput_EmitsLearningCapturedNotification()
    {
        var command = CreateCommand();

        await _handler.Handle(command, CancellationToken.None);

        _notifications.Verify(
            n => n.NotifyLearningCapturedAsync(It.IsAny<LearningEntry>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task Handle_ValidInput_ReturnsSuccessWithEntry()
    {
        var command = CreateCommand();

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().NotBeNull();
        result.Value!.Content.Should().Be(command.Content);
    }

    [Fact]
    public async Task Handle_Disabled_ReturnsSuccessNoOp()
    {
        var handler = CreateHandler(new LearningsConfig { Enabled = false });
        var command = CreateCommand();

        var result = await handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        _store.Verify(s => s.SaveAsync(It.IsAny<LearningEntry>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Handle_UsesTimeProviderForTimestamps()
    {
        var command = CreateCommand();
        LearningEntry? saved = null;
        _store.Setup(s => s.SaveAsync(It.IsAny<LearningEntry>(), It.IsAny<CancellationToken>()))
            .Callback<LearningEntry, CancellationToken>((e, _) => saved = e)
            .ReturnsAsync(Result.Success());

        await _handler.Handle(command, CancellationToken.None);

        saved!.CreatedAt.Should().Be(_timeProvider.GetUtcNow());
    }

    // --- Memory write gate (issue #338) -------------------------------------------------------
    // The learnings channel persists model-derived text that LearningsRecallContextProvider later
    // replays into the instruction channel of a future turn. These assert the handler consults the
    // gate and applies its verdict; the gate's own classification ladder is proven separately by
    // ProvenanceMemoryWriteGateTests, and the end-to-end write-to-recall path by
    // LearningsChannelMemoryPoisoningTests.

    [Fact]
    public async Task Handle_Rejected_IsNotPersistedAndReportsFailure()
    {
        GateReturns(new MemoryWriteDecision
        {
            Persist = false,
            Trust = MemoryTrust.Untrusted,
            Reason = "rejected: Critical/DirectOverride"
        });

        var result = await _handler.Handle(CreateCommand(), CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        _store.Verify(s => s.SaveAsync(It.IsAny<LearningEntry>(), It.IsAny<CancellationToken>()), Times.Never);
        _notifications.Verify(
            n => n.NotifyLearningCapturedAsync(It.IsAny<LearningEntry>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task Handle_Rejected_DoesNotLeakTheGateReasonToCallers()
    {
        // The gate's reason names the injection classification and the result travels to callers
        // that log it, so the wire-visible failure is a stable scrubbed code instead.
        GateReturns(new MemoryWriteDecision
        {
            Persist = false,
            Trust = MemoryTrust.Untrusted,
            Reason = "rejected: Critical/DirectOverride"
        });

        var result = await _handler.Handle(CreateCommand(), CancellationToken.None);

        result.Errors.Should().ContainSingle().Which.Should().Be("learnings.write_rejected");
    }

    [Fact]
    public async Task Handle_Quarantined_IsPersistedButMarkedUntrusted()
    {
        // Quarantine is persist-and-withhold, not drop: the entry stays available for incident
        // response while IsRecallable keeps it out of every agent turn.
        GateReturns(new MemoryWriteDecision
        {
            Persist = true,
            Trust = MemoryTrust.Untrusted,
            Reason = "quarantined: injection/DirectOverride"
        });
        LearningEntry? saved = null;
        _store.Setup(s => s.SaveAsync(It.IsAny<LearningEntry>(), It.IsAny<CancellationToken>()))
            .Callback<LearningEntry, CancellationToken>((e, _) => saved = e)
            .ReturnsAsync(Result.Success());

        var result = await _handler.Handle(CreateCommand(), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        saved.Should().NotBeNull();
        saved!.Trust.Should().Be(MemoryTrust.Untrusted);
        saved.IsRecallable().Should().BeFalse();
    }

    [Fact]
    public async Task Handle_Allowed_IsPersistedAsTrusted()
    {
        LearningEntry? saved = null;
        _store.Setup(s => s.SaveAsync(It.IsAny<LearningEntry>(), It.IsAny<CancellationToken>()))
            .Callback<LearningEntry, CancellationToken>((e, _) => saved = e)
            .ReturnsAsync(Result.Success());

        await _handler.Handle(CreateCommand(), CancellationToken.None);

        saved!.Trust.Should().Be(MemoryTrust.Trusted);
        saved.IsRecallable().Should().BeTrue();
    }

    [Fact]
    public async Task Handle_GatesTheContentThatIsActuallyStored()
    {
        // Guards against the gate being handed a summary, a truncation, or the wrong field: the
        // classified string must be the one that ends up in the store.
        var command = CreateCommand();
        LearningEntry? saved = null;
        _store.Setup(s => s.SaveAsync(It.IsAny<LearningEntry>(), It.IsAny<CancellationToken>()))
            .Callback<LearningEntry, CancellationToken>((e, _) => saved = e)
            .ReturnsAsync(Result.Success());

        await _handler.Handle(command, CancellationToken.None);

        _writeGate.Verify(
            g => g.EvaluateAsync(It.IsAny<string>(), command.Content, It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Once);
        saved!.Content.Should().Be(command.Content);
    }

    [Fact]
    public async Task Handle_Disabled_DoesNotReachTheGate()
    {
        // The disabled path returns a placeholder without persisting anything, so there is nothing
        // to classify — and calling the gate would bill an unnecessary scan on every no-op write.
        var handler = CreateHandler(new LearningsConfig { Enabled = false });

        await handler.Handle(CreateCommand(), CancellationToken.None);

        _writeGate.Verify(
            g => g.EvaluateAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public void Constructor_NullGate_Throws()
    {
        // An optional gate would make "nobody registered it" indistinguishable from "it allowed the
        // write" — the exact shape this handler had before the gate existed.
        var construct = () => new RememberCommandHandler(
            _store.Object,
            _notifications.Object,
            writeGate: null!,
            CreateOptions(_learningsConfig),
            _timeProvider,
            NullLogger<RememberCommandHandler>.Instance);

        construct.Should().Throw<ArgumentNullException>();
    }

    private void GateReturns(MemoryWriteDecision decision) =>
        _writeGate.Setup(g => g.EvaluateAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(decision);

    private RememberCommandHandler CreateHandler(LearningsConfig? config = null) => new(
        _store.Object,
        _notifications.Object,
        _writeGate.Object,
        CreateOptions(config ?? _learningsConfig),
        _timeProvider,
        NullLogger<RememberCommandHandler>.Instance);

    private static RememberCommand CreateCommand(
        LearningCategory category = LearningCategory.FactualCorrection,
        DecayClass? decayClass = null) => new()
    {
        Content = "Test learning content",
        Category = category,
        Scope = new LearningScope { AgentId = "agent-1" },
        Source = new LearningSource
        {
            SourceType = LearningSourceType.HumanCorrection,
            SourceId = "test-source-1",
            SourceDescription = "Test correction"
        },
        Provenance = new LearningProvenance
        {
            OriginPipeline = "test_pipeline",
            OriginTask = "test_task",
            OriginTimestamp = DateTimeOffset.UtcNow,
            Confidence = 0.95
        },
        DecayClass = decayClass
    };

    private static IOptionsMonitor<AppConfig> CreateOptions(LearningsConfig config)
    {
        var appConfig = new AppConfig { AI = new AIConfig { Learnings = config } };
        var mock = new Mock<IOptionsMonitor<AppConfig>>();
        mock.Setup(m => m.CurrentValue).Returns(appConfig);
        return mock.Object;
    }
}
