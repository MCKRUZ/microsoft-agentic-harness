using Application.AI.Common.Prompts.Interfaces;
using Application.AI.Common.Prompts.Models;
using Domain.AI.Prompts;
using Microsoft.Extensions.Logging;

namespace Infrastructure.AI.Prompts;

/// <summary>
/// Fans out a single <see cref="IPromptUsageRecorder.RecordAsync"/> call across multiple
/// inner recorders so OTel tagging and durable persistence can both fire from one
/// call site. Used as the public <see cref="IPromptUsageRecorder"/> when persistence
/// is enabled.
/// </summary>
/// <remarks>
/// <para>
/// Each inner recorder is invoked independently — a failure in one (despite the
/// "Never throws" contract) does NOT short-circuit the others. The composite catches
/// every inner exception and logs at Warning so a buggy persistence backend can't
/// blind the OTel pipeline.
/// </para>
/// <para>
/// The returned <see cref="PromptUsageRecord"/> is whichever inner recorder's record
/// landed first in iteration order, with a defensive fallback to a synthetic record
/// when every inner threw. Callers should not rely on the returned record's identity —
/// it is diagnostic only.
/// </para>
/// </remarks>
public sealed class CompositePromptUsageRecorder : IPromptUsageRecorder
{
    private readonly IReadOnlyList<IPromptUsageRecorder> _inner;
    private readonly ILogger<CompositePromptUsageRecorder> _logger;

    /// <summary>Initializes a new instance.</summary>
    /// <param name="inner">Inner recorders, fanned out in iteration order.</param>
    /// <param name="logger">Logger for inner-recorder failures.</param>
    public CompositePromptUsageRecorder(
        IEnumerable<IPromptUsageRecorder> inner,
        ILogger<CompositePromptUsageRecorder> logger)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(logger);
        _inner = inner.ToList();
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

        PromptUsageRecord? firstRecord = null;
        foreach (var recorder in _inner)
        {
            try
            {
                var record = await recorder.RecordAsync(descriptor, context, cancellationToken).ConfigureAwait(false);
                firstRecord ??= record;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Inner prompt usage recorder '{Recorder}' threw on RecordAsync for '{Prompt}'.",
                    recorder.GetType().Name,
                    descriptor.Name);
            }
        }

        // Defensive fallback when every inner recorder threw — caller still gets a
        // valid record for diagnostics / chaining.
        return firstRecord ?? new PromptUsageRecord
        {
            Descriptor = descriptor,
            CaseId = context.CaseId,
            MetricKey = context.MetricKey,
            RecordedAtUtc = DateTimeOffset.UtcNow,
        };
    }
}
