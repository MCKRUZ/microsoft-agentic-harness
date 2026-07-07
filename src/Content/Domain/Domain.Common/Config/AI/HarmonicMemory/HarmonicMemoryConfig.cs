namespace Domain.Common.Config.AI.HarmonicMemory;

/// <summary>
/// Root configuration for the harmonic memory representation (Memora port). Bound from
/// <c>AppConfig:AI:HarmonicMemory</c> in appsettings.json.
/// </summary>
/// <remarks>
/// <para>
/// Harmonic memory indexes a lightweight scaffolding layer — a primary abstraction plus cue anchors —
/// over each cross-session memory value, rather than embedding the raw value. This yields more precise,
/// controlled recall while preserving full-fidelity content. See the plan at
/// <c>.claude/plans/harmonic-memory-representation.md</c>.
/// </para>
/// <para>
/// <strong>Off by default.</strong> The abstraction/consolidation write path costs one to two LLM calls
/// per remembered fact (vs. today's cheap cache insert), so it must be a deliberate opt-in. The cost
/// guards below bound that spend once enabled.
/// </para>
/// <code>
/// AppConfig.AI.HarmonicMemory
/// ├── Mode                   — Off (default) / AbstractOnly / Full — governs the write-time LLM cost
/// ├── MinContentLengthChars  — Only abstract facts at least this long (short facts stay on the legacy path)
/// ├── ConsolidationTopK      — How many similar existing entries the consolidator sees (Full mode)
/// ├── RecallCueAnchorFanout  — How many shared-cue-anchor neighbors recall pulls into a cluster
/// ├── RecallRrfK             — RRF constant fusing the harmonic and legacy recall lists
/// └── BatchAtSessionFlush    — Defer abstraction to session flush instead of per-RememberAsync
/// </code>
/// </remarks>
public class HarmonicMemoryConfig
{
    /// <summary>
    /// Graduated master toggle. When <see cref="HarmonicMemoryMode.Off"/> (the default), the memory write
    /// path is byte-for-byte the legacy path and no LLM calls are made.
    /// </summary>
    /// <value>Default: <see cref="HarmonicMemoryMode.Off"/></value>
    public HarmonicMemoryMode Mode { get; set; } = HarmonicMemoryMode.Off;

    /// <summary>
    /// Minimum content length, in characters, before a remembered fact is abstracted. Facts shorter than
    /// this skip abstraction and take the legacy path, sparing the LLM cost on trivial one-liners. Zero
    /// (the default) abstracts everything. Must not be negative.
    /// </summary>
    /// <value>Default: 0</value>
    public int MinContentLengthChars { get; set; }

    /// <summary>
    /// Number of similar existing entries the consolidator is shown when deciding merge-vs-create in
    /// <see cref="HarmonicMemoryMode.Full"/>. Higher values improve merge recall at the cost of a larger
    /// consolidation prompt. Ignored in <see cref="HarmonicMemoryMode.AbstractOnly"/>. Must be positive.
    /// </summary>
    /// <value>Default: 5</value>
    public int ConsolidationTopK { get; set; } = 5;

    /// <summary>
    /// Number of shared-cue-anchor neighbors the recall path pulls in around its best abstraction/cue matches
    /// (harmonic recall only; ignored when <see cref="HarmonicMemoryMode.Off"/>). Cue anchors form an implicit
    /// memory graph — two facts sharing an anchor are neighbors — so after ranking the query's direct hits,
    /// recall expands the coherent cluster around them, bounded by this fan-out to keep the result focused.
    /// Zero disables traversal (direct matches only). Must not be negative.
    /// </summary>
    /// <value>Default: 3</value>
    public int RecallCueAnchorFanout { get; set; } = 3;

    /// <summary>
    /// The Reciprocal Rank Fusion constant used to blend the harmonic recall list (abstraction + cue-anchor
    /// matches) with the legacy substring/graph list into one ranking (harmonic recall only). Higher values
    /// flatten the influence of top ranks; 60 matches the RAG retriever's default. Must be positive.
    /// </summary>
    /// <value>Default: 60</value>
    public double RecallRrfK { get; set; } = 60.0;

    /// <summary>
    /// Reserved for a future deferred-batching mode that would amortize the abstraction LLM cost across a
    /// session instead of paying it per <c>RememberAsync</c>.
    /// </summary>
    /// <remarks>
    /// <strong>Not supported in this build.</strong> Abstraction runs inline on each write, and there is no
    /// session-flush seam to defer into (cross-session memory is persisted durably inline). Setting this to
    /// <see langword="true"/> is <em>rejected at startup</em> by <c>HarmonicMemoryConfigValidator</c> rather
    /// than silently ignored — leave it <see langword="false"/>. A future release will add the deferred path
    /// and lift the restriction.
    /// </remarks>
    /// <value>Default: false</value>
    public bool BatchAtSessionFlush { get; set; }
}
