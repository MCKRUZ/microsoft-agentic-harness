namespace Application.Common.Interfaces.MediatR;

/// <summary>
/// Marker interface for commands that should generate an audit trail entry.
/// Consumed by <c>AuditTrailBehavior</c> to record who/what/when/outcome.
/// </summary>
public interface IAuditable
{
    /// <summary>Gets the audit action name (e.g., "FileWrite", "ToolExecution", "McpCall").</summary>
    string AuditAction { get; }

    /// <summary>Gets optional redacted metadata for the audit entry. Null if none.</summary>
    IReadOnlyDictionary<string, string>? AuditMetadata => null;
}
