using Application.AI.Common.Interfaces.RAG;
using Domain.Common.Config;

namespace Infrastructure.AI.Documents;

/// <summary>
/// Whether the document age limit may run (#901): never against stores other processes can write while the load record is
/// this process's own file.
/// </summary>
/// <remarks>
/// <para>
/// A removal deletes from every tenant and collection of the shared stores, but the shipped load record is a SQLite file
/// under one process's base directory. A second host (another replica, or AgentHub beside the Execution API) that loaded
/// the document yesterday wrote its own file, so this host's record still says the document is years old and would delete
/// it for everyone. The architecture guide already confines the built-in SQLite stores to single-instance deployments;
/// this makes that hold for the one that deletes.
/// </para>
/// <para>
/// The age limit may run when it is off, when a consumer registered its own (shared) <see cref="IDocumentLoadLedger"/>,
/// when every store is in-process (the FAISS index with the in-memory graph), or when the deployment declares that every
/// writer records in the same file (<c>LoadLedgerSharedByEveryWriter</c>).
/// </para>
/// </remarks>
internal static class DocumentRetentionSafety
{
    /// <summary>Why the age limit may not run with this configuration and record, or <see langword="null"/> when it may.</summary>
    /// <param name="config">The live configuration.</param>
    /// <param name="ledger">The load record the host resolved.</param>
    /// <returns>The refusal, or <see langword="null"/>.</returns>
    public static string? Refusal(AppConfig config, IDocumentLoadLedger ledger)
    {
        var documents = config.AI.Rag.Documents;
        if (!documents.Retention.Enabled || ledger is not EfCoreDocumentLoadLedger || documents.LoadLedgerSharedByEveryWriter)
            return null;

        return InProcessOnly(config.AI.Rag)
            ? null
            : "The document age limit (AppConfig:AI:Rag:Documents:Retention) is on, the configured document stores can be "
              + "written by other processes, and the load record is this process's own SQLite file, so a document another "
              + "host loaded recently could be deleted for everyone. Register a shared IDocumentLoadLedger, or point every "
              + "host at the same file and set AppConfig:AI:Rag:Documents:LoadLedgerSharedByEveryWriter.";
    }

    /// <summary>
    /// Whether every document store lives in this process or in a file only it opens: the FAISS index with its in-memory
    /// full-text table, the in-memory knowledge graph (<c>managed_code</c> is the same store), and the embedded Kuzu corpus
    /// graph when the graph database is on. Any other provider may be shared.
    /// </summary>
    private static bool InProcessOnly(Domain.Common.Config.AI.RAG.RagConfig rag) =>
        rag.VectorStore.Provider == "faiss"
        && rag.GraphRag.GraphProvider is "in_memory" or "managed_code"
        && (!rag.GraphDatabase.Enabled || rag.GraphDatabase.Provider == "kuzu");
}
