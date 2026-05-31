using System.Runtime.ExceptionServices;
using Application.AI.Common.Interfaces.MediatR;
using Application.AI.Common.Prompts.Interfaces;
using MediatR;
using Microsoft.Extensions.Logging;

namespace Application.AI.Common.MediatRBehaviors;

/// <summary>
/// Drains the request-scoped <see cref="IPromptUsageBag"/> after the handler runs and
/// forwards every tracked entry to <see cref="IPromptUsageRecorder.RecordAsync"/>. This
/// gives MediatR handlers an "auto-record" path: they call
/// <see cref="IPromptUsageBag.Track"/> at the point of resolution, and the pipeline takes
/// care of recording.
/// </summary>
/// <remarks>
/// <para>
/// Skips entirely when the request does not implement <see cref="IConsumesPrompts"/>.
/// </para>
/// <para>
/// Drains the bag <i>regardless of handler success</i>: a partially-failed handler may
/// have tracked prompts before the failure, and that work should still be recorded so
/// trace replay can see what was attempted. The behavior rethrows any handler exception
/// after recording completes (via <see cref="ExceptionDispatchInfo"/> to preserve the
/// original stack trace).
/// </para>
/// <para>
/// <see cref="OperationCanceledException"/> from the handler propagates immediately with
/// no drain, matching the cancellation semantics of the rest of the pipeline (cooperative
/// cancellation should not produce observability side effects).
/// </para>
/// <para>
/// Trusts the <see cref="IPromptUsageRecorder"/> "Never throws" contract — no defensive
/// try/catch around <see cref="IPromptUsageRecorder.RecordAsync"/>. A throwing recorder
/// is a defect to fix, not a transient condition to swallow.
/// </para>
/// </remarks>
public sealed class PromptUsageTrackingBehavior<TRequest, TResponse>
    : IPipelineBehavior<TRequest, TResponse> where TRequest : notnull
{
    private readonly IPromptUsageBag _bag;
    private readonly IPromptUsageRecorder _recorder;
    private readonly ILogger<PromptUsageTrackingBehavior<TRequest, TResponse>> _logger;

    /// <summary>Initializes a new instance.</summary>
    public PromptUsageTrackingBehavior(
        IPromptUsageBag bag,
        IPromptUsageRecorder recorder,
        ILogger<PromptUsageTrackingBehavior<TRequest, TResponse>> logger)
    {
        ArgumentNullException.ThrowIfNull(bag);
        ArgumentNullException.ThrowIfNull(recorder);
        ArgumentNullException.ThrowIfNull(logger);

        _bag = bag;
        _recorder = recorder;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<TResponse> Handle(
        TRequest request,
        RequestHandlerDelegate<TResponse> next,
        CancellationToken cancellationToken)
    {
        if (request is not IConsumesPrompts) return await next();

        TResponse? response = default;
        ExceptionDispatchInfo? captured = null;
        try
        {
            response = await next();
        }
        catch (OperationCanceledException)
        {
            // Don't record on cancellation: handler work is being abandoned, the partial
            // trace is not a useful observability signal.
            throw;
        }
        catch (Exception ex)
        {
            captured = ExceptionDispatchInfo.Capture(ex);
        }

        var entries = _bag.Drain();
        if (entries.Count > 0)
        {
            _logger.LogDebug(
                "Recording {Count} prompt usage entries for {Request}.",
                entries.Count,
                typeof(TRequest).Name);

            foreach (var entry in entries)
            {
                await _recorder.RecordAsync(entry.Descriptor, entry.Context, cancellationToken).ConfigureAwait(false);
            }
        }

        if (captured is not null)
        {
            captured.Throw();
        }

        return response!;
    }
}
