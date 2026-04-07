using CorrelationId;
using CorrelationId.Abstractions;
using Microsoft.Extensions.Options;

namespace Infrastructure.APIAccess.Handlers;

/// <summary>
/// HTTP message handler that propagates correlation IDs to outgoing HTTP requests,
/// enabling end-to-end request tracing across service boundaries.
/// </summary>
/// <remarks>
/// In an agentic harness, tracing requests through multiple service calls
/// (Azure OpenAI, Content Safety, MCP servers) is essential for debugging
/// and performance optimization.
/// <para>
/// <example>
/// Configure with IHttpClientFactory:
/// <code>
/// services.AddHttpClient("ExternalAPI")
///     .AddHttpMessageHandler&lt;CorrelationIdDelegatingHandler&gt;();
/// </code>
/// </example>
/// </para>
/// </remarks>
public sealed class CorrelationIdDelegatingHandler : DelegatingHandler
{
    private readonly ICorrelationContextAccessor _correlationContextAccessor;
    private readonly IOptions<CorrelationIdOptions> _options;

    /// <summary>
    /// Initializes a new instance of <see cref="CorrelationIdDelegatingHandler"/>.
    /// </summary>
    /// <param name="correlationContextAccessor">
    /// Accessor for the current request's correlation context.
    /// </param>
    /// <param name="options">
    /// Configuration options including the header name for correlation propagation.
    /// </param>
    public CorrelationIdDelegatingHandler(
        ICorrelationContextAccessor correlationContextAccessor,
        IOptions<CorrelationIdOptions> options)
    {
        ArgumentNullException.ThrowIfNull(correlationContextAccessor);
        ArgumentNullException.ThrowIfNull(options);

        _correlationContextAccessor = correlationContextAccessor;
        _options = options;
    }

    /// <summary>
    /// Adds the correlation ID header to the outgoing request if not already present,
    /// then passes the request to the next handler in the pipeline.
    /// </summary>
    /// <param name="request">The HTTP request message to be sent.</param>
    /// <param name="cancellationToken">Cancellation token for the async operation.</param>
    /// <returns>The HTTP response message from the downstream service.</returns>
    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        if (!request.Headers.Contains(_options.Value.RequestHeader)
            && _correlationContextAccessor.CorrelationContext is not null)
        {
            request.Headers.Add(
                _options.Value.RequestHeader,
                _correlationContextAccessor.CorrelationContext.CorrelationId);
        }

        return base.SendAsync(request, cancellationToken);
    }
}
