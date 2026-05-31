namespace Application.AI.Common.Interfaces.MediatR;

/// <summary>
/// Marker interface for MediatR requests that resolve and use one or more prompts
/// from <see cref="Prompts.Interfaces.IPromptRegistry"/> during their execution.
/// </summary>
/// <remarks>
/// <para>
/// The <see cref="Application.AI.Common.MediatRBehaviors.PromptUsageTrackingBehavior{TRequest,TResponse}"/>
/// only applies to requests implementing this interface. Requests that do not implement
/// <see cref="IConsumesPrompts"/> pass through the behavior without any prompt-usage recording.
/// </para>
/// <para>
/// Handlers for marker-bearing requests should not call <see cref="Prompts.Interfaces.IPromptUsageRecorder"/>
/// directly — they call <see cref="Prompts.Interfaces.IPromptUsageBag.Track"/> instead, and the
/// behavior drains the bag and records each entry after the handler completes (including on
/// partial failure). This avoids double-recording when a service intermediate (e.g.
/// <c>ConversationFactExtractor</c>) also records — services that own their own recording
/// stay on the direct-recorder path.
/// </para>
/// <para>
/// <see cref="ExpectedPromptNames"/> is purely declarative — used for warmup of the
/// registry's negative-cache and for observability tags. The behavior does not enforce
/// that the handler actually resolved exactly these names; mismatch is not an error.
/// </para>
/// </remarks>
/// <example>
/// <code>
/// public record ClassifyQueryCommand(string Query) : IRequest&lt;Classification&gt;, IConsumesPrompts
/// {
///     public IReadOnlyList&lt;string&gt; ExpectedPromptNames =&gt; [ "query-classifier" ];
/// }
/// </code>
/// </example>
public interface IConsumesPrompts
{
    /// <summary>
    /// Names of the prompts this request is expected to resolve, in resolution order.
    /// Optional — return an empty list when the set is dynamic or unknown ahead of time.
    /// </summary>
    /// <remarks>
    /// Implementations should return a stable, side-effect-free value. The behavior
    /// uses this list for prompt-cache warmup and for OTel tags; consumers may also
    /// inspect it for prompt allow-lists or A/B test routing.
    /// </remarks>
    IReadOnlyList<string> ExpectedPromptNames { get; }
}
