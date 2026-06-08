using Domain.AI.A2A;
using Domain.Common;

namespace Application.AI.Common.Interfaces.A2A;

/// <summary>
/// Abstracts the authentication strategy for A2A calls: in-process trust of
/// the ambient identity context, or cross-process mutual TLS plus
/// workload-identity JWT.
/// </summary>
/// <remarks>
/// <para>
/// One implementation per transport. Client-side:
/// <see cref="StampOutboundCredentialsAsync"/> attaches transport-specific
/// auth material (e.g. a JWT) before the request goes out. Server-side:
/// <see cref="ValidateInboundAsync"/> verifies what arrived and returns the
/// authenticated caller's <c>callerAgentId</c> string (or a failure code).
/// </para>
/// <para>
/// The auth provider returns a <see cref="Result{T}"/> rather than throwing on
/// rejection — the server treats rejection as a stable failure code
/// (<c>a2a.auth_rejected</c>) rather than an exception, so observability
/// dashboards can distinguish auth failures from skill failures cleanly.
/// </para>
/// </remarks>
public interface IA2AAuthenticationProvider
{
    /// <summary>
    /// Identifier of the auth scheme: one of
    /// <c>A2AConventions.AuthSchemeInProcess</c> or
    /// <c>A2AConventions.AuthSchemeMtlsJwt</c>. Stamped on both spans.
    /// </summary>
    string SchemeName { get; }

    /// <summary>
    /// Caller-side hook. Returns the transport-specific auth header(s) to
    /// attach to the outbound request. For the in-process provider this is a
    /// no-op (returns an empty dictionary); for the cross-process provider it
    /// returns an <c>Authorization: Bearer &lt;jwt&gt;</c> header.
    /// </summary>
    /// <param name="envelope">The envelope being sent (caller id is required —
    /// JWT subject claim is derived from it).</param>
    /// <param name="cancellationToken">Cooperative cancellation token.</param>
    /// <returns>A bounded set of transport headers to attach, or
    /// <see cref="Result.Fail(string[])"/> with a stable code if credentials
    /// cannot be acquired.</returns>
    Task<Result<IReadOnlyDictionary<string, string>>> StampOutboundCredentialsAsync(
        A2AEnvelope envelope,
        CancellationToken cancellationToken);

    /// <summary>
    /// Server-side hook. Validates the inbound envelope (and any
    /// transport-attached credentials accumulated by the listener) and returns
    /// the authoritative <c>callerAgentId</c> the call should be attributed to.
    /// </summary>
    /// <param name="envelope">The envelope as it arrived on the wire.</param>
    /// <param name="transportHeaders">Transport-level headers the listener
    /// observed (e.g. <c>Authorization</c>, peer cert subject). May be empty
    /// for in-process calls.</param>
    /// <param name="cancellationToken">Cooperative cancellation token.</param>
    /// <returns>The authoritative caller agent id on success, or
    /// <see cref="Result.Fail(string[])"/> with a stable
    /// <c>a2a.auth_rejected</c> code on failure.</returns>
    Task<Result<string>> ValidateInboundAsync(
        A2AEnvelope envelope,
        IReadOnlyDictionary<string, string> transportHeaders,
        CancellationToken cancellationToken);
}
