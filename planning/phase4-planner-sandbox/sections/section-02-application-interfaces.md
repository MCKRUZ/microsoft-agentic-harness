# Section 02: Application Interfaces

## Overview

This section defines all application-layer interfaces for the Planner, Sandbox, and Attestation subsystems. These are pure contracts with no implementations -- they live in `Application.AI.Common/Interfaces/` and are consumed by Infrastructure (implementations) and Presentation (notification implementations). No new NuGet packages are required.

**Depends on:** Section 01 (Domain Models) -- all interfaces reference domain types from `Domain.AI/Planner/`, `Domain.AI/Sandbox/`, and `Domain.AI/Attestation/`.

**Blocks:** Sections 05, 06, 07, 08, 09, 10, 11, 12, 13, 14 -- nearly every downstream section implements or consumes these interfaces.

---

## Tests

No tests for interfaces themselves -- tested through implementations.

---

## File Inventory

All files under `src/Content/Application/Application.AI.Common/Interfaces/`:

| File | Subfolder | Purpose |
|------|-----------|---------|
| `IPlanExecutor.cs` | `Planner/` | Core DAG execution engine |
| `IPlanStepExecutor.cs` | `Planner/` | Per-step-type executor (keyed DI) |
| `IPlanValidator.cs` | `Planner/` | Pre-execution graph validation |
| `IPlanStateStore.cs` | `Planner/` | Plan persistence and checkpoint/resume |
| `IPlanGenerator.cs` | `Planner/` | LLM-driven plan generation |
| `IPlanProgressNotifier.cs` | `Planner/` | Notification dispatch (AG-UI bridge) |
| `ISandboxExecutor.cs` | `Sandbox/` | Tool execution in isolated environment |
| `IProcessResourceLimiter.cs` | `Sandbox/` | OS-level resource limits |
| `ICapabilityEnforcer.cs` | `Sandbox/` | Capability-based permission checks |
| `IAttestationService.cs` | `Attestation/` | HMAC signing and verification |
| `IAttestationStore.cs` | `Attestation/` | Attestation persistence |

Total: 11 interface files across 3 subdirectories.

---

## Interface Specifications

### IPlanExecutor

```csharp
public interface IPlanExecutor
{
    Task<Result<PlanExecutionSummary>> ExecuteAsync(PlanId planId, CancellationToken ct);
    Task<Result> CancelAsync(PlanId planId, CancellationToken ct);
    Task<Result> RetryStepAsync(PlanId planId, PlanStepId stepId, CancellationToken ct);
}
```

Handles both fresh starts and checkpoint resume. Uses `Result<T>` for expected failures.

### IPlanStepExecutor

```csharp
public interface IPlanStepExecutor
{
    Task<StepExecutionResult> ExecuteAsync(
        PlanStep step,
        IReadOnlyDictionary<PlanStepId, string> upstreamOutputs,
        CancellationToken ct);
}
```

Keyed DI on `StepType`. Five implementations. `upstreamOutputs` provides data flow. `HumanGateStepExecutor` returns `Blocked` immediately.

### IPlanValidator

```csharp
public interface IPlanValidator
{
    Task<Result<PlanValidationResult>> ValidateAsync(PlanGraph plan, CancellationToken ct);
}
```

Returns `PlanValidationResult` with errors, warnings, and estimated critical path duration. Called by both `CreatePlanCommand` and `ExecutePlanCommand`.

### IPlanStateStore

```csharp
public interface IPlanStateStore
{
    Task<Result> SavePlanAsync(PlanGraph plan, CancellationToken ct);
    Task<Result<PlanGraph?>> LoadPlanAsync(PlanId planId, CancellationToken ct);
    Task<Result> UpdateStepStateAsync(StepExecutionState state, CancellationToken ct);
    Task<Result> CheckpointAsync(PlanId planId, IReadOnlyList<StepExecutionState> states, CancellationToken ct);
    Task<Result<IReadOnlyDictionary<PlanStepId, StepExecutionState>>> ResumeAsync(PlanId planId, CancellationToken ct);
    Task<Result<IReadOnlyList<PlanExecutionLogEntry>>> GetExecutionHistoryAsync(PlanId planId, CancellationToken ct);
    Task<Result<IReadOnlyList<PlanGraph>>> ListPlansAsync(
        StepExecutionStatus? statusFilter = null,
        DateTimeOffset? from = null,
        DateTimeOffset? to = null,
        CancellationToken ct = default);
}
```

`ResumeAsync` returns dictionary for ready-queue rebuild. Optimistic concurrency handled by implementation.

### IPlanGenerator

```csharp
public interface IPlanGenerator
{
    Task<Result<PlanGraph>> GenerateAsync(
        string taskDescription,
        PlanGenerationConstraints? constraints = null,
        CancellationToken ct = default);
}
```

LLM generates structured JSON validated before return.

### IPlanProgressNotifier

```csharp
public interface IPlanProgressNotifier
{
    Task NotifyPlanStartedAsync(PlanId planId, string planName, PlanGraph graph, CancellationToken ct);
    Task NotifyStepStartedAsync(PlanId planId, PlanStepId stepId, string stepName, StepType type, CancellationToken ct);
    Task NotifyStepCompletedAsync(PlanId planId, PlanStepId stepId, StepExecutionStatus status, TimeSpan duration, string? outputSummary, CancellationToken ct);
    Task NotifyStateUpdateAsync(PlanId planId, PlanStepId stepId, StepExecutionStatus previousStatus, StepExecutionStatus newStatus, CancellationToken ct);
    Task NotifySandboxStatusAsync(PlanId planId, PlanStepId stepId, string toolName, SandboxIsolationLevel isolationLevel, ResourceUsage usage, string? attestationHash, CancellationToken ct);
    Task NotifyPlanCompletedAsync(PlanId planId, TimeSpan totalDuration, CancellationToken ct);
    Task NotifyPlanFailedAsync(PlanId planId, PlanStepId failedStepId, string errorMessage, CancellationToken ct);
}
```

Follows existing `IDriftNotifier`/`IEscalationNotifier` pattern. Fire-and-forget (returns `Task`, not `Result<T>`).

### ISandboxExecutor

```csharp
public interface ISandboxExecutor
{
    Task<SandboxExecutionResult> ExecuteAsync(
        SandboxExecutionRequest request,
        CancellationToken cancellationToken);
}
```

Two keyed registrations: `SandboxIsolationLevel.Process` and `SandboxIsolationLevel.Container`.

### IProcessResourceLimiter

```csharp
public interface IProcessResourceLimiter : IDisposable
{
    bool Apply(System.Diagnostics.Process process, ResourceLimits limits);
    ResourceUsage? GetUsage();
    bool IsSupported { get; }
}
```

Extends `IDisposable` for Job Object handle cleanup. `IsSupported` enables platform check without try/catch.

### ICapabilityEnforcer

```csharp
public interface ICapabilityEnforcer
{
    Task<ToolPermissionProfile> ResolveProfileAsync(string toolName, CancellationToken ct);
    Task<Result> EnforceAsync(
        string toolName,
        ToolCapability grantedCapabilities,
        IReadOnlyList<string>? requestedPaths = null,
        IReadOnlyList<string>? requestedHosts = null,
        CancellationToken ct = default);
}
```

`ResolveProfileAsync` separate from `EnforceAsync` for inspection without enforcement.

### IAttestationService

```csharp
public interface IAttestationService
{
    Task<ToolExecutionAttestation> SignAsync(string toolName, string input, string output, CancellationToken ct);
    Task<ToolExecutionAttestation> SignFailureAsync(string toolName, string input, string failureReason, CancellationToken ct);
    Task<bool> VerifyAsync(ToolExecutionAttestation attestation, CancellationToken ct);
}
```

`SignFailureAsync` separate from `SignAsync` -- structurally different payloads. Signing key from User Secrets/Key Vault only, never `appsettings.json`.

### IAttestationStore

```csharp
public interface IAttestationStore
{
    Task<Result> SaveAsync(PlanStepId stepId, ToolExecutionAttestation attestation, CancellationToken ct);
    Task<Result<ToolExecutionAttestation?>> GetByStepAsync(PlanStepId stepId, CancellationToken ct);
    Task<Result<IReadOnlyList<ToolExecutionAttestation>>> GetByPlanAsync(PlanId planId, CancellationToken ct);
}
```

---

## Domain Type Dependencies

| Domain Type | Namespace |
|---|---|
| `PlanGraph`, `PlanStep`, `PlanEdge`, `PlanId`, `PlanStepId` | `Domain.AI.Planner` |
| `StepType`, `StepConfiguration`, `StepExecutionStatus`, `StepExecutionState` | `Domain.AI.Planner` |
| `ToolCapability`, `ToolPermissionProfile`, `SandboxIsolationLevel` | `Domain.AI.Sandbox` |
| `SandboxExecutionRequest`, `SandboxExecutionResult`, `ResourceLimits`, `ResourceUsage` | `Domain.AI.Sandbox` |
| `ToolExecutionAttestation` | `Domain.AI.Attestation` |
| `Result`, `Result<T>` | `Domain.Common` |

## No Project File Changes Required

`Application.AI.Common.csproj` already references `Domain.AI.csproj` and `Application.Common.csproj`.

---

## Implementation Deviations

1. **8 supporting domain types created alongside interfaces**: The following types were referenced by interface signatures but not defined in section-01:
   - `PlanExecutionSummary`, `StepExecutionResult`, `PlanValidationResult`, `PlanExecutionLogEntry`, `PlanGenerationConstraints` → `Domain.AI.Planner`
   - `ResourceUsage`, `SandboxExecutionRequest`, `SandboxExecutionResult` → `Domain.AI.Sandbox`
   These were created as sealed records in their respective domain namespaces to allow compilation.

2. **CancellationToken parameter naming**: Spec showed `CancellationToken ct` throughout. Implementation matches — this is consistent with codebase convention (concise parameter names).

3. **Code review result**: APPROVE with no fixes needed. Two informational warnings noted (StepExecutionStatus reuse at plan level, supporting types not in original spec).
