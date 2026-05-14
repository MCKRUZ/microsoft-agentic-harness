using Domain.AI.Escalation;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Presentation.AgentHub.AgUi;
using Presentation.AgentHub.Notifications;
using Xunit;

namespace Presentation.AgentHub.Tests.Notifications;

/// <summary>
/// Tests for AgUiEscalationNotifier -- verifies correct domain-to-AG-UI
/// event translation and writer invocation.
/// </summary>
public class AgUiEscalationNotifierTests
{
    private readonly Mock<IAgUiEventWriterAccessor> _accessorMock = new();
    private readonly Mock<IAgUiEventWriter> _writerMock = new();
    private readonly AgUiEscalationNotifier _sut;

    public AgUiEscalationNotifierTests()
    {
        _accessorMock.Setup(a => a.Writer).Returns(_writerMock.Object);
        _writerMock
            .Setup(w => w.WriteAsync(It.IsAny<AgUiEvent>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _sut = new AgUiEscalationNotifier(
            _accessorMock.Object,
            NullLogger<AgUiEscalationNotifier>.Instance);
    }

    [Fact]
    public async Task NotifyEscalationRequestedAsync_WritesEscalationRequestedEvent()
    {
        var request = CreateRequest();

        await _sut.NotifyEscalationRequestedAsync(request, CancellationToken.None);

        _writerMock.Verify(
            w => w.WriteAsync(It.Is<EscalationRequestedEvent>(e =>
                e.EscalationId == request.EscalationId.ToString() &&
                e.AgentId == "test-agent" &&
                e.ToolName == "dangerous_tool" &&
                e.Priority == "Critical"),
            CancellationToken.None),
            Times.Once);
    }

    [Fact]
    public async Task NotifyEscalationResolvedAsync_WritesEscalationResolvedEvent()
    {
        var outcome = new EscalationOutcome
        {
            EscalationId = Guid.NewGuid(),
            IsApproved = true,
            Decisions =
            [
                new ApproverDecision
                {
                    ApproverName = "admin",
                    Approved = true,
                    Reason = "Looks good",
                    RespondedAt = DateTimeOffset.UtcNow,
                },
            ],
            ResolutionType = EscalationResolutionType.Approved,
            ResolvedAt = DateTimeOffset.UtcNow,
        };

        await _sut.NotifyEscalationResolvedAsync(outcome, CancellationToken.None);

        _writerMock.Verify(
            w => w.WriteAsync(It.Is<EscalationResolvedEvent>(e =>
                e.EscalationId == outcome.EscalationId.ToString() &&
                e.IsApproved == true &&
                e.ResolutionType == "Approved"),
            CancellationToken.None),
            Times.Once);
    }

    [Fact]
    public async Task NotifyEscalationExpiringAsync_WritesEscalationExpiringEvent()
    {
        var request = CreateRequest();
        var remaining = TimeSpan.FromSeconds(45);

        await _sut.NotifyEscalationExpiringAsync(request, remaining, CancellationToken.None);

        _writerMock.Verify(
            w => w.WriteAsync(It.Is<EscalationExpiringEvent>(e =>
                e.EscalationId == request.EscalationId.ToString() &&
                e.RemainingSeconds == 45),
            CancellationToken.None),
            Times.Once);
    }

    [Fact]
    public async Task NotifyEscalationExpiringAsync_NegativeRemaining_ClampsToZero()
    {
        var request = CreateRequest();
        var remaining = TimeSpan.FromSeconds(-5);

        await _sut.NotifyEscalationExpiringAsync(request, remaining, CancellationToken.None);

        _writerMock.Verify(
            w => w.WriteAsync(It.Is<EscalationExpiringEvent>(e =>
                e.RemainingSeconds == 0),
            CancellationToken.None),
            Times.Once);
    }

    [Fact]
    public async Task NotifyEscalationRequestedAsync_CancellationToken_PassedToWriter()
    {
        using var cts = new CancellationTokenSource();
        var request = CreateRequest();

        await _sut.NotifyEscalationRequestedAsync(request, cts.Token);

        _writerMock.Verify(
            w => w.WriteAsync(It.IsAny<EscalationRequestedEvent>(), cts.Token),
            Times.Once);
    }

    [Fact]
    public async Task NotifyEscalationRequestedAsync_WriterThrows_CatchesAndLogs()
    {
        _writerMock
            .Setup(w => w.WriteAsync(It.IsAny<AgUiEvent>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Stream closed"));

        var request = CreateRequest();

        var act = () => _sut.NotifyEscalationRequestedAsync(request, CancellationToken.None);

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task NotifyEscalationRequestedAsync_NoWriter_SilentlyReturns()
    {
        _accessorMock.Setup(a => a.Writer).Returns((IAgUiEventWriter?)null);
        var sut = new AgUiEscalationNotifier(
            _accessorMock.Object,
            NullLogger<AgUiEscalationNotifier>.Instance);

        var request = CreateRequest();

        await sut.NotifyEscalationRequestedAsync(request, CancellationToken.None);

        _writerMock.Verify(
            w => w.WriteAsync(It.IsAny<AgUiEvent>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    private static EscalationRequest CreateRequest() => new()
    {
        EscalationId = Guid.NewGuid(),
        AgentId = "test-agent",
        ToolName = "dangerous_tool",
        Arguments = new Dictionary<string, string> { ["path"] = "/etc/config" },
        Description = "Agent attempted dangerous operation",
        RiskLevel = RiskLevel.High,
        Priority = EscalationPriority.Critical,
        Approvers = ["admin@company.com"],
        TimeoutSeconds = 300,
        RequestedAt = DateTimeOffset.UtcNow,
    };
}
