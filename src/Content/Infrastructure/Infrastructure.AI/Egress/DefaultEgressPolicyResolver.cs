using Application.AI.Common.Interfaces.Egress;
using Domain.AI.Egress;
using Domain.AI.Identity;

namespace Infrastructure.AI.Egress;

/// <summary>
/// PR-3b default <see cref="IEgressPolicyResolver"/>. Returns a single
/// configuration-bound <see cref="IEgressPolicy"/> regardless of identity.
/// PR-3c replaces this with a per-skill resolver backed by the skill manifest.
/// </summary>
/// <remarks>
/// <para>
/// The resolver is the seam that lets identity-specific allowlists override the
/// global default. Until PR-3c wires the skill manifest, every identity sees
/// the same default allowlist drawn from <c>AppConfig.AI.Egress.DefaultAllowlist</c>.
/// That default is empty out of the box, so the layer is default-deny in the
/// absence of explicit consumer configuration.
/// </para>
/// </remarks>
public sealed class DefaultEgressPolicyResolver : IEgressPolicyResolver
{
    private readonly IEgressPolicy _defaultPolicy;

    /// <summary>Initializes a new <see cref="DefaultEgressPolicyResolver"/>.</summary>
    /// <param name="defaultPolicy">The default policy returned for every identity.</param>
    public DefaultEgressPolicyResolver(IEgressPolicy defaultPolicy)
    {
        ArgumentNullException.ThrowIfNull(defaultPolicy);
        _defaultPolicy = defaultPolicy;
    }

    /// <inheritdoc />
    public IEgressPolicy ResolveFor(AgentIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        return _defaultPolicy;
    }
}
