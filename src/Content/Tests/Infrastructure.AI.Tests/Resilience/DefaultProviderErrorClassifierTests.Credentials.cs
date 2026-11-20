using System.Net;
using System.Net.Sockets;
using Azure;
using Azure.Identity;
using Domain.AI.Resilience;
using FluentAssertions;
using Xunit;

namespace Infrastructure.AI.Tests.Resilience;

/// <summary>
/// Azure.Identity reports a token it could not get as an exception with no HTTP status, so neither the status rules
/// nor, for most of its wordings, the message rules recognise it (#957). The type is the evidence: a credential that
/// was rejected stops the chain, one that could not be reached at all may recover.
/// </summary>
public sealed partial class DefaultProviderErrorClassifierTests
{
    [Fact]
    public void Classify_RejectedAzureCredential_IsFatalForChain_WhateverItsWording()
    {
        // No credential pattern appears in this wording, so only the type can say what it is.
        var sut = ResilienceTestSupport.CreateClassifier();

        var classification = sut.Classify(
            new AuthenticationFailedException("ClientCertificateCredential could not load the certificate."));

        classification.Kind.Should().Be(
            ProviderFailureKind.FatalForChain,
            "the chain's providers share the identity, so no retry and no other provider can get a token for it");
        classification.ReasonCode.Should().Be(ProviderFatalReason.InvalidCredentials);
    }

    [Fact]
    public void Classify_UnreachableAzureCredential_IsTransient()
    {
        var sut = ResilienceTestSupport.CreateClassifier();

        var classification = sut.Classify(new CredentialUnavailableException(
            "ManagedIdentityCredential authentication unavailable. No response received from the managed identity endpoint."));

        classification.Kind.Should().Be(
            ProviderFailureKind.Transient, "a managed identity endpoint that did not answer can answer the next time");
    }

    [Fact]
    public void Classify_UnreachableAzureCredential_IsNotReadAsItsRejectedBaseType_OrItsWording()
    {
        // CredentialUnavailableException derives from AuthenticationFailedException, and DefaultAzureCredential lists
        // every credential it tried in one message, so an unavailable chain can still read "authentication failed".
        var sut = ResilienceTestSupport.CreateClassifier();

        var classification = sut.Classify(new CredentialUnavailableException(
            "DefaultAzureCredential failed to retrieve a token from the included credentials.\n" +
            "- ManagedIdentityCredential authentication unavailable.\n" +
            "- AzureCliCredential authentication failed: Please run 'az login' to set up account."));

        classification.Kind.Should().Be(ProviderFailureKind.Transient);
    }

    [Fact]
    public void Classify_RejectedAzureCredential_InsideAWrapper_IsStillFound()
    {
        var sut = ResilienceTestSupport.CreateClassifier();

        var classification = sut.Classify(new InvalidOperationException(
            "The chat call failed.", new AuthenticationFailedException("WorkloadIdentityCredential token file is empty.")));

        classification.Kind.Should().Be(ProviderFailureKind.FatalForChain);
        classification.ReasonCode.Should().Be(ProviderFatalReason.InvalidCredentials);
    }

    [Fact]
    public void Classify_ChainedCredentialRefusal_OverAnEarlierUnavailableOne_IsTheRefusal()
    {
        // A chained credential that found its first credential unavailable and then had the next one refused throws the
        // refusal, with what it tried inside it. The outermost credential link is the chain's own verdict.
        var sut = ResilienceTestSupport.CreateClassifier();

        var classification = sut.Classify(new AuthenticationFailedException(
            "ChainedTokenCredential could not get a token.",
            new AggregateException(
                new CredentialUnavailableException("ManagedIdentityCredential authentication unavailable."),
                new AuthenticationFailedException("ClientCertificateCredential could not load the certificate."))));

        classification.Kind.Should().Be(ProviderFailureKind.FatalForChain);
        classification.ReasonCode.Should().Be(ProviderFatalReason.InvalidCredentials);
    }

    [Fact]
    public void Classify_RejectedAzureCredential_OverAClientErrorFromTheTokenService_NamesTheCredential()
    {
        // The token service's own 400 is about the identity, not about the request the provider would have served.
        var sut = ResilienceTestSupport.CreateClassifier();

        var classification = sut.Classify(new AuthenticationFailedException(
            "ManagedIdentityCredential could not get a token.",
            new RequestFailedException(400, "Identity not found")));

        classification.Kind.Should().Be(ProviderFailureKind.FatalForChain);
        classification.ReasonCode.Should().Be(ProviderFatalReason.InvalidCredentials);
    }

    [Fact]
    public void Classify_RejectedAzureCredential_CausedByATransportFault_StaysTransient()
    {
        // The other direction: a token request that never completed a round trip is a network blip, and a false
        // chain-fatal is the most damaging mistake this classifier can make.
        var sut = ResilienceTestSupport.CreateClassifier();

        var classification = sut.Classify(new AuthenticationFailedException(
            "ClientSecretCredential authentication failed: unable to reach the token endpoint.",
            new HttpRequestException("Connection refused", new SocketException((int)SocketError.ConnectionRefused))));

        classification.Kind.Should().Be(ProviderFailureKind.Transient);
    }

    [Fact]
    public void Classify_RejectedAzureCredential_CausedByATransientStatus_StaysTransient()
    {
        var sut = ResilienceTestSupport.CreateClassifier();

        var classification = sut.Classify(new AuthenticationFailedException(
            "ManagedIdentityCredential authentication failed.",
            new RequestFailedException((int)HttpStatusCode.ServiceUnavailable, "Service Unavailable")));

        classification.Kind.Should().Be(ProviderFailureKind.Transient);
    }
}
