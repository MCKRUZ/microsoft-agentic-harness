using System.Security.Claims;
using Application.Core.CQRS.Compliance.EraseSubjectData;
using Azure.AI.AgentServer.Core;
using Microsoft.AspNetCore.Http;

namespace Presentation.FoundryHost;

/// <summary>
/// Turns the verified per-user id Foundry stamps on every protocol request into the request's principal (#1008), so
/// the caller is resolved, like every other host's, through <c>ClaimsPrincipalExtensions.GetUserIdOrNull()</c>.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Where the id comes from.</strong> On container protocol 2.0.0 the platform injects
/// <c>x-agent-user-id</c> (<see cref="PlatformHeaders.UserId"/>) on every request to the Responses and Invocations
/// endpoints, generated from the caller's verified identity (Microsoft Learn, <em>Hosted agent runtime contract</em>).
/// The gateway forwards only an allowlist of caller headers (<c>x-client-*</c>, content negotiation, tracing,
/// <c>user-agent</c>) and drops every other, so a caller cannot send their own copy through Foundry. That holds only
/// while the container's port is reachable through Foundry alone, which is this host's documented trust boundary.
/// </para>
/// <para>
/// <strong>Read from the request, not from <see cref="FoundryAgentRequestContext.Current"/>.</strong> The SDK fills that
/// ambient context in its own middleware, and nothing fixes whether it runs before the middleware this host adds. If
/// it ran after, the ambient value would still be empty here and every request would run unowned.
/// </para>
/// <para>
/// <strong>Nothing is chosen between values.</strong> Each value of the header becomes one <c>sub</c> claim, untrimmed:
/// a header repeated with two different values is an ambiguous identity, which <c>GetUserIdOrNull</c> resolves to
/// <see langword="null"/>, and the knowledge-scope middleware then refuses the request rather than run it unowned. The
/// same value sent twice is one identity. <c>sub</c>, not <c>oid</c>, because the id is opaque: Foundry does not say it
/// is an Entra object id.
/// </para>
/// <para>
/// <strong>Only an id an erasure can name.</strong> An operator erasure or export refuses a subject id that is blank,
/// longer than <see cref="EraseSubjectDataCommandValidator.MaxSubjectIdLength"/>, or padded with whitespace. If any value
/// breaks one of those rules, the principal is authenticated with no identity, so the request is refused too: otherwise
/// its records would carry an owner no erasure is allowed to name, or a blank copy would be dropped and the other
/// value silently chosen.
/// </para>
/// <para>
/// <strong>A missing header.</strong> Off the platform (a local run) no request carries it, and every request gets an
/// unauthenticated principal and runs unscoped, as before #1008. In a Foundry-hosted container
/// (<see cref="FoundryEnvironment.IsHosted"/>) the platform puts it on every protocol request, and every such request was
/// authenticated at the gateway, so a <c>/responses</c> request without it is treated as authenticated with no identity
/// and refused, never run unowned. The hosting package draws the same line for the other 2.0.0 header: a hosted request
/// without <c>x-agent-foundry-call-id</c> already gets a 501 there. Every route it maps for the protocol is under
/// <c>/responses</c>; other paths, such as the readiness probe, never carry the header and stay anonymous. No other
/// header, including a bearer token or an <c>x-client-*</c> value, can establish who the caller is.
/// </para>
/// </remarks>
internal static class FoundryCallerIdentity
{
    /// <summary>The authentication type of a principal built from the platform header.</summary>
    internal const string AuthenticationType = "FoundryPlatformUser";

    /// <summary>The claim type each header value becomes.</summary>
    internal const string SubjectClaimType = "sub";

    /// <summary>The protocol endpoint this host serves; every request to it carries the header when hosted.</summary>
    internal const string ProtocolPath = "/responses";

    /// <summary>Builds the principal the request runs as from the platform's user-id header.</summary>
    /// <param name="request">The inbound request.</param>
    /// <param name="hosted">Whether the container runs in Foundry (<see cref="FoundryEnvironment.IsHosted"/>).</param>
    /// <returns>
    /// An authenticated principal carrying one <c>sub</c> claim per header value; an authenticated principal with no
    /// identity when a value could not be named by an erasure, or when a hosted protocol request lacks the header; or an
    /// unauthenticated principal when the header is absent otherwise.
    /// </returns>
    internal static ClaimsPrincipal FromRequest(HttpRequest request, bool hosted)
    {
        ArgumentNullException.ThrowIfNull(request);

        var values = request.Headers[PlatformHeaders.UserId];
        if (values.Count == 0)
        {
            return hosted && request.Path.StartsWithSegments(ProtocolPath, StringComparison.OrdinalIgnoreCase)
                ? new ClaimsPrincipal(new ClaimsIdentity(AuthenticationType))
                : new ClaimsPrincipal(new ClaimsIdentity());
        }

        if (!values.All(IsNameableByErasure))
            return new ClaimsPrincipal(new ClaimsIdentity(AuthenticationType));

        var claims = values.Select(value => new Claim(SubjectClaimType, value!));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, AuthenticationType));
    }

    /// <summary>Whether an operator erasure or export would accept <paramref name="value"/> as a subject id.</summary>
    private static bool IsNameableByErasure(string? value) =>
        !string.IsNullOrWhiteSpace(value)
        && value.Length <= EraseSubjectDataCommandValidator.MaxSubjectIdLength
        && value == value.Trim();
}
