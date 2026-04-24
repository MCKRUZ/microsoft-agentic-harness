namespace Domain.AI.KnowledgeGraph.Models;

/// <summary>
/// An entity node in the knowledge graph, extracted from document content via LLM-based
/// entity recognition. Nodes represent named concepts (people, organizations, technologies,
/// topics) and carry references back to the source <see cref="ChunkIds"/> for provenance.
/// </summary>
/// <remarks>
/// <para>
/// Nodes are created during the <c>IndexCorpusAsync</c> pipeline when the LLM extracts
/// entities from document chunks. Each node may appear in multiple chunks, so
/// <see cref="ChunkIds"/> is a list. Duplicate entities (same name, same type) are merged
/// by the graph store, combining their chunk references.
/// </para>
/// <para>
/// The <see cref="Properties"/> dictionary carries domain-specific metadata that varies
/// by entity type (e.g., a "Person" node might have a "role" property). Properties are
/// stored as strings to remain serialization-friendly across all graph backends.
/// </para>
/// </remarks>
public record GraphNode
{
    /// <summary>
    /// Unique identifier for this node. Typically a deterministic hash of
    /// <see cref="Name"/> + <see cref="Type"/> to ensure idempotent upserts.
    /// </summary>
    public required string Id { get; init; }

    /// <summary>
    /// The canonical name of the entity (e.g., "Microsoft", "Azure OpenAI", "OAuth 2.0").
    /// Used for entity resolution and deduplication during graph construction.
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// The entity type category (e.g., "Organization", "Technology", "Person", "Concept").
    /// Used for ontology validation and type-filtered graph queries.
    /// </summary>
    public required string Type { get; init; }

    /// <summary>
    /// Domain-specific metadata properties for this entity. Keys and values are strings
    /// to ensure portability across all graph backends (PostgreSQL JSONB, Neo4j properties,
    /// in-memory dictionaries).
    /// </summary>
    public IReadOnlyDictionary<string, string> Properties { get; init; } =
        new Dictionary<string, string>();

    /// <summary>
    /// The IDs of document chunks from which this entity was extracted. A node may span
    /// multiple chunks when the same entity appears across different documents or sections.
    /// </summary>
    public IReadOnlyList<string> ChunkIds { get; init; } = [];

    /// <summary>
    /// Audit trail metadata tracking how this entity was extracted — which pipeline,
    /// task, document, and with what confidence. Null when provenance stamping is disabled
    /// via <c>GraphRagConfig.ProvenanceEnabled = false</c>.
    /// </summary>
    public ProvenanceStamp? Provenance { get; init; }
}
