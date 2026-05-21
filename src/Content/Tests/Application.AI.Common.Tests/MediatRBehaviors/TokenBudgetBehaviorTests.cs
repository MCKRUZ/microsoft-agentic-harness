using Application.AI.Common.Exceptions;
using Application.AI.Common.Interfaces.AI;
using Application.AI.Common.Interfaces.MediatR;
using Application.AI.Common.MediatRBehaviors;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Application.AI.Common.Tests.MediatRBehaviors;

public sealed class TokenBudgetBehaviorTests
{
    private readonly Mock<ITokenBudgetTracker> _tracker = new();

    // -------------------------------------------------------------------------
    // Pass-through: non-token requests
    // -------------------------------------------------------------------------

    [Fact]
    public async Task Handle_NonTokenRequest_PassesThrough()
    {
        var sut = CreateBehavior<NonTokenRequest, string>();
        var called = false;

        var result = await sut.Handle(
            new NonTokenRequest(),
            () => { called = true; return Task.FromResult("ok"); },
            CancellationToken.None);

        result.Should().Be("ok");
        called.Should().BeTrue();
        _tracker.Verify(t => t.CanAfford(It.IsAny<int>()), Times.Never);
    }

    // -------------------------------------------------------------------------
    // Within budget
    // -------------------------------------------------------------------------

    [Fact]
    public async Task Handle_WithinBudget_PassesThrough()
    {
        _tracker.Setup(t => t.CanAfford(100)).Returns(true);
        _tracker.Setup(t => t.RemainingBudget).Returns(1_000);
        _tracker.Setup(t => t.TotalBudget).Returns(5_000);
        var sut = CreateBehavior<TokenRequest, string>();
        var called = false;

        var result = await sut.Handle(
            new TokenRequest(100),
            () => { called = true; return Task.FromResult("done"); },
            CancellationToken.None);

        result.Should().Be("done");
        called.Should().BeTrue();
    }

    [Fact]
    public async Task Handle_WithinBudget_DoesNotThrow()
    {
        _tracker.Setup(t => t.CanAfford(It.IsAny<int>())).Returns(true);
        var sut = CreateBehavior<TokenRequest, string>();

        var act = () => sut.Handle(
            new TokenRequest(50),
            () => Task.FromResult("ok"),
            CancellationToken.None);

        await act.Should().NotThrowAsync();
    }

    // -------------------------------------------------------------------------
    // Budget exceeded
    // -------------------------------------------------------------------------

    [Fact]
    public async Task Handle_ExceedsBudget_ThrowsTokenBudgetExceededException()
    {
        _tracker.Setup(t => t.CanAfford(5_000)).Returns(false);
        _tracker.Setup(t => t.RemainingBudget).Returns(100);
        var sut = CreateBehavior<TokenRequest, string>();

        var act = () => sut.Handle(
            new TokenRequest(5_000),
            () => Task.FromResult("unreachable"),
            CancellationToken.None);

        await act.Should().ThrowAsync<TokenBudgetExceededException>()
            .Where(e => e.RemainingBudget == 100 && e.RequestedTokens == 5_000);
    }

    [Fact]
    public async Task Handle_ExceedsBudget_HandlerNotCalled()
    {
        _tracker.Setup(t => t.CanAfford(It.IsAny<int>())).Returns(false);
        _tracker.Setup(t => t.RemainingBudget).Returns(0);
        var sut = CreateBehavior<TokenRequest, string>();
        var handlerCalled = false;

        var act = () => sut.Handle(
            new TokenRequest(1_000),
            () => { handlerCalled = true; return Task.FromResult("nope"); },
            CancellationToken.None);

        await act.Should().ThrowAsync<TokenBudgetExceededException>();
        handlerCalled.Should().BeFalse();
    }

    [Fact]
    public async Task Handle_ExceedsBudget_ExceptionMessageContainsBudgetValues()
    {
        _tracker.Setup(t => t.CanAfford(200)).Returns(false);
        _tracker.Setup(t => t.RemainingBudget).Returns(50);
        var sut = CreateBehavior<TokenRequest, string>();

        var ex = await Assert.ThrowsAsync<TokenBudgetExceededException>(() =>
            sut.Handle(new TokenRequest(200), () => Task.FromResult("x"), CancellationToken.None));

        ex.Message.Should().Contain("200");
        ex.Message.Should().Contain("50");
    }

    // -------------------------------------------------------------------------
    // Zero-budget edge cases
    // -------------------------------------------------------------------------

    [Fact]
    public async Task Handle_ZeroEstimatedCost_CanAffordIsConsulted()
    {
        _tracker.Setup(t => t.CanAfford(0)).Returns(true);
        _tracker.Setup(t => t.RemainingBudget).Returns(0);
        _tracker.Setup(t => t.TotalBudget).Returns(0);
        var sut = CreateBehavior<TokenRequest, string>();

        var result = await sut.Handle(
            new TokenRequest(0),
            () => Task.FromResult("zero"),
            CancellationToken.None);

        result.Should().Be("zero");
        _tracker.Verify(t => t.CanAfford(0), Times.Once);
    }

    // -------------------------------------------------------------------------
    // Helpers & test doubles
    // -------------------------------------------------------------------------

    private TokenBudgetBehavior<TRequest, TResponse> CreateBehavior<TRequest, TResponse>()
        where TRequest : notnull =>
        new(_tracker.Object, NullLogger<TokenBudgetBehavior<TRequest, TResponse>>.Instance);

    private sealed record NonTokenRequest;

    private sealed record TokenRequest(int EstimatedTokenCost) : IConsumesTokens;
}
