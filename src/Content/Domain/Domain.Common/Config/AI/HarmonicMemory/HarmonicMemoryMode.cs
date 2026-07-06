namespace Domain.Common.Config.AI.HarmonicMemory;

/// <summary>
/// Graduated toggle for the harmonic memory representation (Memora-style abstraction + cue anchors)
/// on the cross-session memory write path. Bound from <c>AppConfig:AI:HarmonicMemory:Mode</c>.
/// </summary>
/// <remarks>
/// <para>
/// The mode exists to make the write-time LLM cost an explicit, opt-in decision. Producing a primary
/// abstraction and cue anchors costs one LLM call per remembered fact; consolidation adds a second.
/// A fresh consumer pays nothing until they deliberately raise the mode.
/// </para>
/// <para>
/// Lives in <c>Domain.Common</c> (not <c>Domain.AI</c>) because the configuration POCO that carries it
/// also lives here, and <c>Domain.Common</c> may not reference <c>Domain.AI</c> (the dependency runs the
/// other way). <c>Domain.AI</c> and the Application layer consume this enum freely.
/// </para>
/// </remarks>
public enum HarmonicMemoryMode
{
    /// <summary>
    /// Disabled (default). The legacy write path is unchanged: caller-supplied key, substring/graph
    /// recall, no abstraction, no cue anchors, zero LLM calls on write.
    /// </summary>
    Off = 0,

    /// <summary>
    /// Generate a primary abstraction and cue anchors for each remembered fact, but skip consolidation —
    /// every write creates a new entry. One LLM call per write.
    /// </summary>
    AbstractOnly = 1,

    /// <summary>
    /// Full harmonic write path: abstraction + cue anchors, plus LLM consolidation against similar
    /// existing entries (merge into an existing entry vs. create a new one). One to two LLM calls per write.
    /// </summary>
    Full = 2,
}
