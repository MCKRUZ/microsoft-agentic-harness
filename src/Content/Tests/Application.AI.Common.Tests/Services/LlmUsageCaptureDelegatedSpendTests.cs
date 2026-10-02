using Application.AI.Common.Interfaces;
using Application.AI.Common.Services;
using Domain.Common.Config;
using Domain.Common.Config.Observability;
using FluentAssertions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Application.AI.Common.Tests.Services;

/// <summary>
/// <see cref="LlmUsageCapture.RecordDelegated"/> (#756): a delegating turn's totals and cost must
/// include what its sub-agents spent — that is what the conversation budget is charged from — while
/// the turn's tool list and per-call list stay its own.
/// </summary>
public sealed class LlmUsageCaptureDelegatedSpendTests
{
    // $1 per input token, free otherwise, so a cost assertion says which model priced which tokens.
    private static LlmUsageCapture CreateSut()
    {
        var appConfig = new AppConfig();
        appConfig.Observability.LlmPricing.DefaultModel = "parent-model";
        appConfig.Observability.LlmPricing.Models.Add(new ModelPricingEntry
        {
            Name = "parent-model",
            InputPerMillion = 1_000_000m,
            OutputPerMillion = 0m,
            CacheReadPerMillion = 0m,
            CacheWritePerMillion = 0m,
        });
        var monitor = new Mock<IOptionsMonitor<AppConfig>>();
        monitor.Setup(m => m.CurrentValue).Returns(appConfig);
        return new LlmUsageCapture(monitor.Object);
    }

    private static LlmUsageSnapshot Delegate(
        int input, int output, int cacheRead = 0, int cacheWrite = 0, decimal cost = 0m) =>
        new(input, output, cacheRead, cacheWrite, "delegate-model", cost, 0m, ["delegate_tool"])
        {
            Calls = [new LlmCallUsage(input, output, cacheRead, cacheWrite, "delegate-model")],
        };

    [Fact]
    public void RecordDelegated_AddsTheDelegatesTokensToTheTurnTotals()
    {
        var sut = CreateSut();
        sut.Record(100, 20, 0, 0, "parent-model");

        sut.RecordDelegated(Delegate(input: 400, output: 80, cacheRead: 30, cacheWrite: 5));

        var snapshot = sut.TakeSnapshot();
        snapshot.InputTokens.Should().Be(500);
        snapshot.OutputTokens.Should().Be(100);
        snapshot.CacheRead.Should().Be(30);
        snapshot.CacheWrite.Should().Be(5);
    }

    [Fact]
    public void RecordDelegated_AddsTheCostTheDelegateWasPricedAt_NotRepricedUnderTheTurnsModel()
    {
        var sut = CreateSut();
        sut.Record(100, 0, 0, 0, "parent-model"); // $100 at $1/input token

        sut.RecordDelegated(Delegate(input: 50, output: 0, cost: 7m));

        // Repriced under the parent's model the delegate's 50 input tokens would add $50.
        sut.TakeSnapshot().CostUsd.Should().Be(107m);
    }

    [Fact]
    public void RecordDelegated_LeavesTheTurnsOwnToolsAndPerCallListAlone()
    {
        var sut = CreateSut();
        sut.Record(100, 20, 0, 0, "parent-model");
        sut.RecordToolCall("parent_tool");

        sut.RecordDelegated(Delegate(input: 9_000, output: 500));

        var snapshot = sut.TakeSnapshot();
        snapshot.ToolNames.Should().Equal("parent_tool");
        snapshot.Calls.Should().ContainSingle().Which.InputTokens.Should().Be(100);
        snapshot.LastCallPromptTokens.Should().Be(100,
            "the context bar compares against the last call this turn's own model made, never a sub-agent's");
    }

    [Fact]
    public void RecordDelegated_CacheHitRateReflectsTheCombinedSpend()
    {
        var sut = CreateSut();
        sut.Record(100, 0, cacheRead: 0, cacheWrite: 0, "parent-model");

        sut.RecordDelegated(Delegate(input: 0, output: 0, cacheRead: 100));

        // 100 cache-read of 200 total prompt tokens.
        sut.TakeSnapshot().CacheHitPct.Should().Be(0.5m);
    }

    [Fact]
    public void TakeSnapshot_ResetsTheDelegatedSpend()
    {
        var sut = CreateSut();
        sut.RecordDelegated(Delegate(input: 400, output: 80, cost: 3m));
        sut.TakeSnapshot();

        var next = sut.TakeSnapshot();

        next.InputTokens.Should().Be(0);
        next.OutputTokens.Should().Be(0);
        next.CostUsd.Should().Be(0m);
    }

    [Fact]
    public void NestedDelegation_AccumulatesUpToTheOutermostTurn()
    {
        // A sub-agent that itself delegates folds its own delegate's spend into its capture; when the
        // outer turn folds that capture's snapshot in, the grandchild's spend comes with it.
        var parent = CreateSut();
        var child = CreateSut();
        child.Record(200, 40, 0, 0, "parent-model");
        child.RecordDelegated(Delegate(input: 1_000, output: 100, cost: 11m));

        parent.RecordDelegated(child.TakeSnapshot());

        var snapshot = parent.TakeSnapshot();
        snapshot.InputTokens.Should().Be(1_200);
        snapshot.OutputTokens.Should().Be(140);
        snapshot.CostUsd.Should().Be(211m); // child's own $200 + grandchild's $11
    }
}
