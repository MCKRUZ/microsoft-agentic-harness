using System.Security.Claims;

namespace Presentation.Common.Compliance;

/// <summary>
/// DI marker whose presence records that a host deliberately serves the compliance API (self-service and operator
/// erasure, document removal, and personal-data export), together with the host's own answer to which sign-in schemes
/// make every caller the same person. Registered only by <see cref="ComplianceApiMvcBuilderExtensions.AddComplianceApi"/>;
/// <see cref="RequiresComplianceApiOptInAttribute"/> un-matches every compliance route when it is absent.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Why the host names its schemes.</strong> A sign-in bypass authenticates every request as one synthetic person,
/// so "erase my data" under it would erase everyone's, and an operator action could not say which operator asked. Each
/// host has its own bypass handlers (AgentHub's <c>DevAuthHandler</c> and <c>SelfHostedAuthHandler</c>, the Execution
/// API's anonymous mode), and the shared controllers cannot know them, so the host states them when it mounts the API.
/// A host must state them to mount it at all: there is no default that could silently miss one.
/// </para>
/// <para>
/// The schemes are matched against each identity's <see cref="ClaimsIdentity.AuthenticationType"/>, ordinally, on every
/// identity of the principal: a bypass identity next to a real one still makes the caller shared.
/// </para>
/// </remarks>
public sealed class ComplianceApiMarker
{
    private readonly string[] _sharedIdentitySchemes;

    /// <summary>Initializes a new instance of the <see cref="ComplianceApiMarker"/> class.</summary>
    /// <param name="sharedIdentitySchemes">
    /// The authentication schemes under which every caller is the same synthetic person. Empty for a host with no sign-in
    /// bypass at all; never <see langword="null"/>, and no entry may be blank.
    /// </param>
    /// <exception cref="ArgumentException">An entry is <see langword="null"/>, empty or whitespace.</exception>
    public ComplianceApiMarker(IEnumerable<string> sharedIdentitySchemes)
    {
        ArgumentNullException.ThrowIfNull(sharedIdentitySchemes);
        _sharedIdentitySchemes = sharedIdentitySchemes.ToArray();
        if (_sharedIdentitySchemes.Any(string.IsNullOrWhiteSpace))
            throw new ArgumentException("A shared-identity scheme name may not be blank.", nameof(sharedIdentitySchemes));
    }

    /// <summary>The authentication schemes under which every caller is the same synthetic person.</summary>
    public IReadOnlyList<string> SharedIdentitySchemes => _sharedIdentitySchemes;

    /// <summary>
    /// Whether <paramref name="user"/> was signed in by one of <see cref="SharedIdentitySchemes"/> rather than by a real
    /// identity provider, so that an action scoped to "this caller" would act on everyone's data.
    /// </summary>
    /// <param name="user">The caller.</param>
    /// <returns><see langword="true"/> when any authenticated identity of the caller came from a shared scheme.</returns>
    public bool IsSharedSyntheticIdentity(ClaimsPrincipal user)
    {
        ArgumentNullException.ThrowIfNull(user);
        return user.Identities.Any(i =>
            i.IsAuthenticated && i.AuthenticationType is { } scheme
            && _sharedIdentitySchemes.Contains(scheme, StringComparer.Ordinal));
    }
}
