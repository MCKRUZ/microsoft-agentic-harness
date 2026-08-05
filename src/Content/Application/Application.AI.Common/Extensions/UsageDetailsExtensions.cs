using Microsoft.Extensions.AI;

namespace Application.AI.Common.Extensions;

/// <summary>
/// Reads a model provider's reported token usage consistently across the sites that consume it.
/// </summary>
/// <remarks>
/// Providers populate <see cref="UsageDetails"/> inconsistently: some report a total, others report the
/// input and output counts and leave the total unset. Reading only the total therefore discards real
/// billing data that is present, and every consumer that discovers this ends up writing the same coalesce
/// by hand. Written out per site, the rule drifts — and the symptom is two consumers reporting different
/// costs for the same response, which looks like a provider inconsistency rather than a local bug.
/// </remarks>
public static class UsageDetailsExtensions
{
    /// <summary>
    /// Gets the total tokens a response cost, preferring the provider's own total and falling back to the
    /// sum of its parts.
    /// </summary>
    /// <param name="usage">The reported usage. <see langword="null"/> yields 0.</param>
    /// <returns>
    /// The provider's total when it reported one, otherwise input plus output. 0 when nothing was reported,
    /// which callers should treat as "no usage available" rather than as a genuine zero cost.
    /// </returns>
    public static long TotalTokens(this UsageDetails? usage) =>
        usage is null
            ? 0
            : usage.TotalTokenCount ?? ((usage.InputTokenCount ?? 0) + (usage.OutputTokenCount ?? 0));
}
