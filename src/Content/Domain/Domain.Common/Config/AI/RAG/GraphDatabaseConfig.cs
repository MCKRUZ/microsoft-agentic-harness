namespace Domain.Common.Config.AI.RAG;

/// <summary>
/// Configuration for the graph database backend. Bound from AppConfig:AI:Rag:GraphDatabase.
/// </summary>
public sealed class GraphDatabaseConfig
{
    /// <summary>
    /// Graph database provider. Selects keyed DI implementation.
    /// The template ships a backend registration for "kuzu" (default) only; other values
    /// (e.g. "neo4j") are consumer extension points that require registering a matching
    /// keyed <c>IGraphDatabaseBackend</c>. When <see cref="Enabled"/> is true, an
    /// unregistered provider fails the host at startup via <c>RagConfigValidator</c>.
    /// </summary>
    public string Provider { get; set; } = "kuzu";

    /// <summary>
    /// Connection string for external graph databases.
    /// Not required for "kuzu" embedded provider.
    /// </summary>
    public string? ConnectionString { get; set; }

    /// <summary>
    /// Local directory for embedded Kuzu database files.
    /// Only used when Provider is "kuzu".
    /// </summary>
    public string DataDirectory { get; set; } = "./data/graph";

    /// <summary>
    /// Whether the graph database backend is enabled.
    /// When false, falls back to in-memory IKnowledgeGraphStore.
    /// </summary>
    public bool Enabled { get; set; } = true;
}
