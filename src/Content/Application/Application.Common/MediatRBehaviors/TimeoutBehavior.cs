using Application.Common.Interfaces.MediatR;
using Domain.Common.Config;
using MediatR;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Application.Common.MediatRBehaviors;

/// <summary>
/// Wraps request execution with a configurable timeout via a linked <see cref="CancellationTokenSource"/>.
/// Prevents hung LLM calls or unresponsive MCP servers from blocking the pipeline indefinitely.
/// </summary>
/// <remarks>
/// <para>Pipeline position: 2 (inside tracing, wraps everything else).</para>
/// <para>
/// Requests implementing <see cref="IHasTimeout"/> specify a custom timeout.
/// Others use <c>AgentConfig.DefaultRequestTimeoutSec</c>.
/// </para>
/// <para>
/// <b>Cooperative cancellation:</b> on timeout the behavior <i>cancels</i> the downstream work rather
/// than abandoning it. It arms a linked token source (<see cref="CancellationTokenSource.CreateLinkedTokenSource(System.Threading.CancellationToken)"/>
/// plus <see cref="CancellationTokenSource.CancelAfter(System.TimeSpan)"/>) and publishes that token as
/// <see cref="AmbientTimeoutToken"/> for the duration of the continuation. A handler (or inner behavior)
/// that observes <see cref="AmbientTimeoutToken"/> receives an <see cref="OperationCanceledException"/>
/// when the timeout elapses, so it stops work and does not commit side effects after the deadline.
/// </para>
/// <para>
/// MediatR 12's <see cref="RequestHandlerDelegate{TResponse}"/> is parameterless, so the timeout token
/// cannot be threaded through <c>next</c>; it is flowed ambiently instead. The behavior always surfaces
/// the timeout to the caller as a <see cref="TimeoutException"/>, whether or not the handler honored the
/// token, so a non-cooperative handler still cannot block the pipeline indefinitely.
/// </para>
/// </remarks>
public sealed class TimeoutBehavior<TRequest, TResponse>
    : IPipelineBehavior<TRequest, TResponse> where TRequest : notnull
{
    private static readonly AsyncLocal<CancellationToken> AmbientToken = new();

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

    /// <summary>
    /// Gets the timeout-aware cancellation token for the request currently executing on this async flow.
    /// </summary>
    /// <remarks>
    /// While a request is inside <see cref="Handle"/>, this token is cancelled if the incoming token is
    /// cancelled <i>or</i> the configured timeout elapses. Handlers and inner behaviors that want
    /// cooperative timeout cancellation should observe this token (e.g. pass it to async I/O) instead of
    /// relying solely on the MediatR-injected token, which is not aware of the per-request deadline.
    /// Outside a request scope this returns <see cref="CancellationToken.None"/>.
    /// </remarks>
    public static CancellationToken AmbientTimeoutToken => AmbientToken.Value;

    /// <inheritdoc />
    public async Task<TResponse> Handle(
        TRequest request,
        RequestHandlerDelegate<TResponse> next,
        CancellationToken cancellationToken)
    {
        var timeout = (request as IHasTimeout)?.Timeout
            ?? TimeSpan.FromSeconds(_config.CurrentValue.DefaultRequestTimeoutSec);

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        linkedCts.CancelAfter(timeout);

        var previousAmbient = AmbientToken.Value;
        AmbientToken.Value = linkedCts.Token;
        try
        {
            return await next().WaitAsync(linkedCts.Token);
        }
        catch (OperationCanceledException) when (linkedCts.IsCancellationRequested
            && !cancellationToken.IsCancellationRequested)
        {
            // The linked source fired but the incoming token did not: this is a timeout, not a
            // caller-initiated cancellation. Surface it as a timeout while the handler observes its
            // own OperationCanceledException via AmbientTimeoutToken.
            _logger.LogWarning("Request {RequestName} timed out after {Timeout}",
                typeof(TRequest).Name, timeout);
            throw new TimeoutException(
                $"Request '{typeof(TRequest).Name}' exceeded {timeout.TotalSeconds}s timeout.");
        }
        finally
        {
            AmbientToken.Value = previousAmbient;
        }
    }
}
