namespace Domain.Common.Config.AI.RAG;

/// <summary>
/// Configuration for model tiering which routes RAG operations to
/// appropriate model tiers based on task complexity and cost constraints.
/// Bound from <c>AppConfig:AI:Rag:ModelTiering</c> in appsettings.json.
/// </summary>
public class ModelTieringConfig
{
    /// <summary>
    /// Gets or sets whether model tiering is enabled. When <c>false</c>,
    /// all operations use the default agent framework model.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Gets or sets the default tier for operations not listed in
    /// <see cref="OperationOverrides"/>. Must match a <see cref="ModelTierDefinition.Name"/>
    /// in <see cref="Tiers"/>.
    /// </summary>
    public string DefaultTier { get; set; } = "standard";

    /// <summary>
    /// Gets or sets per-operation tier overrides. Keys are RAG operation names;
    /// values are tier names matching <see cref="ModelTierDefinition.Name"/>.
    /// <para>Well-known operation keys:</para>
    /// <list type="bullet">
    ///   <item><description><c>contextual_enrichment</c> — chunk context summary generation</description></item>
    ///   <item><description><c>raptor_summarization</c> — RAPTOR tree node summarization</description></item>
    ///   <item><description><c>crag_evaluation</c> — CRAG relevance scoring</description></item>
    ///   <item><description><c>query_classification</c> — automatic query type detection</description></item>
    ///   <item><description><c>rag_fusion</c> — query variant generation</description></item>
    ///   <item><description><c>hyde_generation</c> — hypothetical document generation</description></item>
    ///   <item><description><c>graph_entity_extraction</c> — GraphRAG entity/relationship extraction</description></item>
    /// </list>
    /// </summary>
    public Dictionary<string, string> OperationOverrides { get; set; } = new();

    /// <summary>
    /// Gets or sets the available model tier definitions. Each tier maps
    /// to a specific Azure OpenAI deployment with rate limits.
    /// </summary>
    public ModelTierDefinition[] Tiers { get; set; } = [];
}
