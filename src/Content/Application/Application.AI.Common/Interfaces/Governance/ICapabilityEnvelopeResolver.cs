using System.Security.Claims;
using Domain.AI.Bundles;

namespace Application.AI.Common.Interfaces.Governance;

/// <summary>
/// Resolves the <see cref="CapabilityEnvelope"/> the host grants to a bundle run for a given caller. The
/// envelope is the authoritative grant against which a bundle's self-declared tool / MCP / autonomy
/// requests are checked.
/// </summary>
/// <remarks>
/// Resolution is per-credential: an exact subject-claim match takes precedence, then the least-privilege
/// combination of the caller's matching roles, then a configured default. The result is always
/// non-null — an unmatched caller resolves to the fail-closed default (which grants nothing unless an
/// operator configured it), so there is no code path on which "no envelope" silently means "no
/// restriction".
/// </remarks>
public interface ICapabilityEnvelopeResolver
{
    /// <summary>
    /// Resolves the capability envelope for <paramref name="principal"/>.
    /// </summary>
    /// <param name="principal">
    /// The caller's authenticated identity. When null (or carrying no subject/role claims that match a
    /// configured grant), the configured default envelope is returned.
    /// </param>
    /// <returns>The granted envelope; never null.</returns>
    CapabilityEnvelope Resolve(ClaimsPrincipal? principal);
}
