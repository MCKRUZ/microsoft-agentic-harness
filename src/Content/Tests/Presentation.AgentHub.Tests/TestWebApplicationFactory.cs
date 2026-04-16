using MediatR;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Presentation.AgentHub.Interfaces;
using Presentation.AgentHub.Config;
using Presentation.AgentHub.Services;

namespace Presentation.AgentHub.Tests;

/// <summary>
/// Integration test factory for <c>Presentation.AgentHub</c>.
///
/// Sets the working directory so <c>AppConfigHelper.LoadAppConfig()</c> can locate
/// <c>appsettings.json</c>, activates the Development environment, and replaces
/// Microsoft.Identity.Web's JWT Bearer handler with <see cref="TestJwtBearerHandler"/>
/// so tests run without valid Azure AD configuration.
///
/// Additionally:
/// <list type="bullet">
///   <item><description>
///     Exposes a <see cref="MockMediator"/> for hub tests to control agent turn results
///     without triggering real AI calls or MediatR pipeline behaviours.
///   </description></item>
///   <item><description>
///     Routes conversation storage to an isolated per-factory temp directory that is
///     deleted on disposal.
///   </description></item>
/// </list>
/// </summary>
public class TestWebApplicationFactory : WebApplicationFactory<Program>
{
    /// <summary>Mock mediator for controlling <c>ExecuteAgentTurnCommand</c> results in hub tests.</summary>
    public Mock<IMediator> MockMediator { get; } = new();

    /// <summary>Isolated temp directory used for conversation storage during this factory's lifetime.</summary>
    public string TempConversationsPath { get; } =
        Path.Combine(Path.GetTempPath(), $"agenthubtests-{Guid.NewGuid():N}");

    /// <inheritdoc/>
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
            // Enable detailed SignalR errors so integration tests see the real server-side
            // exception message instead of the generic "error on the server" wrapper.
            services.AddSignalR(o => o.EnableDetailedErrors = true);

            // Replace Microsoft.Identity.Web's JWT Bearer handler with a no-op stub.
            // TestJwtBearerHandler returns NoResult() when no token is present, causing
            // UseAuthorization to challenge with 401 for [Authorize] endpoints.
            // Tests that need an authenticated user override this via WithWebHostBuilder +
            // ConfigureTestServices using TestAuthHandler.
            services.AddAuthentication(TestJwtBearerHandler.SchemeName)
                .AddScheme<AuthenticationSchemeOptions, TestJwtBearerHandler>(
                    TestJwtBearerHandler.SchemeName, _ => { });

            // Route conversation storage to an isolated temp directory.
            // The last AddSingleton registration wins, replacing DependencyInjection.cs's
            // registration of FileSystemConversationStore with the appsettings path.
            Directory.CreateDirectory(TempConversationsPath);
            services.AddSingleton<IConversationStore>(
                new FileSystemConversationStore(
                    Options.Create(new AgentHubConfig
                    {
                        ConversationsPath = TempConversationsPath,
                        DefaultAgentName = "test-agent",
                        MaxHistoryMessages = 20,
                    }),
                    NullLogger<FileSystemConversationStore>.Instance));

            // Replace IMediator with a mock so hub tests can stub AgentTurnResult
            // without invoking the real MediatR pipeline or AI services.
            services.RemoveAll<IMediator>();
            services.AddSingleton<IMediator>(MockMediator.Object);
        });
    }

    /// <inheritdoc/>
    protected override IHost CreateHost(IHostBuilder builder)
    {
        // Suppress scope validation: MemoizedPromptComposer (singleton) → IPromptSectionProvider
        // (transient) → IAgentExecutionContext (scoped) creates a captive dependency that ASP.NET
        // Core's hosting rejects by default. Matches ConsoleUI behaviour where BuildServiceProvider()
        // does not validate scopes.
        builder.UseDefaultServiceProvider(options =>
        {
            options.ValidateScopes = false;
            options.ValidateOnBuild = false;
        });
        return base.CreateHost(builder);
    }

    /// <inheritdoc/>
    protected override void Dispose(bool disposing)
    {
        if (disposing && Directory.Exists(TempConversationsPath))
            Directory.Delete(TempConversationsPath, recursive: true);
        base.Dispose(disposing);
    }
}
