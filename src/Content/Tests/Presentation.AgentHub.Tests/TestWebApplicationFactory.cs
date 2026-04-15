using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Presentation.AgentHub.Tests;

/// <summary>
/// Integration test factory for <c>Presentation.AgentHub</c>.
/// Sets the working directory so <c>AppConfigHelper.LoadAppConfig()</c> can locate
/// <c>appsettings.json</c>, activates the Development environment, and replaces
/// Microsoft.Identity.Web's JWT Bearer handler with <see cref="TestJwtBearerHandler"/>
/// so tests run without valid Azure AD configuration.
/// </summary>
/// <remarks>
/// Fleshed out in section-07 with full auth overrides and per-test configuration helpers.
/// </remarks>
public class TestWebApplicationFactory : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        // AppConfigHelper.LoadAppConfig() reads appsettings.json from Directory.GetCurrentDirectory().
        // In test context CWD is the test runner directory; redirect to the AgentHub
        // assembly output directory so appsettings.json and appsettings.Development.json are found.
        Directory.SetCurrentDirectory(
            Path.GetDirectoryName(typeof(Program).Assembly.Location)!);

        // Development environment loads appsettings.Development.json, which includes
        // http://localhost:5173 in AllowedOrigins — required by the CORS integration tests.
        builder.UseEnvironment("Development");

        builder.ConfigureTestServices(services =>
        {
            // Replace Microsoft.Identity.Web's JWT Bearer handler with a no-op stub.
            // TestJwtBearerHandler returns NoResult() when no token is present, causing
            // UseAuthorization to challenge with 401 for [Authorize] endpoints — matching
            // real JWT behaviour without requiring valid AzureAd configuration.
            // Tests that need an authenticated user override this via WithWebHostBuilder +
            // ConfigureTestServices using TestAuthHandler.
            services.AddAuthentication(TestJwtBearerHandler.SchemeName)
                .AddScheme<AuthenticationSchemeOptions, TestJwtBearerHandler>(
                    TestJwtBearerHandler.SchemeName, _ => { });
        });
    }

    protected override IHost CreateHost(IHostBuilder builder)
    {
        // The shared GetServices() DI has a pre-existing captive dependency:
        // MemoizedPromptComposer (singleton) → IPromptSectionProvider (transient) →
        // IAgentExecutionContext (scoped). ASP.NET Core's hosting validates scopes by
        // default; ConsoleUI's plain BuildServiceProvider() does not. Suppress validation
        // here to match ConsoleUI behaviour until the upstream registration is corrected.
        builder.UseDefaultServiceProvider(options =>
        {
            options.ValidateScopes = false;
            options.ValidateOnBuild = false;
        });
        return base.CreateHost(builder);
    }
}
