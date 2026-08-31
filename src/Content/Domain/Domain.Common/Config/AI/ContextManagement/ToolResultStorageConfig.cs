namespace Domain.Common.Config.AI.ContextManagement;

/// <summary>
/// Configuration for disk persistence of large tool results that exceed in-context limits.
/// Bound from <c>AppConfig:AI:ContextManagement:ToolResultStorage</c> in appsettings.json.
/// </summary>
/// <remarks>
/// <para>
/// When a tool result exceeds <see cref="PerResultCharLimit"/>, the full result is persisted
/// to disk and a truncated preview of <see cref="PreviewSizeChars"/> characters is kept in the
/// conversation context with a reference pointer to the full result on disk.
/// </para>
/// </remarks>
public class ToolResultStorageConfig
{
    /// <summary>
    /// Gets or sets the character limit for a single tool result. Results exceeding this
    /// threshold are persisted to disk with only a preview kept in context.
    /// </summary>
    public int PerResultCharLimit { get; set; } = 50000;

    /// <summary>
    /// Gets or sets the aggregate character limit for all tool results within a single message.
    /// When the combined size of tool results in one message exceeds this limit, overflow results
    /// are persisted to disk.
    /// </summary>
    public int AggregatePerMessageCharLimit { get; set; } = 200000;

    /// <summary>
    /// Gets or sets the number of characters retained in the conversation context as a preview
    /// when a tool result is persisted to disk.
    /// </summary>
    public int PreviewSizeChars { get; set; } = 2000;

    /// <summary>
    /// Gets or sets the base directory path for storing persisted tool results.
    /// Relative paths are resolved from the working directory.
    /// </summary>
    /// <remarks>
    /// <strong>Windows operators: restrict this path's ACL yourselves.</strong> Since #563 the
    /// spilled copy under this path is the tool's raw, unredacted output, and
    /// <c>FileSystemToolResultStore</c> creates it owner-only on POSIX
    /// (<c>UnixFileMode.UserRead | UserWrite | UserExecute</c>) but falls back to whatever ACL the
    /// parent directory hands it on Windows — a security review flagged this as the one control of
    /// the three that #563 depends on (spill cap, permissions, retention sweep) that is not actually
    /// enforced on this project's primary platform. If this deployment's threat model includes other
    /// local accounts on the host, set an explicit restrictive ACL on this path (or its parent)
    /// through normal Windows administration; the harness does not do it for you here.
    /// </remarks>
    public string StoragePath { get; set; } = ".agent-sessions";

    /// <summary>
    /// Gets or sets the maximum number of characters of a tool's original output that may ever be
    /// spilled to disk for later retrieval via <c>tool_result_fetch</c> (#563). Anything beyond this
    /// is genuinely unrecoverable — the same "no silent caps" convention this file's other limits
    /// follow, made explicit here because this cap, unlike the others, bounds disk rather than the
    /// model's context window.
    /// </summary>
    /// <remarks>
    /// The spilled copy is the tool's raw output — not sanitized, not redacted — so that a page can
    /// be scanned and cleaned on demand, one bounded page at a time, instead of the whole result
    /// needing an unbounded scan before any of it could be stored (#563; the sanitizer has no
    /// per-pattern match timeout — #497 — so an unbounded scan is a real cost concern, not a
    /// theoretical one). Storing raw is a real step back from redacting at rest, which this store's
    /// spill path always did before #563; this cap is what keeps that acceptable — bounded disk
    /// exposure, owner-only directory permissions, and a retention sweep are the three controls that
    /// together make raw-at-rest a deliberate trade-off rather than an oversight. 5,000,000 (~5 MB,
    /// ~1.25M tokens) comfortably holds a full build log or a large file read while still bounding
    /// what one runaway tool call can put on disk before the retention sweep reclaims it.
    /// </remarks>
    public int MaxSpillChars { get; set; } = 5_000_000;
}
