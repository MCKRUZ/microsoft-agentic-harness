using System.Net;
using Domain.AI.Resilience;
using Domain.Common.Config.AI;
using Domain.Common.Config.AI.Resilience;
using FluentAssertions;
using Infrastructure.AI.Resilience;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Moq;
using Tests.AI.Fakes;
using Xunit;

namespace Application.AI.Common.Tests.Fakes;

/// <summary>
/// Proves the load-test fake model (<see cref="SimulatedModelChatClient"/>) holds the three properties
/// the load harness relies on: answers have the profile's length, they take the profile's time, and
/// injected failures are ones the harness's resilience pipeline treats as transient throttling. If any
/// of these drifted, a load test would silently measure the wrong thing.
/// </summary>
public sealed class SimulatedModelChatClientTests
{
    private static readonly ChatMessage[] Prompt = [new(ChatRole.User, "hello there")];

    [Fact]
    public async Task GetStreamingResponseAsync_InstantProfile_EmitsEveryTokenOfTheAnswer()
    {
        var client = new SimulatedModelChatClient(SimulatedModelProfile.Instant);

        var updates = await CollectAsync(client.GetStreamingResponseAsync(Prompt));

        CountTokens(updates).Should().Be(SimulatedModelProfile.Instant.ResponseTokens);
    }

    [Fact]
    public async Task GetStreamingResponseAsync_Completes_WithUsageAndStopFinishReason()
    {
        var client = new SimulatedModelChatClient(SimulatedModelProfile.Instant);

        var updates = await CollectAsync(client.GetStreamingResponseAsync(Prompt));

        var usage = updates.SelectMany(u => u.Contents).OfType<UsageContent>().Single().Details;
        usage.OutputTokenCount.Should().Be(SimulatedModelProfile.Instant.ResponseTokens);
        updates[^1].FinishReason.Should().Be(ChatFinishReason.Stop);
    }

    [Fact]
    public async Task GetStreamingResponseAsync_ThrottledProfile_TakesTimeToFirstTokenPlusStreamingTime()
    {
        // 21 tokens at 10/s: the first arrives at the 1 s time to first token, the last 2 s later.
        var profile = new SimulatedModelProfile("timed", TimeSpan.FromSeconds(1), 10, 21, 0);
        var time = new FakeTimeProvider();
        var client = new SimulatedModelChatClient(profile, time);
        var started = time.GetUtcNow();
        var arrivals = new List<(TimeSpan At, int Tokens)>();

        var consume = Task.Run(async () =>
        {
            await foreach (var update in client.GetStreamingResponseAsync(Prompt))
                arrivals.Add((time.GetUtcNow() - started, CountTokens([update])));
        });
        await DriveUntilCompleteAsync(time, consume);

        var textFrames = arrivals.Where(a => a.Tokens > 0).ToList();
        textFrames[0].At.Should().BeCloseTo(TimeSpan.FromSeconds(1), TimeSpan.FromMilliseconds(200));
        textFrames[^1].At.Should().BeCloseTo(TimeSpan.FromSeconds(3), TimeSpan.FromMilliseconds(200));
        textFrames.Sum(a => a.Tokens).Should().Be(21);
    }

    [Fact]
    public async Task GetResponseAsync_FailureSampled_ThrowsTooManyRequests()
    {
        var client = new SimulatedModelChatClient(
            SimulatedModelProfile.Instant.WithTransientFailureRate(0.05), nextSample: () => 0.01);

        var act = () => client.GetResponseAsync(Prompt);

        (await act.Should().ThrowAsync<HttpRequestException>())
            .Which.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
    }

    [Fact]
    public async Task GetResponseAsync_SampleAboveFailureRate_ReturnsTheAnswer()
    {
        var client = new SimulatedModelChatClient(
            SimulatedModelProfile.Instant.WithTransientFailureRate(0.05), nextSample: () => 0.5);

        var response = await client.GetResponseAsync(Prompt);

        response.Text.Should().NotBeEmpty();
    }

    [Fact]
    public async Task InjectedFailure_IsClassifiedTransient_BySameClassifierProductionUses()
    {
        // Standing rule: retry, breaker and fallback decide via IProviderErrorClassifier. A fake whose
        // failures classified as fatal would make a "flaky" load run measure breaker trips that a
        // real throttled provider would never cause.
        var client = new SimulatedModelChatClient(
            SimulatedModelProfile.Instant.WithTransientFailureRate(1), nextSample: () => 0);
        var config = Mock.Of<IOptionsMonitor<ResilienceConfig>>(m => m.CurrentValue == new ResilienceConfig());
        var classifier = new DefaultProviderErrorClassifier(config);

        var failure = await Record.ExceptionAsync(() => client.GetResponseAsync(Prompt));

        classifier.Classify(failure!, CancellationToken.None).Should().Be(ProviderFailureClassification.Transient);
    }

    [Theory]
    [InlineData(-1, 10, 10, 0)]
    [InlineData(0, -1, 10, 0)]
    [InlineData(0, 10, 0, 0)]
    [InlineData(0, 10, 10, 1.5)]
    public void Constructor_ImpossibleValue_IsRejected(int ttftMs, double tokensPerSecond, int tokens, double failureRate)
    {
        var act = () => new SimulatedModelProfile(
            "bad", TimeSpan.FromMilliseconds(ttftMs), tokensPerSecond, tokens, failureRate);

        act.Should().Throw<ArgumentException>();
    }

    [Theory]
    [InlineData("instant")]
    [InlineData("REALISTIC")]
    [InlineData("Slow")]
    [InlineData("flaky")]
    public void TryGetNamed_KnownNameAnyCase_FindsTheProfile(string name)
    {
        SimulatedModelProfile.TryGetNamed(name, out var profile).Should().BeTrue();
        profile!.Name.Should().Be(name.ToLowerInvariant());
    }

    [Fact]
    public async Task Factory_AnyProvider_BuildsAClientImitatingItsProfile()
    {
        var factory = new SimulatedModelChatClientFactory(SimulatedModelProfile.Slow);

        using var client = await factory.GetChatClientAsync(AIAgentFrameworkClientType.AzureOpenAI, "any-deployment");

        client.Should().BeOfType<SimulatedModelChatClient>();
        factory.GetProviderStatus().IsConfigured.Should().BeTrue();
    }

    private static async Task<List<ChatResponseUpdate>> CollectAsync(IAsyncEnumerable<ChatResponseUpdate> stream)
    {
        var updates = new List<ChatResponseUpdate>();
        await foreach (var update in stream)
            updates.Add(update);
        return updates;
    }

    private static int CountTokens(IEnumerable<ChatResponseUpdate> updates) =>
        updates.SelectMany(u => u.Contents).OfType<TextContent>()
            .Sum(t => t.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length);

    /// <summary>
    /// Advances fake time in small steps, giving the consumer real time to run between them, until it
    /// finishes. Bounded so a fake that never completes fails the test instead of hanging it.
    /// </summary>
    private static async Task DriveUntilCompleteAsync(FakeTimeProvider time, Task consumer)
    {
        for (var step = 0; step < 400 && !consumer.IsCompleted; step++)
        {
            await Task.Delay(10);
            time.Advance(TimeSpan.FromMilliseconds(50));
        }

        await consumer.WaitAsync(TimeSpan.FromSeconds(5));
    }
}
