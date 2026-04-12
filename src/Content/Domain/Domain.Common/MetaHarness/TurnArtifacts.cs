namespace Domain.Common.MetaHarness;

/// <summary>
/// Represents everything written to a <c>turns/{n}/</c> subdirectory for a single agent turn.
/// All properties are nullable — a turn artifact may contain only a subset of files.
/// </summary>
public sealed record TurnArtifacts
{
    /// <summary>1-based turn index.</summary>
    public int TurnNumber { get; init; }

    /// <summary>Contents of <c>system_prompt.md</c>.</summary>
    public string? SystemPrompt { get; init; }

    /// <summary>Raw JSONL string written to <c>tool_calls.jsonl</c>.</summary>
    public string? ToolCallsJsonl { get; init; }

    /// <summary>Contents of <c>model_response.md</c>.</summary>
    public string? ModelResponse { get; init; }

    /// <summary>JSON string written to <c>state_snapshot.json</c>.</summary>
    public string? StateSnapshot { get; init; }

    /// <summary>
    /// Map of <c>callId → serialized result</c> written to <c>tool_results/{callId}.json</c>.
    /// </summary>
    public IReadOnlyDictionary<string, string> ToolResults { get; init; } =
        new Dictionary<string, string>();
}
