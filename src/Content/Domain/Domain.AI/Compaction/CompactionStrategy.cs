namespace Domain.AI.Compaction;

/// <summary>
/// Defines the compaction algorithm to use when reducing context window usage.
/// </summary>
public enum CompactionStrategy
{
    /// <summary>Sends entire history to LLM for summarization. Most thorough but costs an API call.</summary>
    Full,

    /// <summary>Compacts only a portion of the history (before or after a pivot point).</summary>
    Partial,

    /// <summary>Lightweight — replaces stale tool results without an LLM call.</summary>
    Micro
}
