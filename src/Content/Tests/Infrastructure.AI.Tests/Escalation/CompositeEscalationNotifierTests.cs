using Application.AI.Common.Interfaces.Escalation;
using Domain.AI.Escalation;
using Domain.AI.Governance;
using FluentAssertions;
using Infrastructure.AI.Escalation;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Infrastructure.AI.Tests.Escalation;

/// <summary>
/// Tests for <see cref="CompositeEscalationNotifier"/> fan-out behavior.
/// Verifies that notifications reach all registered channels and that
/// individual channel failures do not block other channels.
/// </summary>
public sealed class CompositeEscalationNotifierTests
{
    private readonly Mock<ILogger<CompositeEscalationNotifier>> _logger = new();

    [Fact]
    public async Task NotifyEscalationRequestedAsync_FansOutToAllChannels()
    {
        var channels = CreateMockChannels(3);
        var sut = CreateSut(channels);
        var request = CreateTestRequest();

        await sut.NotifyEscalationRequestedAsync(request, CancellationToken.None);

        foreach (var channel in channels)
        {
            channel.Verify(
                c => c.NotifyEscalationRequestedAsync(request, It.IsAny<CancellationToken>()),
                Times.Once);
        }
    }

    [Fact]
    public async Task NotifyEscalationResolvedAsync_FansOutToAllChannels()
    {
        var channels = CreateMockChannels(2);
        var sut = CreateSut(channels);
        var outcome = CreateTestOutcome(Guid.NewGuid());

        await sut.NotifyEscalationResolvedAsync(outcome, CancellationToken.None);

        foreach (var channel in channels)
        {
            channel.Verify(
                c => c.NotifyEscalationResolvedAsync(outcome, It.IsAny<CancellationToken>()),
                Times.Once);
        }
    }

    [Fact]
    public async Task NotifyEscalationExpiringAsync_FansOutToAllChannels()
    {
        var channels = CreateMockChannels(2);
        var sut = CreateSut(channels);
        var request = CreateTestRequest();
        var remaining = TimeSpan.FromMinutes(2);

        await sut.NotifyEscalationExpiringAsync(request, remaining, CancellationToken.None);

        foreach (var channel in channels)
        {
            channel.Verify(
                c => c.NotifyEscalationExpiringAsync(request, remaining, It.IsAny<CancellationToken>()),
                Times.Once);
        }
    }

    [Fact]
    public async Task NotifyEscalationRequestedAsync_ChannelFailure_DoesNotBlockOthers()
    {
        var channels = CreateMockChannels(3);
        channels[1]
            .Setup(c => c.NotifyEscalationRequestedAsync(It.IsAny<EscalationRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Slack is down"));

        var sut = CreateSut(channels);
        var request = CreateTestRequest();

        await sut.NotifyEscalationRequestedAsync(request, CancellationToken.None);

        channels[0].Verify(
            c => c.NotifyEscalationRequestedAsync(request, It.IsAny<CancellationToken>()),
            Times.Once);
        channels[2].Verify(
            c => c.NotifyEscalationRequestedAsync(request, It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task NotifyEscalationRequestedAsync_ChannelFailure_LogsWarning()
    {
        var channels = CreateMockChannels(1);
        channels[0]
            .Setup(c => c.NotifyEscalationRequestedAsync(It.IsAny<EscalationRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Channel down"));

        var sut = CreateSut(channels);

        await sut.NotifyEscalationRequestedAsync(CreateTestRequest(), CancellationToken.None);

        _logger.Verify(
            x => x.Log(
                LogLevel.Warning,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, t) => true),
                It.IsAny<Exception>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }

    [Fact]
    public async Task NoChannelsRegistered_CompletesSuccessfully()
    {
        var sut = CreateSut([]);
        var request = CreateTestRequest();
        var outcome = CreateTestOutcome(request.EscalationId);

        var act = async () =>
        {
            await sut.NotifyEscalationRequestedAsync(request, CancellationToken.None);
            await sut.NotifyEscalationResolvedAsync(outcome, CancellationToken.None);
            await sut.NotifyEscalationExpiringAsync(request, TimeSpan.FromMinutes(1), CancellationToken.None);
        };

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task NotifyEscalationRequestedAsync_AllChannelsFail_CompletesWithoutException()
    {
        var channels = CreateMockChannels(3);
        foreach (var channel in channels)
        {
            channel
                .Setup(c => c.NotifyEscalationRequestedAsync(It.IsAny<EscalationRequest>(), It.IsAny<CancellationToken>()))
                .ThrowsAsync(new InvalidOperationException("Channel down"));
        }

        var sut = CreateSut(channels);

        var act = () => sut.NotifyEscalationRequestedAsync(CreateTestRequest(), CancellationToken.None);

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task NotifyEscalationRequestedAsync_ForwardsCancellationToken()
    {
        using var cts = new CancellationTokenSource();
        var channels = CreateMockChannels(2);
        var sut = CreateSut(channels);
        var request = CreateTestRequest();

        await sut.NotifyEscalationRequestedAsync(request, cts.Token);

        foreach (var channel in channels)
        {
            channel.Verify(
                c => c.NotifyEscalationRequestedAsync(request, cts.Token),
                Times.Once);
        }
    }

    private CompositeEscalationNotifier CreateSut(IReadOnlyList<Mock<IEscalationNotificationChannel>> channels)
    {
        return new CompositeEscalationNotifier(
            channels.Select(c => c.Object),
            _logger.Object);
    }

    private static List<Mock<IEscalationNotificationChannel>> CreateMockChannels(int count)
    {
        return Enumerable.Range(0, count)
            .Select(_ => new Mock<IEscalationNotificationChannel>())
            .ToList();
    }

    private static EscalationRequest CreateTestRequest() => new()
    {
        EscalationId = Guid.NewGuid(),
        AgentId = "test-agent",
        ToolName = "dangerous_tool",
        Arguments = new Dictionary<string, string> { ["arg1"] = "value1" },
        Description = "Test escalation request",
        RiskLevel = RiskLevel.High,
        Priority = EscalationPriority.Blocking,
        Approvers = ["admin@test.com"],
        RequestedAt = DateTimeOffset.UtcNow
    };

    private static EscalationOutcome CreateTestOutcome(Guid escalationId) => new()
    {
        EscalationId = escalationId,
        IsApproved = true,
        Decisions = [new ApproverDecision
        {
            ApproverName = "admin@test.com",
            Approved = true,
            RespondedAt = DateTimeOffset.UtcNow
        }],
        ResolutionType = EscalationResolutionType.Approved,
        ResolvedAt = DateTimeOffset.UtcNow
    };
}
