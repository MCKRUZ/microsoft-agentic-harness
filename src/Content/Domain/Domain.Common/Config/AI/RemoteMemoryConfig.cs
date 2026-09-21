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
/// <para>
/// <b>Known limitation — read-side trust is not independently verified.</b> Every write this
/// harness sends to the remote service is scanned locally by the same <c>IMemoryWriteGate</c> the
/// local backend uses, before it ever leaves this process — so content this harness itself writes
/// is protected exactly as it is locally. However, when this harness later recalls a fact, the
/// remote service's response carries no signal saying whether that fact passed a safety check, so
/// this harness cannot independently re-verify content already sitting in the remote store (for
/// example, written through some other channel into the same remote memory pool). Enabling this
/// section is therefore only safe when the remote service is a dedicated instance this harness
/// deployment trusts as its sole writer — never a memory pool shared with untrusted writers this
/// harness cannot vouch for. Recall isolation between different callers of this same harness
/// deployment has the same limitation: see <see cref="AvatarId"/>.
/// </para>
/// </remarks>
public sealed class RemoteMemoryConfig
{
    /// <summary>
    /// Master toggle. When <see langword="true"/>, this harness's <c>IConversationFactExtractor</c>,
    /// <c>IKnowledgeMemory</c>, <c>IMemoryAbstractor</c>, <c>IMemoryConsolidator</c>,
    /// <c>IMemoryDecayService</c>, and <c>ICrossSessionMemoryStore</c> seams are all answered by
    /// the external service at <see cref="BaseUrl"/> instead of this harness's own local stores.
    /// See the class remarks for the read-side trust limitation this currently carries.
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
    /// <remarks>
    /// Every remember/recall request also carries the authenticated caller's user and tenant id, so
    /// a remote service that partitions its store by caller can honor per-caller isolation once its
    /// own contract enforces it. The current avatar contract does not filter recall results by
    /// caller identity, so two different users of the same harness deployment can currently see
    /// each other's remote-stored facts through a content search, even though they cannot overwrite
    /// each other's keys. Until the remote contract enforces per-caller filtering, treat one
    /// <see cref="AvatarId"/> as a single shared trust boundary — provision a separate harness
    /// deployment (and <see cref="AvatarId"/>) per isolation boundary you actually need.
    /// </remarks>
    public string AvatarId { get; set; } = string.Empty;

    /// <summary>
    /// Hard timeout in seconds for a single remote-memory HTTP call. Matches
    /// <see cref="KnowledgeBridgeConfig.ExtractionTimeoutSeconds"/>'s default so a remote backend
    /// is held to the same latency budget as the local extraction pipeline it replaces.
    /// </summary>
    public int TimeoutSeconds { get; set; } = 10;

    /// <summary>
    /// Required to be <see langword="true"/> when <see cref="Enabled"/> is <see langword="true"/>
    /// (enforced by <c>RemoteMemoryConfigValidator</c>) — an explicit, conscious acknowledgement
    /// that every caller sharing this <see cref="AvatarId"/> shares one recall pool with no
    /// per-caller filtering (see <see cref="AvatarId"/>'s remarks). Defaults to
    /// <see langword="false"/> so an operator cannot enable remote memory without deliberately
    /// setting this too — the isolation gap is a config value that fails startup, not a paragraph
    /// of documentation an operator can skip past.
    /// </summary>
    public bool AcknowledgeSharedRecallBoundary { get; set; }
}
