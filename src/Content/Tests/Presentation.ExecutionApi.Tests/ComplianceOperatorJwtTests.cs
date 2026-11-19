using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using Domain.Common.Config.AI;
using FluentAssertions;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Presentation.Common.Compliance;
using Xunit;

namespace Presentation.ExecutionApi.Tests;

/// <summary>
/// An operator erasure sent with a signed Entra-shaped token through the host's own <c>AddJwtBearer</c> options (#1012).
/// Every other compliance test signs callers in with <see cref="HeaderIdentityAuthenticationHandler"/>, which mints the
/// claims the operator gate reads directly, so none of them shows that a real token reaches the gate in that shape: that
/// the bearer handler maps <c>roles</c> onto the role type <c>IsInRole</c> reads, and that the tenant survives the
/// inbound claim mapping.
/// </summary>
/// <remarks>
/// Only signing key material is replaced, because the test cannot fetch Entra's keys: the configuration manager that
/// would download them is removed and a test key stands in. The authority, issuers, audience, lifetime rules, claim
/// mapping and role claim type are the host's own.
/// </remarks>
public sealed class ComplianceOperatorJwtTests : IDisposable
{
    private const string TenantId = "7f3c6d1e-0000-4000-8000-0000000a11ce";
    private const string ClientId = "0c1d2e3f-0000-4000-8000-000000ba5e11";
    private const string Issuer = $"https://login.microsoftonline.com/{TenantId}/v2.0";
    private const string Audience = $"api://{ClientId}";

    /// <summary>The host reads its auth section from configuration before the test can reach it, so it is set here.</summary>
    private static readonly Dictionary<string, string?> AuthSettings = new()
    {
        ["AppConfig__AI__BundleExecution__Auth__TenantId"] = TenantId,
        ["AppConfig__AI__BundleExecution__Auth__ClientId"] = ClientId,
        // The Development settings the test host loads opt into anonymous mode, which a configured scheme contradicts.
        ["AppConfig__AI__BundleExecution__Auth__AllowAnonymous"] = "false",
    };

    private readonly SymmetricSecurityKey _signingKey = new(RandomNumberGenerator.GetBytes(32));
    private readonly WebApplicationFactory<Program> _factory;

    /// <summary>Boots the host with the Entra scheme configured; the assembly runs its tests one at a time.</summary>
    public ComplianceOperatorJwtTests()
    {
        var prior = AuthSettings.Keys.ToDictionary(key => key, Environment.GetEnvironmentVariable);
        foreach (var (key, value) in AuthSettings)
            Environment.SetEnvironmentVariable(key, value);
        try
        {
            _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
                builder.ConfigureTestServices(services =>
                {
                    services.PostConfigure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, options =>
                    {
                        options.ConfigurationManager = null;
                        options.TokenValidationParameters.IssuerSigningKey = _signingKey;
                    });
                    services.PostConfigure<ErasureConfig>(c => c.OperatorTenantIds = [TenantId]);
                }));
            _ = _factory.Server; // Start while the settings are visible to the host's eager configuration read.
        }
        finally
        {
            foreach (var (key, value) in prior)
                Environment.SetEnvironmentVariable(key, value);
        }
    }

    /// <inheritdoc />
    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task AnOperatorWithTheRoleAndAnAllowedTenant_IsAllowed()
    {
        var response = await EraseAsync(Token(roles: [ComplianceController.EraseSubjectRole], tenant: TenantId));

        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task AnOperatorWithoutTheRole_IsRefused()
    {
        var response = await EraseAsync(Token(roles: ["Some.Other.Role"], tenant: TenantId));

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task AnOperatorWithoutATenant_IsRefused()
    {
        var response = await EraseAsync(Token(roles: [ComplianceController.EraseSubjectRole], tenant: null));

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task ATokenSignedWithAnotherKey_IsNotSignedIn()
    {
        // The control for the three above: they pass because the host validated the token, not because it took any.
        var forged = Token([ComplianceController.EraseSubjectRole], TenantId, new SymmetricSecurityKey(RandomNumberGenerator.GetBytes(32)));

        var response = await EraseAsync(forged);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    private async Task<HttpResponseMessage> EraseAsync(string token)
    {
        using var client = _factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/compliance/erasures")
        {
            Content = JsonContent.Create(new { subjectId = $"subject-{Guid.NewGuid():N}" }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await client.SendAsync(request);
    }

    /// <summary>A delegated (person's) access token in the shape Entra issues for this API.</summary>
    private string Token(string[] roles, string? tenant, SecurityKey? key = null)
    {
        var claims = new Dictionary<string, object>
        {
            ["oid"] = $"operator-{Guid.NewGuid():N}",
            ["sub"] = $"pairwise-{Guid.NewGuid():N}",
            ["azp"] = "client-app",
            ["scp"] = "access_as_user",
            ["roles"] = roles,
        };
        if (tenant is not null)
            claims["tid"] = tenant;

        return new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = Issuer,
            Audience = Audience,
            Claims = claims,
            NotBefore = DateTime.UtcNow.AddMinutes(-1),
            Expires = DateTime.UtcNow.AddMinutes(10),
            SigningCredentials = new SigningCredentials(key ?? _signingKey, SecurityAlgorithms.HmacSha256),
        });
    }
}
