# Section 03: OTel Conventions and Metrics

## Overview

This section adds two **conventions** static classes (`EscalationConventions`, `ResilienceConventions`) in `Domain.AI/Telemetry/Conventions/` and two **metrics** static classes (`EscalationMetrics`, `ResilienceMetrics`) in `Application.AI.Common/OpenTelemetry/Metrics/`. These define all OTel attribute names, metric identifiers, and pre-created instrument instances used by the escalation and resilience subsystems throughout Phase 2.

**Dependencies:** Section 01 (domain escalation enums referenced in tag value design) and Section 02 (domain resilience enums referenced in tag value design). However, this section only uses string constants -- it does not import the enum types themselves. The conventions classes are pure string constants with no dependencies beyond the `Domain.AI.Telemetry.Conventions` namespace.

## Files to Create

| File | Project | Purpose |
|------|---------|---------|
| `src/Content/Domain/Domain.AI/Telemetry/Conventions/EscalationConventions.cs` | Domain.AI | Attribute names + metric identifiers for escalation |
| `src/Content/Domain/Domain.AI/Telemetry/Conventions/ResilienceConventions.cs` | Domain.AI | Attribute names + metric identifiers for resilience |
| `src/Content/Application/Application.AI.Common/OpenTelemetry/Metrics/EscalationMetrics.cs` | Application.AI.Common | Static instrument instances for escalation |
| `src/Content/Application/Application.AI.Common/OpenTelemetry/Metrics/ResilienceMetrics.cs` | Application.AI.Common | Static instrument instances for resilience |

## Files to Modify

| File | Change |
|------|--------|
| `src/Content/Tests/Application.AI.Common.Tests/OpenTelemetry/Metrics/MetricsInstrumentTests.cs` | Add test methods for new metric classes |

---

## Tests First

All new tests go in the existing `MetricsInstrumentTests.cs` file at `src/Content/Tests/Application.AI.Common.Tests/OpenTelemetry/Metrics/MetricsInstrumentTests.cs`, following the established pattern. Each test verifies that the static instrument property is non-null and (for counters/histograms) has a non-empty name.

### Escalation Metrics Tests

```csharp
// In MetricsInstrumentTests.cs -- add these test methods

[Fact]
public void EscalationMetrics_Requests_IsNotNull()
{
    EscalationMetrics.Requests.Should().NotBeNull();
    EscalationMetrics.Requests.Name.Should().Be(EscalationConventions.Requests);
}

[Fact]
public void EscalationMetrics_Resolutions_IsNotNull()
{
    EscalationMetrics.Resolutions.Should().NotBeNull();
    EscalationMetrics.Resolutions.Name.Should().Be(EscalationConventions.Resolutions);
}

[Fact]
public void EscalationMetrics_DurationMs_IsNotNull()
{
    EscalationMetrics.DurationMs.Should().NotBeNull();
    EscalationMetrics.DurationMs.Name.Should().Be(EscalationConventions.DurationMs);
}

[Fact]
public void EscalationMetrics_Timeouts_IsNotNull()
{
    EscalationMetrics.Timeouts.Should().NotBeNull();
    EscalationMetrics.Timeouts.Name.Should().Be(EscalationConventions.Timeouts);
}

[Fact]
public void EscalationMetrics_Pending_IsNotNull()
{
    // UpDownCounter for gauge-like behavior
    EscalationMetrics.Pending.Should().NotBeNull();
    EscalationMetrics.Pending.Name.Should().Be(EscalationConventions.Pending);
}

[Fact]
public void EscalationMetrics_ApproverResponseMs_IsNotNull()
{
    EscalationMetrics.ApproverResponseMs.Should().NotBeNull();
    EscalationMetrics.ApproverResponseMs.Name.Should().Be(EscalationConventions.ApproverResponseMs);
}
```

### Resilience Metrics Tests

```csharp
[Fact]
public void ResilienceMetrics_FallbackActivations_IsNotNull()
{
    ResilienceMetrics.FallbackActivations.Should().NotBeNull();
    ResilienceMetrics.FallbackActivations.Name.Should().Be(ResilienceConventions.FallbackActivations);
}

[Fact]
public void ResilienceMetrics_CircuitStateChanges_IsNotNull()
{
    ResilienceMetrics.CircuitStateChanges.Should().NotBeNull();
    ResilienceMetrics.CircuitStateChanges.Name.Should().Be(ResilienceConventions.CircuitStateChanges);
}

[Fact]
public void ResilienceMetrics_CircuitState_IsNotNull()
{
    // UpDownCounter for gauge-like per-provider state
    ResilienceMetrics.CircuitState.Should().NotBeNull();
    ResilienceMetrics.CircuitState.Name.Should().Be(ResilienceConventions.CircuitState);
}

[Fact]
public void ResilienceMetrics_RetryAttempts_IsNotNull()
{
    ResilienceMetrics.RetryAttempts.Should().NotBeNull();
    ResilienceMetrics.RetryAttempts.Name.Should().Be(ResilienceConventions.RetryAttempts);
}

[Fact]
public void ResilienceMetrics_ProviderDurationMs_IsNotNull()
{
    ResilienceMetrics.ProviderDurationMs.Should().NotBeNull();
    ResilienceMetrics.ProviderDurationMs.Name.Should().Be(ResilienceConventions.ProviderDurationMs);
}

[Fact]
public void ResilienceMetrics_DegradationEvents_IsNotNull()
{
    ResilienceMetrics.DegradationEvents.Should().NotBeNull();
    ResilienceMetrics.DegradationEvents.Name.Should().Be(ResilienceConventions.DegradationEvents);
}

[Fact]
public void ResilienceMetrics_QueueSize_IsNotNull()
{
    ResilienceMetrics.QueueSize.Should().NotBeNull();
    ResilienceMetrics.QueueSize.Name.Should().Be(ResilienceConventions.QueueSize);
}

[Fact]
public void ResilienceMetrics_QueueExpired_IsNotNull()
{
    ResilienceMetrics.QueueExpired.Should().NotBeNull();
    ResilienceMetrics.QueueExpired.Name.Should().Be(ResilienceConventions.QueueExpired);
}
```

### Conventions Naming Convention Tests

```csharp
[Fact]
public void EscalationConventions_Constants_FollowNamingConvention()
{
    // All metric name constants should start with "agent.escalation."
    EscalationConventions.Requests.Should().StartWith("agent.escalation.");
    EscalationConventions.Resolutions.Should().StartWith("agent.escalation.");
    EscalationConventions.DurationMs.Should().StartWith("agent.escalation.");
    EscalationConventions.Timeouts.Should().StartWith("agent.escalation.");
    EscalationConventions.Pending.Should().StartWith("agent.escalation.");
    EscalationConventions.ApproverResponseMs.Should().StartWith("agent.escalation.");
}

[Fact]
public void ResilienceConventions_Constants_FollowNamingConvention()
{
    // All metric name constants should start with "agent.resilience."
    ResilienceConventions.FallbackActivations.Should().StartWith("agent.resilience.");
    ResilienceConventions.CircuitStateChanges.Should().StartWith("agent.resilience.");
    ResilienceConventions.CircuitState.Should().StartWith("agent.resilience.");
    ResilienceConventions.RetryAttempts.Should().StartWith("agent.resilience.");
    ResilienceConventions.ProviderDurationMs.Should().StartWith("agent.resilience.");
    ResilienceConventions.DegradationEvents.Should().StartWith("agent.resilience.");
    ResilienceConventions.QueueSize.Should().StartWith("agent.resilience.");
    ResilienceConventions.QueueExpired.Should().StartWith("agent.resilience.");
}
```

Add `using Domain.AI.Telemetry.Conventions;` to the test file's imports.

---

## Implementation Details

### Established Pattern

All conventions and metrics classes in this codebase follow a strict pattern:

1. **Conventions** (`Domain.AI/Telemetry/Conventions/`): A `public static class` containing only `public const string` fields. Attribute name constants use dotted hierarchical naming (e.g., `agent.governance.policy`). Metric identifier constants also use dotted naming (e.g., `agent.governance.decisions`). Nested static classes hold well-known tag values (e.g., `ActionValues`, `ScopeValues`).

2. **Metrics** (`Application.AI.Common/OpenTelemetry/Metrics/`): A `public static class` containing `public static` read-only properties. Each property creates an instrument via `AppInstrument.Meter.Create*<T>()`, referencing the matching convention constant for the instrument name. Instrument types: `Counter<long>` for monotonic counts, `Histogram<double>` for distributions, `UpDownCounter<long>` for gauge-like values that can increase or decrease.

3. **No `app.*` prefix on metric names.** The collector namespace handles prefixing. Double-prefix is a known bug in this project (see memory `feedback_no_app_prefix.md`).

### EscalationConventions

**File:** `src/Content/Domain/Domain.AI/Telemetry/Conventions/EscalationConventions.cs`

**Namespace:** `Domain.AI.Telemetry.Conventions`

This class defines:

**Attribute name constants** (used as OTel span/log attribute keys):
- `EscalationId` = `"agent.escalation.id"` -- unique escalation identifier for correlation
- `AgentId` = `"agent.escalation.agent_id"` -- the agent that triggered escalation
- `ToolName` = `"agent.escalation.tool"` -- the tool that was attempted
- `Priority` = `"agent.escalation.priority"` -- informational/blocking/critical
- `ResolutionType` = `"agent.escalation.resolution_type"` -- approved/denied/timed_out/escalated
- `Strategy` = `"agent.escalation.strategy"` -- any_of/all_of/quorum
- `ApproverName` = `"agent.escalation.approver"` -- individual approver identifier

**Metric identifier constants** (used as instrument names):
- `Requests` = `"agent.escalation.requests"` -- counter of escalation requests created
- `Resolutions` = `"agent.escalation.resolutions"` -- counter of resolved escalations, tagged by resolution_type and priority
- `DurationMs` = `"agent.escalation.duration_ms"` -- histogram of time from request to resolution
- `Timeouts` = `"agent.escalation.timeouts"` -- counter of escalations that timed out
- `Pending` = `"agent.escalation.pending"` -- gauge of currently active pending escalations
- `ApproverResponseMs` = `"agent.escalation.approver_response_ms"` -- histogram of individual approver response latency

**Nested tag value classes:**
- `PriorityValues`: `Informational = "informational"`, `Blocking = "blocking"`, `Critical = "critical"`
- `ResolutionValues`: `Approved = "approved"`, `Denied = "denied"`, `TimedOut = "timed_out"`, `Escalated = "escalated"`
- `StrategyValues`: `AnyOf = "any_of"`, `AllOf = "all_of"`, `Quorum = "quorum"`

### ResilienceConventions

**File:** `src/Content/Domain/Domain.AI/Telemetry/Conventions/ResilienceConventions.cs`

**Namespace:** `Domain.AI.Telemetry.Conventions`

**Attribute name constants:**
- `ProviderName` = `"agent.resilience.provider"` -- provider identifier for per-provider tagging
- `CircuitStateName` = `"agent.resilience.circuit.state_name"` -- healthy/degraded/unavailable
- `TransitionFrom` = `"agent.resilience.circuit.from"` -- state transition source
- `TransitionTo` = `"agent.resilience.circuit.to"` -- state transition target
- `FailedProviders` = `"agent.resilience.failed_providers"` -- comma-separated list of failed provider names

**Metric identifier constants:**
- `FallbackActivations` = `"agent.resilience.fallback.activations"` -- counter per provider switch
- `CircuitStateChanges` = `"agent.resilience.circuit.state_changes"` -- counter per provider, per transition type
- `CircuitState` = `"agent.resilience.circuit.state"` -- gauge per provider (0=healthy, 1=degraded, 2=unavailable)
- `RetryAttempts` = `"agent.resilience.retry.attempts"` -- counter per provider
- `ProviderDurationMs` = `"agent.resilience.provider.duration_ms"` -- histogram per provider
- `DegradationEvents` = `"agent.resilience.degradation.events"` -- counter for full exhaustion events
- `QueueSize` = `"agent.resilience.queue.size"` -- gauge for retry queue depth
- `QueueExpired` = `"agent.resilience.queue.expired"` -- counter for TTL-expired requests

**Nested tag value classes:**
- `HealthValues`: `Healthy = "healthy"`, `Degraded = "degraded"`, `Unavailable = "unavailable"`

### EscalationMetrics

**File:** `src/Content/Application/Application.AI.Common/OpenTelemetry/Metrics/EscalationMetrics.cs`

**Namespace:** `Application.AI.Common.OpenTelemetry.Metrics`

**Imports:** `System.Diagnostics.Metrics`, `Domain.AI.Telemetry.Conventions`, `Domain.Common.Telemetry`

Static class with these instrument properties:

| Property | Instrument Type | Convention Constant | Unit | Description |
|----------|----------------|---------------------|------|-------------|
| `Requests` | `Counter<long>` | `EscalationConventions.Requests` | `"{request}"` | Escalation requests created |
| `Resolutions` | `Counter<long>` | `EscalationConventions.Resolutions` | `"{resolution}"` | Escalation resolutions. Tags: resolution_type, priority |
| `DurationMs` | `Histogram<double>` | `EscalationConventions.DurationMs` | `"ms"` | Escalation request-to-resolution duration |
| `Timeouts` | `Counter<long>` | `EscalationConventions.Timeouts` | `"{timeout}"` | Escalation timeout events |
| `Pending` | `UpDownCounter<long>` | `EscalationConventions.Pending` | `"{escalation}"` | Currently pending escalations (inc on request, dec on resolution) |
| `ApproverResponseMs` | `Histogram<double>` | `EscalationConventions.ApproverResponseMs` | `"ms"` | Per-approver response latency |

Each property follows the pattern: `public static {InstrumentType} {Name} { get; } = AppInstrument.Meter.Create{InstrumentType}(ConventionConstant, unit, description);`

Use `CreateUpDownCounter<long>` for `Pending` since it needs both increment and decrement operations. The `System.Diagnostics.Metrics` API provides this type for gauge-like semantics.

### ResilienceMetrics

**File:** `src/Content/Application/Application.AI.Common/OpenTelemetry/Metrics/ResilienceMetrics.cs`

**Namespace:** `Application.AI.Common.OpenTelemetry.Metrics`

**Imports:** Same as EscalationMetrics.

| Property | Instrument Type | Convention Constant | Unit | Description |
|----------|----------------|---------------------|------|-------------|
| `FallbackActivations` | `Counter<long>` | `ResilienceConventions.FallbackActivations` | `"{activation}"` | Fallback provider activations |
| `CircuitStateChanges` | `Counter<long>` | `ResilienceConventions.CircuitStateChanges` | `"{change}"` | Circuit breaker state transitions. Tags: provider, from, to |
| `CircuitState` | `UpDownCounter<long>` | `ResilienceConventions.CircuitState` | `"{state}"` | Per-provider circuit state gauge (0/1/2) |
| `RetryAttempts` | `Counter<long>` | `ResilienceConventions.RetryAttempts` | `"{attempt}"` | Retry attempts per provider |
| `ProviderDurationMs` | `Histogram<double>` | `ResilienceConventions.ProviderDurationMs` | `"ms"` | Per-provider request duration |
| `DegradationEvents` | `Counter<long>` | `ResilienceConventions.DegradationEvents` | `"{event}"` | Full provider exhaustion events |
| `QueueSize` | `UpDownCounter<long>` | `ResilienceConventions.QueueSize` | `"{request}"` | Retry queue depth gauge |
| `QueueExpired` | `Counter<long>` | `ResilienceConventions.QueueExpired` | `"{expiry}"` | TTL-expired queued requests |

---

## Usage by Downstream Sections

These constants and instruments are consumed throughout Phase 2:

- **Section 08 (DefaultEscalationService):** Increments `EscalationMetrics.Requests` on new escalation, `EscalationMetrics.Resolutions` on resolution (with `ResolutionType` and `Priority` tags), `EscalationMetrics.Timeouts` on timeout. Records `EscalationMetrics.DurationMs` histogram on resolution. Adjusts `EscalationMetrics.Pending` (+1 on request, -1 on resolution). Records `EscalationMetrics.ApproverResponseMs` on each `SubmitDecisionAsync`. Uses attribute constants for structured log tags.

- **Section 11 (ProviderResiliencePipelineBuilder):** Increments `ResilienceMetrics.RetryAttempts` per retry. Records `ResilienceMetrics.ProviderDurationMs` per provider call.

- **Section 12 (ResilientChatClient):** Increments `ResilienceMetrics.FallbackActivations` each time it falls back to the next provider. Increments `ResilienceMetrics.DegradationEvents` when all providers are exhausted.

- **Section 13 (PollyProviderHealthMonitor):** Increments `ResilienceMetrics.CircuitStateChanges` on state transitions (tagged with `from`/`to`). Updates `ResilienceMetrics.CircuitState` gauge per provider.

- **Section 15 (LlmRetryQueue):** Updates `ResilienceMetrics.QueueSize` on enqueue/dequeue. Increments `ResilienceMetrics.QueueExpired` on TTL expiry.

## XML Documentation

All public types and members must have full XML documentation. This is a template project -- the docs are teaching material for consumers. Each conventions constant should have a `<summary>` explaining what the metric/attribute tracks and what tags it expects. Each metrics property should document the instrument type, unit, and expected tag keys.

---

## Implementation Notes

**Status:** Complete
**Commit:** (see git log)

### Deviations from Plan
- Added `<remarks>` XML doc block to `ResilienceMetrics.CircuitState` documenting the delta-recording requirement for UpDownCounter-as-gauge semantics (per code review). Section 13 implementors must track previous state and record the difference (newState - previousState).

### Test Results
- 16 new test methods added to `MetricsInstrumentTests.cs`
- 6 escalation metric instrument tests (not-null + name match against convention constant)
- 8 resilience metric instrument tests (not-null + name match against convention constant)
- 2 conventions naming convention tests (all metric constants follow `agent.escalation.*` / `agent.resilience.*` prefix)
- All 30 tests pass (14 existing + 16 new)
