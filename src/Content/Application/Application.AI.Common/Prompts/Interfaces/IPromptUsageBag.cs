using Domain.AI.Prompts;

namespace Application.AI.Common.Prompts.Interfaces;

/// <summary>
/// Request-scoped accumulator for prompts resolved during a MediatR request's lifetime.
/// Handlers for <see cref="MediatR.IConsumesPrompts"/> requests <see cref="Track"/> each
/// resolved descriptor + context here; the
/// <see cref="Application.AI.Common.MediatRBehaviors.PromptUsageTrackingBehavior{TRequest,TResponse}"/>
/// drains the accumulator after the handler completes and forwards every entry to
/// <see cref="IPromptUsageRecorder.RecordAsync"/>.
/// </summary>
/// <remarks>
/// <para>
/// Implementations MUST be thread-safe: a handler may resolve prompts from multiple
/// async branches concurrently (parallel sub-queries, fan-out fact extraction, etc.).
/// </para>
/// <para>
/// Lifetime is per MediatR request (DI Scoped). Each request gets a fresh bag; cross-request
/// state never leaks. The pipeline behavior is the single canonical drainer — handlers
/// should NOT call <see cref="Drain"/> themselves.
/// </para>
/// </remarks>
public interface IPromptUsageBag
{
    /// <summary>
    /// Records that <paramref name="descriptor"/> was used in the supplied attribution
    /// <paramref name="context"/>. Idempotency is not enforced — duplicate Track calls
    /// produce duplicate recorder entries; handlers should call once per logical use.
    /// </summary>
    void Track(PromptDescriptor descriptor, PromptUsageContext context);

    /// <summary>
    /// Returns all entries accumulated so far and clears the bag atomically. After
    /// <see cref="Drain"/> returns, the bag is empty regardless of how many entries it held.
    /// </summary>
    /// <returns>The drained entries in insertion order.</returns>
    IReadOnlyList<PromptUsageBagEntry> Drain();
}

/// <summary>
/// A single (descriptor, context) pair captured by <see cref="IPromptUsageBag.Track"/>
/// and replayed against <see cref="IPromptUsageRecorder.RecordAsync"/> by the pipeline behavior.
/// </summary>
public sealed record PromptUsageBagEntry(PromptDescriptor Descriptor, PromptUsageContext Context);
