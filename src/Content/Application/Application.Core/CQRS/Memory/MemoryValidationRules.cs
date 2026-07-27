namespace Application.Core.CQRS.Memory;

/// <summary>
/// Shared validation constants for the cross-session memory CQRS surface. Centralized so the
/// remember, recall, and forget validators agree on what a well-formed memory key looks like —
/// a key that passes the write validator is always addressable by the forget validator.
/// </summary>
public static class MemoryValidationRules
{
    /// <summary>
    /// Maximum accepted memory key length. Keys are human-meaningful identifiers
    /// (e.g. <c>"favorite-color"</c>), not content — 128 characters is generous.
    /// </summary>
    public const int MaxKeyLength = 128;

    /// <summary>
    /// Safe charset for a caller-supplied key that is embedded verbatim (trimmed + lowercased)
    /// into the scope-namespaced graph node id <c>memory:{tenant}:{user}:{key}</c>.
    /// <c>':'</c> is the namespace delimiter of that id and is deliberately excluded so a key can
    /// never visually mimic a scope segment in audit logs or id-parsing tooling; whitespace and
    /// control characters are excluded for the same id-hygiene reason. Must start with a letter
    /// or digit so ids stay unambiguous after the scope prefix.
    /// </summary>
    public const string KeyPattern = "^[A-Za-z0-9][A-Za-z0-9._-]*$";

    /// <summary>
    /// Maximum accepted fact content size in characters (32 KB). Large enough for any realistic
    /// remembered fact, small enough that a single request cannot balloon the graph store or the
    /// write gate's prompt-injection scan (which reads the full content on every write).
    /// </summary>
    public const int MaxContentLength = 32 * 1024;

    /// <summary>Maximum accepted entity-type length (e.g. <c>"Fact"</c>, <c>"Preference"</c>).</summary>
    public const int MaxEntityTypeLength = 64;

    /// <summary>
    /// Safe charset for entity-type names: a leading letter then letters/digits/underscore/hyphen.
    /// The entity type becomes the graph node's <c>Type</c> and participates in type-filtered
    /// queries, so it gets the same id-hygiene treatment as the key.
    /// </summary>
    public const string EntityTypePattern = "^[A-Za-z][A-Za-z0-9_-]*$";

    /// <summary>Maximum accepted recall query length.</summary>
    public const int MaxQueryLength = 1024;

    /// <summary>Upper bound for recall <c>MaxResults</c> — caps per-request graph traversal work.</summary>
    public const int MaxRecallResults = 50;
}
