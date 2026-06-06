using Domain.AI.Identity;
using Domain.Common;

namespace Application.AI.Common.Interfaces.Identity;

/// <summary>
/// Acquires an <see cref="AgentIdentity"/> for the current agent execution from a
/// specific credential kind (federated, managed identity, certificate, client secret,
/// or development fixture). Multiple providers are registered side-by-side; the
/// <see cref="IAgentIdentityResolver"/> orchestrates them in the fixed credential
/// hierarchy order and returns the first success.
/// </summary>
/// <remarks>
/// <para>
/// Providers are stateless and reentrant. The <see cref="CredentialContext"/> passed
/// to <see cref="ResolveAsync"/> carries per-acquisition data (audience, issuer,
/// scopes) so the same provider implementation can run in any deployment without
/// per-environment configuration baked in.
/// </para>
/// <para>
/// Resolution failures are <em>expected</em> outcomes — a missing certificate, a
/// federated-credential exchange that's not configured, a managed-identity request
/// from a non-Azure runtime — and are returned as <see cref="Result{T}"/> failures,
/// not exceptions. The resolver chains providers and only escalates a failure when
/// every provider in the configured order has been tried.
/// </para>
/// <para>
/// Implementations MUST emit stable error codes (prefix <c>agent_identity.</c>) in
/// the failure result so consumers can match on a code rather than parse English
/// from <see cref="Result.Errors"/>. Raw exception text MUST NOT be returned —
/// log the full exception via structured logging, surface a scrubbed code in the
/// result.
/// </para>
/// </remarks>
public interface IAgentCredentialProvider
{
    /// <summary>
    /// The credential kind this provider handles. Used by the
    /// <see cref="IAgentIdentityResolver"/> to dispatch to providers in the fixed
    /// hierarchy order (federated → managed identity → certificate → client secret).
    /// </summary>
    AgentIdentityKind Kind { get; }

    /// <summary>
    /// Attempts to acquire an <see cref="AgentIdentity"/> using this provider's
    /// credential kind.
    /// </summary>
    /// <param name="context">The per-acquisition credential metadata.</param>
    /// <param name="cancellationToken">Cancellation token for the resolution call.</param>
    /// <returns>
    /// A successful <see cref="Result{T}"/> carrying the resolved
    /// <see cref="AgentIdentity"/>, or a failure result with an
    /// <c>agent_identity.*</c> error code when this provider cannot resolve an
    /// identity in the current environment.
    /// </returns>
    Task<Result<AgentIdentity>> ResolveAsync(
        CredentialContext context,
        CancellationToken cancellationToken);
}
