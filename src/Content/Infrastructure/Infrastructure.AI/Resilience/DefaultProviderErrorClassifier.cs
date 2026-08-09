using System.ClientModel;
using System.Net;
using System.Net.Sockets;
using Application.AI.Common.Interfaces.Resilience;
using Azure;
using Domain.AI.Resilience;
using Domain.Common.Config.AI.Resilience;
using Microsoft.Extensions.Options;
using Polly.Timeout;

namespace Infrastructure.AI.Resilience;

/// <summary>
/// Classifies provider failures from their HTTP status and error text, across the differing
/// exception types the supported provider SDKs throw.
/// </summary>
/// <remarks>
/// <para>
/// Three SDKs, three exception types for the same HTTP status: Azure OpenAI and OpenAI throw
/// <see cref="ClientResultException"/>, Azure AI Inference throws
/// <see cref="RequestFailedException"/>, and Anthropic throws <see cref="HttpRequestException"/>.
/// This class reads the status out of whichever shape arrived, so the resilience pipeline can
/// make one decision for all of them.
/// </para>
/// <para>
/// <b>Status wins over message text, in one direction only.</b> A status that positively
/// indicates a transient failure (429, any 5xx) returns immediately and is never overridden by
/// a message match. Message patterns are consulted only for client errors (4xx) and for
/// failures carrying no status at all. This asymmetry is deliberate: a false "fatal" is far
/// more damaging than a false "transient", because it both skips retries and stops the fallback
/// chain, so a broad pattern such as <c>"expired"</c> must not be able to turn a genuine rate
/// limit into a hard stop.
/// </para>
/// <para>
/// Anything not positively recognised is <see cref="ProviderFailureKind.Unknown"/>: not
/// retried, because an unrecognised failure may not be safe to repeat, but still counted
/// against the provider's health, because it remains evidence something is wrong.
/// </para>
/// </remarks>
public sealed class DefaultProviderErrorClassifier : IProviderErrorClassifier
{
    private const int MaxInnerExceptionDepth = 5;

    /// <summary>Wordings that mean the credential itself was rejected.</summary>
    private static readonly string[] CredentialPatterns =
    [
        "invalid api key", "incorrect api key", "invalid_api_key", "invalid x-api-key",
        "api key not valid", "authentication failed", "unauthorized", "access token is invalid"
    ];

    /// <summary>Wordings that mean the account cannot currently be billed.</summary>
    private static readonly string[] BillingPatterns =
    [
        "credit balance is too low", "insufficient credits", "insufficient_quota",
        "billing", "subscription has been suspended", "no longer active"
    ];

    /// <summary>Wordings that mean the credential is valid but not permitted.</summary>
    private static readonly string[] AccessPatterns =
    [
        "account is disabled", "account has been disabled", "account deactivated",
        "permission denied", "access denied"
    ];

    /// <summary>Wordings that mean this provider does not host the requested model.</summary>
    private static readonly string[] ModelNotFoundPatterns =
    [
        "model not found", "model_not_found", "deployment not found",
        "unknown model", "does not exist"
    ];

    private readonly IOptionsMonitor<ResilienceConfig> _config;

    /// <summary>Creates a classifier reading its consumer-supplied patterns from configuration.</summary>
    /// <param name="config">Resilience configuration; only the ErrorClassification section is read.</param>
    public DefaultProviderErrorClassifier(IOptionsMonitor<ResilienceConfig> config)
    {
        _config = config;
    }

    /// <inheritdoc/>
    public ProviderFailureClassification Classify(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        if (TryGetHttpStatus(exception) is int status)
        {
            if (IsTransientStatus(status))
                return ProviderFailureClassification.Transient;

            if (status is >= 400 and < 500)
                return ClassifyByMessage(exception) ?? MapClientErrorStatus(status);

            // A non-error status that still surfaced as an exception is not something we can
            // reason about — treat it as unrecognised rather than guessing.
            return ProviderFailureClassification.Unknown;
        }

        return ClassifyByMessage(exception)
            ?? (IsNetworkLevelFailure(exception)
                ? ProviderFailureClassification.Transient
                : ProviderFailureClassification.Unknown);
    }

    /// <summary>
    /// Reads the HTTP status out of whichever provider exception shape arrived, walking inner
    /// exceptions because SDKs and Polly both wrap.
    /// </summary>
    /// <returns>The status, or <see langword="null"/> when the failure carried none.</returns>
    private static int? TryGetHttpStatus(Exception exception)
    {
        foreach (var current in Unwrap(exception))
        {
            switch (current)
            {
                // Anthropic.SDK and any HttpClient-based provider.
                case HttpRequestException { StatusCode: { } code }:
                    return (int)code;

                // Azure OpenAI and OpenAI (System.ClientModel). Status is 0 when no response
                // was received, which is an absent status rather than a real one.
                case ClientResultException { Status: > 0 } clientResult:
                    return clientResult.Status;

                // Azure AI Inference and other Azure.Core clients. Same zero-means-absent rule.
                case RequestFailedException { Status: > 0 } requestFailed:
                    return requestFailed.Status;
            }
        }

        return null;
    }

    /// <summary>
    /// Statuses that plausibly succeed on repetition. Every 5xx qualifies: a gateway or
    /// upstream fault is the archetypal transient failure.
    /// </summary>
    private static bool IsTransientStatus(int status) => status switch
    {
        (int)HttpStatusCode.RequestTimeout => true,
        (int)HttpStatusCode.Conflict => true,
        425 => true, // Too Early — no HttpStatusCode member on this target framework.
        (int)HttpStatusCode.TooManyRequests => true,
        >= 500 and < 600 => true,
        _ => false
    };

    /// <summary>Maps a 4xx with no recognised wording to its default meaning.</summary>
    private static ProviderFailureClassification MapClientErrorStatus(int status) => status switch
    {
        (int)HttpStatusCode.Unauthorized =>
            ProviderFailureClassification.FatalForChain(ProviderFatalReason.InvalidCredentials),
        (int)HttpStatusCode.PaymentRequired =>
            ProviderFailureClassification.FatalForChain(ProviderFatalReason.BillingExhausted),
        (int)HttpStatusCode.Forbidden =>
            ProviderFailureClassification.FatalForChain(ProviderFatalReason.AccessDenied),
        (int)HttpStatusCode.NotFound =>
            ProviderFailureClassification.FatalForProvider(ProviderFatalReason.ModelNotFound),
        _ => ProviderFailureClassification.FatalForProvider(ProviderFatalReason.RequestRejected)
    };

    /// <summary>
    /// Matches the failure's text against the built-in and consumer-supplied patterns.
    /// Chain-fatal wordings are tested before provider-fatal ones so a consumer can downgrade
    /// a status-derived hard stop but never accidentally soften a billing failure.
    /// </summary>
    /// <returns>A classification, or <see langword="null"/> when no pattern matched.</returns>
    private ProviderFailureClassification? ClassifyByMessage(Exception exception)
    {
        var text = CollectMessages(exception);
        if (text.Length == 0)
            return null;

        var classification = _config.CurrentValue.ErrorClassification;

        if (ContainsAny(text, classification.AdditionalChainFatalMessagePatterns))
            return ProviderFailureClassification.FatalForChain(ProviderFatalReason.Configuration);

        if (ContainsAny(text, classification.AdditionalProviderFatalMessagePatterns))
            return ProviderFailureClassification.FatalForProvider(ProviderFatalReason.RequestRejected);

        if (ContainsAny(text, BillingPatterns))
            return ProviderFailureClassification.FatalForChain(ProviderFatalReason.BillingExhausted);

        if (ContainsAny(text, CredentialPatterns))
            return ProviderFailureClassification.FatalForChain(ProviderFatalReason.InvalidCredentials);

        if (ContainsAny(text, AccessPatterns))
            return ProviderFailureClassification.FatalForChain(ProviderFatalReason.AccessDenied);

        if (ContainsAny(text, ModelNotFoundPatterns))
            return ProviderFailureClassification.FatalForProvider(ProviderFatalReason.ModelNotFound);

        return null;
    }

    /// <summary>
    /// Failure shapes that mean the request never reached the provider — a connection reset,
    /// a DNS failure, a timeout. These are transient by nature and carry no HTTP status.
    /// </summary>
    private static bool IsNetworkLevelFailure(Exception exception)
        => Unwrap(exception).Any(e => e
            is HttpRequestException
            or SocketException
            or IOException
            or TimeoutException
            or TimeoutRejectedException
            or OperationCanceledException);

    /// <summary>Joins the messages of the exception and its inners into one searchable string.</summary>
    private static string CollectMessages(Exception exception)
        => string.Join(' ', Unwrap(exception).Select(e => e.Message).Where(m => !string.IsNullOrEmpty(m)));

    /// <summary>
    /// Walks the exception and its inner exceptions, bounded so a self-referencing or
    /// pathologically deep chain cannot stall a predicate that runs on every failed attempt.
    /// </summary>
    private static IEnumerable<Exception> Unwrap(Exception exception)
    {
        var current = exception;

        for (var depth = 0; current is not null && depth < MaxInnerExceptionDepth; depth++)
        {
            yield return current;

            if (current is AggregateException aggregate)
            {
                current = aggregate.InnerExceptions.Count > 0 ? aggregate.InnerExceptions[0] : null;
                continue;
            }

            current = current.InnerException;
        }
    }

    private static bool ContainsAny(string text, string[] patterns)
    {
        foreach (var pattern in patterns)
        {
            if (!string.IsNullOrWhiteSpace(pattern)
                && text.Contains(pattern, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
