using System.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Infrastructure.Common.Middleware.Security;

/// <summary>
/// Logs security-relevant audit information for every HTTP request.
/// Captures timing, user context, client metadata, and response status
/// for security analysis, incident response, and compliance reporting.
/// </summary>
/// <remarks>
/// <para>
/// Register early in the pipeline (before authentication/authorization) to ensure
/// all requests are audited regardless of outcome:
/// <code>
/// app.UseMiddleware&lt;SecurityAuditMiddleware&gt;();
/// </code>
/// </para>
/// <para>
/// <strong>Key features:</strong>
/// </para>
/// <list type="bullet">
///   <item><description>Automatic request duration measurement via <see cref="Stopwatch"/>.</description></item>
///   <item><description>Structured logging with method, status code, path, and user agent.</description></item>
///   <item><description>Exception-safe: the <c>finally</c> block guarantees audit logging even when downstream middleware throws.</description></item>
///   <item><description>Non-intrusive: does not modify requests or responses.</description></item>
/// </list>
/// <para>
/// <strong>Security considerations:</strong> Audit logs may contain request metadata.
/// Configure secure log storage and appropriate retention policies for compliance.
/// </para>
/// </remarks>
public sealed class SecurityAuditMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<SecurityAuditMiddleware> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="SecurityAuditMiddleware"/> class.
    /// </summary>
    /// <param name="next">The next delegate in the middleware pipeline.</param>
    /// <param name="logger">Logger for security audit events.</param>
    public SecurityAuditMiddleware(RequestDelegate next, ILogger<SecurityAuditMiddleware> logger)
    {
        ArgumentNullException.ThrowIfNull(next);
        ArgumentNullException.ThrowIfNull(logger);

        _next = next;
        _logger = logger;
    }

    /// <summary>
    /// Processes the HTTP request and logs a security audit event upon completion.
    /// The audit log is written in a <c>finally</c> block to ensure it fires even
    /// when downstream middleware throws an exception.
    /// </summary>
    /// <param name="context">The HTTP context for the current request.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    public async Task InvokeAsync(HttpContext context)
    {
        var stopwatch = Stopwatch.StartNew();

        try
        {
            await _next(context);
        }
        finally
        {
            stopwatch.Stop();

            _logger.LogInformation(
                "Security audit: {Method} {Path} responded {StatusCode} in {ElapsedMs}ms | UserAgent={UserAgent} | IP={RemoteIp}",
                context.Request.Method,
                context.Request.Path,
                context.Response.StatusCode,
                stopwatch.ElapsedMilliseconds,
                context.Request.Headers.UserAgent.ToString(),
                context.Connection.RemoteIpAddress);
        }
    }
}
