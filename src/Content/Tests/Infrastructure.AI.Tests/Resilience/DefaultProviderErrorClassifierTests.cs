using System.ClientModel;
using System.Net;
using System.Net.Sockets;
using System.Security.Authentication;
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
            sut.Classify(exception, CancellationToken.None).Kind.Should().Be(
                ProviderFailureKind.Transient,
                "{0} carrying HTTP {1} is a transient failure whatever SDK threw it",
                exception.GetType().Name, status);
        }
    }

    [Theory]
    [InlineData(401, ProviderFatalReason.InvalidCredentials)]
    [InlineData(402, ProviderFatalReason.BillingExhausted)]
    public void Classify_SharedAccountStatus_AcrossAllThreeSdkShapes_IsFatalForChain(
        int status, string expectedReason)
    {
        var sut = ResilienceTestSupport.CreateClassifier();

        foreach (var exception in ExceptionsForStatus(status, "request rejected"))
        {
            var classification = sut.Classify(exception, CancellationToken.None);

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
            "model not available", null, HttpStatusCode.NotFound), CancellationToken.None);

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
            null, HttpStatusCode.BadRequest), CancellationToken.None);

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
            "Rate limit exceeded for your billing tier", null, HttpStatusCode.TooManyRequests), CancellationToken.None);

        classification.Kind.Should().Be(
            ProviderFailureKind.Transient,
            "a false 'fatal' is more damaging than a false 'transient' — it skips retries and halts fallback");
    }

    [Fact]
    public void Classify_NetworkFailureWithNoStatus_IsTransient()
    {
        var sut = ResilienceTestSupport.CreateClassifier();

        sut.Classify(new HttpRequestException("Connection reset by peer"), CancellationToken.None)
            .Kind.Should().Be(ProviderFailureKind.Transient);
        sut.Classify(new SocketException(10054), CancellationToken.None)
            .Kind.Should().Be(ProviderFailureKind.Transient);
    }

    [Fact]
    public void Classify_TlsHandshakeFailure_IsTransient_NotARejectedCredential()
    {
        // The exact shape .NET raises when the TLS handshake fails: no HTTP status, and an inner
        // AuthenticationException whose message is literally "Authentication failed, ...". That
        // text matches the credential pattern, so consulting message text before the transport
        // signal turned a connectivity blip into a chain-fatal "invalid API key" — skipping every
        // retry and halting the fallback chain for a failure that never reached the provider.
        var sut = ResilienceTestSupport.CreateClassifier();

        var tlsFailure = new HttpRequestException(
            HttpRequestError.SecureConnectionError,
            "The SSL connection could not be established, see inner exception.",
            new AuthenticationException("Authentication failed, see inner exception."));

        var classification = sut.Classify(tlsFailure, CancellationToken.None);

        classification.Kind.Should().Be(
            ProviderFailureKind.Transient,
            "the request never reached the provider, so its credentials were never judged");
        classification.ReasonCode.Should().NotBe(ProviderFatalReason.InvalidCredentials);
    }

    [Theory]
    [InlineData(HttpRequestError.NameResolutionError)]
    [InlineData(HttpRequestError.ConnectionError)]
    [InlineData(HttpRequestError.SecureConnectionError)]
    public void Classify_TransportFaultCarryingFatalWording_StaysTransient(HttpRequestError error)
    {
        // The same asymmetry the class already applies to a transient HTTP status, extended to a
        // transport fault the HTTP stack itself categorised: a positive "never reached the
        // provider" signal is never overridden by words found in the error text.
        var sut = ResilienceTestSupport.CreateClassifier();

        var failure = new HttpRequestException(error, "unauthorized: authentication failed");

        sut.Classify(failure, CancellationToken.None).Kind.Should().Be(ProviderFailureKind.Transient);
    }

    [Fact]
    public void Classify_CredentialRejectionWithNoStatus_IsStillFatalForChain()
    {
        // The control for the two tests above. Anthropic reports API errors through the same
        // exception type as a transport fault, but leaves HttpRequestError unset — so message
        // text remains the only signal there and must keep working. Without this case, the fix
        // could have classified every status-less failure as transient and still looked green.
        var sut = ResilienceTestSupport.CreateClassifier();

        var classification = sut.Classify(new HttpRequestException("invalid x-api-key"), CancellationToken.None);

        classification.Kind.Should().Be(ProviderFailureKind.FatalForChain);
        classification.ReasonCode.Should().Be(ProviderFatalReason.InvalidCredentials);
    }

    [Fact]
    public void Classify_CallerCancellation_IsCallerCancelled_NotTransient()
    {
        // A user pressing Stop, or a request the caller abandoned. Nothing here says the
        // provider is unwell — treating it as Transient means it gets retried (for a caller who
        // already left) and counted against the circuit breaker (taking a healthy provider
        // offline for every other caller once enough cancellations land in one sampling window).
        // The ambient token being cancelled is the ground truth confirming this is a withdrawal —
        // see Classify_CancellationShapedException_WithTokenNotCancelled_IsUnknown below for what
        // happens without it.
        var sut = ResilienceTestSupport.CreateClassifier();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var classification = sut.Classify(new OperationCanceledException("The operation was canceled."), cts.Token);

        classification.Kind.Should().Be(ProviderFailureKind.CallerCancelled);
        classification.ShouldRetry.Should().BeFalse("nobody is waiting for a retried response");
        classification.CountsTowardHealth.Should().BeFalse("a withdrawal is not evidence the provider is down");
    }

    [Fact]
    public void Classify_TaskCancelledWithNoInnerTimeout_IsAlsoCallerCancelled()
    {
        // TaskCanceledException is the shape HttpClient/SDKs actually throw for a signalled
        // token, not the bare base type. Without covering it, the fix above would look complete
        // and still miss almost every real cancellation.
        var sut = ResilienceTestSupport.CreateClassifier();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var classification = sut.Classify(new TaskCanceledException("A task was canceled."), cts.Token);

        classification.Kind.Should().Be(ProviderFailureKind.CallerCancelled);
    }

    [Fact]
    public void Classify_CancellationShapedException_WithTokenNotCancelled_IsUnknown_NotAssumedCallerCancelled()
    {
        // The ground-truth requirement itself: reaching here cancellation-shaped is not, on its
        // own, proof the caller withdrew — a future provider SDK could throw
        // OperationCanceledException for some unrelated reason while the ambient token the caller
        // actually passed is still healthy. Without confirmation, this must not be assumed
        // CallerCancelled, which would wrongly suppress retry, breaker accounting, and fallback
        // for something that might be a genuine provider problem. It falls back to Unknown, the
        // same conservative default every other unrecognised failure gets.
        var sut = ResilienceTestSupport.CreateClassifier();

        var classification = sut.Classify(new OperationCanceledException("mystery cancellation"), CancellationToken.None);

        classification.Kind.Should().Be(
            ProviderFailureKind.Unknown,
            "shape alone cannot prove the caller withdrew — only the ambient token can");
    }

    [Fact]
    public void Classify_CallerCancellation_OutranksMessageDrivenClassification()
    {
        // A cancellation-shaped exception whose message happens to contain credential wording —
        // messages are collected from every node in the chain, so this is not far-fetched — must
        // still report CallerCancelled once the ambient token confirms it. Message text is a
        // weaker, interpretive signal than ground truth about the caller's own intent. Without
        // this ordering, a coincidental wording match reports an unrelated "invalid credentials"
        // failure for a request the caller explicitly withdrew.
        var sut = ResilienceTestSupport.CreateClassifier();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var classification = sut.Classify(new OperationCanceledException("invalid api key"), cts.Token);

        classification.Kind.Should().Be(
            ProviderFailureKind.CallerCancelled,
            "a confirmed caller withdrawal outranks message-pattern classification");
    }

    [Fact]
    public void Classify_SameWording_WithoutConfirmedCancellation_StillClassifiesByMessage()
    {
        // The control for the test above — moving the cancellation check earlier must not make
        // message classification unreachable for a cancellation-shaped exception that is NOT
        // confirmed against the ambient token.
        var sut = ResilienceTestSupport.CreateClassifier();

        var classification = sut.Classify(new OperationCanceledException("invalid api key"), CancellationToken.None);

        classification.Kind.Should().Be(ProviderFailureKind.FatalForChain);
        classification.ReasonCode.Should().Be(ProviderFatalReason.InvalidCredentials);
    }

    [Fact]
    public void Classify_HttpClientTimeout_StaysTransient_EvenWhenTheAmbientTokenIsAlsoCancelled()
    {
        // The control for the tests above. An HttpClient per-request timeout throws
        // TaskCanceledException wrapping a TimeoutException — the exact shape a caller
        // cancellation does NOT carry. The ambient token is deliberately cancelled here too: a
        // timeout and a caller withdrawal can coincide in the real world, and the transport-fault
        // check must still win regardless of token state, because it runs before the cancellation
        // check in Classify(). A fix that excluded OperationCanceledException wholesale, rather
        // than distinguishing the two, would silently stop retrying real timeouts too.
        var sut = ResilienceTestSupport.CreateClassifier();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var classification = sut.Classify(
            new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout",
                new TimeoutException()),
            cts.Token);

        classification.Kind.Should().Be(
            ProviderFailureKind.Transient,
            "an HttpClient timeout is a genuine transient failure, not a caller withdrawing");
        classification.ShouldRetry.Should().BeTrue();
        classification.CountsTowardHealth.Should().BeTrue();
    }

    [Fact]
    public void Classify_PollyTimeoutRejection_StaysTransient_NotCallerCancelled()
    {
        // The second control: Polly's own per-attempt timeout strategy throws
        // TimeoutRejectedException, not OperationCanceledException, and was already Transient
        // before this fix. Confirms the new cancellation path does not regress it.
        var sut = ResilienceTestSupport.CreateClassifier();

        sut.Classify(new Polly.Timeout.TimeoutRejectedException("attempt timed out"), CancellationToken.None)
            .Kind.Should().Be(ProviderFailureKind.Transient);
    }

    [Fact]
    public void Classify_UnrecognisedFailure_IsUnknown_NotTransient()
    {
        var sut = ResilienceTestSupport.CreateClassifier();

        sut.Classify(new InvalidOperationException("something went wrong in our own code"), CancellationToken.None)
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

        sut.Classify(nested, CancellationToken.None).Kind.Should().Be(ProviderFailureKind.FatalForChain);
    }

    [Fact]
    public void Classify_ZeroStatusOnClientResultException_IsNotReadAsAStatus()
    {
        // System.ClientModel reports Status = 0 when no response arrived. Reading that as a
        // real status would classify every connection failure as an unrecognised non-4xx.
        var sut = ResilienceTestSupport.CreateClassifier();

        var noResponse = new ClientResultException("connection failure",
            innerException: new HttpRequestException("connection refused"));

        sut.Classify(noResponse, CancellationToken.None).Kind.Should().Be(ProviderFailureKind.Transient);
    }

    [Fact]
    public void Classify_RetiredModelOn400_DoesNotStopTheChainAsABillingFailure()
    {
        // Azure OpenAI reports a retired deployment with this wording. A bare "no longer active"
        // billing pattern matched it, halting the whole fallback chain and reporting an
        // exhausted balance — for a problem the next provider in the chain could have served.
        var sut = ResilienceTestSupport.CreateClassifier();

        var classification = sut.Classify(new HttpRequestException(
            "The model 'gpt-4-32k' is no longer active. Please use a supported model.",
            null, HttpStatusCode.BadRequest), CancellationToken.None);

        classification.Kind.Should().NotBe(
            ProviderFailureKind.FatalForChain,
            "a retired model on one provider says nothing about the account's billing state");
    }

    [Fact]
    public void Classify_UnrelatedResourceOn400_IsNotReportedAsAMissingModel()
    {
        // A bare "does not exist" pattern claimed any 4xx mentioning a missing anything.
        var sut = ResilienceTestSupport.CreateClassifier();

        var classification = sut.Classify(new HttpRequestException(
            "Assistant with id 'asst_abc123' does not exist", null, HttpStatusCode.BadRequest), CancellationToken.None);

        classification.ReasonCode.Should().NotBe(
            ProviderFatalReason.ModelNotFound,
            "a missing assistant is not a missing model, and mis-naming it misdirects the operator");
    }

    [Fact]
    public void Classify_GenuineMissingDeployment_IsStillReportedAsAMissingModel()
    {
        // The control for the test above — narrowing the patterns must not lose the real case.
        var sut = ResilienceTestSupport.CreateClassifier();

        sut.Classify(new HttpRequestException(
                "The API deployment for this resource does not exist", null, HttpStatusCode.BadRequest), CancellationToken.None)
            .ReasonCode.Should().Be(ProviderFatalReason.ModelNotFound);
    }

    [Fact]
    public void Classify_NullPatternArray_DoesNotThrowInsideTheFailurePredicate()
    {
        // The binder never yields null, but the property is settable, and this code runs inside
        // Polly's ShouldHandle — a throw here replaces the real provider error during an incident.
        var config = new ResilienceConfig
        {
            ErrorClassification = new ProviderErrorClassificationConfig
            {
                AdditionalChainFatalMessagePatterns = null!,
                AdditionalProviderFatalMessagePatterns = null!
            }
        };
        var sut = ResilienceTestSupport.CreateClassifier(config);

        var act = () => sut.Classify(new HttpRequestException("boom", null, HttpStatusCode.BadRequest), CancellationToken.None);

        act.Should().NotThrow();
    }

    [Fact]
    public void Classify_DefaultConfigWithNothingSet_HasTheBuiltInPatternsActive()
    {
        // The config object is constructed with nothing configured at all — the built-in
        // patterns must not depend on a consumer having populated anything.
        var sut = ResilienceTestSupport.CreateClassifier(new ResilienceConfig());

        sut.Classify(new HttpRequestException("Invalid API key provided", null, HttpStatusCode.BadRequest), CancellationToken.None)
            .ReasonCode.Should().Be(ProviderFatalReason.InvalidCredentials);
        sut.Classify(new HttpRequestException("insufficient credits", null, HttpStatusCode.BadRequest), CancellationToken.None)
            .ReasonCode.Should().Be(ProviderFatalReason.BillingExhausted);
        sut.Classify(new HttpRequestException("This account is disabled", null, HttpStatusCode.BadRequest), CancellationToken.None)
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

        sut.Classify(new HttpRequestException("tenant is not provisioned", null, HttpStatusCode.BadRequest), CancellationToken.None)
            .Kind.Should().Be(ProviderFailureKind.FatalForChain, "the consumer's own wording is honoured");

        sut.Classify(new HttpRequestException("insufficient credits", null, HttpStatusCode.BadRequest), CancellationToken.None)
            .Kind.Should().Be(
                ProviderFailureKind.FatalForChain,
                "adding one provider's wording must not silently drop coverage for every other provider");
    }

    [Fact]
    public void Classify_ConsumerProviderFatalPattern_CannotSoftenABuiltInBillingFailure()
    {
        // The class documents that every chain-fatal wording is tested before any provider-fatal
        // one. It was not: the consumer's provider-fatal list ran second, ahead of the built-in
        // billing patterns. A consumer routing one regional wording onward with a broad "account"
        // also caught "billing account", so a shared billing failure rotated through every
        // provider and surfaced as "all providers exhausted".
        var config = new ResilienceConfig
        {
            ErrorClassification = new ProviderErrorClassificationConfig
            {
                AdditionalProviderFatalMessagePatterns = ["account"]
            }
        };
        var sut = ResilienceTestSupport.CreateClassifier(config);

        var classification = sut.Classify(new HttpRequestException(
            "Your billing account is not in good standing", null, HttpStatusCode.BadRequest), CancellationToken.None);

        classification.Kind.Should().Be(
            ProviderFailureKind.FatalForChain,
            "a billing failure is shared by every provider on the account, whatever the consumer added");
        classification.ReasonCode.Should().Be(ProviderFatalReason.BillingExhausted);
    }

    [Fact]
    public void Classify_ConsumerProviderFatalPattern_StillWorksOnItsOwnWording()
    {
        // The control for the test above — reordering must not make the consumer's list inert.
        var config = new ResilienceConfig
        {
            ErrorClassification = new ProviderErrorClassificationConfig
            {
                AdditionalProviderFatalMessagePatterns = ["region is not enabled"]
            }
        };
        var sut = ResilienceTestSupport.CreateClassifier(config);

        sut.Classify(new HttpRequestException(
                "This region is not enabled for the resource", null, HttpStatusCode.BadRequest), CancellationToken.None)
            .Kind.Should().Be(ProviderFailureKind.FatalForProvider);
    }

    [Fact]
    public void Classify_Forbidden_RotatesTheChainRatherThanAbandoningIt()
    {
        // 403 is routinely scoped to one resource — a network ACL, a private endpoint, a role
        // missing on one deployment — unlike 401 and 402, which are shared account config.
        // Stopping the chain abandons a healthy secondary that would have served the request.
        var sut = ResilienceTestSupport.CreateClassifier();

        var classification = sut.Classify(new HttpRequestException(
            "Public access is disabled for this resource", null, HttpStatusCode.Forbidden), CancellationToken.None);

        classification.StopsChain.Should().BeFalse(
            "one endpoint refusing the caller says nothing about the next provider in the chain");
        classification.ReasonCode.Should().Be(ProviderFatalReason.AccessDenied);
    }

    [Fact]
    public void Classify_ForbiddenWording_AlsoRotatesTheChain()
    {
        // Without this, the status fix above is inert: a 403 body almost always contains
        // "access denied", and message patterns are consulted before the status mapping — so the
        // wording, not the status, is what actually decided the verdict.
        var sut = ResilienceTestSupport.CreateClassifier();

        sut.Classify(new HttpRequestException(
                "Access denied due to Virtual Network/Firewall rules", null, HttpStatusCode.Forbidden), CancellationToken.None)
            .StopsChain.Should().BeFalse();
    }

    [Fact]
    public void Classify_DisabledAccountWording_StillStopsTheWholeChain()
    {
        // The control for the two tests above. Splitting resource-scoped refusals out of the
        // access patterns must not downgrade a genuinely account-wide shutdown, which every
        // provider sharing the account would hit identically.
        var sut = ResilienceTestSupport.CreateClassifier();

        sut.Classify(new HttpRequestException(
                "This account is disabled", null, HttpStatusCode.Forbidden), CancellationToken.None)
            .StopsChain.Should().BeTrue();
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
