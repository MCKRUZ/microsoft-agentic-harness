namespace Domain.Common.Config.AI.RAG;

/// <summary>
/// Configuration for Corrective RAG (CRAG) which evaluates retrieved chunk
/// relevance and applies corrective actions when quality is insufficient.
/// Bound from <c>AppConfig:AI:Rag:Crag</c> in appsettings.json.
/// </summary>
public class CragConfig
{
    /// <summary>
    /// Gets or sets whether CRAG evaluation is enabled. When <c>true</c>,
    /// retrieved chunks are scored for relevance and corrective actions
    /// are applied based on threshold comparisons.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Gets or sets the minimum relevance score (0-1) above which chunks
    /// are accepted without correction. Chunks scoring at or above this
    /// threshold use the <c>accept</c> action.
    /// </summary>
    public double AcceptThreshold { get; set; } = 0.7;

    /// <summary>
    /// Gets or sets the relevance score threshold (0-1) below which chunks
    /// are considered irrelevant. Scores between <see cref="RefineThreshold"/>
    /// and <see cref="AcceptThreshold"/> trigger the <c>refine</c> action;
    /// scores below trigger <c>web_fallback</c> or <c>reject</c>.
    /// </summary>
    public double RefineThreshold { get; set; } = 0.4;

    /// <summary>
    /// Gets or sets whether web search fallback is allowed when retrieved
    /// chunks score below <see cref="RefineThreshold"/>. When <c>false</c>,
    /// the <c>reject</c> action is used instead of <c>web_fallback</c>.
    /// </summary>
    public bool AllowWebFallback { get; set; } = false;
}
