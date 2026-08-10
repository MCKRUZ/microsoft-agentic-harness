using System.ClientModel;
using System.Net;
using System.Net.Sockets;
using System.Security.Authentication;
using Application.AI.Common.Interfaces.Resilience;
using Azure;
using Domain.AI.Resilience;
using Domain.Common.Config.AI.Resilience;
using Microsoft.Extensions.Options;
using Polly.CircuitBreaker;
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
/// <b>Positive evidence wins over message text, in one direction only.</b> A status that
/// indicates a transient failure (429, any 5xx), or an unambiguous transport fault such as a
/// failed TLS handshake, returns immediately and is never overridden by a message match.
/// Message patterns are consulted only for client errors (4xx) and for failures that carry
/// neither. This asymmetry is deliberate: a false "fatal" is far more damaging than a false
/// "transient", because it both skips retries and stops the fallback chain, so a broad pattern
/// such as <c>"expired"</c> must not be able to turn a genuine rate limit — or a network blip —
/// into a hard stop.
/// </para>
/// <para>
/// Anything not positively recognised is <see cref="ProviderFailureKind.Unknown"/>: not
/// retried, because an unrecognised failure may not be safe to repeat, but still counted
/// against the provider's health, because it remains evidence something is wrong.
/// </para>
/// </remarks>
public class DefaultProviderErrorClassifier : IProviderErrorClassifier
{
    private const int MaxInnerExceptionDepth = 5;

    /// <summary>Wordings that mean the credential itself was rejected.</summary>
    private static readonly string[] CredentialPatterns =
    [
        "invalid api key", "incorrect api key", "invalid_api_key", "invalid x-api-key",
        "api key not valid", "authentication failed", "unauthorized", "access token is invalid"
    ];

    /// <summary>
    /// Wordings that mean the account cannot currently be billed.
    /// </summary>
    /// <remarks>
    /// Every entry is anchored to a phrase that cannot plausibly appear in an unrelated error.
    /// A bare <c>"billing"</c> and a bare <c>"no longer active"</c> were both tried and removed:
    /// Azure OpenAI reports a retired deployment as a 400 reading "The model ... is no longer
    /// active", which the bare pattern turned into a chain-fatal billing error — halting the
    /// fallback chain and misnaming the cause for a problem another chain member could serve.
    /// This is the failure mode the whole design calls the most damaging one, so these patterns
    /// stay narrow even at the cost of missing a wording.
    /// </remarks>
    private static readonly string[] BillingPatterns =
    [
        "credit balance is too low", "insufficient credits", "insufficient_quota",
        "billing details", "billing account", "billing has been disabled",
        "subscription has been suspended", "subscription has expired"
    ];

    /// <summary>
    /// Wordings that mean the whole account is shut off, which every provider sharing it hits.
    /// </summary>
    private static readonly string[] AccountDisabledPatterns =
    [
        "account is disabled", "account has been disabled", "account deactivated"
    ];

    /// <summary>
    /// Wordings that mean this particular resource refused the caller.
    /// </summary>
    /// <remarks>
    /// Split out from the account-level wordings above, which stop the chain. A refusal scoped to
    /// one resource must not: Azure OpenAI returns 403 for a network ACL or disabled public
    /// access on a single endpoint, and AI Foundry for a deployment the caller's role cannot
    /// invoke. Treating those as chain-fatal abandons a healthy secondary that would have served
    /// the request. A genuinely account-wide 403 still surfaces — it just costs one attempt per
    /// chain member to establish, which is the cheap direction to be wrong in.
    /// </remarks>
    private static readonly string[] ResourceForbiddenPatterns =
    [
        "permission denied", "access denied"
    ];

    /// <summary>
    /// Wordings that mean this provider does not host the requested model.
    /// </summary>
    /// <remarks>
    /// A bare <c>"does not exist"</c> was tried and removed — it also matches unrelated 4xx
    /// bodies such as "Assistant with id '...' does not exist", suppressing retry for a request
    /// that is merely malformed and reporting the wrong cause to the operator.
    /// </remarks>
    private static readonly string[] ModelNotFoundPatterns =
    [
        "model not found", "model_not_found", "deployment not found", "unknown model",
        "model does not exist", "deployment does not exist", "resource does not exist"
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

        // One bounded walk of the exception chain gathers everything the decision needs. It used
        // to take three (status, then messages, then network shape), which meant three iterator
        // allocations and three traversals on every failed attempt — on the path that only runs
        // during an incident.
        var facts = Inspect(exception);

        if (facts.Status is int status)
        {
            if (IsTransientStatus(status))
                return ProviderFailureClassification.Transient;

            if (status is >= 400 and < 500)
                return ClassifyByMessage(facts.Messages) ?? MapClientErrorStatus(status);

            // A non-error status that still surfaced as an exception is not something we can
            // reason about — treat it as unrecognised rather than guessing.
            return ProviderFailureClassification.Unknown;
        }

        // A positively identified transport fault outranks message text, for the same reason a
        // transient status does. The request never reached the provider, so nothing in the text
        // can be a verdict on the credential, the balance, or the model.
        if (facts.IsTransportFault)
            return ProviderFailureClassification.Transient;

        return ClassifyByMessage(facts.Messages)
            ?? (facts.IsNetworkLevel
                ? ProviderFailureClassification.Transient
                : ProviderFailureClassification.Unknown);
    }

    /// <summary>
    /// Walks the exception and its inner exceptions once, collecting the status, the messages,
    /// and whether any link is a network-level fault.
    /// </summary>
    /// <remarks>
    /// Bounded by <see cref="MaxInnerExceptionDepth"/> so a self-referencing or pathologically
    /// deep chain cannot stall a predicate that runs on every failed attempt. SDKs and Polly both
    /// wrap, so the status is often not on the outermost exception.
    /// </remarks>
    private FailureFacts Inspect(Exception exception)
    {
        var messages = new List<string>(MaxInnerExceptionDepth);
        int? status = null;
        var isNetworkLevel = false;
        var isTransportFault = false;
        var current = exception;

        for (var depth = 0; current is not null && depth < MaxInnerExceptionDepth; depth++)
        {
            status ??= TryGetHttpStatus(current);
            isNetworkLevel |= IsNetworkLevelFailure(current);
            isTransportFault |= IsTransportFault(current);

            if (!string.IsNullOrEmpty(current.Message))
                messages.Add(current.Message);

            current = current is AggregateException { InnerExceptions.Count: > 0 } aggregate
                ? aggregate.InnerExceptions[0]
                : current.InnerException;
        }

        return new FailureFacts(status, messages, isNetworkLevel, isTransportFault);
    }

    /// <summary>What one walk of the exception chain yielded.</summary>
    private readonly record struct FailureFacts(
        int? Status, IReadOnlyList<string> Messages, bool IsNetworkLevel, bool IsTransportFault);

    /// <summary>
    /// Reads the HTTP status out of a single exception, whichever provider SDK shape it is.
    /// </summary>
    /// <remarks>
    /// This is the seam to override when adding a provider whose SDK carries the status somewhere
    /// else. Everything below it — the status-to-classification mapping and the message patterns —
    /// is shared, so teaching the harness a fourth SDK means overriding this one method rather
    /// than reimplementing the classifier. Called once per link of the exception chain; return
    /// <see langword="null"/> for shapes you do not recognise so the base cases still apply.
    /// </remarks>
    /// <param name="exception">A single exception from the chain, already unwrapped.</param>
    /// <returns>The status, or <see langword="null"/> when this exception carries none.</returns>
    protected virtual int? TryGetHttpStatus(Exception exception) => exception switch
    {
        // Anthropic.SDK and any HttpClient-based provider.
        HttpRequestException { StatusCode: { } code } => (int)code,

        // Azure OpenAI and OpenAI (System.ClientModel). Status is 0 when no response was
        // received, which is an absent status rather than a real one.
        ClientResultException { Status: > 0 } clientResult => clientResult.Status,

        // Azure AI Inference and other Azure.Core clients. Same zero-means-absent rule.
        RequestFailedException { Status: > 0 } requestFailed => requestFailed.Status,

        _ => null
    };

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
        // Provider-fatal, not chain-fatal: unlike 401 and 402, a 403 is routinely scoped to one
        // resource — a network ACL, a private endpoint, a role missing on one deployment. See
        // ResourceForbiddenPatterns.
        (int)HttpStatusCode.Forbidden =>
            ProviderFailureClassification.FatalForProvider(ProviderFatalReason.AccessDenied),
        (int)HttpStatusCode.NotFound =>
            ProviderFailureClassification.FatalForProvider(ProviderFatalReason.ModelNotFound),
        _ => ProviderFailureClassification.FatalForProvider(ProviderFatalReason.RequestRejected)
    };

    /// <summary>
    /// Matches the failure's text against the built-in and consumer-supplied patterns.
    /// Chain-fatal wordings are tested before provider-fatal ones so a consumer can downgrade
    /// a status-derived hard stop but never accidentally soften a billing failure.
    /// </summary>
    /// <param name="messages">The messages gathered from the exception chain.</param>
    /// <returns>A classification, or <see langword="null"/> when no pattern matched.</returns>
    private ProviderFailureClassification? ClassifyByMessage(IReadOnlyList<string> messages)
    {
        if (messages.Count == 0)
            return null;

        var classification = _config.CurrentValue.ErrorClassification;

        // Every chain-fatal wording — the consumer's own first, then the built-ins — is tested
        // before any provider-fatal one. The consumer's provider-fatal list used to sit second,
        // which inverted this for the built-ins: a consumer adding a broad wording such as
        // "account" to route one regional message onward also caught "billing account", so a
        // shared billing failure rotated through every provider and was reported as the whole
        // chain being exhausted. That is the failure this class exists to prevent.
        if (ContainsAny(messages, classification.AdditionalChainFatalMessagePatterns))
            return ProviderFailureClassification.FatalForChain(ProviderFatalReason.Configuration);

        if (ContainsAny(messages, BillingPatterns))
            return ProviderFailureClassification.FatalForChain(ProviderFatalReason.BillingExhausted);

        if (ContainsAny(messages, CredentialPatterns))
            return ProviderFailureClassification.FatalForChain(ProviderFatalReason.InvalidCredentials);

        if (ContainsAny(messages, AccountDisabledPatterns))
            return ProviderFailureClassification.FatalForChain(ProviderFatalReason.AccessDenied);

        if (ContainsAny(messages, classification.AdditionalProviderFatalMessagePatterns))
            return ProviderFailureClassification.FatalForProvider(ProviderFatalReason.RequestRejected);

        if (ContainsAny(messages, ResourceForbiddenPatterns))
            return ProviderFailureClassification.FatalForProvider(ProviderFatalReason.AccessDenied);

        if (ContainsAny(messages, ModelNotFoundPatterns))
            return ProviderFailureClassification.FatalForProvider(ProviderFatalReason.ModelNotFound);

        return null;
    }

    /// <summary>
    /// Failure shapes that say, on their own evidence, that the request never completed a round
    /// trip to the provider. Unlike <see cref="IsNetworkLevelFailure"/>, every shape here is
    /// unambiguous, which is what earns it the right to outrank message text.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The distinction exists because <see cref="HttpRequestException"/> is overloaded: the HTTP
    /// stack raises it for transport faults, and Anthropic's SDK also authors it by hand to report
    /// ordinary API errors. Its <see cref="HttpRequestException.HttpRequestError"/> separates the
    /// two — the stack sets a category such as <c>SecureConnectionError</c>, while a hand-built
    /// instance leaves it <c>Unknown</c>. So the property, not the type, is the signal.
    /// </para>
    /// <para>
    /// This exists because of a real misclassification: a failed TLS handshake surfaces as a
    /// status-less <see cref="HttpRequestException"/> wrapping an
    /// <see cref="AuthenticationException"/> reading "Authentication failed, see inner exception."
    /// That text matches the credential pattern, so a connectivity blip was reported as a rejected
    /// API key — skipping retries and halting the fallback chain, the exact outcome this class
    /// calls its most damaging.
    /// </para>
    /// <para>
    /// <see cref="OperationCanceledException"/> is deliberately absent: a cancellation is as
    /// likely to be the caller withdrawing as the transport failing, so it does not gain the
    /// power to overrule a message. Note what this does <i>not</i> do — a cancellation carrying
    /// no recognised wording still reaches <see cref="IsNetworkLevelFailure"/> below and is
    /// classified transient, so a caller pressing Stop is still counted against provider health.
    /// Correcting that needs an outcome meaning "not evidence, not retryable, not worth failing
    /// over", which this type does not have; it is tracked separately rather than smuggled in
    /// here.
    /// </para>
    /// </remarks>
    private static bool IsTransportFault(Exception exception) => exception switch
    {
        HttpRequestException { HttpRequestError: not HttpRequestError.Unknown } => true,
        SocketException or AuthenticationException or IOException
            or TimeoutException or TimeoutRejectedException => true,
        _ => false
    };

    /// <summary>
    /// The shapes that suggest a network-level failure without proving one — the residual left
    /// after <see cref="IsTransportFault"/> has claimed everything unambiguous.
    /// </summary>
    /// <remarks>
    /// Only two shapes qualify, and both are ambiguous for the same reason: something other than
    /// the transport can raise them. A bare <see cref="HttpRequestException"/> is how Anthropic's
    /// SDK reports an ordinary API error, and an <see cref="OperationCanceledException"/> is as
    /// likely to be the caller withdrawing. So they are consulted only after message text, where
    /// a recognised wording can still overrule them.
    /// <para>
    /// The genuinely unambiguous shapes — sockets, I/O, timeouts, TLS — are deliberately absent
    /// rather than duplicated here. This list is read only on the path where
    /// <see cref="IsTransportFault"/> matched nothing anywhere in the chain, so repeating them
    /// could not change an outcome. That makes the two lists disjoint, which is the point: each
    /// shape appears once, under the precedence it earns. It does mean the split depends on
    /// <see cref="Classify"/> testing transport faults first.
    /// </para>
    /// </remarks>
    private static bool IsNetworkLevelFailure(Exception exception)
        => exception is HttpRequestException or OperationCanceledException;

    /// <summary>
    /// Whether any of the failure's messages contains any of the patterns, case-insensitively.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Messages are scanned individually rather than joined into one string. The join allocated a
    /// copy of the entire provider error text — commonly a verbose 4xx body — on every
    /// classification, purely to give this method one argument. Scanning separately reads the same
    /// characters and allocates nothing. It also stops a pattern from matching across a message
    /// boundary, which was never intended and was only ever an artefact of the joining separator.
    /// </para>
    /// <para>
    /// The null check is not defensive padding. The configuration binder never yields null, but
    /// the property is a settable array, so a consumer's <c>services.Configure</c> can. This runs
    /// inside Polly's failure predicate, where a NullReferenceException would replace the real
    /// provider error with a meaningless one on every failed attempt — visible only mid-incident.
    /// </para>
    /// </remarks>
    private static bool ContainsAny(IReadOnlyList<string> messages, string[]? patterns)
    {
        if (patterns is null)
            return false;

        foreach (var pattern in patterns)
        {
            if (string.IsNullOrWhiteSpace(pattern))
                continue;

            foreach (var message in messages)
            {
                if (message.Contains(pattern, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }

        return false;
    }
}
