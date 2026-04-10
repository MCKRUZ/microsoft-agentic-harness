using System.Collections.Concurrent;
using Domain.Common.Config;
using Domain.Common.Config.AI.MCP;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using ModelContextProtocol;
using ModelContextProtocol.AspNetCore;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Infrastructure.AI.MCPServer.Extensions;

/// <summary>
/// Extension methods for configuring the MCP server services including
/// server options, transport, handlers, and authentication.
/// </summary>
public static class McpServerExtensions
{
    /// <summary>
    /// Registers MCP server services with HTTP transport and protocol handlers.
    /// </summary>
    public static IServiceCollection AddMcpServerServices(
        this IServiceCollection services, AppConfig appConfig)
    {
        var mcpConfig = appConfig.AI.MCP;
        var subscriptions = new ConcurrentDictionary<string, byte>();
        services.AddSingleton(subscriptions);

        services
            .AddMcpServer(options =>
            {
                options.ServerInfo = new Implementation
                {
                    Name = mcpConfig.ServerName,
                    Version = mcpConfig.ServerVersion
                };
                options.ServerInstructions = mcpConfig.ServerInstructions;
                options.InitializationTimeout = mcpConfig.InitializationTimeout;
            })
            .WithHttpTransport()
            // Always load tools/prompts from this assembly (SkillTools, etc.)
            .WithToolsFromAssembly(typeof(McpServerExtensions).Assembly)
            // Load additional tools/prompts from externally configured assemblies
            .LoadToolsFromAssemblies(mcpConfig)
            .LoadPromptsFromAssemblies(mcpConfig)
            .LoadResourcesFromAssemblies(mcpConfig)
            .WithSubscribeToResourcesHandler(CreateSubscribeHandler(subscriptions))
            .WithUnsubscribeFromResourcesHandler(CreateUnsubscribeHandler(subscriptions))
            .WithSetLoggingLevelHandler(CreateSetLoggingLevelHandler());

        return services;
    }

    /// <summary>
    /// Configures JWT Bearer authentication for the MCP server.
    /// </summary>
    public static IServiceCollection AddMcpAuthentication(
        this IServiceCollection services, AppConfig appConfig, IConfiguration configuration)
    {
        var auth = appConfig.AI.MCP.Auth;

        if (!auth.IsConfigured)
        {
            // No auth configured — allow anonymous for development
            services.AddAuthentication();
            services.AddAuthorization(options =>
            {
                options.FallbackPolicy = null;
            });
            return services;
        }

        if (auth.Type == McpServerAuthType.Entra)
        {
            var authority = $"https://login.microsoftonline.com/{auth.TenantId}/v2.0";
            var audience = $"api://{auth.ClientId}";

            services
                .AddAuthentication(options =>
                {
                    options.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
                    options.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;
                })
                .AddJwtBearer(options =>
                {
                    options.Authority = authority;
                    options.Audience = audience;
                    options.TokenValidationParameters = new TokenValidationParameters
                    {
                        ValidateIssuer = true,
                        ValidIssuers =
                        [
                            $"https://sts.windows.net/{auth.TenantId}/",
                            $"https://login.microsoftonline.com/{auth.TenantId}/v2.0"
                        ],
                        ValidateAudience = true,
                        ValidAudience = audience,
                        ValidateLifetime = true,
                        ValidateIssuerSigningKey = true,
                        ClockSkew = TimeSpan.Zero
                    };
                });
        }

        services.AddAuthorization();
        return services;
    }

    private static McpRequestHandler<SubscribeRequestParams, EmptyResult>
        CreateSubscribeHandler(ConcurrentDictionary<string, byte> subscriptions)
    {
        return (ctx, ct) =>
        {
            var uri = ctx.Params?.Uri;
            if (uri is not null)
                subscriptions.TryAdd(uri, 0);

            return new ValueTask<EmptyResult>(new EmptyResult());
        };
    }

    private static McpRequestHandler<UnsubscribeRequestParams, EmptyResult>
        CreateUnsubscribeHandler(ConcurrentDictionary<string, byte> subscriptions)
    {
        return (ctx, ct) =>
        {
            var uri = ctx.Params?.Uri;
            if (uri is not null)
                subscriptions.TryRemove(uri, out _);

            return new ValueTask<EmptyResult>(new EmptyResult());
        };
    }

    private static McpRequestHandler<SetLevelRequestParams, EmptyResult>
        CreateSetLoggingLevelHandler()
    {
        return async (ctx, ct) =>
        {
            var level = ctx.Params?.Level;
            if (level is null)
                throw new McpException("Missing required argument 'level'");

            await ctx.Server.SendNotificationAsync(
                method: "notifications/message",
                parameters: new
                {
                    Level = "debug",
                    Logger = "agentic-harness",
                    Data = $"Logging level set to {level}",
                },
                cancellationToken: ct);

            return new EmptyResult();
        };
    }
}
