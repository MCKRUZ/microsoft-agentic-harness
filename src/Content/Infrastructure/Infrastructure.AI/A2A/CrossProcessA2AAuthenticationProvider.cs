using Application.AI.Common.Interfaces.A2A;
using Domain.AI.A2A;
using Domain.AI.Telemetry.Conventions;
using Domain.Common;
using Microsoft.Extensions.Logging;

namespace Infrastructure.AI.A2A;

/// <summary>
/// Cross-process implementation of <see cref="IA2AAuthenticationProvider"/>.
/// Stamps an outbound <c>Authorization: Bearer &lt;jwt&gt;</c> header on the
/// caller side and validates the inbound JWT on the server side via the
/// registered <see cref="IA2ATokenAcquirer"/> / <see cref="IA2ATokenValidator"/>.
/// </summary>
/// <remarks>
/// <para>
/// The full validation chain on the server side runs in this order: (1) mTLS
/// peer-cert acceptance happens at the Kestrel listener BEFORE the request
/// reaches the harness — Kestrel hands us an already-authenticated client
/// connection; (2) this provider validates the JWT signature, issuer,
/// audience, expiry; (3) the JWT <c>sub</c> claim becomes the
/// authoritative caller id (overriding whatever the envelope declared); (4)
/// the server compares the JWT sub against the envelope's declared
/// <c>callerAgentId</c> and rejects mismatches as <c>a2a.auth_rejected</c>.
/// </para>
/// <para>
/// Authoritative caller id from JWT — not the envelope — protects against a
/// caller that holds a valid token for agent A but tries to declare itself
/// as agent B in the envelope payload.
/// </para>
/// </remarks>
public sealed class CrossProcessA2AAuthenticationProvider : IA2AAuthenticationProvider
{
    private const string AuthorizationHeader = "Authorization";
    private const string BearerScheme = "Bearer ";

    private readonly IA2ATokenAcquirer _tokenAcquirer;
    private readonly IA2ATokenValidator _tokenValidator;
    private readonly ILogger<CrossProcessA2AAuthenticationProvider> _logger;

    /// <summary>Creates a new cross-process auth provider.</summary>
    /// <param name="tokenAcquirer">Consumer-supplied JWT acquisition strategy.</param>
    /// <param name="tokenValidator">Consumer-supplied JWT validation strategy.</param>
    /// <param name="logger">Logger for diagnostics. Exception text is logged here, never on the wire.</param>
    public CrossProcessA2AAuthenticationProvider(
        IA2ATokenAcquirer tokenAcquirer,
        IA2ATokenValidator tokenValidator,
        ILogger<CrossProcessA2AAuthenticationProvider> logger)
    {
        _tokenAcquirer = tokenAcquirer;
        _tokenValidator = tokenValidator;
        _logger = logger;
    }

    /// <inheritdoc />
    public string SchemeName => A2AConventions.AuthSchemeMtlsJwt;

    /// <inheritdoc />
    public async Task<Result<IReadOnlyDictionary<string, string>>> StampOutboundCredentialsAsync(
        A2AEnvelope envelope,
        CancellationToken cancellationToken)
    {
        if (envelope is null) throw new ArgumentNullException(nameof(envelope));

        var tokenResult = await _tokenAcquirer.AcquireAsync(envelope, cancellationToken).ConfigureAwait(false);
        if (!tokenResult.IsSuccess)
        {
            _logger.LogWarning(
                "A2A token acquisition failed for caller {CallerId}: {Errors}",
                envelope.CallerAgentId,
                string.Join(',', tokenResult.Errors));
            return Result<IReadOnlyDictionary<string, string>>.Fail(tokenResult.Errors.ToArray());
        }

        IReadOnlyDictionary<string, string> headers = new Dictionary<string, string>
        {
            [AuthorizationHeader] = BearerScheme + tokenResult.Value
        };
        return Result<IReadOnlyDictionary<string, string>>.Success(headers);
    }

    /// <inheritdoc />
    public async Task<Result<string>> ValidateInboundAsync(
        A2AEnvelope envelope,
        IReadOnlyDictionary<string, string> transportHeaders,
        CancellationToken cancellationToken)
    {
        if (envelope is null) throw new ArgumentNullException(nameof(envelope));
        if (transportHeaders is null) throw new ArgumentNullException(nameof(transportHeaders));

        if (!transportHeaders.TryGetValue(AuthorizationHeader, out var authHeader) ||
            string.IsNullOrEmpty(authHeader) ||
            !authHeader.StartsWith(BearerScheme, StringComparison.Ordinal))
        {
            return Result<string>.Fail("a2a.auth_rejected");
        }

        var jwt = authHeader[BearerScheme.Length..];
        var validation = await _tokenValidator.ValidateAsync(jwt, cancellationToken).ConfigureAwait(false);
        if (!validation.IsSuccess)
        {
            // Errors already scrubbed by the validator.
            return Result<string>.Fail(validation.Errors.ToArray());
        }

        // JWT sub is authoritative — reject if the envelope declared a different
        // caller. This prevents a holder of token-for-A from masquerading as
        // agent B in the envelope payload.
        if (!string.Equals(validation.Value, envelope.CallerAgentId, StringComparison.Ordinal))
        {
            _logger.LogWarning(
                "A2A envelope caller {Declared} does not match JWT sub {Authoritative}",
                envelope.CallerAgentId,
                validation.Value);
            return Result<string>.Fail("a2a.auth_rejected");
        }

        return Result<string>.Success(validation.Value!);
    }
}
