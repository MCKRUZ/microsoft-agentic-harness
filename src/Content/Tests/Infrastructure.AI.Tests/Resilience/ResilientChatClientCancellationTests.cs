using Application.AI.Common.Interfaces.Resilience;
using Domain.AI.Resilience;
using FluentAssertions;
using Infrastructure.AI.Resilience;
using Microsoft.Extensions.AI;
using Moq;
using Polly;
using Xunit;

namespace Infrastructure.AI.Tests.Resilience;

/// <summary>
/// Verifies that a caller cancellation propagates immediately rather than being treated as a
/// reason to try the next provider — the behaviour #353 added. Distinct from
/// <see cref="ResilientChatClientFatalErrorTests"/>: a chain-fatal failure throws
/// <see cref="ProviderFatalErrorException"/> naming a cause, but a cancellation is not a
/// provider failure at all, so it rethrows the caller's own exception unchanged.
/// </summary>
public sealed class ResilientChatClientCancellationTests
{
    [Fact]
    public async Task GetResponse_CallerCancellation_DoesNotTryTheNextProvider()
    {
        // The classifier confirms a cancellation against the ambient token — the same one passed
        // to GetResponseAsync — rather than assuming it from exception shape. The fake client
        // cancels that exact token before throwing, simulating the caller's own Stop button
        // firing mid-flight.
        using var cts = new CancellationTokenSource();
        var primary = new FakeChatClient(() =>
        {
            cts.Cancel();
            return new OperationCanceledException("client stop", cts.Token);
        });
        var secondary = new FakeChatClient(() => new InvalidOperationException("must never be reached"));
        var sut = CreateClient(primary, secondary);

        var act = () => sut.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")], cancellationToken: cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        secondary.CallCount.Should().Be(0, "the caller withdrew — no provider is waiting for a response");
    }

    [Fact]
    public async Task GetResponse_HttpClientTimeout_StillFallsBackToTheNextProvider()
    {
        // The control: same chain shape, a failure that is genuinely evidence the primary
        // provider is unwell and another provider might serve.
        var primary = new FakeChatClient(() => new TaskCanceledException("timed out", new TimeoutException()));
        var secondary = new FakeChatClient("ok");
        var sut = CreateClient(primary, secondary);

        var response = await sut.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")]);

        response.Should().NotBeNull();
        secondary.CallCount.Should().Be(1, "an HttpClient timeout is not a caller withdrawing");
    }

    [Fact]
    public async Task GetStreamingResponse_CallerCancellationAtInitiation_DoesNotTryTheNextProvider()
    {
        using var cts = new CancellationTokenSource();
        var primary = new FakeChatClient(() =>
        {
            cts.Cancel();
            return new OperationCanceledException("client stop", cts.Token);
        });
        var secondary = new FakeChatClient(() => new InvalidOperationException("must never be reached"));
        var sut = CreateClient(primary, secondary);

        var act = async () =>
        {
            await foreach (var _ in sut.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "hi")], cancellationToken: cts.Token))
            {
                // Draining the stream is what surfaces the failure.
            }
        };

        await act.Should().ThrowAsync<OperationCanceledException>();
        secondary.CallCount.Should().Be(0);
    }

    private static ResilientChatClient CreateClient(IChatClient primary, IChatClient secondary)
    {
        var healthMonitor = new Mock<IProviderHealthMonitor>();
        healthMonitor.Setup(m => m.GetProviderHealth(It.IsAny<string>()))
            .Returns(ProviderHealthState.Healthy);
        healthMonitor.Setup(m => m.GetAllProviderHealth())
            .Returns(new Dictionary<string, ProviderHealthState>());

        var entries = new[]
        {
            new ResilientChatClient.ProviderEntry(
                "primary", primary, ResiliencePipeline<ChatResponse>.Empty, ResiliencePipeline.Empty),
            new ResilientChatClient.ProviderEntry(
                "secondary", secondary, ResiliencePipeline<ChatResponse>.Empty, ResiliencePipeline.Empty)
        };

        return new ResilientChatClient(entries, healthMonitor.Object, ResilienceTestSupport.CreateClassifier());
    }
}
