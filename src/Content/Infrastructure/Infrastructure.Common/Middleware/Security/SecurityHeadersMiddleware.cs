using Microsoft.AspNetCore.Http;

namespace Infrastructure.Common.Middleware.Security;

/// <summary>
/// Applies defense-in-depth security headers to every HTTP response.
/// Should be registered early in the middleware pipeline before response bodies are written.
/// </summary>
/// <remarks>
/// <para>
/// Headers applied:
/// </para>
/// <list type="table">
///   <listheader>
///     <term>Header</term>
///     <description>Purpose</description>
///   </listheader>
///   <item><term>X-Content-Type-Options: nosniff</term><description>Prevents MIME-type sniffing attacks.</description></item>
///   <item><term>X-Frame-Options: DENY</term><description>Blocks clickjacking via iframe embedding.</description></item>
///   <item><term>X-XSS-Protection: 1; mode=block</term><description>Legacy XSS filter for older browsers.</description></item>
///   <item><term>Referrer-Policy: no-referrer</term><description>Prevents leaking referrer information to external sites.</description></item>
///   <item><term>Content-Security-Policy</term><description>Restricts content sources to prevent XSS and injection.</description></item>
///   <item><term>Permissions-Policy</term><description>Disables sensitive browser APIs (geolocation, camera, microphone).</description></item>
///   <item><term>Strict-Transport-Security</term><description>Enforces HTTPS for 1 year with subdomain coverage and preload.</description></item>
/// </list>
/// <para>
/// Usage:
/// <code>
/// app.UseMiddleware&lt;SecurityHeadersMiddleware&gt;();
/// </code>
/// </para>
/// </remarks>
public sealed class SecurityHeadersMiddleware
{
    private readonly RequestDelegate _next;

    /// <summary>
    /// Initializes a new instance of the <see cref="SecurityHeadersMiddleware"/> class.
    /// </summary>
    /// <param name="next">The next delegate in the middleware pipeline.</param>
    public SecurityHeadersMiddleware(RequestDelegate next)
    {
        ArgumentNullException.ThrowIfNull(next);
        _next = next;
    }

    /// <summary>
    /// Adds security headers to the response and invokes the next middleware.
    /// </summary>
    /// <param name="context">The HTTP context for the current request.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    public Task InvokeAsync(HttpContext context)
    {
        var headers = context.Response.Headers;

        headers["X-Content-Type-Options"] = "nosniff";
        headers["X-Frame-Options"] = "DENY";
        headers["X-XSS-Protection"] = "1; mode=block";
        headers["Referrer-Policy"] = "no-referrer";
        headers["Content-Security-Policy"] = "default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self' data:; font-src 'self'; connect-src 'self'";
        headers["Permissions-Policy"] = "geolocation=(), microphone=(), camera=()";
        headers["Strict-Transport-Security"] = "max-age=31536000; includeSubDomains; preload";

        return _next(context);
    }
}
