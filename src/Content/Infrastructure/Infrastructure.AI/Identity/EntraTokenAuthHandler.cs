using System.Net.Http.Headers;
using Application.Common.Factories;
using Azure.Core;
using Domain.Common.Config.AI.MCP;
using Domain.Common.Config.Azure;

namespace Infrastructure.AI.Identity;

/// <summary>
/// Delegating handler that attaches a freshly-minted Microsoft Entra access token to
/// every outbound request as an <c>Authorization: Bearer</c> header. Used to authenticate
/// the harness to an external MCP server with the harness's <em>own</em> workload identity
/// rather than forwarding a caller's credential.
/// </summary>
/// <remarks>
/// <para>
/// The token is acquired per request from the supplied <see cref="TokenCredential"/>.
/// Azure.Identity credentials cache the token in-process and refresh it shortly before
/// expiry, so the per-request <see cref="TokenCredential.GetTokenAsync(TokenRequestContext, CancellationToken)"/>
/// call is near-instant on the hot path while guaranteeing a long-lived, cached MCP
/// connection never sends an expired token. There is no standing secret on the wire and
/// nothing to rotate manually.
/// </para>
/// <para>
/// The handler is designed to sit in front of the shared SSRF-guard terminal handler:
/// construct it via <see cref="Create"/> with the egress <see cref="HttpMessageHandler"/>
/// as its inner handler so token injection composes with connect-time IP filtering.
/// </para>
/// </remarks>
public sealed class EntraTokenAuthHandler : DelegatingHandler
{
    private readonly TokenCredential _credential;
    private readonly string[] _scopes;

    /// <summary>
    /// Initializes a new instance of the <see cref="EntraTokenAuthHandler"/> class.
    /// </summary>
    /// <param name="credential">The token credential used to mint access tokens.</param>
    /// <param name="scopes">The OAuth scopes to request. Must contain at least one entry.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="credential"/> or
    /// <paramref name="scopes"/> is null.</exception>
    /// <exception cref="ArgumentException">Thrown when <paramref name="scopes"/> is empty —
    /// without a scope there is no resource to mint a token for.</exception>
    public EntraTokenAuthHandler(TokenCredential credential, IReadOnlyList<string> scopes)
    {
        ArgumentNullException.ThrowIfNull(credential);
        ArgumentNullException.ThrowIfNull(scopes);

        if (scopes.Count == 0)
            throw new ArgumentException("At least one scope is required to mint an Entra token.", nameof(scopes));

        _credential = credential;
        _scopes = [.. scopes];
    }

    /// <summary>
    /// Builds an <see cref="EntraTokenAuthHandler"/> for the given MCP server auth
    /// configuration, selecting the credential shape (managed identity, client secret,
    /// or certificate) via <see cref="AzureCredentialFactory"/>.
    /// </summary>
    /// <param name="auth">The validated Entra auth configuration. Callers must ensure
    /// <see cref="McpServerAuthConfig.IsValid"/> before calling.</param>
    /// <param name="innerHandler">The inner handler the token-bearing request is forwarded
    /// to — typically the shared SSRF-guard handler.</param>
    /// <returns>A configured handler wired to <paramref name="innerHandler"/>.</returns>
    public static EntraTokenAuthHandler Create(McpServerAuthConfig auth, HttpMessageHandler innerHandler)
    {
        ArgumentNullException.ThrowIfNull(auth);
        ArgumentNullException.ThrowIfNull(innerHandler);

        var credential = AzureCredentialFactory.CreateTokenCredential(new EntraCredentialConfig
        {
            TenantId = auth.TenantId,
            ClientId = auth.ClientId,
            ClientSecret = auth.ClientSecret,
            CertificatePath = auth.CertificatePath
        });

        return new EntraTokenAuthHandler(credential, auth.Scopes) { InnerHandler = innerHandler };
    }

    /// <inheritdoc />
    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var token = await _credential
            .GetTokenAsync(new TokenRequestContext(_scopes), cancellationToken)
            .ConfigureAwait(false);

        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);

        return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
    }
}
