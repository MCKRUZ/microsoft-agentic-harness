namespace Domain.Common.Config.AI;

/// <summary>
/// Configuration for delegating this harness's knowledge-memory seams to an external,
/// HTTP-reachable memory-hosting service instead of the harness's own local Neo4j/SQLite/Kuzu
/// stack. Bound to <c>AppConfig:AI:RemoteMemory</c>.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Distinct from <see cref="KnowledgeBridgeConfig"/>.</strong> That section is the master
/// switch for this harness's own <em>local</em> extraction-and-recall pipeline (fact extraction
/// via <c>ConversationFactExtractor</c>, recall via <c>KnowledgeMemoryContextProvider</c>). This
/// section instead decides <em>which backend</em> answers those same seams — local or remote —
/// and is orthogonal to whether the pipeline runs at all.
/// </para>
/// <para>
/// Off by default so a cloned template never silently phones home to a URL nobody configured.
/// </para>
/// </remarks>
public sealed class RemoteMemoryConfig
{
    /// <summary>
    /// Master toggle. When <see langword="true"/>, this harness's <c>IConversationFactExtractor</c>,
    /// <c>IKnowledgeMemory</c>, <c>IMemoryAbstractor</c>, <c>IMemoryConsolidator</c>,
    /// <c>IMemoryDecayService</c>, and <c>ICrossSessionMemoryStore</c> seams are all answered by
    /// the external service at <see cref="BaseUrl"/> instead of this harness's own local stores.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Base address of the remote memory-hosting service (e.g. <c>https://my-avatar.example.com</c>).
    /// Required when <see cref="Enabled"/> is <see langword="true"/>.
    /// </summary>
    public string BaseUrl { get; set; } = string.Empty;

    /// <summary>
    /// API key sent on every request as the <c>X-Api-Key</c> header. Like every other secret in
    /// this harness, the value here should come from user-secrets/environment configuration, never
    /// a committed appsettings file.
    /// </summary>
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>
    /// Identifies which avatar/persona instance this harness deployment serves — sent as a path
    /// segment on every request. One harness deployment serves exactly one remote memory owner, so
    /// this is deployment configuration, not per-request state.
    /// </summary>
    public string AvatarId { get; set; } = string.Empty;

    /// <summary>
    /// Hard timeout in seconds for a single remote-memory HTTP call. Matches
    /// <see cref="KnowledgeBridgeConfig.ExtractionTimeoutSeconds"/>'s default so a remote backend
    /// is held to the same latency budget as the local extraction pipeline it replaces.
    /// </summary>
    public int TimeoutSeconds { get; set; } = 10;
}
