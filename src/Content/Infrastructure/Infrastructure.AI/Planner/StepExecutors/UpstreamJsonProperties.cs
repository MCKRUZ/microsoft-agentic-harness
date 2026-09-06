using System.Text.Json;

namespace Infrastructure.AI.Planner.StepExecutors;

/// <summary>
/// Parses an upstream plan step's JSON output and yields its top-level properties, tolerating
/// malformed input the same way every caller already did independently before this was extracted.
/// </summary>
/// <remarks>
/// #595 code-review: <c>ToolUseStepExecutor.BuildToolArguments</c> and
/// <c>ConditionalBranchStepExecutor.BuildEvaluationContext</c> each independently implemented the
/// identical "parse, guard a non-object root, enumerate, catch <see cref="JsonException"/>" shape —
/// and the non-object-root guard was added to one during this PR's own review and missed in the
/// other until a later round, the exact drift class this extraction exists to prevent. What is NOT
/// shared: how a caller converts each <see cref="JsonProperty"/>'s value — <c>BuildToolArguments</c>
/// needs JSON-encoded text for re-serialization, <c>BuildEvaluationContext</c> needs typed CLR values
/// for condition evaluation. Those are genuinely different value semantics per consumer, so each
/// caller keeps its own conversion switch; only the parsing boilerplate is centralized here.
/// </remarks>
internal static class UpstreamJsonProperties
{
    /// <summary>
    /// Yields <paramref name="output"/>'s top-level properties, or nothing at all when
    /// <paramref name="output"/> is null/empty, is not valid JSON, or does not parse to a JSON object
    /// (a bare array or number is valid JSON but has no properties to enumerate) — every case a
    /// malformed or unexpectedly-shaped upstream output can take, none of them thrown as an exception
    /// to the caller.
    /// </summary>
    public static IEnumerable<JsonProperty> Parse(string? output)
    {
        if (string.IsNullOrEmpty(output))
            yield break;

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(output);
        }
        catch (JsonException)
        {
            yield break;
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                yield break;

            foreach (var property in document.RootElement.EnumerateObject())
                yield return property;
        }
    }
}
