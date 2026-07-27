using Application.AI.Common.Interfaces.Escalation;
using Application.Core.CQRS.Escalation;
using Domain.AI.Escalation;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Application.Core.Tests.CQRS.Escalation;

/// <summary>
/// Tests for <see cref="GetPendingEscalationsForApproverQueryHandler"/> — the handler delegates
/// the roster filter to the service and projects results to the wire-safe summary, dropping the
/// internal originating governance decision.
/// </summary>
public sealed class GetPendingEscalationsForApproverQueryHandlerTests
{
    private readonly Mock<IEscalationService> _service = new();
    private readonly GetPendingEscalationsForApproverQueryHandler _handler;

    public GetPendingEscalationsForApproverQueryHandlerTests()
    {
        _handler = new GetPendingEscalationsForApproverQueryHandler(
            _service.Object, NullLogger<GetPendingEscalationsForApproverQueryHandler>.Instance);
    }

    [Fact]
    public async Task Handle_PendingItems_ProjectsToSummaries()
    {
        var request = EscalationTestData.NewRequest(approvers: ["alice@contoso.com", "bob@contoso.com"]);
        _service.Setup(s => s.GetPendingEscalationsAsync("alice@contoso.com", It.IsAny<CancellationToken>()))
            .ReturnsAsync([request]);

        var result = await _handler.Handle(
            new GetPendingEscalationsForApproverQuery { ApproverName = "alice@contoso.com" },
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().HaveCount(1);
        var summary = result.Value![0];
        summary.EscalationId.Should().Be(request.EscalationId);
        summary.ToolName.Should().Be("file_system");
        summary.Approvers.Should().BeEquivalentTo("alice@contoso.com", "bob@contoso.com");
    }

    [Fact]
    public async Task Handle_PassesApproverNameToServiceVerbatim()
    {
        // The service owns roster matching (ApproverNames.Comparer); the handler must not
        // normalize or rewrite the identity on the way through.
        _service.Setup(s => s.GetPendingEscalationsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);

        await _handler.Handle(
            new GetPendingEscalationsForApproverQuery { ApproverName = "Alice@Contoso.COM" },
            CancellationToken.None);

        _service.Verify(
            s => s.GetPendingEscalationsAsync("Alice@Contoso.COM", It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task Handle_NoPendingItems_ReturnsEmptySuccess()
    {
        _service.Setup(s => s.GetPendingEscalationsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);

        var result = await _handler.Handle(
            new GetPendingEscalationsForApproverQuery { ApproverName = "nobody@contoso.com" },
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().BeEmpty();
    }
}
