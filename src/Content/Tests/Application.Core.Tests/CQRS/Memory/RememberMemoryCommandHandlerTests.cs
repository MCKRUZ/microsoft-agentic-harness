using Application.AI.Common.Interfaces.KnowledgeGraph;
using Application.Core.CQRS.Memory;
using Domain.AI.KnowledgeGraph.Models;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Application.Core.Tests.CQRS.Memory;

/// <summary>
/// Tests for <see cref="RememberMemoryCommandHandler"/> — the handler must pass the fact through
/// to <see cref="IKnowledgeMemory.RememberAsync"/> unchanged and surface the gate's decision
/// honestly, including rejection (which is an expected outcome, not a failure).
/// </summary>
public sealed class RememberMemoryCommandHandlerTests
{
    private readonly Mock<IKnowledgeMemory> _memory = new();
    private readonly RememberMemoryCommandHandler _handler;

    public RememberMemoryCommandHandlerTests()
    {
        _handler = new RememberMemoryCommandHandler(
            _memory.Object, NullLogger<RememberMemoryCommandHandler>.Instance);
    }

    private static RememberMemoryCommand Command() => new()
    {
        Key = "favorite-color",
        Content = "The user's favorite color is blue",
        EntityType = "Preference"
    };

    [Fact]
    public async Task Handle_TrustedWrite_ReturnsPersistedOutcome()
    {
        _memory.Setup(m => m.RememberAsync("favorite-color", It.IsAny<string>(), "Preference", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MemoryWriteDecision { Persist = true, Trust = MemoryTrust.Trusted, Reason = "trusted" });

        var result = await _handler.Handle(Command(), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value!.Outcome.Should().Be(MemoryWriteOutcome.Persisted);
        result.Value.Reason.Should().Be("trusted");
        _memory.Verify(
            m => m.RememberAsync("favorite-color", "The user's favorite color is blue", "Preference", It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task Handle_QuarantinedWrite_ReturnsQuarantinedOutcome()
    {
        _memory.Setup(m => m.RememberAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MemoryWriteDecision { Persist = true, Trust = MemoryTrust.Untrusted, Reason = "quarantined: DirectOverride" });

        var result = await _handler.Handle(Command(), CancellationToken.None);

        result.IsSuccess.Should().BeTrue("quarantine is an expected, reportable outcome");
        result.Value!.Outcome.Should().Be(MemoryWriteOutcome.Quarantined);
        result.Value.Reason.Should().Be("quarantined: DirectOverride");
    }

    [Fact]
    public async Task Handle_RejectedWrite_ReturnsRejectedOutcome_AsSuccess()
    {
        _memory.Setup(m => m.RememberAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MemoryWriteDecision { Persist = false, Trust = MemoryTrust.Untrusted, Reason = "rejected: Critical" });

        var result = await _handler.Handle(Command(), CancellationToken.None);

        result.IsSuccess.Should().BeTrue("rejection must be reported honestly, not masked as a 500");
        result.Value!.Outcome.Should().Be(MemoryWriteOutcome.Rejected);
        result.Value.Reason.Should().Be("rejected: Critical");
    }
}
