namespace Application.Core.CQRS.Escalation;

/// <summary>
/// Shared validation constants for the escalation CQRS surface. Centralized so every command and
/// query agrees on what a well-formed approver name and decision reason look like — an identity
/// that can list pending escalations can always also submit a decision.
/// </summary>
public static class EscalationValidationRules
{
    /// <summary>
    /// Maximum accepted approver-name length. Approver names are token claims (UPNs, emails,
    /// service-principal names) compared against operator-authored rosters; 256 characters
    /// comfortably covers every realistic identity format while bounding log and audit lines.
    /// </summary>
    public const int MaxApproverNameLength = 256;

    /// <summary>
    /// Maximum accepted decision/cancellation reason length. Reasons are free-text audit
    /// context, not documents — 2000 characters keeps the JSONL audit records readable and
    /// prevents a single decision from ballooning the audit store.
    /// </summary>
    public const int MaxReasonLength = 2000;
}
