namespace Domain.Common.Config.AI.RAG;

/// <summary>
/// Configuration for GraphRAG graph-based retrieval using community
/// summaries and entity relationships.
/// Bound from <c>AppConfig:AI:Rag:GraphRag</c> in appsettings.json.
/// </summary>
public class GraphRagConfig
{
    /// <summary>
    /// Gets or sets whether GraphRAG is enabled. When <c>false</c>,
    /// graph-based retrieval is skipped entirely.
    /// </summary>
    public bool Enabled { get; set; } = false;

    /// <summary>
    /// Gets or sets the graph storage provider.
    /// Options: <c>"managed_code"</c> (in-memory), <c>"neo4j"</c>, <c>"cosmos_gremlin"</c>.
    /// </summary>
    public string GraphProvider { get; set; } = "managed_code";

    /// <summary>
    /// Gets or sets the connection string for the graph database.
    /// Only required when <see cref="GraphProvider"/> is not <c>"managed_code"</c>.
    /// Should be stored in User Secrets (dev) or Azure Key Vault (prod).
    /// </summary>
    public string? ConnectionString { get; set; }

    /// <summary>
    /// Gets or sets the community detection level used for retrieval.
    /// Level 0 is the most granular (individual entities); higher levels
    /// represent progressively broader community summaries.
    /// </summary>
    public int CommunityLevel { get; set; } = 2;

    /// <summary>
    /// Gets or sets the maximum token budget for entity extraction
    /// prompts during graph construction.
    /// </summary>
    public int MaxEntityExtractionTokens { get; set; } = 4096;

    /// <summary>
    /// Gets or sets whether feedback-weighted search is enabled. When <c>true</c>,
    /// retrieval results are re-ranked by blending semantic relevance with accumulated
    /// feedback weights on graph nodes and edges.
    /// </summary>
    public bool FeedbackEnabled { get; set; } = false;

    /// <summary>
    /// Gets or sets the learning rate for feedback weight updates using exponential
    /// moving average: <c>newWeight = alpha * feedbackScore + (1 - alpha) * oldWeight</c>.
    /// Higher values make the system more responsive to recent feedback but less stable.
    /// </summary>
    /// <value>Default: 0.3. Valid range: 0.0 (ignore feedback) to 1.0 (use only latest feedback).</value>
    public double FeedbackAlpha { get; set; } = 0.3;

    /// <summary>
    /// Gets or sets whether entity-level provenance stamping is enabled.
    /// When <c>true</c>, every extracted node and edge is stamped with source pipeline,
    /// task, timestamp, and extraction confidence metadata.
    /// </summary>
    public bool ProvenanceEnabled { get; set; } = true;

    /// <summary>
    /// Gets or sets whether multi-tenant knowledge isolation is enabled.
    /// When <c>true</c>, all graph operations are scoped by tenant and dataset,
    /// preventing cross-tenant data access.
    /// </summary>
    public bool MultiTenantIsolation { get; set; } = false;

    /// <summary>
    /// Gets or sets the default tenant ID for single-tenant deployments.
    /// Used when <see cref="MultiTenantIsolation"/> is <c>true</c> but no tenant
    /// is specified in the request context.
    /// </summary>
    public string? DefaultTenantId { get; set; }

    /// <summary>
    /// Gets or sets the default dataset ID for operations that don't specify a dataset.
    /// </summary>
    public string? DefaultDatasetId { get; set; }

    // --- Compliance Configuration ---

    /// <summary>
    /// Gets or sets whether the compliance layer is enabled. When <c>true</c>,
    /// <c>ComplianceAwareGraphStore</c> decorator stamps temporal metadata,
    /// filters expired nodes, and emits audit events.
    /// </summary>
    public bool ComplianceEnabled { get; set; } = true;

    /// <summary>
    /// Gets or sets the audit sink provider key for keyed DI resolution.
    /// Options: <c>"no_op"</c>, <c>"structured_logging"</c>.
    /// </summary>
    public string AuditSinkProvider { get; set; } = "structured_logging";

    /// <summary>
    /// Gets or sets the interval for the retention enforcement background service.
    /// Default: 24 hours. Set to <see cref="TimeSpan.Zero"/> to disable.
    /// </summary>
    public TimeSpan RetentionEnforcementInterval { get; set; } = TimeSpan.FromHours(24);

    /// <summary>
    /// Gets or sets retention policies per entity type. Key is entity type name,
    /// value is the retention duration. Entity types not listed get indefinite retention.
    /// </summary>
    public Dictionary<string, TimeSpan> RetentionPolicies { get; set; } = new()
    {
        ["Fact"] = TimeSpan.FromDays(365),
        ["SkillMetric"] = TimeSpan.FromDays(180),
        ["SkillAmendment"] = TimeSpan.FromDays(365),
        ["Concept"] = TimeSpan.FromDays(730),
    };

    // --- Procedural Memory Configuration ---

    /// <summary>
    /// Gets or sets whether skill effectiveness tracking is enabled.
    /// When <c>true</c>, agents record which skills succeed for which query types.
    /// </summary>
    public bool SkillEffectivenessEnabled { get; set; } = true;

    /// <summary>
    /// Gets or sets whether skill instruction amendments are enabled.
    /// When <c>true</c>, agents can persist learned notes that append to skill instructions.
    /// </summary>
    public bool SkillAmendmentsEnabled { get; set; } = true;
}
