using Application.AI.Common.Interfaces;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Presentation.AgentHub.Config;
using Presentation.AgentHub.Interfaces;
using Presentation.AgentHub.Services;
using Presentation.AgentHub.Tests.Fakes;

namespace Presentation.AgentHub.Tests;

/// <summary>
/// Integration test factory that keeps the real MediatR pipeline but replaces
/// external AI services with fakes. Use this to exercise handler bodies,
/// pipeline behaviors, and agent execution end-to-end without real AI calls.
/// </summary>
public class IntegrationTestFactory : WebApplicationFactory<Program>
{
    /// <summary>Fake AI client factory — configure responses before each test.</summary>
    public FakeChatClientFactory FakeChatClientFactory { get; } = new();

    /// <summary>Isolated temp directory for conversation storage.</summary>
    public string TempConversationsPath { get; } =
        Path.Combine(Path.GetTempPath(), $"agenthubtests-integ-{Guid.NewGuid():N}");

    /// <inheritdoc/>
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        Directory.SetCurrentDirectory(
            Path.GetDirectoryName(typeof(Program).Assembly.Location)!);

        builder.UseEnvironment("Development");

        builder.ConfigureAppConfiguration((_, config) =>
        {
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AppConfig:Logging:PipeName"] = string.Empty,
            });
        });

        builder.ConfigureTestServices(services =>
        {
            services.AddSignalR(o => o.EnableDetailedErrors = true);

            services.AddAuthentication(TestAuthHandler.SchemeName)
                .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(
                    TestAuthHandler.SchemeName, _ => { });

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

            // Replace AI services with fakes — keeps real MediatR pipeline
            services.RemoveAll<IChatClientFactory>();
            services.AddSingleton<IChatClientFactory>(FakeChatClientFactory);
        });
    }

    /// <inheritdoc/>
    protected override IHost CreateHost(IHostBuilder builder)
    {
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
