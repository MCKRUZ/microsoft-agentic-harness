using Microsoft.Extensions.Logging;

namespace Infrastructure.APIAccess.Handlers;

/// <summary>
/// HTTP message handler that logs all outgoing HTTP requests at Debug level
/// for troubleshooting and performance analysis.
/// </summary>
/// <remarks>
/// This handler is registered in the HTTP client pipeline and logs both
/// synchronous and asynchronous requests. Debug-level logging avoids
/// cluttering production logs while providing detailed information
/// during development.
/// </remarks>
public sealed class LoggingDelegatingHandler : DelegatingHandler
{
    private readonly ILogger<LoggingDelegatingHandler> _logger;

    /// <summary>
    /// Initializes a new instance of <see cref="LoggingDelegatingHandler"/>.
    /// </summary>
    /// <param name="logger">Logger for recording HTTP request information.</param>
    public LoggingDelegatingHandler(ILogger<LoggingDelegatingHandler> logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
    }

    /// <summary>
    /// Logs the synchronous HTTP request before passing it to the next handler.
    /// </summary>
    /// <param name="request">The HTTP request message to be sent.</param>
    /// <param name="cancellationToken">Cancellation token for the operation.</param>
    /// <returns>The HTTP response message from the downstream service.</returns>
    protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        _logger.LogDebug("Outbound HTTP request: {Request}", request);
        return base.Send(request, cancellationToken);
    }

    /// <summary>
    /// Logs the asynchronous HTTP request before passing it to the next handler.
    /// </summary>
    /// <param name="request">The HTTP request message to be sent.</param>
    /// <param name="cancellationToken">Cancellation token for the operation.</param>
    /// <returns>A task representing the HTTP response from the downstream service.</returns>
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        _logger.LogDebug("Outbound HTTP request: {Request}", request);
        return base.SendAsync(request, cancellationToken);
    }
}
