namespace Domain.Common.Middleware;

/// <summary>
/// Configuration options for the global error handling middleware.
/// Allows customization of error responses, exception-to-status-code mappings,
/// and development-mode behavior.
/// </summary>
/// <remarks>
/// Used by <c>Presentation.Common.Extensions.IApplicationBuilderExtensions.UseGlobalErrorHandler()</c>
/// to configure how unhandled exceptions are translated into HTTP error responses.
/// </remarks>
public class GlobalErrorHandlerOptions
{
    /// <summary>
    /// Gets or sets the default error message for HTTP 500 responses.
    /// </summary>
    /// <value>Default: "An internal server error occurred. Please try again later."</value>
    public string DefaultErrorMessage { get; set; } = "An internal server error occurred. Please try again later.";

    /// <summary>
    /// Gets or sets the response content type for error responses.
    /// </summary>
    /// <value>Default: "application/json".</value>
    public string ContentType { get; set; } = "application/json";

    /// <summary>
    /// Gets or sets whether to include exception details (type, message, stack trace)
    /// in error responses when running in a Development environment.
    /// </summary>
    /// <value>Default: true.</value>
    public bool IncludeExceptionDetailsInDevelopment { get; set; } = true;

    /// <summary>
    /// Gets or sets whether to include the request path in error responses.
    /// </summary>
    /// <value>Default: true.</value>
    public bool IncludeRequestPath { get; set; } = true;

    /// <summary>
    /// Gets or sets custom exception-to-HTTP-status-code mappings.
    /// These take priority over the built-in mappings.
    /// </summary>
    /// <remarks>
    /// Built-in mappings (applied when no custom mapping matches):
    /// <list type="bullet">
    ///   <item><c>ArgumentException</c> → 400</item>
    ///   <item><c>UnauthorizedAccessException</c> → 401</item>
    ///   <item><c>KeyNotFoundException</c> → 404</item>
    ///   <item><c>NotImplementedException</c> → 501</item>
    ///   <item><c>TimeoutException</c> → 408</item>
    ///   <item><c>OperationCanceledException</c> → 408</item>
    ///   <item><c>InvalidOperationException</c> → 400</item>
    /// </list>
    /// </remarks>
    public Dictionary<Type, (int StatusCode, string Message)> CustomExceptionMappings { get; set; } = new();
}
