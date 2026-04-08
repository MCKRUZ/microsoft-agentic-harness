using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using System.Net;

namespace Presentation.Common.Filters;

/// <summary>
/// Extension methods for <see cref="ExceptionContext"/> that produce standardized
/// error responses across all exception filters in the application.
/// </summary>
/// <remarks>
/// <para>
/// Use this in custom <see cref="IExceptionFilter"/> or <see cref="IAsyncExceptionFilter"/>
/// implementations to ensure every unhandled exception returns the same JSON envelope:
/// <code>
/// {
///   "error":      "Human-readable message",
///   "statusCode": 500,
///   "errors":     ["detail-1", "detail-2"],
///   "timestamp":  "2026-04-07T12:34:56.789Z"
/// }
/// </code>
/// </para>
/// <para>
/// The method marks the exception as handled and returns the context for fluent chaining,
/// so filters can short-circuit in a single expression.
/// </para>
/// </remarks>
/// <example>
/// <code>
/// public class GlobalExceptionFilter : IExceptionFilter
/// {
///     public void OnException(ExceptionContext context)
///     {
///         context.ReplaceResponse(
///             HttpStatusCode.InternalServerError,
///             "An unexpected error occurred.",
///             new List&lt;string&gt; { context.Exception.Message });
///     }
/// }
/// </code>
/// </example>
public static class ExceptionContextExtensions
{
    /// <summary>
    /// Replaces the exception context result with a standardized error response
    /// and marks the exception as handled.
    /// </summary>
    /// <param name="context">The exception context to modify.</param>
    /// <param name="statusCode">The HTTP status code to return to the caller.</param>
    /// <param name="bodyContext">The primary error message included in the response body.</param>
    /// <param name="errors">
    /// Optional list of detailed error messages (e.g., validation failures).
    /// Defaults to an empty list when <c>null</c>.
    /// </param>
    /// <returns>The modified <see cref="ExceptionContext"/> for fluent chaining.</returns>
    /// <remarks>
    /// The response body is an anonymous object serialized as JSON containing:
    /// <list type="bullet">
    ///   <item><c>error</c> — the primary error message from <paramref name="bodyContext"/></item>
    ///   <item><c>statusCode</c> — the numeric HTTP status code</item>
    ///   <item><c>errors</c> — detailed error list (empty when none provided)</item>
    ///   <item><c>timestamp</c> — UTC timestamp of the error occurrence</item>
    /// </list>
    /// After this call, <see cref="ExceptionContext.ExceptionHandled"/> is <c>true</c>
    /// and the exception will not propagate further through the filter pipeline.
    /// </remarks>
    public static ExceptionContext ReplaceResponse(
        this ExceptionContext context,
        HttpStatusCode statusCode,
        string bodyContext,
        List<string>? errors = null)
    {
        var response = new
        {
            error = bodyContext,
            statusCode = (int)statusCode,
            errors = errors ?? new List<string>(),
            timestamp = DateTime.UtcNow
        };

        context.Result = new ObjectResult(response)
        {
            StatusCode = (int)statusCode
        };

        context.ExceptionHandled = true;
        return context;
    }
}
