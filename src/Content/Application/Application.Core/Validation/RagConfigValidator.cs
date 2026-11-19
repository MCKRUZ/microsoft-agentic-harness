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
///   <item>The Azure AI Search document index and its endpoint come together (#1012): choosing
///     <c>"azure_ai_search"</c> as <see cref="VectorStoreConfig.Provider"/> requires
///     <see cref="VectorStoreConfig.Endpoint"/>, and an endpoint is refused with the in-process
///     <c>"faiss"</c> provider, which never reads it. Either half alone means the host searches,
///     ingests into and erases from a store other than the index that holds the chunks: with the
///     endpoint lost (a failed Key Vault reference, an environment override) the Azure stores point
///     at a placeholder; with the endpoint set but the provider left at the FAISS default, the
///     in-process stores are used. Either way an erasure would clear a store that holds nothing and
///     report the person erased while their chunks stay in the real index. Refusing to start is the
///     only outcome that cannot certify that. A case variant of the Azure key is refused with its own
///     message: the stores are resolved by the exact key, and the full-text store falls back to the
///     in-process one for any other spelling. A provider a consumer registers may read the endpoint
///     as it likes; a key nothing registered fails the startup smoke check
///     (<c>StartupRegistrationSmokeCheck</c>), which builds the document index.</item>
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

    /// <summary>
    /// The <see cref="VectorStoreConfig.Provider"/> key that selects the Azure AI Search stores, the only shipped provider
    /// that reads <see cref="VectorStoreConfig.Endpoint"/>.
    /// </summary>
    private const string AzureAISearchProvider = "azure_ai_search";

    /// <summary>The <see cref="VectorStoreConfig.Provider"/> key of the in-process stores, which read no endpoint.</summary>
    private const string InProcessProvider = "faiss";

    /// <summary>Initializes a new instance of the <see cref="RagConfigValidator"/> class.</summary>
    public RagConfigValidator()
    {
        // Compared ignoring case so a case variant gets a message naming the fix; the DI layer resolves the exact key only.
        // Any other key that is not registered fails the startup smoke check when the store is built.
        When(x => string.Equals(x.VectorStore.Provider, AzureAISearchProvider, StringComparison.OrdinalIgnoreCase), () =>
        {
            RuleFor(x => x.VectorStore.Provider)
                .Equal(AzureAISearchProvider, StringComparer.Ordinal)
                .WithMessage(x =>
                    $"AppConfig:AI:Rag:VectorStore:Provider is '{x.VectorStore.Provider}'; write it exactly " +
                    "'azure_ai_search'. The stores are resolved by the exact key: the vector store would not resolve, and " +
                    "the full-text store would fall back to the in-process one, which never holds the index's chunks.");

            RuleFor(x => x.VectorStore.Endpoint)
                .NotEmpty()
                .WithMessage(
                    "AppConfig:AI:Rag:VectorStore:Provider is 'azure_ai_search' but AppConfig:AI:Rag:VectorStore:Endpoint " +
                    "is blank. Without the endpoint the host cannot reach the index that holds the document chunks, so " +
                    "erasing a person would report them erased while their chunks stay in it. Set the endpoint (check that " +
                    "a Key Vault reference or environment override did not resolve it to blank), or set the provider to " +
                    "'faiss' if this host does not use an Azure AI Search index.");
        });

        When(x => !string.IsNullOrWhiteSpace(x.VectorStore.Endpoint), () =>
        {
            RuleFor(x => x.VectorStore.Provider)
                .Must(p => !string.Equals(p, InProcessProvider, StringComparison.OrdinalIgnoreCase))
                .WithMessage(x =>
                    $"AppConfig:AI:Rag:VectorStore:Endpoint is set but AppConfig:AI:Rag:VectorStore:Provider is " +
                    $"'{x.VectorStore.Provider}' ('faiss' is the default when it is not set), so the host would use its " +
                    "in-process stores and never the index at the endpoint: erasing a person would leave their chunks in " +
                    "it. Set the provider to 'azure_ai_search' to use the index, or clear the endpoint once that index no " +
                    "longer holds anyone's data.");
        });

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
            // COUPLING NOTE: this rule keys off VectorStore.Provider, which today also
            // selects the BM25 store (VectorStoreFactory derives the BM25 key from the
            // same value: "azure_ai_search" pairs both Azure stores, anything else pairs
            // FAISS + SQLite FTS5). If a separate BM25 provider setting is ever
            // introduced, it needs its own guard here or a collection-ignoring BM25
            // backend would silently escape this check.
            RuleFor(x => x.VectorStore.Provider)
                .Must(p => !string.Equals(p, AzureAISearchProvider, StringComparison.OrdinalIgnoreCase))
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
