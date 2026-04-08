using System.Threading.RateLimiting;
using Domain.Common.Config;
using Infrastructure.AI.MCPServer.Extensions;

namespace Infrastructure.AI.MCPServer;

/// <summary>
/// Entry point for the agentic harness MCP server.
/// Exposes tools, prompts, and resources via MCP protocol over HTTP transport.
/// </summary>
public static class Program
{
    public static void Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);

        // Bind AppConfig
        builder.Services.Configure<AppConfig>(
            builder.Configuration.GetSection("AppConfig"));

        // Add MCP server services
        var appConfig = builder.Configuration
            .GetSection("AppConfig")
            .Get<AppConfig>() ?? new AppConfig();

        builder.Services.AddMcpServerServices(appConfig);
        builder.Services.AddMcpAuthentication(appConfig, builder.Configuration);

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
        if (!app.Environment.IsDevelopment())
            app.UseHsts();

        app.UseAuthentication();
        app.UseAuthorization();
        app.UseRateLimiter();

        // Map MCP endpoints with auth + rate limiting
        app.MapMcp()
            .RequireAuthorization()
            .RequireRateLimiting("mcp");

        app.Run();
    }
}
