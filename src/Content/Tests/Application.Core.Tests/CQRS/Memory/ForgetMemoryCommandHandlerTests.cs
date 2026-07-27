using Application.AI.Common.Interfaces.KnowledgeGraph;
using Application.Core.CQRS.Memory;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Application.Core.Tests.CQRS.Memory;

/// <summary>
/// Tests for <see cref="ForgetMemoryCommandHandler"/> — forget delegates to
/// <see cref="IKnowledgeMemory.ForgetAsync"/> and is idempotent: the underlying graph delete
/// no-ops for a missing node, so an unknown key still yields success.
/// </summary>
public sealed class ForgetMemoryCommandHandlerTests
{
    private readonly Mock<IKnowledgeMemory> _memory = new();
    private readonly ForgetMemoryCommandHandler _handler;

    public ForgetMemoryCommandHandlerTests()
    {
        _handler = new ForgetMemoryCommandHandler(
            _memory.Object, NullLogger<ForgetMemoryCommandHandler>.Instance);
    }

    [Fact]
    public async Task Handle_DelegatesToMemoryAndSucceeds()
    {
        var result = await _handler.Handle(
            new ForgetMemoryCommand { Key = "favorite-color" }, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        _memory.Verify(m => m.ForgetAsync("favorite-color", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Handle_UnknownKey_IsIdempotentSuccess()
    {
        // ForgetAsync completing without effect (missing node) is the contract for an unknown
        // key — the handler must report success because the desired end state already holds.
        _memory.Setup(m => m.ForgetAsync("never-written", It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var result = await _handler.Handle(
            new ForgetMemoryCommand { Key = "never-written" }, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
    }
}
