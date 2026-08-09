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

    /// <summary>
    /// True when the failure must not be retried against the same provider —
    /// either kind of fatal.
    /// </summary>
    public bool IsFatal => Kind is ProviderFailureKind.FatalForProvider or ProviderFailureKind.FatalForChain;
}
