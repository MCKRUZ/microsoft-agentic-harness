using Application.AI.Common.Interfaces.RAG;
using Domain.Common.Config;
using FluentAssertions;
using Infrastructure.AI.RAG.Orchestration;
using Infrastructure.AI.RAG.Tests.Helpers;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Infrastructure.AI.RAG.Tests.Orchestration;

public sealed class RetrievalCostTrackerTests
{
    private RetrievalCostTracker CreateTracker(Action<AppConfig>? configure = null)
    {
        var config = RagTestData.CreateConfigMonitor(configure);
        return new RetrievalCostTracker(config);
    }

    [Fact]
    public void RecordCall_SingleCall_TracksTokens()
    {
        var tracker = CreateTracker();
        tracker.RecordCall(promptTokens: 100, completionTokens: 50, TimeSpan.FromMilliseconds(200));

        var summary = tracker.GetSummary();
        summary.PromptTokens.Should().Be(100);
        summary.CompletionTokens.Should().Be(50);
        summary.TotalTokensUsed.Should().Be(150);
        summary.RetrievalCalls.Should().Be(1);
    }

    [Fact]
    public void RecordCall_MultipleCalls_AggregatesCorrectly()
    {
        var tracker = CreateTracker();
        tracker.RecordCall(promptTokens: 100, completionTokens: 50, TimeSpan.FromMilliseconds(200));
        tracker.RecordCall(promptTokens: 200, completionTokens: 80, TimeSpan.FromMilliseconds(300));
        tracker.RecordCall(promptTokens: 150, completionTokens: 60, TimeSpan.FromMilliseconds(250));

        var summary = tracker.GetSummary();
        summary.PromptTokens.Should().Be(450);
        summary.CompletionTokens.Should().Be(190);
        summary.TotalTokensUsed.Should().Be(640);
        summary.RetrievalCalls.Should().Be(3);
        summary.TotalLatency.TotalMilliseconds.Should().Be(750);
    }

    [Fact]
    public void GetSummary_CalculatesEstimatedCost()
    {
        var tracker = CreateTracker();
        tracker.RecordCall(promptTokens: 1_000_000, completionTokens: 100_000, TimeSpan.FromSeconds(1));

        var summary = tracker.GetSummary();
        // default pricing: $2.50/1M input, $10.00/1M output
        summary.EstimatedCost.Should().BeApproximately(2.50 + 1.00, precision: 0.01);
    }

    [Fact]
    public void Reset_ClearsAllCounters()
    {
        var tracker = CreateTracker();
        tracker.RecordCall(promptTokens: 100, completionTokens: 50, TimeSpan.FromMilliseconds(200));
        tracker.Reset();

        var summary = tracker.GetSummary();
        summary.PromptTokens.Should().Be(0);
        summary.CompletionTokens.Should().Be(0);
        summary.TotalTokensUsed.Should().Be(0);
        summary.RetrievalCalls.Should().Be(0);
        summary.TotalLatency.Should().Be(TimeSpan.Zero);
        summary.EstimatedCost.Should().Be(0.0);
    }

    [Fact]
    public async Task RecordCall_ConcurrentAccess_ThreadSafe()
    {
        var tracker = CreateTracker();
        const int threadCount = 50;
        const int callsPerThread = 100;

        var tasks = Enumerable.Range(0, threadCount)
            .Select(_ => Task.Run(() =>
            {
                for (var i = 0; i < callsPerThread; i++)
                    tracker.RecordCall(promptTokens: 10, completionTokens: 5, TimeSpan.FromMilliseconds(1));
            }))
            .ToArray();

        await Task.WhenAll(tasks);

        var summary = tracker.GetSummary();
        summary.RetrievalCalls.Should().Be(threadCount * callsPerThread);
        summary.PromptTokens.Should().Be(threadCount * callsPerThread * 10);
        summary.CompletionTokens.Should().Be(threadCount * callsPerThread * 5);
    }
}
