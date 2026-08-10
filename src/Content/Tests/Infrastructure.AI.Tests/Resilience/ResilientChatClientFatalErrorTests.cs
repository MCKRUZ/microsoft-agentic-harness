using System.Net;
using System.Runtime.CompilerServices;
using Application.AI.Common.Interfaces.Resilience;
using Domain.AI.Resilience;
using FluentAssertions;
using Infrastructure.AI.Resilience;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Moq;
using Polly;
using Xunit;

namespace Infrastructure.AI.Tests.Resilience;

/// <summary>
/// Verifies that the provider fallback chain stops on a failure no other provider can serve,
/// and keeps rotating on one they can.
/// </summary>
public sealed class ResilientChatClientFatalErrorTests
{
    [Fact]
    public async Task GetResponse_InvalidCredential_DoesNotTryTheNextProvider()
    {
        var primary = new FakeChatClient(
            () => new HttpRequestException("Invalid API key", null, HttpStatusCode.Unauthorized));
        var secondary = new FakeChatClient(() => new InvalidOperationException("must never be reached"));
        var sut = CreateClient(primary, secondary);

        var act = () => sut.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")]);

        await act.Should().ThrowAsync<ProviderFatalErrorException>();
        secondary.CallCount.Should().Be(
            0,
            "the key is shared configuration — rotating providers cannot fix it and only hides the cause");
    }

    [Fact]
    public async Task GetResponse_RateLimited_StillFallsBackToTheNextProvider()
    {
        // The control: same chain shape, a failure the next provider genuinely might serve.
        var primary = new FakeChatClient(
            () => new HttpRequestException("Too many requests", null, HttpStatusCode.TooManyRequests));
        var secondary = new FakeChatClient("ok");
        var sut = CreateClient(primary, secondary);

        var response = await sut.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")]);

        response.Should().NotBeNull();
        secondary.CallCount.Should().Be(1, "a rate limit on one provider says nothing about the next");
    }

    [Fact]
    public async Task GetResponse_ModelNotFound_StillFallsBackToTheNextProvider()
    {
        // Fatal for this provider, but not for the chain: another deployment may host the model.
        var primary = new FakeChatClient(
            () => new HttpRequestException("model not found", null, HttpStatusCode.NotFound));
        var secondary = new FakeChatClient("ok");
        var sut = CreateClient(primary, secondary);

        var response = await sut.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")]);

        response.Should().NotBeNull();
        secondary.CallCount.Should().Be(1);
    }

    [Fact]
    public async Task GetResponse_InvalidCredential_NamesTheCauseAndKeepsProviderTextOutOfTheMessage()
    {
        const string providerText = "Invalid API key sk-live-abcd1234 supplied";
        var primary = new FakeChatClient(
            () => new HttpRequestException(providerText, null, HttpStatusCode.Unauthorized));
        var logger = new RecordingLogger<ResilientChatClient>();
        var sut = CreateClient([primary], logger);

        var act = () => sut.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")]);

        var thrown = (await act.Should().ThrowAsync<ProviderFatalErrorException>()).Which;
        thrown.ReasonCode.Should().Be(ProviderFatalReason.InvalidCredentials);
        thrown.ProviderName.Should().Be("primary");
        thrown.Message.Should().Contain(ProviderFatalReason.InvalidCredentials, "the operator needs the cause named");
        thrown.Message.Should().NotContain(
            "sk-live-abcd1234",
            "provider error text can echo credential fragments and this message may reach an API response");
    }

    [Fact]
    public async Task GetResponse_InvalidCredential_WritesTheProviderMessageToTheStructuredLog()
    {
        const string providerText = "Invalid API key supplied — check your deployment configuration";
        var primary = new FakeChatClient(
            () => new HttpRequestException(providerText, null, HttpStatusCode.Unauthorized));
        var logger = new RecordingLogger<ResilientChatClient>();
        var sut = CreateClient([primary], logger);

        try
        {
            await sut.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")]);
        }
        catch (ProviderFatalErrorException)
        {
            // The throw is asserted elsewhere; this test is about what reached the log.
        }

        logger.Entries.Should().Contain(
            e => e.Level == LogLevel.Error && e.Message.Contains(providerText),
            "the diagnosis lives in the provider's own words, which must survive somewhere");
    }

    [Fact]
    public async Task GetResponse_FatalOnAFallbackProvider_StillReportsTheProvidersThatFailedFirst()
    {
        // Primary is rate-limited (a normal fallback), the fallback then rejects the credential.
        // Reporting only the fallback tells the operator a provider they never chose has a bad
        // key, with no sign the primary was failing at all.
        var primary = new FakeChatClient(
            () => new HttpRequestException("Too many requests", null, HttpStatusCode.TooManyRequests));
        var secondary = new FakeChatClient(
            () => new HttpRequestException("Invalid API key", null, HttpStatusCode.Unauthorized));
        var sut = CreateClient(primary, secondary);

        var act = () => sut.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")]);

        var thrown = (await act.Should().ThrowAsync<ProviderFatalErrorException>()).Which;
        thrown.ProviderName.Should().Be("secondary");
        thrown.FailedProviders.Should().ContainSingle().Which.Should().Be("primary");
        thrown.Message.Should().Contain("primary", "the operator needs the whole picture, not the last hop");
    }

    [Fact]
    public async Task GetStreamingResponse_InvalidCredentialAtInitiation_DoesNotTryTheNextProvider()
    {
        var primary = new FakeChatClient(
            () => new HttpRequestException("Invalid API key", null, HttpStatusCode.Unauthorized));
        var secondary = new FakeChatClient(() => new InvalidOperationException("must never be reached"));
        var sut = CreateClient(primary, secondary);

        var act = async () =>
        {
            await foreach (var _ in sut.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "hi")]))
            {
                // Draining the stream is what surfaces the failure.
            }
        };

        await act.Should().ThrowAsync<ProviderFatalErrorException>();
        secondary.CallCount.Should().Be(0);
    }

    [Fact]
    public async Task GetStreamingResponse_RateLimitedAtInitiation_StillFallsBack()
    {
        var primary = new FakeChatClient(
            () => new HttpRequestException("Too many requests", null, HttpStatusCode.TooManyRequests));
        var secondary = new FakeChatClient("ok");
        var sut = CreateClient(primary, secondary);

        var chunks = 0;
        await foreach (var _ in sut.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "hi")]))
        {
            chunks++;
        }

        chunks.Should().BeGreaterThan(0);
        secondary.CallCount.Should().Be(1);
    }

    private static ResilientChatClient CreateClient(IChatClient primary, IChatClient secondary)
        => CreateClient([primary, secondary], logger: null);

    private static ResilientChatClient CreateClient(
        IReadOnlyList<IChatClient> clients, ILogger<ResilientChatClient>? logger)
    {
        var healthMonitor = new Mock<IProviderHealthMonitor>();
        healthMonitor.Setup(m => m.GetProviderHealth(It.IsAny<string>()))
            .Returns(ProviderHealthState.Healthy);
        healthMonitor.Setup(m => m.GetAllProviderHealth())
            .Returns(new Dictionary<string, ProviderHealthState>());

        var names = new[] { "primary", "secondary", "tertiary" };
        var entries = clients
            .Select((client, i) => new ResilientChatClient.ProviderEntry(
                names[i], client, ResiliencePipeline<ChatResponse>.Empty, ResiliencePipeline.Empty))
            .ToList();

        return new ResilientChatClient(
            entries, healthMonitor.Object, ResilienceTestSupport.CreateClassifier(), logger);
    }

}
