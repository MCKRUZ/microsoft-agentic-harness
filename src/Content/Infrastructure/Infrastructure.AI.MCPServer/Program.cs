using System.Threading.RateLimiting;
using Application.AI.Common.Interfaces;
using Domain.Common.Config;
using Infrastructure.AI.MCPServer.Extensions;
using Infrastructure.AI.Skills;
using Infrastructure.Common.Middleware.Security;

namespace Infrastructure.AI.MCPServer;

/// <summary>
/// Entry point for the agentic harness MCP server.
/// Exposes tools, prompts, and resources via MCP protocol over HTTP transport.
/// </summary>
/// <remarks>
/// Non-static so integration tests can host the full pipeline via
/// <c>WebApplicationFactory&lt;Program&gt;</c> (static types cannot be type arguments).
/// </remarks>
public class Program
{
    public static void Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);

        // Enforce the harness's boot-time DI validation policy (audit item H2) in ALL
        // environments: ValidateScopes catches captive dependencies (a singleton capturing
        // per-request scoped state) and ValidateOnBuild eagerly constructs every registered
        // service, failing loudly at boot instead of silently at first use. The flags are
        // inlined rather than sharing Presentation.Common's ApplyValidationPolicy because this
        // Infrastructure-layer host must not take a Presentation dependency (Clean Architecture).
        builder.Host.UseDefaultServiceProvider(options =>
        {
            options.ValidateScopes = true;
            options.ValidateOnBuild = true;
        });

        // Bind AppConfig
        builder.Services.Configure<AppConfig>(
            builder.Configuration.GetSection("AppConfig"));

        // Add MCP server services
        var appConfig = builder.Configuration
            .GetSection("AppConfig")
            .Get<AppConfig>() ?? new AppConfig();

        builder.Services.AddMcpServerServices(appConfig);
        // Fail-closed: throws unless an auth scheme is configured or the operator
        // explicitly opted into anonymous serving — regardless of environment.
        builder.Services.AddMcpAuthentication(appConfig);

        // Skill catalog — discovered from the configured skills directory
        builder.Services.AddSingleton<SkillMetadataParser>();
        builder.Services.AddSingleton<ISkillMetadataRegistry, SkillMetadataRegistry>();

        // Rate limiting
        builder.Services.AddRateLimiter(options =>
        {
            options.AddPolicy("mcp", _ =>
                RateLimitPartition.GetFixedWindowLimiter("mcp",
                    _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = 100,
                        Window = TimeSpan.FromMinutes(1)
                    }));
        });

        var app = builder.Build();

        // Middleware pipeline
        app.UseMiddleware<SecurityHeadersMiddleware>();
        if (!app.Environment.IsDevelopment())
            app.UseHsts();
        app.UseHttpsRedirection();

        app.UseAuthentication();
        app.UseAuthorization();
        app.UseRateLimiter();

        // Map MCP endpoints with auth + rate limiting. Authorization is mandatory on
        // every MCP endpoint; the only exception is the explicit AllowAnonymous
        // opt-in, made visible here as a deliberate .AllowAnonymous() rather than a
        // silently weakened policy. AddMcpAuthentication has already validated the
        // config, so these two flags are mutually exclusive.
        var mcpEndpoints = app.MapMcp().RequireRateLimiting("mcp");
        if (appConfig.AI.MCP.Auth.AllowAnonymous)
            mcpEndpoints.AllowAnonymous();
        else
            mcpEndpoints.RequireAuthorization();

        app.Run();
    }
}
