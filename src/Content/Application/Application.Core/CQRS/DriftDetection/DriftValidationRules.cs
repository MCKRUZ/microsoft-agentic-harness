namespace Application.Core.CQRS.DriftDetection;

/// <summary>
/// Shared validation constants for the drift-monitoring CQRS surface. Centralized so every
/// command and query agrees on what a well-formed scope identifier, caller identity, and
/// query window look like — and so the caps that keep a hostile caller from flooding the EWMA
/// pipeline or the audit reader live in exactly one place.
/// </summary>
public static class DriftValidationRules
{
    /// <summary>
    /// Maximum accepted scope-identifier length. Scope identifiers are agent ids, skill names,
    /// or task-type names — short operational labels, not documents. Bounding them keeps graph
    /// node ids, deterministic EWMA keys, and audit lines readable.
    /// </summary>
    public const int MaxScopeIdentifierLength = 200;

    /// <summary>
    /// Maximum accepted caller-identity length. Caller ids are token claims (object ids, UPNs,
    /// service-principal names) stamped onto audit records; 256 characters covers every
    /// realistic identity format while bounding audit lines. Matches the escalation surface's
    /// approver-name cap.
    /// </summary>
    public const int MaxCallerIdLength = 256;

    /// <summary>
    /// Maximum number of dimension entries accepted in a single evaluation push. The
    /// <c>DriftDimension</c> enum currently defines six dimensions; the cap leaves headroom for
    /// consumer-extended enums while rejecting degenerate payloads outright.
    /// </summary>
    public const int MaxDimensionsPerEvaluation = 16;

    /// <summary>
    /// Maximum drift-history query window in days. History reads enumerate every persisted
    /// evaluation for the scope; bounding the window bounds the response. The baseline
    /// recalculation window (<c>DriftDetectionConfig.BaselineWindowDays</c>, default 7) fits
    /// comfortably inside it.
    /// </summary>
    public const int MaxHistoryWindowDays = 90;

    /// <summary>
    /// Hard cap on audit records returned by a single audits query. The JSONL audit store is
    /// append-only and unbounded over time; the cap keeps a single read from streaming the
    /// whole trail over the wire.
    /// </summary>
    public const int MaxAuditResults = 1000;

    /// <summary>
    /// Default number of audit records returned when the caller does not specify a limit.
    /// </summary>
    public const int DefaultAuditResults = 500;
}
