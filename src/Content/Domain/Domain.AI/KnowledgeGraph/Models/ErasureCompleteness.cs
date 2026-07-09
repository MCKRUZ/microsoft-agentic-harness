namespace Domain.AI.KnowledgeGraph.Models;

/// <summary>
/// Whether a right-to-erasure request was fulfilled across its full declared scope, or only
/// partially. Recorded on <see cref="ErasureReceipt"/> so an erasure that could not complete
/// its scope never presents as a clean success.
/// </summary>
public enum ErasureCompleteness
{
    /// <summary>
    /// Every sweep the request's scope implies ran successfully. For an owner-scoped erasure
    /// this means the owner's nodes, owner-scoped edges, feedback weights, derived vector/BM25
    /// content, and cross-session memory were all purged. For a node-scoped erasure it means
    /// the targeted nodes and their derived content were purged.
    /// </summary>
    Full = 0,

    /// <summary>
    /// The erasure ran but could not fulfil its full declared scope — for example an
    /// owner-scoped request whose owner identity could not be resolved, so owner-scoped edge
    /// and cross-session-memory sweeps were skipped. The accompanying
    /// <see cref="ErasureReceipt.CompletenessReason"/> explains what was left unpurged.
    /// </summary>
    Partial = 1
}
