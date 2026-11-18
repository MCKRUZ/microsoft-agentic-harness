using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace Presentation.ExecutionApi.LoadTarget;

/// <summary>
/// Stands in for Entra ID during a load run: mints signed access tokens shaped like Entra's, and
/// hands the host the key to verify them with.
/// </summary>
/// <remarks>
/// <para>
/// The point is to keep the host's real JWT validation on every request. Signature checks, issuer,
/// audience and lifetime validation are real per-request costs, and a load test that swapped in a
/// header-trusting handler would leave them out of every number. Only the source of the signing key
/// changes: a fresh in-memory RSA key, served through a static configuration manager so the host never
/// fetches Entra's metadata document.
/// </para>
/// <para>
/// Issuer and audience follow the exact format the Execution API derives from its configured
/// <c>TenantId</c> and <c>ClientId</c>, so the host validates these tokens through its unmodified
/// <c>TokenValidationParameters</c>.
/// </para>
/// </remarks>
public sealed class LocalTokenIssuer : IDisposable
{
    private readonly RSA _rsa = RSA.Create(2048);
    private readonly RsaSecurityKey _key;
    private readonly JsonWebTokenHandler _handler = new();

    /// <summary>Creates an issuer with a new signing key and a new tenant/client pair.</summary>
    public LocalTokenIssuer()
    {
        _key = new RsaSecurityKey(_rsa) { KeyId = Guid.NewGuid().ToString("N") };
    }

    /// <summary>The Entra tenant id the host is configured with.</summary>
    public string TenantId { get; } = Guid.NewGuid().ToString();

    /// <summary>The application (client) id the host is configured with.</summary>
    public string ClientId { get; } = Guid.NewGuid().ToString();

    /// <summary>The issuer, in Entra's v2.0 format, that the host accepts.</summary>
    public string Issuer => $"https://login.microsoftonline.com/{TenantId}/v2.0";

    /// <summary>The audience, in the <c>api://</c> format, that the host accepts.</summary>
    public string Audience => $"api://{ClientId}";

    /// <summary>
    /// A configuration manager that always returns this issuer and its signing key. The host's JWT
    /// handler uses it instead of downloading Entra's OpenID Connect metadata.
    /// </summary>
    public IConfigurationManager<OpenIdConnectConfiguration> CreateConfigurationManager()
    {
        var configuration = new OpenIdConnectConfiguration { Issuer = Issuer };
        configuration.SigningKeys.Add(_key);
        return new StaticConfigurationManager<OpenIdConnectConfiguration>(configuration);
    }

    /// <summary>Mints an access token for one caller.</summary>
    /// <param name="objectId">The caller's <c>oid</c>; the host's ownership and rate-limit identity.</param>
    /// <param name="tenantId">The caller's <c>tid</c>; drives the knowledge scope.</param>
    /// <param name="lifetime">How long the token is valid. The host allows no clock skew.</param>
    /// <param name="roles">App roles to include, such as the tool-invocation role.</param>
    public string Mint(string objectId, string tenantId, TimeSpan lifetime, IReadOnlyList<string> roles)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(objectId);
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        ArgumentNullException.ThrowIfNull(roles);

        var claims = new List<Claim> { new("oid", objectId), new("sub", objectId), new("tid", tenantId) };
        claims.AddRange(roles.Select(role => new Claim("roles", role)));

        var now = DateTime.UtcNow;
        return _handler.CreateToken(new SecurityTokenDescriptor
        {
            Issuer = Issuer,
            Audience = Audience,
            Subject = new ClaimsIdentity(claims),
            IssuedAt = now,
            NotBefore = now,
            Expires = now.Add(lifetime),
            SigningCredentials = new SigningCredentials(_key, SecurityAlgorithms.RsaSha256),
        });
    }

    /// <inheritdoc />
    public void Dispose() => _rsa.Dispose();
}
