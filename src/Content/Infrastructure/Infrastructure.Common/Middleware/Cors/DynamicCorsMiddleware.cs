using Domain.Common.Config;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Infrastructure.Common.Middleware.Cors;

/// <summary>
/// Runtime-configurable CORS middleware that reads allowed origins from
/// <see cref="AppConfig.Http"/> via <c>IOptionsMonitor&lt;AppConfig&gt;</c>,
/// enabling origin changes without application restart.
/// </summary>
/// <remarks>
/// <para>
/// Unlike the built-in ASP.NET Core CORS middleware (which requires startup-time policy
/// configuration), this middleware evaluates the current <c>AppConfig.Http.CorsAllowedOrigins</c>
/// on every request, picking up configuration changes immediately.
/// </para>
/// <para>
/// <strong>Configuration:</strong> Origins are semicolon-separated in appsettings.json:
/// <code>
/// "AppConfig": {
///   "Http": {
///     "CorsAllowedOrigins": "https://app.example.com;https://localhost:4200"
///   }
/// }
/// </code>
/// </para>
/// <para>
/// <strong>Security:</strong> Wildcard origins are not supported. Each allowed origin
/// must be explicitly listed. Requests from unlisted origins receive no CORS headers
/// and will be blocked by the browser.
/// </para>
/// <para>
/// Usage:
/// <code>
/// app.UseMiddleware&lt;DynamicCorsMiddleware&gt;();
/// </code>
/// </para>
/// </remarks>
public sealed class DynamicCorsMiddleware
{
    private readonly RequestDelegate _next;
    private readonly IOptionsMonitor<AppConfig> _appConfig;
    private readonly ILogger<DynamicCorsMiddleware> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="DynamicCorsMiddleware"/> class.
    /// </summary>
    /// <param name="next">The next delegate in the middleware pipeline.</param>
    /// <param name="appConfig">Configuration monitor for runtime CORS origin changes.</param>
    /// <param name="logger">Logger for CORS violations and debugging.</param>
    public DynamicCorsMiddleware(
        RequestDelegate next,
        IOptionsMonitor<AppConfig> appConfig,
        ILogger<DynamicCorsMiddleware> logger)
    {
        ArgumentNullException.ThrowIfNull(next);
        ArgumentNullException.ThrowIfNull(appConfig);
        ArgumentNullException.ThrowIfNull(logger);

        _next = next;
        _appConfig = appConfig;
        _logger = logger;
    }

    /// <summary>
    /// Evaluates the request origin against the current allowed-origins configuration,
    /// applies CORS headers for valid origins, and short-circuits preflight OPTIONS requests.
    /// </summary>
    /// <param name="context">The HTTP context for the current request.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    public async Task InvokeAsync(HttpContext context)
    {
        var origins = _appConfig.CurrentValue.Http.CorsAllowedOrigins
            ?.Split(';', StringSplitOptions.RemoveEmptyEntries) ?? [];

        var origin = context.Request.Headers.Origin.ToString();

        if (string.IsNullOrEmpty(origin))
        {
            _logger.LogDebug("CORS request with empty Origin header on {Path}", context.Request.Path);
        }
        else if (origins.Contains(origin, StringComparer.OrdinalIgnoreCase))
        {
            ApplyCorsHeaders(context.Response.Headers, origin);
        }

        if (context.Request.Method == HttpMethods.Options)
        {
            context.Response.StatusCode = StatusCodes.Status204NoContent;
            return;
        }

        try
        {
            await _next(context);
        }
        catch (OperationCanceledException ex)
        {
            _logger.LogWarning(ex, "Request canceled on {Path}", context.Request.Path);
        }
    }

    private static void ApplyCorsHeaders(IHeaderDictionary headers, string origin)
    {
        headers["Access-Control-Allow-Origin"] = origin;
        headers["Vary"] = "Origin";
        headers["Access-Control-Allow-Headers"] = "*";
        headers["Access-Control-Allow-Methods"] = "GET,POST,PUT,DELETE,OPTIONS";
        headers["Access-Control-Expose-Headers"] = "Content-Type,Content-Length,Last-Modified";
    }
}
