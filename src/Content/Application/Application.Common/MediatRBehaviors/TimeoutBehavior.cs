using Application.Common.Interfaces.MediatR;
using Domain.Common.Config;
using MediatR;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Application.Common.MediatRBehaviors;

/// <summary>
/// Wraps request execution with a configurable timeout via <see cref="CancellationTokenSource"/>.
/// Prevents hung LLM calls or unresponsive MCP servers from blocking the pipeline indefinitely.
/// </summary>
/// <remarks>
/// Pipeline position: 2 (inside tracing, wraps everything else).
/// Requests implementing <see cref="IHasTimeout"/> specify a custom timeout.
/// Others use <c>AgentConfig.DefaultRequestTimeoutSec</c>.
/// </remarks>
public sealed class TimeoutBehavior<TRequest, TResponse>
    : IPipelineBehavior<TRequest, TResponse> where TRequest : notnull
{
    private readonly IOptionsMonitor<AgentConfig> _config;
    private readonly ILogger<TimeoutBehavior<TRequest, TResponse>> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="TimeoutBehavior{TRequest, TResponse}"/> class.
    /// </summary>
    public TimeoutBehavior(IOptionsMonitor<AgentConfig> config, ILogger<TimeoutBehavior<TRequest, TResponse>> logger)
    {
        _config = config;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<TResponse> Handle(
        TRequest request,
        RequestHandlerDelegate<TResponse> next,
        CancellationToken cancellationToken)
    {
        var timeout = (request as IHasTimeout)?.Timeout
            ?? TimeSpan.FromSeconds(_config.CurrentValue.DefaultRequestTimeoutSec);

        try
        {
            return await next().WaitAsync(timeout, cancellationToken);
        }
        catch (TimeoutException ex)
        {
            _logger.LogWarning(ex, "Request {RequestName} timed out after {Timeout}",
                typeof(TRequest).Name, timeout);
            throw new TimeoutException(
                $"Request '{typeof(TRequest).Name}' exceeded {timeout.TotalSeconds}s timeout.", ex);
        }
    }
}
