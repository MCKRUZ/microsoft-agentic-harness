using System.Diagnostics;
using Application.AI.Common.Prompts.Interfaces;
using Application.AI.Common.Prompts.Models;
using Domain.AI.Prompts;
using Microsoft.Extensions.Logging;

namespace Infrastructure.AI.Prompts;

/// <summary>
/// <see cref="IPromptUsageRecorder"/> that durably persists each usage event to
/// <see cref="IPromptUsageStore"/> so trace-replay can recover prompt assignments
/// after process restart.
/// </summary>
/// <remarks>
/// <para>
/// Captures trace/span ids from <c>Activity.Current</c> when present so persisted
/// rows correlate with OTel-stamped spans. The composite recorder
/// (<see cref="CompositePromptUsageRecorder"/>) wires this alongside
/// <see cref="OtelPromptUsageRecorder"/> when persistence is enabled.
/// </para>
/// <para>
/// Honors the <see cref="IPromptUsageRecorder"/> "Never throws" contract: store
/// failures are caught + logged at Warning, never propagated to the caller.
/// Observability code must not break the LLM hot path.
/// </para>
/// </remarks>
public sealed class PersistencePromptUsageRecorder : IPromptUsageRecorder
{
    private readonly IPromptUsageStore _store;
    private readonly ILogger<PersistencePromptUsageRecorder> _logger;

    /// <summary>Initializes a new instance.</summary>
    public PersistencePromptUsageRecorder(
        IPromptUsageStore store,
        ILogger<PersistencePromptUsageRecorder> logger)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(logger);
        _store = store;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<PromptUsageRecord> RecordAsync(
        PromptDescriptor descriptor,
        PromptUsageContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentNullException.ThrowIfNull(context);

        var activity = Activity.Current;
        var record = new PromptUsageRecord
        {
            Descriptor = descriptor,
            CaseId = context.CaseId,
            MetricKey = context.MetricKey,
            TraceId = activity?.TraceId.ToString(),
            SpanId = activity?.SpanId.ToString(),
            RecordedAtUtc = DateTimeOffset.UtcNow,
        };

        try
        {
            await _store.AppendAsync(record, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Honors the IPromptUsageRecorder contract: never throw on the caller's hot path.
            _logger.LogWarning(ex,
                "Failed to persist prompt usage for '{Prompt}' (case '{Case}').",
                descriptor.Name,
                context.CaseId);
        }

        return record;
    }
}
