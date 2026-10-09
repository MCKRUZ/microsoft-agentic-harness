using Azure.Core;

namespace Infrastructure.AI.Tests.Helpers;

/// <summary>
/// A <see cref="TokenCredential"/> that always returns the same token and records the scopes it
/// was asked for, so a test can assert both the bearer value on the wire and the audience requested.
/// </summary>
public sealed class StaticTokenCredential : TokenCredential
{
    private readonly string _token;
    private readonly List<string> _requestedScopes = [];

    /// <summary>Creates a credential that issues <paramref name="token"/>.</summary>
    public StaticTokenCredential(string token) => _token = token;

    /// <summary>The scopes requested so far, in call order.</summary>
    public IReadOnlyList<string> RequestedScopes
    {
        get { lock (_requestedScopes) return [.. _requestedScopes]; }
    }

    /// <inheritdoc />
    public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken)
    {
        lock (_requestedScopes) _requestedScopes.AddRange(requestContext.Scopes);
        return new AccessToken(_token, DateTimeOffset.UtcNow.AddHours(1));
    }

    /// <inheritdoc />
    public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken)
        => new(GetToken(requestContext, cancellationToken));
}
