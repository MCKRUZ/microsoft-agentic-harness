using System.ClientModel;
using System.Net;
using System.Net.Sockets;
using Domain.AI.Resilience;
using Domain.Common.Config.AI.Resilience;
using FluentAssertions;
using Xunit;

namespace Infrastructure.AI.Tests.Resilience;

/// <summary>
/// Tests for <c>DefaultProviderErrorClassifier</c> — the single seam deciding what the
/// resilience pipeline retries, counts against provider health, and falls back on.
/// </summary>
/// <remarks>
/// The exception types here are the real ones: Azure OpenAI and OpenAI throw
/// <see cref="ClientResultException"/>, Azure AI Inference throws
/// <see cref="Azure.RequestFailedException"/>, Anthropic throws
/// <see cref="HttpRequestException"/>. Testing only one of them is what allowed the original
/// defect — a pipeline that recognised Anthropic's shape and silently ignored the two providers
/// actually in the shipped fallback chain.
/// </remarks>
public sealed class DefaultProviderErrorClassifierTests
{
    [Theory]
    [InlineData(429)]
    [InlineData(500)]
    [InlineData(502)]
    [InlineData(503)]
    [InlineData(504)]
    [InlineData(408)]
    public void Classify_TransientStatus_AcrossAllThreeSdkShapes_IsTransient(int status)
    {
        var sut = ResilienceTestSupport.CreateClassifier();

        foreach (var exception in ExceptionsForStatus(status, "service is busy"))
        {
            sut.Classify(exception).Kind.Should().Be(
                ProviderFailureKind.Transient,
                "{0} carrying HTTP {1} is a transient failure whatever SDK threw it",
                exception.GetType().Name, status);
        }
    }

    [Theory]
    [InlineData(401, ProviderFatalReason.InvalidCredentials)]
    [InlineData(402, ProviderFatalReason.BillingExhausted)]
    [InlineData(403, ProviderFatalReason.AccessDenied)]
    public void Classify_CredentialClassStatus_AcrossAllThreeSdkShapes_IsFatalForChain(
        int status, string expectedReason)
    {
        var sut = ResilienceTestSupport.CreateClassifier();

        foreach (var exception in ExceptionsForStatus(status, "request rejected"))
        {
            var classification = sut.Classify(exception);

            classification.Kind.Should().Be(
                ProviderFailureKind.FatalForChain,
                "{0} carrying HTTP {1} cannot be fixed by retrying or by another provider",
                exception.GetType().Name, status);
            classification.ReasonCode.Should().Be(expectedReason);
        }
    }

    [Fact]
    public void Classify_NotFound_IsFatalForProvider_SoTheChainStillRotates()
    {
        var sut = ResilienceTestSupport.CreateClassifier();

        var classification = sut.Classify(new HttpRequestException(
            "model not available", null, HttpStatusCode.NotFound));

        classification.Kind.Should().Be(
            ProviderFailureKind.FatalForProvider,
            "another provider in the chain may host the model this one does not");
        classification.ReasonCode.Should().Be(ProviderFatalReason.ModelNotFound);
    }

    [Fact]
    public void Classify_BillingWordingOnA400_IsFatalForChain()
    {
        // Anthropic reports an exhausted balance as a 400, not a 402. Status alone would call
        // this a malformed request and keep rotating providers that share the same account.
        var sut = ResilienceTestSupport.CreateClassifier();

        var classification = sut.Classify(new HttpRequestException(
            "Your credit balance is too low to access the Anthropic API",
            null, HttpStatusCode.BadRequest));

        classification.Kind.Should().Be(ProviderFailureKind.FatalForChain);
        classification.ReasonCode.Should().Be(ProviderFatalReason.BillingExhausted);
    }

    [Fact]
    public void Classify_FatalWordingOnATransientStatus_StaysTransient()
    {
        // The safety rule that makes broad patterns survivable: a status that positively says
        // "transient" is never overridden by message text. Without it, a rate-limit body that
        // happens to mention billing would stop retries AND stop the fallback chain.
        var sut = ResilienceTestSupport.CreateClassifier();

        var classification = sut.Classify(new HttpRequestException(
            "Rate limit exceeded for your billing tier", null, HttpStatusCode.TooManyRequests));

        classification.Kind.Should().Be(
            ProviderFailureKind.Transient,
            "a false 'fatal' is more damaging than a false 'transient' — it skips retries and halts fallback");
    }

    [Fact]
    public void Classify_NetworkFailureWithNoStatus_IsTransient()
    {
        var sut = ResilienceTestSupport.CreateClassifier();

        sut.Classify(new HttpRequestException("Connection reset by peer"))
            .Kind.Should().Be(ProviderFailureKind.Transient);
        sut.Classify(new SocketException(10054))
            .Kind.Should().Be(ProviderFailureKind.Transient);
    }

    [Fact]
    public void Classify_UnrecognisedFailure_IsUnknown_NotTransient()
    {
        var sut = ResilienceTestSupport.CreateClassifier();

        sut.Classify(new InvalidOperationException("something went wrong in our own code"))
            .Kind.Should().Be(
                ProviderFailureKind.Unknown,
                "an unrecognised failure is as likely to be our bug as a provider blip, and must not be replayed");
    }

    [Fact]
    public void Classify_StatusNestedInsideAWrapper_IsStillFound()
    {
        // Polly and the SDKs both wrap. A classifier that only reads the outermost exception
        // would see an AggregateException with no status and fall through to Unknown.
        var sut = ResilienceTestSupport.CreateClassifier();

        var nested = new AggregateException(
            new InvalidOperationException("outer",
                new HttpRequestException("nope", null, HttpStatusCode.Unauthorized)));

        sut.Classify(nested).Kind.Should().Be(ProviderFailureKind.FatalForChain);
    }

    [Fact]
    public void Classify_ZeroStatusOnClientResultException_IsNotReadAsAStatus()
    {
        // System.ClientModel reports Status = 0 when no response arrived. Reading that as a
        // real status would classify every connection failure as an unrecognised non-4xx.
        var sut = ResilienceTestSupport.CreateClassifier();

        var noResponse = new ClientResultException("connection failure",
            innerException: new HttpRequestException("connection refused"));

        sut.Classify(noResponse).Kind.Should().Be(ProviderFailureKind.Transient);
    }

    [Fact]
    public void Classify_DefaultConfigWithNothingSet_HasTheBuiltInPatternsActive()
    {
        // The config object is constructed with nothing configured at all — the built-in
        // patterns must not depend on a consumer having populated anything.
        var sut = ResilienceTestSupport.CreateClassifier(new ResilienceConfig());

        sut.Classify(new HttpRequestException("Invalid API key provided", null, HttpStatusCode.BadRequest))
            .ReasonCode.Should().Be(ProviderFatalReason.InvalidCredentials);
        sut.Classify(new HttpRequestException("insufficient credits", null, HttpStatusCode.BadRequest))
            .ReasonCode.Should().Be(ProviderFatalReason.BillingExhausted);
        sut.Classify(new HttpRequestException("This account is disabled", null, HttpStatusCode.BadRequest))
            .ReasonCode.Should().Be(ProviderFatalReason.AccessDenied);
    }

    [Fact]
    public void Classify_ConsumerPattern_AddsToTheDefaultsRatherThanReplacingThem()
    {
        var config = new ResilienceConfig
        {
            ErrorClassification = new ProviderErrorClassificationConfig
            {
                AdditionalChainFatalMessagePatterns = ["tenant is not provisioned"]
            }
        };
        var sut = ResilienceTestSupport.CreateClassifier(config);

        sut.Classify(new HttpRequestException("tenant is not provisioned", null, HttpStatusCode.BadRequest))
            .Kind.Should().Be(ProviderFailureKind.FatalForChain, "the consumer's own wording is honoured");

        sut.Classify(new HttpRequestException("insufficient credits", null, HttpStatusCode.BadRequest))
            .Kind.Should().Be(
                ProviderFailureKind.FatalForChain,
                "adding one provider's wording must not silently drop coverage for every other provider");
    }

    /// <summary>
    /// The same HTTP status expressed as each of the three exception types the supported
    /// provider SDKs actually throw.
    /// </summary>
    private static IEnumerable<Exception> ExceptionsForStatus(int status, string message)
    {
        yield return new HttpRequestException(message, null, (HttpStatusCode)status);
        yield return new ClientResultException(message, new StubPipelineResponse(status));
        yield return new Azure.RequestFailedException(status, message);
    }
}
