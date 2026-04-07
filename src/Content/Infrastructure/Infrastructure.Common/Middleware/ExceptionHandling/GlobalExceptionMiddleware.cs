using Application.Common.Exceptions.ExceptionTypes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Infrastructure.Common.Middleware.ExceptionHandling;

/// <summary>
/// Centralized exception handling middleware for ASP.NET Core applications.
/// Intercepts all unhandled exceptions during request processing and maps them
/// to consistent, structured HTTP error responses.
/// </summary>
/// <remarks>
/// <para>
/// Designed for minimal APIs and applications that do not use MVC exception filters.
/// Register early in the pipeline so all downstream exceptions are caught:
/// <code>
/// app.UseMiddleware&lt;GlobalExceptionMiddleware&gt;();
/// </code>
/// </para>
/// <para>
/// <strong>Exception-to-status-code mappings:</strong>
/// </para>
/// <list type="table">
///   <listheader>
///     <term>Exception Type</term>
///     <description>HTTP Status Code</description>
///   </listheader>
///   <item><term><see cref="NoContentException"/></term><description>204 No Content</description></item>
///   <item><term><see cref="BadRequestException"/></term><description>400 Bad Request</description></item>
///   <item><term><see cref="UnauthorizedAccessException"/></term><description>401 Unauthorized</description></item>
///   <item><term><see cref="ForbiddenAccessException"/></term><description>403 Forbidden</description></item>
///   <item><term><see cref="EntityNotFoundException"/></term><description>404 Not Found</description></item>
///   <item><term><see cref="DatabaseInteractionException"/></term><description>422 Unprocessable Entity</description></item>
///   <item><term>Unhandled (Development)</term><description>500 Internal Server Error</description></item>
///   <item><term>Unhandled (Production)</term><description>400 Bad Request (generic message)</description></item>
/// </list>
/// <para>
/// <strong>Security:</strong> Production environments receive generic error messages to
/// prevent information disclosure. Development environments receive full exception details.
/// All exceptions are logged regardless of environment.
/// </para>
/// </remarks>
public sealed class GlobalExceptionMiddleware
{
    private static readonly Dictionary<Type, int> ExceptionStatusMap = new()
    {
        [typeof(BadRequestException)] = StatusCodes.Status400BadRequest,
        [typeof(UnauthorizedAccessException)] = StatusCodes.Status401Unauthorized,
        [typeof(ForbiddenAccessException)] = StatusCodes.Status403Forbidden,
        [typeof(EntityNotFoundException)] = StatusCodes.Status404NotFound,
        [typeof(DatabaseInteractionException)] = StatusCodes.Status422UnprocessableEntity,
    };

    private readonly RequestDelegate _next;
    private readonly IWebHostEnvironment _env;
    private readonly ILogger<GlobalExceptionMiddleware> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="GlobalExceptionMiddleware"/> class.
    /// </summary>
    /// <param name="next">The next delegate in the middleware pipeline.</param>
    /// <param name="environment">Web host environment for determining response detail level.</param>
    /// <param name="logger">Logger for exception tracking and diagnostics.</param>
    public GlobalExceptionMiddleware(
        RequestDelegate next,
        IWebHostEnvironment environment,
        ILogger<GlobalExceptionMiddleware> logger)
    {
        ArgumentNullException.ThrowIfNull(next);
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(logger);

        _next = next;
        _env = environment;
        _logger = logger;
    }

    /// <summary>
    /// Processes the HTTP request and handles any exceptions thrown by downstream middleware.
    /// </summary>
    /// <param name="context">The HTTP context for the current request.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await _next(context);
        }
        catch (Exception ex)
        {
            await HandleExceptionAsync(context, ex);
        }
    }

    private async Task HandleExceptionAsync(HttpContext context, Exception ex)
    {
        _logger.LogWarning(ex, "Unhandled exception on {Method} {Path}", context.Request.Method, context.Request.Path);

        context.Response.ContentType = "application/json";

        var exceptionType = ex is AggregateException aggregate
            ? aggregate.Flatten().InnerExceptions[0].GetType()
            : ex.GetType();

        if (exceptionType == typeof(NoContentException))
        {
            context.Response.StatusCode = StatusCodes.Status204NoContent;
            return;
        }

        if (ExceptionStatusMap.TryGetValue(exceptionType, out var statusCode))
        {
            await WriteErrorResponseAsync(context, statusCode, ex.Message);
            return;
        }

        await WriteUnhandledExceptionResponseAsync(context, ex);
    }

    private async Task WriteUnhandledExceptionResponseAsync(HttpContext context, Exception ex)
    {
        if (_env.IsDevelopment())
        {
            context.Response.StatusCode = StatusCodes.Status500InternalServerError;
            await context.Response.WriteAsJsonAsync(new
            {
                error = ex.Message,
                type = ex.GetType().Name,
                stackTrace = ex.StackTrace,
                statusCode = StatusCodes.Status500InternalServerError,
                timestamp = DateTime.UtcNow,
            });
            return;
        }

        await WriteErrorResponseAsync(
            context,
            StatusCodes.Status400BadRequest,
            "An unexpected error occurred. Please try again later.");
    }

    private static Task WriteErrorResponseAsync(HttpContext context, int statusCode, string message)
    {
        context.Response.StatusCode = statusCode;

        return context.Response.WriteAsJsonAsync(new
        {
            error = message,
            statusCode,
            timestamp = DateTime.UtcNow,
        });
    }
}
