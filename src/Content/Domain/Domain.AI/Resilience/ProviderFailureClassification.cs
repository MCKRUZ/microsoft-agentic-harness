namespace Domain.AI.Resilience;

/// <summary>
/// The result of classifying a provider failure: what the harness should do about it
/// (<see cref="Kind"/>) and, when the answer is "stop", a stable code naming why
/// (<see cref="ReasonCode"/>).
/// </summary>
/// <param name="Kind">Drives retry, circuit-breaker accounting, and cross-provider fallback.</param>
/// <param name="ReasonCode">
/// A <see cref="ProviderFatalReason"/> constant when <paramref name="Kind"/> is
/// <see cref="ProviderFailureKind.FatalForChain"/> or <see cref="ProviderFailureKind.FatalForProvider"/>;
/// <see langword="null"/> otherwise. Never contains provider-supplied text.
/// </param>
public readonly record struct ProviderFailureClassification(
    ProviderFailureKind Kind,
    string? ReasonCode = null)
{
    /// <summary>A transient failure — retry it and count it toward the circuit breaker.</summary>
    public static ProviderFailureClassification Transient { get; } = new(ProviderFailureKind.Transient);

    /// <summary>An unrecognised failure — do not retry, but still count it toward the circuit breaker.</summary>
    public static ProviderFailureClassification Unknown { get; } = new(ProviderFailureKind.Unknown);

    /// <summary>Creates a classification that stops this provider but allows fallback to the next.</summary>
    /// <param name="reasonCode">A <see cref="ProviderFatalReason"/> constant.</param>
    public static ProviderFailureClassification FatalForProvider(string reasonCode)
        => new(ProviderFailureKind.FatalForProvider, reasonCode);

    /// <summary>Creates a classification that stops the whole chain — no retry, no fallback.</summary>
    /// <param name="reasonCode">A <see cref="ProviderFatalReason"/> constant.</param>
    public static ProviderFailureClassification FatalForChain(string reasonCode)
        => new(ProviderFailureKind.FatalForChain, reasonCode);

    /// <summary>The caller withdrew — not evidence about any provider. No reason code: nothing was rejected.</summary>
    public static ProviderFailureClassification CallerCancelled { get; } = new(ProviderFailureKind.CallerCancelled);

    /// <summary>
    /// Whether the same provider should be asked again.
    /// </summary>
    /// <remarks>
    /// Only a positively-recognised transient failure is repeated. An unrecognised one is not:
    /// it is as likely to be a defect in our own code as a provider blip, and repeating a
    /// non-idempotent call has a real cost.
    /// </remarks>
    public bool ShouldRetry => Kind is ProviderFailureKind.Transient;

    /// <summary>
    /// Whether this failure is evidence about the provider's health, and so should count
    /// toward its circuit breaker's failure ratio.
    /// </summary>
    /// <remarks>
    /// A rejected credential says nothing about whether the provider is up. Letting it trip the
    /// breaker is precisely what buries the real cause under "circuit open for provider X".
    /// An unrecognised failure does still count — it remains evidence something is wrong.
    /// </remarks>
    public bool CountsTowardHealth => Kind is ProviderFailureKind.Transient or ProviderFailureKind.Unknown;

    /// <summary>
    /// Whether the whole fallback chain should be abandoned rather than advancing to the next
    /// provider.
    /// </summary>
    /// <remarks>
    /// True only for causes that live in shared configuration, which every provider in the chain
    /// would hit identically. <see cref="ProviderFailureKind.CallerCancelled"/> is deliberately
    /// excluded: it also must not fall back, but <see cref="IsCallerCancellation"/> is the
    /// signal for that, because the two are reported differently — a chain stop surfaces
    /// <c>ProviderFatalErrorException</c> naming a cause, cancellation propagates the caller's
    /// own exception unchanged.
    /// </remarks>
    public bool StopsChain => Kind is ProviderFailureKind.FatalForChain;

    /// <summary>
    /// Whether this failure is the caller withdrawing rather than any provider signal —
    /// the original exception should propagate immediately, not advance to the next provider.
    /// </summary>
    public bool IsCallerCancellation => Kind is ProviderFailureKind.CallerCancelled;
}
