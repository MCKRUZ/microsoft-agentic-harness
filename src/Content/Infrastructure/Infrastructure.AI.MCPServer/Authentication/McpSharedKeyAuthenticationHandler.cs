using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace Infrastructure.AI.MCPServer.Authentication;

/// <summary>
/// Authenticates inbound MCP requests against a pre-shared credential — either an API
/// key in a configurable header (<see cref="McpSharedKeyAuthenticationDefaults.ApiKeyScheme"/>)
/// or a static token in <c>Authorization: Bearer</c>
/// (<see cref="McpSharedKeyAuthenticationDefaults.BearerScheme"/>).
/// </summary>
/// <remarks>
/// The comparison hashes both values and uses
/// <see cref="CryptographicOperations.FixedTimeEquals(ReadOnlySpan{byte}, ReadOnlySpan{byte})"/>
/// so neither content nor length differences leak through response timing.
/// Failures return 401 with no detail about which part of the credential was wrong.
/// </remarks>
public sealed class McpSharedKeyAuthenticationHandler(
    IOptionsMonitor<McpSharedKeyAuthenticationOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder)
    : AuthenticationHandler<McpSharedKeyAuthenticationOptions>(options, logger, encoder)
{
    /// <inheritdoc />
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue(Options.HeaderName, out var headerValues))
            return Task.FromResult(AuthenticateResult.NoResult());

        var presented = headerValues.ToString();

        if (Options.ValuePrefix.Length > 0)
        {
            // RFC 7235: the auth-scheme token is case-insensitive and separated from
            // the credential by at least one space (extra whitespace tolerated).
            var scheme = Options.ValuePrefix.TrimEnd(' ');
            if (presented.Length <= scheme.Length
                || !presented.StartsWith(scheme, StringComparison.OrdinalIgnoreCase)
                || presented[scheme.Length] != ' ')
                return Task.FromResult(AuthenticateResult.NoResult());

            presented = presented[scheme.Length..].TrimStart(' ');
            if (presented.Length == 0)
                return Task.FromResult(AuthenticateResult.NoResult());
        }

        if (!CredentialsMatch(presented, Options.ExpectedCredential))
        {
            Logger.LogWarning(
                "MCP shared-key authentication failed. Scheme={Scheme} Header={Header}",
                Scheme.Name, Options.HeaderName);
            return Task.FromResult(AuthenticateResult.Fail("Invalid credential."));
        }

        var identity = new ClaimsIdentity(
            [new Claim(ClaimTypes.Name, "mcp-shared-key-client")], Scheme.Name);
        var ticket = new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name);
        return Task.FromResult(AuthenticateResult.Success(ticket));
    }

    /// <summary>
    /// Compares a presented credential to the expected one in constant time by
    /// comparing SHA-256 digests, which also normalizes length differences.
    /// </summary>
    internal static bool CredentialsMatch(string presented, string expected)
    {
        var presentedHash = SHA256.HashData(Encoding.UTF8.GetBytes(presented));
        var expectedHash = SHA256.HashData(Encoding.UTF8.GetBytes(expected));
        return CryptographicOperations.FixedTimeEquals(presentedHash, expectedHash);
    }
}
