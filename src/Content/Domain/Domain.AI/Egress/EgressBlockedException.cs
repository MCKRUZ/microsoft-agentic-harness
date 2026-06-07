namespace Domain.AI.Egress;

/// <summary>
/// Thrown by the egress delegating handler when an outbound HTTP request is
/// blocked. Carries the original <see cref="EgressDecision"/> so callers can
/// surface the reason and matched-entry context without re-running policy
/// resolution.
/// </summary>
/// <remarks>
/// <para>
/// This is an exceptional control-flow signal at the <see cref="HttpClient"/>
/// boundary — the harness's CQRS surface uses <c>Result&lt;T&gt;</c> for
/// expected failures, but a blocked request must short-circuit the caller's
/// <c>SendAsync</c> awaitable, and <see cref="HttpClient"/> has no
/// <c>Result&lt;T&gt;</c> escape hatch. Callers that need a non-exception
/// response should catch <see cref="EgressBlockedException"/> at their own
/// boundary and map it onto a <c>Result.Fail</c>.
/// </para>
/// <para>
/// The <see cref="Decision"/> is always non-null and always has
/// <see cref="EgressDecision.Allowed"/> = false. The exception's
/// <see cref="Exception.Message"/> is derived from
/// <see cref="EgressDecision.Reason"/> and the target host — safe to log.
/// </para>
/// </remarks>
public sealed class EgressBlockedException : Exception
{
    /// <summary>The decision that triggered the block. Never null; <see cref="EgressDecision.Allowed"/> is always false.</summary>
    public EgressDecision Decision { get; }

    /// <summary>Initializes a new <see cref="EgressBlockedException"/> wrapping the supplied <paramref name="decision"/>.</summary>
    /// <param name="decision">The deny decision. Must have <see cref="EgressDecision.Allowed"/> = false.</param>
    /// <exception cref="ArgumentNullException"><paramref name="decision"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="decision"/> has <see cref="EgressDecision.Allowed"/> = true.</exception>
    public EgressBlockedException(EgressDecision decision)
        : base(BuildMessage(decision))
    {
        ArgumentNullException.ThrowIfNull(decision);
        if (decision.Allowed)
        {
            throw new ArgumentException(
                "EgressBlockedException requires a deny decision.",
                nameof(decision));
        }
        Decision = decision;
    }

    private static string BuildMessage(EgressDecision decision)
    {
        ArgumentNullException.ThrowIfNull(decision);
        return $"Egress to '{decision.Target.Host}' blocked by harness policy: {decision.Reason}";
    }
}
