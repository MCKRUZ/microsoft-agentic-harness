using Domain.Common.Config;
using Domain.Common.Config.Cache;
using Microsoft.Extensions.Logging;

namespace Presentation.FoundryHost;

/// <summary>
/// What FoundryHost tells the deployer at startup about erasing and exporting the personal data it holds (#1003).
/// </summary>
/// <remarks>
/// <para>
/// <strong>Why this host serves no erasure or export endpoint.</strong> Callers reach a hosted agent only through its
/// Foundry endpoint, whose documented routes are the protocols and the sessions API; none leads to an arbitrary container
/// path, so <c>/api/compliance/*</c> would not be reachable. Every protocol call also lands in one session's sandbox, and each session
/// has its own <c>$HOME</c>, where this host keeps all of its own state (<c>$HOME/agent-state</c>,
/// <c>$HOME/.checkpoints</c>). An erasure run inside the container could reach only that one session, while deleting the
/// session through Foundry's session API removes its <c>$HOME</c> with everything in it, and the session files API copies
/// it. That is the route the data-protection guide gives, and an in-container endpoint would be strictly weaker.
/// </para>
/// <para>
/// <strong>What session deletion does not reach.</strong> A store promoted to an external service is shared by every
/// session. This host sets no knowledge scope, so what it writes there carries no owner: neither session deletion nor a
/// per-person erasure can find one person's records. The notice names each such store the host was bound with, from the
/// bound configuration rather than the promotion variables, so a store a deployer set through <c>AppConfig__</c> keys is
/// named too.
/// </para>
/// </remarks>
internal static class FoundryHostErasure
{
    /// <summary>Where the data-protection guide explains erasure and export for this host.</summary>
    internal const string GuideAnchor = "documentation/security/08-data-protection.html#foundry-erasure";

    /// <summary>The startup notices: one always, and a warning for each store shared across sessions.</summary>
    /// <param name="config">The configuration the host bound.</param>
    /// <returns>The notices to log, each with its level.</returns>
    internal static IReadOnlyList<(LogLevel Level, string Message)> Describe(AppConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);

        var notices = new List<(LogLevel, string)>
        {
            (LogLevel.Information,
                "FoundryHost serves no erasure or export endpoint: callers reach this container only through Foundry's " +
                "protocol endpoints, and each call reaches one session's sandbox. This host keeps its own state in the session's " +
                "$HOME ($HOME/agent-state and $HOME/.checkpoints): deleting the Foundry session erases it, and the session " +
                $"files API copies it ({GuideAnchor})."),
        };

        foreach (var store in SharedStores(config))
        {
            notices.Add((LogLevel.Warning,
                $"{store} is shared by every session, and FoundryHost writes its records with no owner: deleting a " +
                $"person's sessions leaves what they put there, and it cannot be erased or copied for one person ({GuideAnchor})."));
        }

        return notices;
    }

    /// <summary>The stores the host was bound with that live outside the session, named by the setting that chose them.</summary>
    private static IEnumerable<string> SharedStores(AppConfig config)
    {
        var rag = config.AI.Rag;

        var graphProvider = rag.GraphRag.GraphProvider;
        if (graphProvider is not ("in_memory" or "managed_code"))
            yield return $"The knowledge graph store (AppConfig:AI:Rag:GraphRag:GraphProvider = {graphProvider})";

        if (rag.GraphDatabase.Enabled && !string.Equals(rag.GraphDatabase.Provider, "kuzu", StringComparison.OrdinalIgnoreCase))
            yield return $"The graph database (AppConfig:AI:Rag:GraphDatabase:Provider = {rag.GraphDatabase.Provider})";

        // Fails closed: only the in-process FAISS index, or an Azure index with no endpoint (which holds nothing), stays in
        // the session; any other provider, including one a consumer adds, is named.
        var vectorProvider = rag.VectorStore.Provider;
        var inSession = string.Equals(vectorProvider, "faiss", StringComparison.OrdinalIgnoreCase)
            || (string.Equals(vectorProvider, "azure_ai_search", StringComparison.OrdinalIgnoreCase) && !rag.VectorStore.IsConfigured);
        if (!inSession)
            yield return $"The document index (AppConfig:AI:Rag:VectorStore:Provider = {vectorProvider})";

        if (!string.IsNullOrWhiteSpace(config.Observability.PostgresConnectionString))
            yield return "The observability database (AppConfig:Observability:PostgresConnectionString)";

        if (config.Cache.CacheType == CacheType.RedisCache)
            yield return "The Redis cache (AppConfig:Cache:CacheType = RedisCache), whose entries go only when they expire,";
    }
}
