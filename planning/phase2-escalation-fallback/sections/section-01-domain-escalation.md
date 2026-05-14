# Section 01: Domain Escalation Models

## Overview

This section creates the foundational domain types for the human escalation subsystem. All types live in `Domain.AI/Escalation/` and are pure value objects (records and enums) with no infrastructure dependencies. Every other escalation-related section depends on these types.

**Layer:** `Domain.AI`
**Namespace:** `Domain.AI.Escalation`
**Dependencies:** None (this is the root of the escalation dependency chain)
**Blocks:** section-03 (OTel conventions), section-05 (approval strategies), section-06 (escalation interfaces), section-08 (escalation service)

---

## Background

The harness already has governance types in `Domain.AI/Governance/` (e.g., `GovernanceDecision`, `AutonomyExceededResult`, `GovernancePolicyAction.RequireApproval`). When an agent attempts an action beyond its authority, the governance pipeline currently only allows or denies. Phase 2 adds a third path: **escalation** -- the structured representation of "this agent tried something beyond its authority, and a human must decide."

Two trigger paths converge into escalation:
1. `GovernanceDecision` with `Action == RequireApproval` (tool-level governance)
2. `AutonomyExceededResult` from supervisor delegation (autonomy tier violation)

Both produce an `EscalationRequest` that flows through approval strategies, notification channels, and audit stores defined in later sections.

---

## Files to Create

All files go under `src/Content/Domain/Domain.AI/Escalation/`:

| File | Type | Description |
|------|------|-------------|
| `EscalationPriority.cs` | enum | Urgency level of an escalation |
| `EscalationWaitBehavior.cs` | enum | Whether the agent blocks or continues while waiting |
| `EscalationTimeoutAction.cs` | enum | What happens when an escalation times out |
| `EscalationResolutionType.cs` | enum | How an escalation was ultimately resolved |
| `ApprovalStrategyType.cs` | enum | Which multi-approver strategy to use |
| `EscalationAuditRecordType.cs` | enum | Discriminator for audit log entries |
| `RiskLevel.cs` | enum | Risk level of an agent action (Low/Medium/High/Critical) |
| `ApproverDecision.cs` | record | A single approver's response |
| `ApprovalEvaluation.cs` | record | Result of evaluating collected decisions against a strategy |
| `EscalationRequest.cs` | record | The full escalation request with all context |
| `EscalationOutcome.cs` | record | The resolved result of an escalation |
| `EscalationAuditRecord.cs` | record | A single audit log entry |

Test file: `src/Content/Tests/Domain.AI.Tests/Escalation/EscalationDomainModelTests.cs`

---

## Tests First

Create `src/Content/Tests/Domain.AI.Tests/Escalation/EscalationDomainModelTests.cs`.

The test class validates factory methods, computed properties, and correct defaults on the domain records. Follow the project's existing test style (xUnit, `Assert.*`, no FluentAssertions on domain tests based on existing patterns in `GovernanceDecisionTests.cs`).

```csharp
namespace Domain.AI.Tests.Escalation;

/// <summary>
/// Tests for escalation domain records and their factory methods/computed properties.
/// Pure record types with no factory methods don't need tests --
/// only test types that have behavioral factory methods or non-trivial defaults.
/// </summary>
public sealed class EscalationDomainModelTests
{
    // --- EscalationRequest ---

    // Test: EscalationRequest_WithDefaults_SetsExpectedValues
    //   Construct an EscalationRequest with only required properties.
    //   Verify: EscalationId is non-empty Guid, RequestedAt is populated,
    //   QuorumThreshold defaults to 0, TimeoutSeconds defaults to 300,
    //   ApprovalStrategy defaults to AnyOf, OriginatingDecision is null.

    // Test: EscalationRequest_WithAllProperties_RoundTrips
    //   Construct with all properties set including OriginatingDecision.
    //   Verify all values round-trip through the record.

    // --- EscalationOutcome ---

    // Test: EscalationOutcome_Approved_IsApprovedTrue
    //   Construct with ResolutionType = Approved, IsApproved = true.
    //   Verify IsApproved is true and ResolutionType matches.

    // Test: EscalationOutcome_Denied_IsApprovedFalse
    //   Construct with ResolutionType = Denied, IsApproved = false.
    //   Verify IsApproved is false.

    // Test: EscalationOutcome_TimedOut_HasCorrectResolutionType
    //   Construct with ResolutionType = TimedOut.
    //   Verify ResolutionType is TimedOut, IsApproved is false, EscalatedToTier is null.

    // Test: EscalationOutcome_Escalated_HasEscalatedToTier
    //   Construct with ResolutionType = Escalated, EscalatedToTier set.
    //   Verify EscalatedToTier is populated.

    // --- EscalationAuditRecord ---

    // Test: EscalationAuditRecord_RequestType_SerializesCorrectly
    //   Construct with RecordType = Request and a serialized EscalationRequest as Payload.
    //   Verify RecordType and that Payload is non-null.

    // Test: EscalationAuditRecord_DecisionType_HasCorrectDiscriminator
    //   Construct with RecordType = Decision.
    //   Verify RecordType == EscalationAuditRecordType.Decision.

    // --- ApprovalEvaluation ---

    // Test: ApprovalEvaluation_Resolved_HasEmptyPendingApprovers
    //   Construct with IsResolved = true.
    //   PendingApprovers should be empty when fully resolved.

    // Test: ApprovalEvaluation_NotResolved_HasPendingApprovers
    //   Construct with IsResolved = false and a list of pending approver names.
    //   Verify PendingApprovers contains the expected names.

    // --- ApproverDecision ---

    // Test: ApproverDecision_Approved_HasCorrectProperties
    //   Construct with Approved = true, verify all properties.

    // Test: ApproverDecision_Denied_WithReason_HasReason
    //   Construct with Approved = false, Reason = "Too risky".
    //   Verify Reason is populated.
}
```

---

## Implementation Details

### Enums

Each enum gets its own file. Follow the XML doc style used by `GovernancePolicyAction` and `AutonomyLevel` in the existing codebase.

**`EscalationPriority.cs`** -- Urgency level that drives timeout configuration and notification behavior. Numeric ordering enables `>=` comparisons.

```csharp
namespace Domain.AI.Escalation;

/// <summary>
/// Urgency level of an escalation request. Higher values indicate greater urgency.
/// Maps to <c>EscalationPriorityConfig</c> for per-priority timeout and notification settings.
/// </summary>
public enum EscalationPriority
{
    /// <summary>Non-blocking notification. Agent may continue other work.</summary>
    Informational = 0,
    /// <summary>Agent is blocked until the escalation resolves.</summary>
    Blocking = 1,
    /// <summary>Highest urgency. All approvers notified simultaneously regardless of strategy.</summary>
    Critical = 2
}
```

**`EscalationWaitBehavior.cs`** -- Configured per autonomy tier. Controls whether the agent pauses or continues other work while waiting for approval.

```csharp
namespace Domain.AI.Escalation;

/// <summary>
/// Controls agent behavior while an escalation is pending.
/// Configured per autonomy tier in <c>PermissionsConfig</c>.
/// </summary>
public enum EscalationWaitBehavior
{
    /// <summary>Agent pauses and awaits the escalation outcome before continuing.</summary>
    Block,
    /// <summary>Agent continues processing other work; escalation resolves asynchronously.</summary>
    QueueAndContinue
}
```

**`EscalationTimeoutAction.cs`** -- What happens when no approvers respond within the timeout window.

```csharp
namespace Domain.AI.Escalation;

/// <summary>
/// Action taken when an escalation request expires without sufficient approver responses.
/// </summary>
public enum EscalationTimeoutAction
{
    /// <summary>Deny the action on timeout.</summary>
    Deny,
    /// <summary>Deny the action and escalate to a higher authority tier.</summary>
    DenyAndEscalate,
    /// <summary>Auto-approve the action on timeout (use with caution).</summary>
    Approve,
    /// <summary>Escalate to a higher authority tier without denying.</summary>
    Escalate
}
```

**`EscalationResolutionType.cs`** -- How the escalation was ultimately resolved. Used in `EscalationOutcome` and OTel metrics tagging.

```csharp
namespace Domain.AI.Escalation;

/// <summary>
/// How an escalation was ultimately resolved. Used for audit records and OTel metric tags.
/// </summary>
public enum EscalationResolutionType
{
    /// <summary>Approved by sufficient approvers per the strategy.</summary>
    Approved,
    /// <summary>Denied by an approver or by strategy rules.</summary>
    Denied,
    /// <summary>No sufficient response within the timeout window.</summary>
    TimedOut,
    /// <summary>Forwarded to a higher authority tier.</summary>
    Escalated
}
```

**`ApprovalStrategyType.cs`** -- Determines which multi-approver logic evaluates collected decisions. Used as keyed DI key for `IApprovalStrategy` resolution (section-05).

```csharp
namespace Domain.AI.Escalation;

/// <summary>
/// Strategy for evaluating multiple approver decisions.
/// Used as the keyed DI discriminator for <c>IApprovalStrategy</c> resolution.
/// </summary>
public enum ApprovalStrategyType
{
    /// <summary>First approver response wins. Fastest resolution.</summary>
    AnyOf,
    /// <summary>All designated approvers must approve. A single denial immediately denies.</summary>
    AllOf,
    /// <summary>N-of-M approvers must agree. Requires <c>QuorumThreshold</c> on the request.</summary>
    Quorum
}
```

**`EscalationAuditRecordType.cs`** -- Discriminator for polymorphic audit log deserialization.

```csharp
namespace Domain.AI.Escalation;

/// <summary>
/// Discriminator for <see cref="EscalationAuditRecord"/> entries.
/// Determines how the <c>Payload</c> field should be deserialized.
/// </summary>
public enum EscalationAuditRecordType
{
    /// <summary>An escalation was requested.</summary>
    Request,
    /// <summary>An approver submitted a decision.</summary>
    Decision,
    /// <summary>The escalation was resolved (approved, denied, timed out, or escalated).</summary>
    Outcome
}
```

### Records

**`ApproverDecision.cs`** -- A single approver's response to an escalation. Immutable value object.

```csharp
namespace Domain.AI.Escalation;

/// <summary>
/// A single approver's response to an escalation request.
/// Collected by the escalation service and evaluated by the approval strategy.
/// </summary>
public sealed record ApproverDecision
{
    /// <summary>Identifier of the approver (user name, role, or service principal).</summary>
    public required string ApproverName { get; init; }

    /// <summary>Whether the approver granted approval.</summary>
    public required bool Approved { get; init; }

    /// <summary>Optional reason for the decision. Especially useful for denials.</summary>
    public string? Reason { get; init; }

    /// <summary>When the approver responded.</summary>
    public required DateTimeOffset RespondedAt { get; init; }
}
```

**`ApprovalEvaluation.cs`** -- Returned by `IApprovalStrategy.EvaluateDecision()`. Tells the escalation service whether the collected decisions are sufficient to resolve.

```csharp
namespace Domain.AI.Escalation;

/// <summary>
/// Result of evaluating collected approver decisions against an approval strategy.
/// Returned by <c>IApprovalStrategy.EvaluateDecision()</c>.
/// </summary>
public sealed record ApprovalEvaluation
{
    /// <summary>Whether enough decisions have been collected to resolve the escalation.</summary>
    public required bool IsResolved { get; init; }

    /// <summary>The approval verdict. Only meaningful when <see cref="IsResolved"/> is true.</summary>
    public required bool IsApproved { get; init; }

    /// <summary>Approvers who have not yet responded. Empty when fully resolved.</summary>
    public required IReadOnlyList<string> PendingApprovers { get; init; }
}
```

**`EscalationRequest.cs`** -- The central domain type. Represents a structured request for human approval. Built from either a `GovernanceDecision` (RequireApproval) or an `AutonomyExceededResult` (tier violation).

```csharp
using Domain.AI.Governance;

namespace Domain.AI.Escalation;

/// <summary>
/// A structured request for human approval of an agent action that exceeds its authority.
/// Built from a <see cref="GovernanceDecision"/> with <c>RequireApproval</c> action,
/// or from an <see cref="AutonomyExceededResult"/> during delegation.
/// </summary>
public sealed record EscalationRequest
{
    /// <summary>Unique identifier for this escalation.</summary>
    public required Guid EscalationId { get; init; }

    /// <summary>The agent that attempted the action.</summary>
    public required string AgentId { get; init; }

    /// <summary>The tool or operation the agent tried to invoke.</summary>
    public required string ToolName { get; init; }

    /// <summary>Arguments passed to the tool (sanitized for audit display).</summary>
    public required IReadOnlyDictionary<string, string> Arguments { get; init; }

    /// <summary>Human-readable summary of the attempted action.</summary>
    public required string Description { get; init; }

    /// <summary>Risk level derived from the matched governance rule.</summary>
    public required RiskLevel RiskLevel { get; init; }

    /// <summary>Urgency of this escalation, drives timeout and notification behavior.</summary>
    public required EscalationPriority Priority { get; init; }

    /// <summary>Strategy for evaluating multiple approver decisions.</summary>
    public ApprovalStrategyType ApprovalStrategy { get; init; } = ApprovalStrategyType.AnyOf;

    /// <summary>Ordered list of approver identifiers.</summary>
    public required IReadOnlyList<string> Approvers { get; init; }

    /// <summary>For Quorum strategy, the N in N-of-M required approvals.</summary>
    public int QuorumThreshold { get; init; }

    /// <summary>Seconds before this escalation expires.</summary>
    public int TimeoutSeconds { get; init; } = 300;

    /// <summary>Action to take when the escalation times out.</summary>
    public EscalationTimeoutAction TimeoutAction { get; init; } = EscalationTimeoutAction.DenyAndEscalate;

    /// <summary>When the escalation was created.</summary>
    public required DateTimeOffset RequestedAt { get; init; }

    /// <summary>
    /// The governance decision that triggered this escalation. Null when triggered
    /// by an <see cref="AutonomyExceededResult"/> from the supervisor.
    /// </summary>
    public GovernanceDecision? OriginatingDecision { get; init; }
}
```

**`EscalationOutcome.cs`** -- The resolved result of an escalation. Created by the escalation service when approval decisions or timeout resolve the request.

```csharp
using Domain.AI.Governance;

namespace Domain.AI.Escalation;

/// <summary>
/// The resolved result of an escalation request. Created when sufficient approver
/// decisions have been collected, the request times out, or it is escalated.
/// </summary>
public sealed record EscalationOutcome
{
    /// <summary>Correlates back to the originating <see cref="EscalationRequest"/>.</summary>
    public required Guid EscalationId { get; init; }

    /// <summary>Final approval verdict.</summary>
    public required bool IsApproved { get; init; }

    /// <summary>Individual approver decisions collected during the escalation.</summary>
    public required IReadOnlyList<ApproverDecision> Decisions { get; init; }

    /// <summary>How the escalation was resolved.</summary>
    public required EscalationResolutionType ResolutionType { get; init; }

    /// <summary>When the escalation was resolved.</summary>
    public required DateTimeOffset ResolvedAt { get; init; }

    /// <summary>
    /// If resolution was <see cref="EscalationResolutionType.Escalated"/>,
    /// which authority tier received the escalated request. Null otherwise.
    /// </summary>
    public AutonomyLevel? EscalatedToTier { get; init; }
}
```

**`EscalationAuditRecord.cs`** -- A single audit log entry. Used by `IEscalationAuditStore` (section-09) for JSONL persistence. The `Payload` field is the serialized request, decision, or outcome as a string, discriminated by `RecordType`.

```csharp
namespace Domain.AI.Escalation;

/// <summary>
/// A single audit log entry for an escalation lifecycle event.
/// Used by <c>IEscalationAuditStore</c> for append-only JSONL persistence.
/// The <see cref="Payload"/> field contains the serialized event data,
/// discriminated by <see cref="RecordType"/>.
/// </summary>
public sealed record EscalationAuditRecord
{
    /// <summary>Discriminator for deserialization of <see cref="Payload"/>.</summary>
    public required EscalationAuditRecordType RecordType { get; init; }

    /// <summary>Correlates to the originating escalation.</summary>
    public required Guid EscalationId { get; init; }

    /// <summary>When this audit record was created.</summary>
    public required DateTimeOffset Timestamp { get; init; }

    /// <summary>
    /// Serialized JSON of the request, decision, or outcome depending on <see cref="RecordType"/>.
    /// </summary>
    public required string Payload { get; init; }
}
```

---

## Conventions and Patterns

These records follow the same patterns as existing domain types in the codebase:

- **Namespace:** `Domain.AI.Escalation` (parallel to `Domain.AI.Governance`, `Domain.AI.Orchestration`)
- **Record style:** `sealed record` with `required` init-only properties (matches `AutonomyExceededResult`, `DelegationResult`)
- **XML docs:** Full documentation on all public types and members (template teaching material)
- **No framework dependencies:** Domain records reference only other domain types and BCL types
- **Immutable collections:** `IReadOnlyList<T>`, `IReadOnlyDictionary<K,V>` for all collection properties
- **Enum XML docs:** Each enum value documented (matches `GovernancePolicyAction`, `AutonomyLevel`)
- **One type per file:** Consistent with project conventions
- **`EscalatedToTier`** uses the existing `AutonomyLevel` enum from `Domain.AI.Governance` -- this is the only cross-reference to another domain namespace

---

## Relationship to Existing Types

| Existing Type | Relationship |
|---------------|-------------|
| `GovernanceDecision` | Its `Approvers` list feeds `EscalationRequest.Approvers`. Stored as `EscalationRequest.OriginatingDecision` for correlation. |
| `GovernancePolicyAction.RequireApproval` | The trigger enum value -- when governance evaluates to this action, an `EscalationRequest` is built (section-17). |
| `AutonomyExceededResult` | Provides `AttemptedAction`, `CurrentLevel`, `RequiredLevel`, `Reason` that map to `EscalationRequest` fields (section-17). |
| `AutonomyLevel` | Used as `EscalationOutcome.EscalatedToTier` type. Also used by `AutonomyExceededResult` for tier comparison. |
| `AutonomyTierPolicy` | Will be extended with `EscalationWaitBehavior` mapping (section-04). |

---

## Verification

After creating all files, run:

```
dotnet build src/Content/Domain/Domain.AI/Domain.AI.csproj
dotnet test src/Content/Tests/Domain.AI.Tests/Domain.AI.Tests.csproj --filter "FullyQualifiedName~Domain.AI.Tests.Escalation"
```

The build should succeed with zero warnings. All tests should pass. No new NuGet dependencies are needed -- these are pure domain types using only BCL types and existing domain references.
