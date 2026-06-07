using Domain.AI.Egress;
using Domain.AI.Identity;

namespace Application.AI.Common.Interfaces.Egress;

/// <summary>
/// Selects the <see cref="IEgressPolicy"/> applicable to a given
/// <see cref="AgentIdentity"/>. The egress delegating handler asks the resolver
/// once per outbound request before consulting the chosen policy.
/// </summary>
/// <remarks>
/// <para>
/// PR-3b ships a default implementation that returns a single
/// configuration-bound policy regardless of identity — the layer is functional
/// end-to-end with that fallback. PR-3c replaces the implementation with a
/// per-skill resolver backed by the skill manifest, so identity-specific
/// allowlists override the default.
/// </para>
/// <para>
/// The resolver is invoked on the request-processing hot path. Implementations
/// must be cheap; lookup logic over a cached registry, not synchronous I/O.
/// </para>
/// </remarks>
public interface IEgressPolicyResolver
{
    /// <summary>Resolve the policy for the supplied identity.</summary>
    /// <param name="identity">The agent identity attached to the outbound request.</param>
    /// <returns>The applicable <see cref="IEgressPolicy"/>; never null.</returns>
    IEgressPolicy ResolveFor(AgentIdentity identity);
}
