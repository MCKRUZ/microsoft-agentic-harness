using Domain.AI.Identity;
using Domain.Common;

namespace Application.AI.Common.Interfaces.Identity;

/// <summary>
/// Orchestrates the registered <see cref="IAgentCredentialProvider"/>s in the fixed
/// credential-hierarchy order and returns the first successfully-resolved
/// <see cref="AgentIdentity"/>. Consumed by <c>AgentFactory</c> at agent
/// construction to stamp the identity onto the agent-execution scope.
/// </summary>
/// <remarks>
/// <para>
/// The hierarchy order is invariant: federated credential → managed identity →
/// certificate → client secret. A consumer can register fewer providers (e.g. only
/// federated + managed identity) but cannot reorder the providers that are
/// registered. This is enforced by the resolver implementation, not by configuration.
/// </para>
/// <para>
/// The <see cref="AgentIdentityKind.Development"/> kind is only honoured when the
/// host environment is Development; production resolvers reject it even when
/// registered, to prevent a misconfigured deployment from running with a fixture
/// identity.
/// </para>
/// <para>
/// Returns a failure result with the aggregated <c>agent_identity.no_provider_succeeded</c>
/// error code when every registered provider has been tried and none succeeded.
/// Caller MUST NOT proceed without an identity when
/// <see cref="Domain.Common.Config.AI.AppConfig"/>'s identity flag is enabled.
/// </para>
/// </remarks>
public interface IAgentIdentityResolver
{
    /// <summary>
    /// Resolves an agent identity by walking the credential-hierarchy provider chain.
    /// </summary>
    /// <param name="context">The per-acquisition credential metadata.</param>
    /// <param name="cancellationToken">Cancellation token for the resolution call.</param>
    /// <returns>
    /// A successful <see cref="Result{T}"/> carrying the first successfully-resolved
    /// <see cref="AgentIdentity"/>, or a failure result with
    /// <c>agent_identity.no_provider_succeeded</c> when no registered provider could
    /// resolve an identity in the current environment.
    /// </returns>
    Task<Result<AgentIdentity>> ResolveAsync(
        CredentialContext context,
        CancellationToken cancellationToken);
}
