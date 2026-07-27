using Domain.Common.Config.AI.RAG;
using FluentValidation;

namespace Application.Core.Validation;

/// <summary>
/// Validates the cross-section coherence of <see cref="RagConfig"/> — specifically the
/// contract between the GraphRag feature knobs (<see cref="GraphRagConfig"/>) and the graph
/// database backend (<see cref="GraphDatabaseConfig"/>) they depend on. Rules:
/// <list type="bullet">
///   <item>When <see cref="GraphDatabaseConfig.Enabled"/> is <c>true</c>, the
///     <see cref="GraphDatabaseConfig.Provider"/> must be a backend the DI layer actually
///     registers (currently only <c>"kuzu"</c>). Before this rule, an enabled backend with an
///     unregistered provider composed cleanly and threw on the first
///     <c>IGraphDatabaseBackend</c> resolution — a first-use landmine instead of a startup
///     failure.</item>
///   <item>When <see cref="GraphRagConfig.IndexOnIngest"/> is <c>true</c>, the graph database
///     backend must be enabled: corpus-graph indexing writes through
///     <c>IGraphRagService</c>, which exists only alongside the backend. Failing at boot is
///     kinder than silently skipping the stage on every ingest.</item>
///   <item>When <see cref="ScopedCollectionsConfig.Enabled"/> is <c>true</c>, the retrieval
///     stack must actually honor collection names: the <c>"azure_ai_search"</c> store pairing
///     queries one pre-provisioned index and ignores the collection parameter, and the
///     agentic-retrieval backend always queries its one configured knowledge base. Either
///     combination would accept tenant-derived collection names and silently search the
///     shared index anyway — a cross-tenant leak. Failing at boot is the closed, predictable
///     alternative.</item>
///   <item>When <see cref="ScopedCollectionsConfig.Enabled"/> is <c>true</c>,
///     <see cref="GraphRagConfig.IndexOnIngest"/> must be off: the corpus graph is a single
///     shared graph with no collection concept, so indexing scoped ingests into it would
///     make every tenant's chunks readable through the GraphRag strategy and the Phase-D
///     <c>"graph"</c> source. The validator makes the unsafe combination unrepresentable.</item>
/// </list>
/// All class defaults satisfy every rule, so hosts that omit the section keep booting
/// unchanged.
/// </summary>
/// <remarks>
/// Auto-discovered via <c>AddValidatorsFromAssembly</c> on the Application.Core assembly —
/// no manual registration required. Wired into the startup options pipeline at
/// <c>RegisterValidatedConfigSections</c> with <c>ValidateOnStart</c>.
/// </remarks>
public sealed class RagConfigValidator : AbstractValidator<RagConfig>
{
    /// <summary>
    /// Backend provider keys registered by <c>Infrastructure.AI.RAG</c>'s
    /// <c>AddRagGraphDatabase</c>. Kept case-insensitive to match keyed-DI lookup behavior
    /// being driven by operator-typed configuration.
    /// </summary>
    private static readonly HashSet<string> RegisteredBackendProviders =
        new(StringComparer.OrdinalIgnoreCase) { "kuzu" };

    /// <summary>Initializes a new instance of the <see cref="RagConfigValidator"/> class.</summary>
    public RagConfigValidator()
    {
        When(x => x.GraphDatabase.Enabled, () =>
        {
            RuleFor(x => x.GraphDatabase.Provider)
                .Must(p => p is not null && RegisteredBackendProviders.Contains(p))
                .WithMessage(x =>
                    $"GraphDatabase.Provider '{x.GraphDatabase.Provider}' has no registered " +
                    $"IGraphDatabaseBackend. Registered providers: " +
                    $"{string.Join(", ", RegisteredBackendProviders)}. Either use a registered " +
                    "provider, disable AppConfig:AI:Rag:GraphDatabase, or register a backend " +
                    "for this key in Infrastructure.AI.RAG.");
        });

        When(x => x.GraphRag.IndexOnIngest, () =>
        {
            RuleFor(x => x.GraphDatabase.Enabled)
                .Equal(true)
                .WithMessage(
                    "GraphRag.IndexOnIngest requires the graph database backend: corpus-graph " +
                    "indexing writes through IGraphRagService, which is registered only when " +
                    "AppConfig:AI:Rag:GraphDatabase:Enabled is true. Enable the backend or turn " +
                    "off AppConfig:AI:Rag:GraphRag:IndexOnIngest.");
        });

        When(x => x.ScopedCollections.Enabled, () =>
        {
            RuleFor(x => x.VectorStore.Provider)
                .Must(p => !string.Equals(p, "azure_ai_search", StringComparison.OrdinalIgnoreCase))
                .WithMessage(
                    "ScopedCollections requires a collection-aware store pairing. The " +
                    "'azure_ai_search' stores query one pre-provisioned index and ignore " +
                    "collection names, so tenant-derived collections would silently search the " +
                    "shared index — a cross-tenant leak. Set AppConfig:AI:Rag:VectorStore:Provider " +
                    "to 'faiss' (FAISS + SQLite FTS5, both collection-aware), or provision " +
                    "per-tenant Azure indexes and register a collection-aware store before " +
                    "enabling AppConfig:AI:Rag:ScopedCollections.");

            RuleFor(x => x.AgenticRetrieval.Enabled)
                .Equal(false)
                .WithMessage(
                    "ScopedCollections cannot be combined with AgenticRetrieval: the Azure " +
                    "knowledge-base retriever always queries the one configured knowledge base " +
                    "and ignores collection names, so every tenant would search the same shared " +
                    "knowledge base. Disable AppConfig:AI:Rag:AgenticRetrieval or " +
                    "AppConfig:AI:Rag:ScopedCollections.");

            RuleFor(x => x.GraphRag.IndexOnIngest)
                .Equal(false)
                .WithMessage(
                    "ScopedCollections cannot be combined with GraphRag.IndexOnIngest: the corpus " +
                    "graph is a single shared graph with no collection concept, so scoped ingests " +
                    "would land every tenant's chunks in one graph readable via the GraphRag " +
                    "strategy and the multi-source 'graph' source. Disable " +
                    "AppConfig:AI:Rag:GraphRag:IndexOnIngest or AppConfig:AI:Rag:ScopedCollections.");
        });
    }
}
