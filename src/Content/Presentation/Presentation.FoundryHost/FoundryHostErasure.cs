using Domain.Common.Config;
using Domain.Common.Config.Cache;
using Microsoft.Extensions.Logging;

namespace Presentation.FoundryHost;

/// <summary>
/// What FoundryHost tells the deployer at startup about erasing and exporting the personal data it holds (#1003, #1008).
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
/// session. Each request runs as the caller Foundry verified (<see cref="FoundryCallerIdentity"/>), so the graph stores
/// and the document index record that caller as owner, and an erasure or export sent to another host bound to the same
/// store, naming that id, reaches them. The observability database and the Redis cache key nothing by person, so the
/// owner does not reach them, and they are named in a warning. Each store is named from the bound configuration rather
/// than the promotion variables, so a store a deployer set through <c>AppConfig__</c> keys is named too.
/// </para>
/// </remarks>
internal static class FoundryHostErasure
{
    /// <summary>Where the data-protection guide explains erasure and export for this host.</summary>
    internal const string GuideAnchor = "documentation/security/08-data-protection.html#foundry-erasure";

    /// <summary>
    /// The startup notices: one always, one for each shared store that records its owner, and a warning for each shared
    /// store that does not.
    /// </summary>
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

        foreach (var store in OwnedSharedStores(config))
        {
            notices.Add((LogLevel.Information,
                $"{store} is shared by every session, so deleting a person's sessions leaves what they put there. FoundryHost " +
                "records the caller Foundry verified (x-agent-user-id) as its owner: an erasure or export naming that id, " +
                "sent to a host bound to the same store, reaches it. Records written by a request without that header, by " +
                $"background work, or derived from ingested documents carry no owner ({GuideAnchor})."));
        }

        foreach (var (store, why) in UnownedSharedStores(config))
        {
            notices.Add((LogLevel.Warning,
                $"{store} is shared by every session, and its records carry no owner ({why}): deleting a person's sessions " +
                $"leaves what they put there, and it cannot be erased or copied for one person ({GuideAnchor})."));
        }

        return notices;
    }

    /// <summary>
    /// The stores outside the session that stamp the request's knowledge-scope owner on what they write, named by the
    /// setting that chose them.
    /// </summary>
    private static IEnumerable<string> OwnedSharedStores(AppConfig config)
    {
        var rag = config.AI.Rag;

        var graphProvider = rag.GraphRag.GraphProvider;
        if (graphProvider is not ("in_memory" or "managed_code"))
            yield return $"The knowledge graph store (AppConfig:AI:Rag:GraphRag:GraphProvider = {graphProvider})";

        // Compared as the keyed registrations resolve them, ordinally, so the notice describes the store that is built.
        if (rag.GraphDatabase.Enabled && rag.GraphDatabase.Provider != "kuzu")
            yield return $"The graph database (AppConfig:AI:Rag:GraphDatabase:Provider = {rag.GraphDatabase.Provider})";

        // Fails closed: only the in-process FAISS index, or an Azure index with no endpoint (which holds nothing), stays in
        // the session; any other provider, including one a consumer adds, is named.
        var vectorProvider = rag.VectorStore.Provider;
        var inSession = vectorProvider == "faiss" || (vectorProvider == "azure_ai_search" && !rag.VectorStore.IsConfigured);
        if (!inSession)
            yield return $"The document index (AppConfig:AI:Rag:VectorStore:Provider = {vectorProvider})";
    }

    /// <summary>The stores outside the session that key nothing by person, each with the reason the owner does not reach it.</summary>
    private static IEnumerable<(string Store, string Why)> UnownedSharedStores(AppConfig config)
    {
        if (!string.IsNullOrWhiteSpace(config.Observability.PostgresConnectionString))
            yield return ("The observability database (AppConfig:Observability:PostgresConnectionString)",
                "its rows name only a conversation, and the only record of whose conversation it was stays in the session");

        if (config.Cache.CacheType == CacheType.RedisCache)
            yield return ("The Redis cache (AppConfig:Cache:CacheType = RedisCache)",
                "its entries are keyed by a hash of the model request and go only when they expire");
    }
}
