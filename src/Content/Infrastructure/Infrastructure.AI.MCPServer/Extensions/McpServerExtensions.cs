using System.Collections.Concurrent;
using System.Diagnostics;
using Domain.Common.Config;
using Domain.Common.Config.AI.MCP;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.IdentityModel.Tokens;
using Microsoft.Net.Http.Headers;
using Infrastructure.AI.MCPServer.Authentication;
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

        // AddMcpAuthentication is fail-closed: the host only boots with a configured
        // scheme or the explicit AllowAnonymous opt-in. Unless that opt-in is set,
        // every inbound tool call must carry an authenticated principal — re-checked
        // at the tool-dispatch layer below as defense-in-depth behind the endpoint's
        // RequireAuthorization().
        var authenticationRequired = !mcpConfig.Auth.AllowAnonymous;

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
    /// Configures fail-closed authentication for the MCP server host: ApiKey, static
    /// Bearer token, or Entra ID (JWT) enforcement on every MCP endpoint.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Fail-closed contract:</strong> if no authentication scheme is properly
    /// configured (<c>Type=None</c>, or a type missing its credential material), this
    /// method throws and the host refuses to start — in <em>every</em> environment.
    /// The single escape hatch is the explicit
    /// <c>AppConfig:AI:MCP:Auth:AllowAnonymous=true</c> opt-in, which boots the server
    /// open and logs a prominent startup warning. Combining that opt-in with a
    /// configured type is rejected as contradictory.
    /// </para>
    /// <para>
    /// When authentication is configured, a fallback authorization policy requiring an
    /// authenticated user is installed, so any endpoint mapped without explicit
    /// authorization metadata is still protected.
    /// </para>
    /// </remarks>
    /// <exception cref="InvalidOperationException">
    /// Authentication is unconfigured without the anonymous opt-in, misconfigured for
    /// the selected type, or contradictorily combined with <c>AllowAnonymous</c>.
    /// </exception>
    public static IServiceCollection AddMcpAuthentication(
        this IServiceCollection services, AppConfig appConfig)
    {
        var auth = appConfig.AI.MCP.Auth;

        if (auth.AllowAnonymous && auth.IsConfigured)
            throw new InvalidOperationException(
                "AppConfig:AI:MCP:Auth is contradictory: AllowAnonymous=true cannot be combined " +
                $"with a configured authentication type ({auth.Type}). Remove AllowAnonymous to " +
                "enforce the configured scheme, or set Type=None to run anonymously.");

        if (!auth.IsConfigured)
        {
            if (!auth.AllowAnonymous)
                throw new InvalidOperationException(
                    "MCP server authentication is not configured — refusing to start (fail-closed). " +
                    "Set AppConfig:AI:MCP:Auth:Type to ApiKey, Bearer, or Entra and supply the matching " +
                    "credential material via User Secrets or Key Vault. For local development only, " +
                    "authentication can be consciously disabled with AppConfig:AI:MCP:Auth:AllowAnonymous=true; " +
                    "running under Environment=Development alone does not disable it.");

            // Explicit anonymous opt-in — boot open, loudly.
            services.AddAuthentication();
            services.AddAuthorization();
            services.AddHostedService<McpAnonymousModeStartupWarning>();
            return services;
        }

        if (!auth.IsValidForServer)
            throw new InvalidOperationException(
                $"AppConfig:AI:MCP:Auth:Type={auth.Type} is missing required credential material: " +
                $"{RequiredServerMaterial(auth.Type)}. Refusing to start (fail-closed).");

        switch (auth.Type)
        {
            case McpServerAuthType.ApiKey:
                AddApiKeyScheme(services, auth);
                break;
            case McpServerAuthType.Bearer:
                AddSharedBearerScheme(services, auth);
                break;
            case McpServerAuthType.Entra:
                AddEntraScheme(services, auth);
                break;
            default:
                throw new InvalidOperationException(
                    $"Unsupported MCP server authentication type '{auth.Type}'. Refusing to start (fail-closed).");
        }

        // Fallback policy closes the unmapped-endpoint gap: an endpoint added without
        // explicit authorization metadata still requires an authenticated caller.
        services.AddAuthorization(options =>
            options.FallbackPolicy = new AuthorizationPolicyBuilder()
                .RequireAuthenticatedUser()
                .Build());

        return services;
    }

    /// <summary>
    /// Describes the credential material a server-side auth type requires, for
    /// startup error messages (names config keys only — never secret values).
    /// </summary>
    private static string RequiredServerMaterial(McpServerAuthType type) => type switch
    {
        McpServerAuthType.ApiKey => "ApiKey (the shared key inbound requests must present)",
        McpServerAuthType.Bearer => "BearerToken (the shared token inbound requests must present)",
        McpServerAuthType.Entra => "TenantId and ClientId (issuer and audience validation)",
        _ => "a supported authentication type"
    };

    /// <summary>Registers API-key authentication on the configured header.</summary>
    private static void AddApiKeyScheme(IServiceCollection services, McpServerAuthConfig auth)
    {
        services
            .AddAuthentication(McpSharedKeyAuthenticationDefaults.ApiKeyScheme)
            .AddScheme<McpSharedKeyAuthenticationOptions, McpSharedKeyAuthenticationHandler>(
                McpSharedKeyAuthenticationDefaults.ApiKeyScheme, options =>
                {
                    options.HeaderName = auth.ApiKeyHeader;
                    options.ExpectedCredential = auth.ApiKey!;
                });
    }

    /// <summary>
    /// Registers static shared-token authentication on <c>Authorization: Bearer</c>.
    /// </summary>
    private static void AddSharedBearerScheme(IServiceCollection services, McpServerAuthConfig auth)
    {
        services
            .AddAuthentication(McpSharedKeyAuthenticationDefaults.BearerScheme)
            .AddScheme<McpSharedKeyAuthenticationOptions, McpSharedKeyAuthenticationHandler>(
                McpSharedKeyAuthenticationDefaults.BearerScheme, options =>
                {
                    options.HeaderName = HeaderNames.Authorization;
                    options.ValuePrefix = "Bearer ";
                    options.ExpectedCredential = auth.BearerToken!;
                });
    }

    /// <summary>
    /// Registers Entra ID JWT bearer validation (issuer + audience + lifetime +
    /// signing key, zero clock skew) per the repo security baseline.
    /// </summary>
    private static void AddEntraScheme(IServiceCollection services, McpServerAuthConfig auth)
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
