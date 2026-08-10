namespace Domain.AI.Resilience;

/// <summary>
/// Classifies a provider failure by what the harness can usefully do about it.
/// </summary>
/// <remarks>
/// <para>
/// The resilience pipeline decides retry, circuit-breaker accounting, and cross-provider
/// fallback from this value rather than from the exception's .NET type. Provider SDKs
/// disagree on the exception type they throw for the same HTTP status — Azure OpenAI throws
/// <c>ClientResultException</c>, Azure AI Inference throws <c>RequestFailedException</c>, and
/// Anthropic throws <c>HttpRequestException</c> — so type-based handling silently covers only
/// whichever provider happens to match.
/// </para>
/// <para>
/// The three behaviours each kind drives:
/// <list type="table">
///   <listheader><term>Kind</term><description>Retry / breaker / fallback</description></listheader>
///   <item>
///     <term><see cref="Transient"/></term>
///     <description>Retried, counted toward the breaker's failure ratio, falls back on exhaustion.</description>
///   </item>
///   <item>
///     <term><see cref="FatalForProvider"/></term>
///     <description>Not retried, not counted, but does fall back — another provider may serve it.</description>
///   </item>
///   <item>
///     <term><see cref="FatalForChain"/></term>
///     <description>Not retried, not counted, and does <b>not</b> fall back — rotating providers
///     cannot fix a rejected credential or an exhausted balance.</description>
///   </item>
///   <item>
///     <term><see cref="Unknown"/></term>
///     <description>Not retried (an unrecognised failure may not be safe to repeat) but still
///     counted toward the breaker, because it remains evidence this provider is failing.</description>
///   </item>
/// </list>
/// </para>
/// </remarks>
public enum ProviderFailureKind
{
    /// <summary>
    /// An unrecognised failure. Not retried, but counted toward the circuit breaker and
    /// eligible for fallback. This is the deliberate default for anything the classifier
    /// cannot positively identify.
    /// </summary>
    Unknown = 0,

    /// <summary>
    /// A failure that plausibly succeeds on repetition — rate limiting, a gateway error,
    /// a timeout, or a connection-level fault with no HTTP status at all.
    /// </summary>
    Transient = 1,

    /// <summary>
    /// A failure that will never succeed against <i>this</i> provider however many times it is
    /// repeated, but that another provider in the chain may serve — a missing model deployment
    /// or a request this provider rejects as malformed.
    /// </summary>
    FatalForProvider = 2,

    /// <summary>
    /// A failure that no provider in the chain can serve, because its cause is shared
    /// configuration rather than provider state — a rejected API key, an exhausted credit
    /// balance, a disabled account. Retrying burns wall-clock and rotating providers hides
    /// the real cause behind a generic availability failure.
    /// </summary>
    FatalForChain = 3
}
