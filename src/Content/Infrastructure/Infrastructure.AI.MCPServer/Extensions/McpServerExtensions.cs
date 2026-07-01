using System.Collections.Concurrent;
using System.Diagnostics;
using Domain.Common.Config;
using Domain.Common.Config.AI.MCP;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using Microsoft.Extensions.Hosting;
using Infrastructure.AI.MCPServer.Authorization;
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

        // Auth is configured in all non-Development environments (AddMcpAuthentication
        // enforces this). When it is, every inbound tool call must carry an
        // authenticated principal — re-checked at the tool-dispatch layer below as
        // defense-in-depth behind the endpoint's RequireAuthorization().
        var authenticationRequired = mcpConfig.Auth.IsConfigured;

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
            // Enable [Authorize]/[AllowAnonymous] attributes on individual tools so a
            // high-risk tool can be locked to a role without touching the baseline gate.
            .AddAuthorizationFilters()
            // Always load tools/prompts from this assembly (SkillTools, etc.)
            .WithToolsFromAssembly(typeof(McpServerExtensions).Assembly)
            // Load additional tools/prompts from externally configured assemblies
            .LoadToolsFromAssemblies(mcpConfig)
            .LoadPromptsFromAssemblies(mcpConfig)
            .LoadResourcesFromAssemblies(mcpConfig)
            .WithSubscribeToResourcesHandler(CreateSubscribeHandler(subscriptions))
            .WithUnsubscribeFromResourcesHandler(CreateUnsubscribeHandler(subscriptions))
            .WithSetLoggingLevelHandler(CreateSetLoggingLevelHandler())
            // Baseline per-tool-call authorization gate (defense-in-depth) + audit.
            .WithRequestFilters(filters =>
            {
                filters.AddCallToolFilter(next => async (context, cancellationToken) =>
                {
                    var logger = context.Services?
                        .GetService<ILoggerFactory>()?
                        .CreateLogger("Mcp.ToolAudit");
                    var toolName = context.Params?.Name ?? "(unknown)";
                    var user = context.User?.Identity?.Name ?? "anonymous";
                    // W3C trace id correlates this audit line with the request's spans.
                    var correlationId = Activity.Current?.TraceId.ToString() ?? "none";

                    var denied = McpToolAuthorizationFilter.Evaluate(authenticationRequired, context.User);
                    if (denied is not null)
                    {
                        logger?.LogWarning(
                            "MCP tool call denied. User={User} ToolName={ToolName} Reason=unauthenticated CorrelationId={CorrelationId}",
                            user, toolName, correlationId);
                        return denied;
                    }

                    logger?.LogInformation(
                        "MCP tool call authorized. User={User} ToolName={ToolName} CorrelationId={CorrelationId}",
                        user, toolName, correlationId);

                    // Guaranteed outcome line (mirrors the WebUI controller path): every
                    // authorized call logs a terminal success/error/faulted record so the
                    // audit trail is never left at "authorized" with no resolution.
                    try
                    {
                        var result = await next(context, cancellationToken);
                        logger?.LogInformation(
                            "MCP tool call completed. User={User} ToolName={ToolName} Status={Status} CorrelationId={CorrelationId}",
                            user, toolName, result?.IsError == true ? "error" : "success", correlationId);
                        // non-null: the call-tool handler pipeline always yields a CallToolResult;
                        // the ! is compile-time only and does not alter the returned value.
                        return result!;
                    }
                    catch (Exception ex)
                    {
                        logger?.LogWarning(ex,
                            "MCP tool call faulted. User={User} ToolName={ToolName} Status=faulted CorrelationId={CorrelationId}",
                            user, toolName, correlationId);
                        throw;
                    }
                });
            });

        return services;
    }

    /// <summary>
    /// Configures JWT Bearer authentication for the MCP server.
    /// </summary>
    public static IServiceCollection AddMcpAuthentication(
        this IServiceCollection services, AppConfig appConfig, IConfiguration configuration,
        IHostEnvironment environment)
    {
        var auth = appConfig.AI.MCP.Auth;

        if (!auth.IsConfigured)
        {
            if (!environment.IsDevelopment())
                throw new InvalidOperationException(
                    "MCP server authentication must be configured in non-Development environments. " +
                    "Set AppConfig:AI:MCP:Auth in appsettings or User Secrets.");

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
