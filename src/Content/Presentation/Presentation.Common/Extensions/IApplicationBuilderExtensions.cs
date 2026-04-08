using Domain.Common.Middleware;
using Infrastructure.Common.Middleware.Cors;
using Infrastructure.Common.Middleware.ExceptionHandling;
using Infrastructure.Common.Middleware.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Diagnostics;
using System.Text.Json;

namespace Presentation.Common.Extensions;

/// <summary>
/// Extension methods for <see cref="IApplicationBuilder"/> that wire middleware
/// and provide a global error handler for the ASP.NET Core request pipeline.
/// </summary>
/// <remarks>
/// <para>
/// Middleware registration order matters. <see cref="UseAllMiddleware"/> applies
/// middleware in the correct sequence so that security headers are set before
/// exception handling, and CORS is evaluated first.
/// </para>
/// <para>
/// The <see cref="UseGlobalErrorHandler"/> method converts unhandled exceptions
/// into structured JSON error responses with consistent shapes across all endpoints.
/// </para>
/// </remarks>
public static class IApplicationBuilderExtensions
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    /// <summary>
    /// Wires all custom middleware in the correct order:
    /// DynamicCors, SecurityAudit, SecurityHeaders, GlobalException.
    /// </summary>
    /// <param name="app">The application builder.</param>
    /// <returns>The application builder for chaining.</returns>
    /// <remarks>
    /// Call this early in the pipeline, before routing and authentication:
    /// <code>
    /// app.UseAllMiddleware();
    /// app.UseAuthentication();
    /// app.UseAuthorization();
    /// app.MapControllers();
    /// </code>
    /// </remarks>
    public static IApplicationBuilder UseAllMiddleware(this IApplicationBuilder app)
    {
        app.UseDynamicCorsMiddleware();
        app.UseSecurityAuditMiddleware();
        app.UseSecurityHeadersMiddleware();
        app.UseGlobalExceptionMiddleware();

        return app;
    }

    /// <summary>
    /// Adds the <see cref="DynamicCorsMiddleware"/> which applies CORS headers
    /// based on runtime configuration rather than static policy alone.
    /// </summary>
    /// <param name="app">The application builder.</param>
    /// <returns>The application builder for chaining.</returns>
    public static IApplicationBuilder UseDynamicCorsMiddleware(this IApplicationBuilder app)
    {
        return app.UseMiddleware<DynamicCorsMiddleware>();
    }

    /// <summary>
    /// Adds the <see cref="SecurityAuditMiddleware"/> which logs security-relevant
    /// request metadata (IP, user agent, auth status) for audit trails.
    /// </summary>
    /// <param name="app">The application builder.</param>
    /// <returns>The application builder for chaining.</returns>
    public static IApplicationBuilder UseSecurityAuditMiddleware(this IApplicationBuilder app)
    {
        return app.UseMiddleware<SecurityAuditMiddleware>();
    }

    /// <summary>
    /// Adds the <see cref="SecurityHeadersMiddleware"/> which sets protective HTTP headers
    /// (X-Frame-Options, X-Content-Type-Options, CSP, HSTS) on every response.
    /// </summary>
    /// <param name="app">The application builder.</param>
    /// <returns>The application builder for chaining.</returns>
    public static IApplicationBuilder UseSecurityHeadersMiddleware(this IApplicationBuilder app)
    {
        return app.UseMiddleware<SecurityHeadersMiddleware>();
    }

    /// <summary>
    /// Adds the <see cref="GlobalExceptionMiddleware"/> which catches unhandled exceptions
    /// and returns structured error responses via the Infrastructure layer's handler.
    /// </summary>
    /// <param name="app">The application builder.</param>
    /// <returns>The application builder for chaining.</returns>
    public static IApplicationBuilder UseGlobalExceptionMiddleware(this IApplicationBuilder app)
    {
        return app.UseMiddleware<GlobalExceptionMiddleware>();
    }

    /// <summary>
    /// Configures a global error handler using <see cref="ExceptionHandlerExtensions.UseExceptionHandler"/>
    /// that catches all unhandled exceptions and returns structured JSON error responses.
    /// </summary>
    /// <param name="app">The application builder.</param>
    /// <param name="environment">
    /// The hosting environment. In Development, exception details (type, message, stack trace)
    /// are included in responses.
    /// </param>
    /// <param name="logger">Logger for recording unhandled exceptions.</param>
    /// <param name="configureOptions">
    /// Optional callback to customize error handling behavior including custom exception mappings,
    /// error messages, and response format.
    /// </param>
    /// <returns>The application builder for chaining.</returns>
    /// <remarks>
    /// <para>
    /// Built-in exception-to-status-code mappings (overridden by <see cref="GlobalErrorHandlerOptions.CustomExceptionMappings"/>):
    /// </para>
    /// <list type="bullet">
    ///   <item><see cref="ArgumentException"/> — 400 Bad Request</item>
    ///   <item><see cref="UnauthorizedAccessException"/> — 401 Unauthorized</item>
    ///   <item><see cref="KeyNotFoundException"/> — 404 Not Found</item>
    ///   <item><see cref="NotImplementedException"/> — 501 Not Implemented</item>
    ///   <item><see cref="TimeoutException"/> — 408 Request Timeout</item>
    ///   <item><see cref="OperationCanceledException"/> — 408 Request Timeout</item>
    ///   <item><see cref="InvalidOperationException"/> — 400 Bad Request</item>
    ///   <item>All other exceptions — 500 Internal Server Error</item>
    /// </list>
    /// <para>
    /// Response shape:
    /// <code>
    /// {
    ///   "statusCode": 500,
    ///   "message": "An internal server error occurred.",
    ///   "timestamp": "2026-04-07T12:00:00Z",
    ///   "requestId": "00-abc123...",
    ///   "path": "/api/v1/agents",
    ///   "exception": { ... }  // Development only
    /// }
    /// </code>
    /// </para>
    /// </remarks>
    public static IApplicationBuilder UseGlobalErrorHandler(
        this IApplicationBuilder app,
        IHostEnvironment environment,
        ILogger logger,
        Action<GlobalErrorHandlerOptions>? configureOptions = null)
    {
        var options = new GlobalErrorHandlerOptions();
        configureOptions?.Invoke(options);

        app.UseExceptionHandler(errorApp =>
        {
            errorApp.Run(async context =>
            {
                var exceptionFeature = context.Features.Get<IExceptionHandlerFeature>();
                var exception = exceptionFeature?.Error;

                if (exception is null)
                    return;

                var (statusCode, message) = MapException(exception, options);

                logger.LogError(
                    exception,
                    "Unhandled exception on {Method} {Path}: {Message}",
                    context.Request.Method,
                    context.Request.Path,
                    exception.Message);

                context.Response.StatusCode = statusCode;
                context.Response.ContentType = options.ContentType;

                var body = BuildErrorBody(
                    statusCode,
                    message,
                    context,
                    exception,
                    environment,
                    options);

                await context.Response.WriteAsync(
                    JsonSerializer.Serialize(body, JsonOptions));
            });
        });

        return app;
    }

    /// <summary>
    /// Maps an exception to an HTTP status code and user-facing message using
    /// custom mappings first, then built-in defaults.
    /// </summary>
    private static (int StatusCode, string Message) MapException(
        Exception exception,
        GlobalErrorHandlerOptions options)
    {
        var exceptionType = exception.GetType();

        // Check custom mappings first (exact type match)
        if (options.CustomExceptionMappings.TryGetValue(exceptionType, out var custom))
            return custom;

        // Check custom mappings for base types
        foreach (var mapping in options.CustomExceptionMappings)
        {
            if (mapping.Key.IsAssignableFrom(exceptionType))
                return mapping.Value;
        }

        // Built-in mappings
        return exception switch
        {
            ArgumentException => (StatusCodes.Status400BadRequest, "Invalid argument provided."),
            UnauthorizedAccessException => (StatusCodes.Status401Unauthorized, "Access denied."),
            KeyNotFoundException => (StatusCodes.Status404NotFound, "The requested resource was not found."),
            NotImplementedException => (StatusCodes.Status501NotImplemented, "This feature is not yet implemented."),
            TimeoutException => (StatusCodes.Status408RequestTimeout, "The request timed out."),
            OperationCanceledException => (StatusCodes.Status408RequestTimeout, "The operation was cancelled."),
            InvalidOperationException => (StatusCodes.Status400BadRequest, "Invalid operation."),
            _ => (StatusCodes.Status500InternalServerError, options.DefaultErrorMessage)
        };
    }

    /// <summary>
    /// Builds the structured error response body, conditionally including
    /// exception details in development environments.
    /// </summary>
    private static Dictionary<string, object?> BuildErrorBody(
        int statusCode,
        string message,
        HttpContext context,
        Exception exception,
        IHostEnvironment environment,
        GlobalErrorHandlerOptions options)
    {
        var body = new Dictionary<string, object?>
        {
            ["statusCode"] = statusCode,
            ["message"] = message,
            ["timestamp"] = DateTime.UtcNow.ToString("O"),
            ["requestId"] = Activity.Current?.Id
        };

        if (options.IncludeRequestPath)
            body["path"] = context.Request.Path.ToString();

        if (options.IncludeExceptionDetailsInDevelopment && environment.IsDevelopment())
        {
            body["exception"] = new Dictionary<string, object?>
            {
                ["type"] = exception.GetType().FullName,
                ["message"] = exception.Message,
                ["stackTrace"] = exception.StackTrace,
                ["source"] = exception.Source,
                ["innerException"] = exception.InnerException is not null
                    ? new Dictionary<string, object?>
                    {
                        ["type"] = exception.InnerException.GetType().FullName,
                        ["message"] = exception.InnerException.Message,
                        ["stackTrace"] = exception.InnerException.StackTrace
                    }
                    : null
            };
        }

        return body;
    }
}
