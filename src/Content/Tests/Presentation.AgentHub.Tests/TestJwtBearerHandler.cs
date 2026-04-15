using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Text.Encodings.Web;

namespace Presentation.AgentHub.Tests;

/// <summary>
/// No-op JWT Bearer handler used by <see cref="TestWebApplicationFactory"/> as the default
/// authentication scheme. Mimics the real JWT Bearer handler behaviour for requests without
/// a token: returns <see cref="AuthenticateResult.NoResult"/>, causing
/// <c>UseAuthorization</c> to challenge with 401 for <c>[Authorize]</c> endpoints.
/// The base-class <see cref="AuthenticationHandler{TOptions}.HandleChallengeAsync"/> sets
/// <c>Response.StatusCode = 401</c>, so no override is needed.
/// </summary>
/// <remarks>
/// This handler eliminates the need for valid Azure AD configuration in tests.
/// Tests that require an authenticated user override the default scheme via
/// <c>ConfigureTestServices</c> + <see cref="TestAuthHandler"/>.
/// </remarks>
public class TestJwtBearerHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    /// <summary>Authentication scheme name registered as the default in integration tests.</summary>
    public const string SchemeName = "TestJwtBearer";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        => Task.FromResult(AuthenticateResult.NoResult());
}
