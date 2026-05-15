# Section 01: Domain Models

## Overview

This section defines all domain types for the Planner and Code Sandbox subsystems. These are pure domain models with no framework dependencies beyond `System.Text.Json` (for polymorphic serialization attributes). All types live in `Domain.AI` under three new folders: `Planner/`, `Sandbox/`, and `Attestation/`.

This is **Batch 1** in the implementation order -- the foundation with no dependencies. Sections 02 through 09 all depend on these types.

---

## Tests First

All test files go in the existing test project at:
`src/Content/Tests/Domain.AI.Tests/`

The project already has xUnit, FluentAssertions, and Moq. No new package references needed for these tests.

### Test File: `src/Content/Tests/Domain.AI.Tests/Planner/PlanGraphTests.cs`

```csharp
// Namespace: Domain.AI.Tests.Planner

// Test: PlanId_NewId_GeneratesUniqueGuids
//   Arrange: call PlanId.New() twice
//   Assert: both have non-empty Guid values, and they differ

// Test: PlanId_Equality_SameGuidAreEqual
//   Arrange: create two PlanId with identical Guid
//   Assert: they are equal (record structural equality)

// Test: PlanGraph_Steps_IsImmutableList
//   Arrange: create PlanGraph with Steps set to an array
//   Assert: Steps is IReadOnlyList<PlanStep>, cannot be cast to List<PlanStep>

// Test: PlanStep_RequiredFields_CannotBeNull
//   Arrange: attempt to create PlanStep with null Name
//   Assert: ArgumentNullException or compiler warning enforcement (init required props)

// Test: PlanConfiguration_MaxSubPlanDepth_DefaultsFive
//   Arrange: create PlanConfiguration with no overrides
//   Assert: MaxSubPlanDepth == 5, PlanTimeout == 30 minutes, MaxParallelSteps == 10

// Test: EdgeType_ConditionalTrue_HasDistinctValue
//   Arrange: enumerate EdgeType values
//   Assert: ConditionalTrue and ConditionalFalse are distinct, all four values exist

// Test: StepExecutionStatus_AllStates_CoverExpectedTransitions
//   Arrange: enumerate StepExecutionStatus values
//   Assert: exactly 7 values (Pending, Ready, Running, Completed, Failed, Skipped, Blocked)
```

### Test File: `src/Content/Tests/Domain.AI.Tests/Planner/StepConfigurationTests.cs`

```csharp
// Namespace: Domain.AI.Tests.Planner

// Test: StepConfiguration_JsonPolymorphic_RoundTripsAllFiveSubtypes
//   Arrange: create one of each subtype (LlmCallConfig, ToolUseConfig, HumanGateConfig,
//            ConditionalBranchConfig, SubPlanConfig)
//   Act: serialize each to JSON as StepConfiguration, then deserialize back
//   Assert: deserialized type matches original, all properties preserved

// Test: StepConfiguration_Discriminator_PreservesTypeInfoThroughSerialization
//   Arrange: serialize LlmCallConfig as StepConfiguration
//   Act: inspect raw JSON string
//   Assert: contains "type":"llm_call" discriminator property
```

### Test File: `src/Content/Tests/Domain.AI.Tests/Planner/RetryPolicyTests.cs`

```csharp
// Namespace: Domain.AI.Tests.Planner

// Test: RetryPolicy_Defaults_ThreeRetriesExponentialBackoff
//   Arrange: create RetryPolicy with no overrides
//   Assert: MaxRetries == 3, Strategy == Exponential, InitialDelay == 1s,
//           OnExhausted == FailStep
```

### Test File: `src/Content/Tests/Domain.AI.Tests/Sandbox/ToolCapabilityTests.cs`

```csharp
// Namespace: Domain.AI.Tests.Sandbox

// Test: ToolCapability_Flags_CanCombineMultiple
//   Arrange: combine FileRead | NetworkAccess
//   Assert: HasFlag(FileRead) == true, HasFlag(NetworkAccess) == true,
//           HasFlag(FileWrite) == false

// Test: ToolCapability_BitwiseAnd_DetectsMissingCapabilities
//   Arrange: required = FileRead | FileWrite | NetworkAccess,
//            granted = FileRead | FileWrite
//   Act: missing = required & ~granted
//   Assert: missing == NetworkAccess

// Test: ToolPermissionProfile_DeniedPaths_OverrideAllowedPaths
//   Arrange: profile with AllowedPaths = ["/workspace"] and DeniedPaths = ["/workspace/secret"]
//   Assert: profile.DeniedPaths contains "/workspace/secret",
//           profile.AllowedPaths contains "/workspace"
//   Note: actual deny-overrides-allow enforcement is in section-06 (CapabilityEnforcer).
//         This test just validates the data model supports both collections.

// Test: SandboxIsolationLevel_Ordering_ContainerHigherThanProcess
//   Assert: (int)Container > (int)Process > (int)None

// Test: ToolCapabilityAttribute_OnClass_DeclaresCapabilitiesAndMinIsolation
//   Arrange: define a test class decorated with [ToolCapability(FileRead | FileWrite, MinimumIsolation = Container)]
//   Act: reflect to read attribute
//   Assert: Capabilities == FileRead | FileWrite, MinimumIsolation == Container
```

### Test File: `src/Content/Tests/Domain.AI.Tests/Attestation/ToolExecutionAttestationTests.cs`

```csharp
// Namespace: Domain.AI.Tests.Attestation

// Test: ToolExecutionAttestation_FailureAttestation_HasNullOutputHash
//   Arrange: create attestation with IsFailureAttestation = true, OutputHash = null
//   Assert: OutputHash is null, IsFailureAttestation is true,
//           FailureReason is populated

// Test: ToolExecutionAttestation_SuccessAttestation_HasBothHashes
//   Arrange: create attestation with both InputHash and OutputHash
//   Assert: neither is null, IsFailureAttestation is false

// Test: ToolExecutionAttestation_KeyVersion_IsRequired
//   Arrange: create attestation with KeyVersion set
//   Assert: KeyVersion is non-null and non-empty
```

---

## Implementation

### Strongly-Typed IDs

Create two value-object ID types. These follow the same pattern as other value types in the codebase (e.g., record-based, `Guid`-wrapping).

**File: `src/Content/Domain/Domain.AI/Planner/PlanId.cs`**

```csharp
/// <summary>
/// Strongly-typed identifier for a <see cref="PlanGraph"/>.
/// Wraps a <see cref="Guid"/> to prevent primitive obsession and accidental
/// misuse of raw GUIDs across different entity boundaries.
/// </summary>
public readonly record struct PlanId(Guid Value)
{
    /// <summary>Generates a new unique PlanId.</summary>
    public static PlanId New() => new(Guid.NewGuid());
}
```

**File: `src/Content/Domain/Domain.AI/Planner/PlanStepId.cs`**

Same pattern as `PlanId` but for individual plan steps.

```csharp
/// <summary>
/// Strongly-typed identifier for a <see cref="PlanStep"/>.
/// </summary>
public readonly record struct PlanStepId(Guid Value)
{
    public static PlanStepId New() => new(Guid.NewGuid());
}
```

### Planner Domain Models

All files go under `src/Content/Domain/Domain.AI/Planner/`.

**File: `Planner/StepType.cs`**

Enum with 5 members determining which keyed `IPlanStepExecutor` handles the step:

- `LlmCall` -- delegates to `RunConversationCommand`
- `ToolUse` -- routes through sandbox
- `HumanGate` -- non-blocking escalation
- `ConditionalBranch` -- evaluates condition expression, activates true/false edge
- `SubPlanInvocation` -- child plan in isolated scope

**File: `Planner/EdgeType.cs`**

Enum with 4 members:

- `DataFlow` -- output of From feeds as input to To
- `ControlFlow` -- From must complete before To starts
- `ConditionalTrue` -- follow if condition evaluates true
- `ConditionalFalse` -- follow if condition evaluates false

**File: `Planner/StepExecutionStatus.cs`**

Enum with 7 members representing the step state machine:

- `Pending` (not yet eligible)
- `Ready` (dependencies met, awaiting scheduler)
- `Running` (currently executing)
- `Completed` (finished successfully)
- `Failed` (finished with error, may retry)
- `Skipped` (skipped due to upstream failure or conditional branch)
- `Blocked` (waiting on escalation/human gate)

**File: `Planner/BackoffStrategy.cs`**

Enum: `Fixed`, `Linear`, `Exponential`

**File: `Planner/ErrorRecovery.cs`**

Enum: `FailStep`, `SkipStep`, `FailPlan`, `Escalate`

**File: `Planner/RetryPolicy.cs`**

Immutable record with defaults:

- `MaxRetries` -- default 3
- `InitialDelay` -- default 1 second
- `Strategy` -- default `Exponential`
- `OnExhausted` -- default `FailStep`

**File: `Planner/PlanConfiguration.cs`**

Immutable record with defaults:

- `PlanTimeout` -- default 30 minutes
- `MaxParallelSteps` -- default 10
- `MaxSubPlanDepth` -- default 5 (prevents unbounded recursion)

**File: `Planner/PlanEdge.cs`**

Immutable record with required properties:

- `From: PlanStepId` (required)
- `To: PlanStepId` (required)
- `Type: EdgeType` (required)
- `Condition: string?` (nullable, for conditional edges)

**File: `Planner/StepConfiguration.cs`**

Abstract record base with `System.Text.Json` polymorphic serialization attributes. This is the key polymorphic type -- EF Core persists it as a JSON column using the `type` discriminator.

```csharp
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(LlmCallConfig), "llm_call")]
[JsonDerivedType(typeof(ToolUseConfig), "tool_use")]
[JsonDerivedType(typeof(HumanGateConfig), "human_gate")]
[JsonDerivedType(typeof(ConditionalBranchConfig), "conditional_branch")]
[JsonDerivedType(typeof(SubPlanConfig), "sub_plan")]
public abstract record StepConfiguration;
```

**File: `Planner/LlmCallConfig.cs`**

Record extending `StepConfiguration`:

- `SystemPrompt: string` -- prompt text for the LLM call
- `ModelDeploymentKey: string` -- references an AI deployment from config
- `Temperature: double` -- default 0.7
- `MaxTokens: int` -- default 4096

**File: `Planner/ToolUseConfig.cs`**

Record extending `StepConfiguration`:

- `ToolName: string` -- the keyed DI tool name (e.g., `"file_system"`)
- `InputParameters: IReadOnlyDictionary<string, object?>` -- input map
- `IsolationLevelOverride: SandboxIsolationLevel?` -- optional override (nullable)

Note: `SandboxIsolationLevel` is defined in the Sandbox folder (below). This creates a cross-folder reference within the same `Domain.AI` project, which is fine.

**File: `Planner/HumanGateConfig.cs`**

Record extending `StepConfiguration`:

- `EscalationMessage: string` -- what to show the human approver
- `ApprovalStrategy: string` -- one of `"AnyOf"`, `"AllOf"`, `"Quorum"`
- `Timeout: TimeSpan` -- how long to wait before timing out the gate (default 1 hour)

**File: `Planner/ConditionalBranchConfig.cs`**

Record extending `StepConfiguration`:

- `ConditionExpression: string` -- expression string evaluated using the `DecisionRule` pattern from `Domain.Common/Workflow/DecisionRule.cs`. Only JSON path comparisons, boolean operators, and null checks permitted.
- `TrueEdgeTargetId: PlanStepId` -- step to follow when condition is true
- `FalseEdgeTargetId: PlanStepId` -- step to follow when condition is false

**File: `Planner/SubPlanConfig.cs`**

Record extending `StepConfiguration`:

- `ChildPlanId: PlanId?` -- reference to an existing plan (nullable if inline)
- `InlinePlanDefinition: PlanGraph?` -- inline plan definition (nullable if referencing existing)
- `IsolateContext: bool` -- default true; whether the child gets isolated context

Exactly one of `ChildPlanId` or `InlinePlanDefinition` must be set. Validation enforced in section-03.

**File: `Planner/PlanStep.cs`**

Immutable record:

- `Id: PlanStepId` (required)
- `Name: string` (required)
- `Type: StepType` (required)
- `Configuration: StepConfiguration` (required)
- `RetryPolicy: RetryPolicy` (required)
- `Timeout: TimeSpan` -- default 60 seconds
- `RequiredAutonomyLevel: AutonomyLevel?` -- nullable, references existing `Domain.AI.Governance.AutonomyLevel` enum

**File: `Planner/PlanGraph.cs`**

The central domain model -- a directed acyclic graph of plan steps:

- `Id: PlanId` (required)
- `Name: string` (required)
- `Steps: IReadOnlyList<PlanStep>` (required, immutable)
- `Edges: IReadOnlyList<PlanEdge>` (required, immutable)
- `Configuration: PlanConfiguration` (required)
- `ParentPlanId: PlanId?` -- nullable, for sub-plan invocation linkage

**File: `Planner/StepExecutionState.cs`**

Per-step execution tracking:

- `StepId: PlanStepId` (required)
- `Status: StepExecutionStatus` (required)
- `AttemptCount: int` -- default 0
- `StartedAt: DateTimeOffset?` -- nullable
- `CompletedAt: DateTimeOffset?` -- nullable
- `Output: string?` -- nullable
- `ErrorMessage: string?` -- nullable
- `Attestation: ToolExecutionAttestation?` -- nullable for non-tool steps and crashed executions

### Sandbox Domain Models

All files go under `src/Content/Domain/Domain.AI/Sandbox/`.

**File: `Sandbox/ToolCapability.cs`**

Flags enum with 8 capabilities:

```csharp
[Flags]
public enum ToolCapability
{
    None           = 0,
    FileRead       = 1 << 0,
    FileWrite      = 1 << 1,
    NetworkAccess  = 1 << 2,
    Subprocess     = 1 << 3,
    EnvRead        = 1 << 4,
    DatabaseRead   = 1 << 5,
    DatabaseWrite  = 1 << 6,
    LlmInvocation  = 1 << 7
}
```

**File: `Sandbox/SandboxIsolationLevel.cs`**

Enum with 3 levels. The numeric ordering matters -- higher values represent stricter isolation. Code in section-06 (capability model) relies on `(int)Container > (int)Process > (int)None` for never-downgrade checks.

```csharp
public enum SandboxIsolationLevel
{
    None = 0,       // Direct execution (existing behavior for safe, read-only tools)
    Process = 1,    // Subprocess with Job Object resource limits (default)
    Container = 2   // Docker container with full isolation (elevated)
}
```

**File: `Sandbox/ToolPermissionProfile.cs`**

Immutable record representing a tool's capability requirements and access scoping:

- `RequiredCapabilities: ToolCapability` (required)
- `AllowedPaths: IReadOnlyList<string>` -- default empty
- `AllowedHosts: IReadOnlyList<string>` -- default empty
- `AllowedPrograms: IReadOnlyList<string>` -- default empty
- `DeniedPaths: IReadOnlyList<string>` -- default empty
- `DeniedHosts: IReadOnlyList<string>` -- default empty
- `MinimumIsolation: SandboxIsolationLevel` -- default `Process`

Deny-overrides-allow semantics: if a path appears in both `AllowedPaths` and `DeniedPaths`, the deny wins. Enforcement is in section-06 (capability model), but the data model must support both lists.

**File: `Sandbox/ToolCapabilityAttribute.cs`**

Custom attribute for compile-time tool capability declarations. Applied to tool classes. Runtime overridable via appsettings (section-16).

```csharp
[AttributeUsage(AttributeTargets.Class)]
public class ToolCapabilityAttribute : Attribute
{
    public ToolCapability Capabilities { get; }
    public SandboxIsolationLevel MinimumIsolation { get; init; } = SandboxIsolationLevel.Process;

    public ToolCapabilityAttribute(ToolCapability capabilities)
    {
        Capabilities = capabilities;
    }
}
```

**File: `Sandbox/ResourceLimits.cs`**

Immutable record with sensible defaults:

- `MemoryLimitBytes: long` -- default 256 MB (256 * 1024 * 1024)
- `CpuTimeSeconds: double` -- default 30
- `MaxSubprocesses: int` -- default 5
- `DiskQuotaBytes: long` -- default 100 MB (100 * 1024 * 1024)

### Attestation Domain Model

**File: `src/Content/Domain/Domain.AI/Attestation/ToolExecutionAttestation.cs`**

Immutable record for HMAC-signed execution proof:

- `ToolName: string` (required)
- `InputHash: string` (required) -- SHA-256
- `OutputHash: string?` -- SHA-256, null if execution crashed
- `Timestamp: DateTimeOffset` (required)
- `Signature: string` (required) -- HMAC-SHA256
- `KeyVersion: string` (required) -- for key rotation verification
- `IsFailureAttestation: bool` -- default false; true when output unavailable
- `FailureReason: string?` -- populated for failure attestations

---

## File Summary

### New Files to Create

| File Path | Type | Description |
|-----------|------|-------------|
| `src/Content/Domain/Domain.AI/Planner/PlanId.cs` | Value object | Strongly-typed Guid wrapper |
| `src/Content/Domain/Domain.AI/Planner/PlanStepId.cs` | Value object | Strongly-typed Guid wrapper |
| `src/Content/Domain/Domain.AI/Planner/StepType.cs` | Enum | 5 step types for keyed executor dispatch |
| `src/Content/Domain/Domain.AI/Planner/EdgeType.cs` | Enum | 4 edge types (DataFlow, ControlFlow, ConditionalTrue/False) |
| `src/Content/Domain/Domain.AI/Planner/StepExecutionStatus.cs` | Enum | 7-state step state machine |
| `src/Content/Domain/Domain.AI/Planner/BackoffStrategy.cs` | Enum | Fixed, Linear, Exponential |
| `src/Content/Domain/Domain.AI/Planner/ErrorRecovery.cs` | Enum | FailStep, SkipStep, FailPlan, Escalate |
| `src/Content/Domain/Domain.AI/Planner/ApprovalStrategy.cs` | Enum | AnyOf, AllOf, Quorum (added during review) |
| `src/Content/Domain/Domain.AI/Planner/RetryPolicy.cs` | Record | Retry configuration with defaults |
| `src/Content/Domain/Domain.AI/Planner/PlanConfiguration.cs` | Record | Plan-level execution settings |
| `src/Content/Domain/Domain.AI/Planner/PlanEdge.cs` | Record | Directed edge between steps |
| `src/Content/Domain/Domain.AI/Planner/StepConfiguration.cs` | Abstract record | Polymorphic base with JsonDerivedType |
| `src/Content/Domain/Domain.AI/Planner/LlmCallConfig.cs` | Record | StepConfiguration subtype |
| `src/Content/Domain/Domain.AI/Planner/ToolUseConfig.cs` | Record | StepConfiguration subtype |
| `src/Content/Domain/Domain.AI/Planner/HumanGateConfig.cs` | Record | StepConfiguration subtype |
| `src/Content/Domain/Domain.AI/Planner/ConditionalBranchConfig.cs` | Record | StepConfiguration subtype |
| `src/Content/Domain/Domain.AI/Planner/SubPlanConfig.cs` | Record | StepConfiguration subtype |
| `src/Content/Domain/Domain.AI/Planner/PlanStep.cs` | Record | Graph node with type + config + retry |
| `src/Content/Domain/Domain.AI/Planner/PlanGraph.cs` | Record | Central DAG model |
| `src/Content/Domain/Domain.AI/Planner/StepExecutionState.cs` | Record | Per-step runtime tracking |
| `src/Content/Domain/Domain.AI/Sandbox/ToolCapability.cs` | Flags enum | 8 capability flags |
| `src/Content/Domain/Domain.AI/Sandbox/SandboxIsolationLevel.cs` | Enum | None, Process, Container |
| `src/Content/Domain/Domain.AI/Sandbox/ToolPermissionProfile.cs` | Record | Capability requirements + access scoping |
| `src/Content/Domain/Domain.AI/Sandbox/ToolCapabilityAttribute.cs` | Attribute | Compile-time capability declaration |
| `src/Content/Domain/Domain.AI/Sandbox/ResourceLimits.cs` | Record | Memory, CPU, subprocess, disk defaults |
| `src/Content/Domain/Domain.AI/Attestation/ToolExecutionAttestation.cs` | Record | HMAC-signed execution proof |
| `src/Content/Tests/Domain.AI.Tests/Planner/PlanGraphTests.cs` | Test class | PlanId, PlanGraph, PlanStep, config, edge, status tests |
| `src/Content/Tests/Domain.AI.Tests/Planner/StepConfigurationTests.cs` | Test class | JSON polymorphic serialization round-trip |
| `src/Content/Tests/Domain.AI.Tests/Planner/RetryPolicyTests.cs` | Test class | Default values |
| `src/Content/Tests/Domain.AI.Tests/Sandbox/ToolCapabilityTests.cs` | Test class | Flags, bitwise ops, attribute, isolation ordering |
| `src/Content/Tests/Domain.AI.Tests/Attestation/ToolExecutionAttestationTests.cs` | Test class | Failure/success attestation, key version |

### Existing Files -- No Modifications

No existing files are modified in this section. All types are additive. The `Domain.AI.csproj` already includes `System.Text.Json` via the implicit usings, so no new package references are needed for `JsonPolymorphicAttribute`.

---

## Implementation Deviations

The following changes were made during implementation based on code review findings:

1. **Added `ApprovalStrategy` enum** (`src/Content/Domain/Domain.AI/Planner/ApprovalStrategy.cs`): The spec defined `HumanGateConfig.ApprovalStrategy` as `string`. During review, we introduced an enum (AnyOf, AllOf, Quorum) for type safety and consistency with all other constrained-value types in this section.

2. **`ToolUseConfig.InputParameters` default changed to `ImmutableDictionary<string, object?>.Empty`**: The spec used `new Dictionary<string, object?>()` which is downcasting-vulnerable. Changed to truly immutable default for template teaching purposes.

3. **Added missing `PlanStep_RequiredFields_CannotBeNull` test**: The spec listed 7 tests for PlanGraphTests.cs but only 6 were initially implemented. Added the 7th test.

4. **Final file count**: 26 implementation files (25 planned + 1 ApprovalStrategy enum) + 5 test files (18 total test cases).

---

## Dependencies

### This Section Depends On

Nothing. This is the foundation section (Batch 1).

### Sections That Depend On This

- **Section 02** (Application Interfaces) -- defines interfaces using these types
- **Section 03** (Plan Validation) -- validates `PlanGraph`, `StepConfiguration` subtypes
- **Section 04** (EF Core Persistence) -- maps these records to entity configurations
- **Section 06** (Capability Model) -- enforces `ToolPermissionProfile`, `ToolCapability`
- **Section 07** (Process Sandbox) -- uses `ResourceLimits`, `SandboxIsolationLevel`
- **Section 08** (Docker Sandbox) -- uses `ResourceLimits`, `ToolPermissionProfile`
- **Section 09** (Attestation) -- creates and verifies `ToolExecutionAttestation`

---

## Implementation Notes

1. **Namespace convention**: Follow existing pattern. `Domain.AI.Planner`, `Domain.AI.Sandbox`, `Domain.AI.Attestation`. The existing codebase uses `Domain.AI.Governance`, `Domain.AI.Permissions`, etc. as the convention.

2. **XML documentation**: Full XML docs on all public types. This is a template project -- docs serve as teaching material for consumers.

3. **Immutability**: All types use `record` with `init`-only properties and `required` where appropriate. Collections use `IReadOnlyList<T>` and `IReadOnlyDictionary<K,V>` per the coding-style rules.

4. **No using statements needed for System.Text.Json**: The project has `<ImplicitUsings>enable</ImplicitUsings>`, but `System.Text.Json.Serialization` is NOT implicit. Add a `using System.Text.Json.Serialization;` in `StepConfiguration.cs` for the `JsonPolymorphic` and `JsonDerivedType` attributes.

5. **Cross-reference to existing types**: `PlanStep.RequiredAutonomyLevel` references the existing `Domain.AI.Governance.AutonomyLevel` enum. `ToolUseConfig.IsolationLevelOverride` references `SandboxIsolationLevel` from the Sandbox folder. Both are intra-project references within `Domain.AI`.

6. **The `SubPlanConfig` contains a `PlanGraph?` property**: This creates a recursive type reference. This is intentional for inline sub-plan definitions. EF Core serialization (section-04) handles this via JSON columns with depth limits.

7. **`ToolCapabilityAttribute` constructor**: Takes `ToolCapability` as positional parameter (required). `MinimumIsolation` is an `init`-only named parameter with a default. This follows the `[AttributeUsage]` pattern already in the codebase.
