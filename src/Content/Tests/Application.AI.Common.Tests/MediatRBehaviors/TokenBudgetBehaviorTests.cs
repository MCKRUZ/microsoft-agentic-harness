using Application.AI.Common.Exceptions;
using Application.AI.Common.Interfaces.AI;
using Application.AI.Common.Interfaces.MediatR;
using Application.AI.Common.MediatRBehaviors;
using Application.AI.Common.Services.AI;
using Domain.Common.Config;
using Domain.Common.Config.AI;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
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
    // Live tracker arithmetic — the real TokenBudgetTracker seeded from config
    // -------------------------------------------------------------------------

    [Fact]
    public void Tracker_SeededFromConfig_ExposesTotalAndRemaining()
    {
        var tracker = BuildTracker(5_000);

        tracker.TotalBudget.Should().Be(5_000);
        tracker.RemainingBudget.Should().Be(5_000);
    }

    [Fact]
    public void RecordUsage_DecrementsRemainingBudget_ByTokensUsed()
    {
        var tracker = BuildTracker(5_000);

        tracker.RecordUsage(1_200);

        tracker.RemainingBudget.Should().Be(3_800);
    }

    [Fact]
    public void CanAfford_EstimateExceedsRemaining_ReturnsFalse()
    {
        var tracker = BuildTracker(1_000);
        tracker.RecordUsage(800);

        tracker.CanAfford(500).Should().BeFalse();
        tracker.CanAfford(200).Should().BeTrue();
    }

    [Fact]
    public void RecordUsage_OverDraws_ClampsRemainingAtZero()
    {
        var tracker = BuildTracker(1_000);

        tracker.RecordUsage(5_000);

        tracker.RemainingBudget.Should().Be(0);
    }

    // -------------------------------------------------------------------------
    // Live path: behavior + real tracker. Estimate gate rejects; actual usage
    // recorded post-turn from the IAgentTurnResult response.
    // -------------------------------------------------------------------------

    [Fact]
    public async Task Handle_RealTracker_EstimateExceedsBudget_ShortCircuitsWithoutSpending()
    {
        var tracker = BuildTracker(100);
        var sut = new TokenBudgetBehavior<TokenRequest, AgentResult>(
            tracker, NullLogger<TokenBudgetBehavior<TokenRequest, AgentResult>>.Instance);
        var handlerCalled = false;

        var act = () => sut.Handle(
            new TokenRequest(5_000),
            () => { handlerCalled = true; return Task.FromResult(new AgentResult()); },
            CancellationToken.None);

        await act.Should().ThrowAsync<TokenBudgetExceededException>();
        handlerCalled.Should().BeFalse();
        tracker.RemainingBudget.Should().Be(100);
    }

    [Fact]
    public async Task Handle_RealTracker_SuccessfulTurn_DecrementsByActualTokens()
    {
        var tracker = BuildTracker(10_000);
        var sut = new TokenBudgetBehavior<TokenRequest, AgentResult>(
            tracker, NullLogger<TokenBudgetBehavior<TokenRequest, AgentResult>>.Instance);
        var result = new AgentResult { InputTokens = 300, OutputTokens = 1_200 };

        var response = await sut.Handle(
            new TokenRequest(500),
            () => Task.FromResult(result),
            CancellationToken.None);

        response.Should().BeSameAs(result);
        // Budget reflects actual usage (1,500), not the pre-flight estimate (500).
        tracker.RemainingBudget.Should().Be(8_500);
    }

    // -------------------------------------------------------------------------
    // Helpers & test doubles
    // -------------------------------------------------------------------------

    private TokenBudgetBehavior<TRequest, TResponse> CreateBehavior<TRequest, TResponse>()
        where TRequest : notnull =>
        new(_tracker.Object, NullLogger<TokenBudgetBehavior<TRequest, TResponse>>.Instance);

    private static TokenBudgetTracker BuildTracker(int defaultTokenBudget)
    {
        var cfg = new AppConfig();
        cfg.AI.AgentFramework = new AgentFrameworkConfig { DefaultTokenBudget = defaultTokenBudget };
        var monitor = Mock.Of<IOptionsMonitor<AppConfig>>(m => m.CurrentValue == cfg);
        return new TokenBudgetTracker(monitor, NullLogger<TokenBudgetTracker>.Instance);
    }

    private sealed record NonTokenRequest;

    private sealed record TokenRequest(int EstimatedTokenCost) : IConsumesTokens;

    private sealed record AgentResult : IAgentTurnResult
    {
        public bool Success { get; init; } = true;
        public string Response { get; init; } = string.Empty;
        public int InputTokens { get; init; }
        public int OutputTokens { get; init; }
    }
}
