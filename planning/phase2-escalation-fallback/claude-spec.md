# Phase 2: Enterprise Trust — Complete Specification

## Overview

Phase 2 closes two platform gaps: **Human Escalation** (Gap 3) and **Fallback Chains** (Gap 4). Together, these turn the harness from a system that "asks permission" into one that orchestrates trust: agents are bounded by autonomy tiers (Phase 1), escalations are handled with structured approval workflows, and LLM failures degrade gracefully through resilient provider chains.

**Branch:** `feat/agt-governance-integration`
**Dependency:** Phase 1 (Autonomy Tiers + Supervisor Agent) — committed at `8edf626`

---

## Gap 3: Human Escalation

### Current State (55%)

- `GovernanceApprovalWorkflow` with MAF `RequestPort<ApprovalRequest, ApprovalResponse>` — stub complete
- `CreateApprovalRequestExecutor` and `ProcessApprovalOutcomeExecutor` — stub executors
- `GovernanceWorkflowTypes` — defines `ApprovalRequest`, `ApprovalResponse`, `GovernanceApprovalOutcome`
- `AutonomyLevel` enum (`Restricted`, `Supervised`, `Autonomous`) and `AutonomyTierPolicy` — complete
- `IAutonomyTierResolver` — resolves agent type to tier policy
- `GovernanceDecision` with `GovernancePolicyAction.RequireApproval` — policy engine can signal escalation
- `DelegationResult.FailAutonomyExceeded(AutonomyExceededResult)` — structured escalation trigger from supervisor

### What Needs Building

#### 3.1 Escalation Trigger Integration

When `DelegationResult.AutonomyExceeded` is non-null or `GovernanceDecision.Action == RequireApproval`, the system must:
1. Create an `EscalationRequest` with full context (agent ID, tool name, arguments, risk level, matched rule)
2. Determine the approval strategy from policy config
3. Route to the appropriate approval channel
4. Handle the waiting behavior based on tier policy

#### 3.2 Configurable Wait Behavior (Per-Tier)

| Tier | Behavior | Implementation |
|------|----------|----------------|
| Restricted | Block and wait | Agent's current action pauses entirely; `TaskCompletionSource<ApprovalOutcome>` blocks execution |
| Supervised | Queue and continue | Blocked action queued; agent proceeds with other work; queued action resumes on approval |
| Autonomous | Should not escalate often | Same as Supervised if it does trigger |

Configuration via `AppConfig.AI.Permissions.TierPolicies[tier].EscalationBehavior` enum: `Block`, `QueueAndContinue`.

#### 3.3 Multi-Approver Strategies

Implement all three via `IApprovalStrategy` interface:

| Strategy | Behavior | Use Case |
|----------|----------|----------|
| `AnyOfApprovalStrategy` | First approver unblocks | Routine escalations |
| `AllOfApprovalStrategy` | Every designated reviewer must approve | Critical/destructive actions |
| `QuorumApprovalStrategy` | N-of-M majority threshold | Balanced speed + safety |

Strategy resolved from policy config per escalation rule. Default: `AnyOf`.

#### 3.4 Timeout/Expiry

Default behavior: **Auto-deny + escalate**
- Pending approval expires → deny the request (agent unblocked with denial)
- Simultaneously escalate notification to next approver tier for visibility
- Configurable per-policy: `TimeoutAction` enum with `Deny`, `DenyAndEscalate`, `Approve`, `Escalate`
- Default timeout: 5 minutes (configurable via `AppConfig.AI.Governance.Escalation.DefaultTimeoutSeconds`)

#### 3.5 Notification Delivery

**Architecture:** AG-UI events for dashboard + extensible `IEscalationNotifier` adapter pattern.

**AG-UI event types to add:**
- `ESCALATION_REQUESTED` — new approval pending
- `ESCALATION_RESOLVED` — approval decision made (approved/denied/expired)
- `ESCALATION_EXPIRING` — warning before timeout

**Adapter interface:** `IEscalationNotifier`
- `NotifyEscalationRequestedAsync(EscalationRequest)`
- `NotifyEscalationResolvedAsync(EscalationOutcome)`
- `NotifyEscalationExpiringAsync(string requestId, TimeSpan remaining)`

**Implementations:**
- `AgUiEscalationNotifier` — pushes AG-UI events via existing SSE stream
- `NoOpSlackNotifier` — stub for Slack (extension point)
- `NoOpTeamsNotifier` — stub for Teams (extension point)
- `CompositeEscalationNotifier` — fans out to all registered notifiers

#### 3.6 Approval Audit Trail

**Dual persistence:**
- `JsonlApprovalStore` — append-only JSONL file (consistent with `JsonlDelegationStore` from Phase 1), queryable audit trail
- Structured logging via existing `StructuredLogAuditSink` — real-time observability

**Audit record shape:** `ApprovalAuditRecord` with request details, strategy used, approver(s), decision, timestamps, timeout info.

#### 3.7 Escalation Priority Levels

| Level | Meaning | Default Timeout |
|-------|---------|-----------------|
| `Informational` | FYI — logged but doesn't block | No timeout (async) |
| `Blocking` | Agent paused, awaiting decision | 5 minutes |
| `Critical` | Destructive or high-risk action | 2 minutes, escalates to all approvers |

Derived from `GovernanceDecision.Action` and policy config.

---

## Gap 4: Fallback Chains

### Current State (25%)

- Polly v8 NuGet package in `Directory.Packages.props` — imported but unused
- `IChatClientFactory` with `IsAvailable()`, `GetChatClientAsync()`, `GetAvailableProviders()` — integration point
- `AIAgentFrameworkClientType` enum: `AzureOpenAI`, `OpenAI`, `AzureAIInference`, `PersistentAgents`, `Anthropic`, `Echo`
- `ChatClientFactory` in Infrastructure.AI — creates clients per provider type
- `AgentExecutionContextFactory` — wires chat clients into agent contexts

### What Needs Building

#### 4.1 Provider Fallback Chain

Ordered preference list of LLM providers, configured per agent or globally:

```
Primary (AzureOpenAI/gpt-4o) → Secondary (Anthropic/claude-sonnet) → Tertiary (OpenAI/gpt-4o)
```

**Implementation:** `ResilientChatClient : IChatClient` wrapping a `ResiliencePipeline<ChatResponse>`.

Pipeline composition (outermost to innermost):
1. **Fallback** — catches `BrokenCircuitException` + provider errors, routes to next provider
2. **Retry** — exponential backoff with jitter per provider attempt
3. **Circuit breaker** — ratio-based per provider, prevents cascading failures
4. **Timeout** — per-attempt timeout

#### 4.2 Circuit Breaker Per Provider

Each provider gets its own circuit breaker instance (not shared). Polly v8 ratio-based:
- `FailureRatio`: 0.5 (50% failure threshold)
- `SamplingDuration`: 30 seconds
- `MinimumThroughput`: 5 requests minimum before evaluating
- `BreakDuration`: 60 seconds

All values configurable via `AppConfig.AI.Resilience.CircuitBreaker.*`.

Use `CircuitBreakerStateProvider` per provider for health reporting to OTel.

#### 4.3 Retry with Exponential Backoff

Per-provider retry before moving to next in chain:
- `MaxRetryAttempts`: 2 (don't retry excessively — move to fallback)
- `Delay`: 1 second base
- `BackoffType`: Exponential with jitter
- `ShouldHandle`: HTTP 429, 500, 503, `HttpRequestException`, `TaskCanceledException`

#### 4.4 Response Metadata for Agent Adaptation

`FallbackMetadata` record attached to response:

```
record FallbackMetadata(
    string ActiveProvider,           // Which provider actually served the response
    bool IsFallback,                 // Was this a fallback (not primary)?
    IReadOnlyList<string> FailedProviders,  // Providers that failed before this one
    IReadOnlySet<string> DisabledCapabilities);  // Features unavailable on this provider
```

Agent receives this via `ChatResponse` extension data or `AgentExecutionContext.AdditionalProperties`. Agent can adapt: simplify prompts, skip tool use requiring unavailable features.

#### 4.5 Health Checking: Passive with Pre-Warm

- **Passive:** Track real-traffic failures to feed circuit breaker state. No synthetic probes during normal operation.
- **Pre-warm on recovery:** When circuit transitions Open → HalfOpen, send a lightweight synthetic probe (`ChatMessage` with minimal tokens) before routing real traffic. This validates recovery without risking user-facing requests.
- **Health state exposed** via `IProviderHealthMonitor` for OTel metrics and dashboard display.

#### 4.6 Degraded Mode

When ALL providers in the chain fail:
1. Return structured `ProviderExhaustedException` with `RetryAfter` hint
2. Queue the original request in an in-memory retry queue
3. Background service monitors circuit breaker states; when any circuit closes, drain the retry queue
4. Retry queue has configurable TTL (default: 5 minutes) — expired requests return permanent failure

No cached/static responses. No read-only fallback. Clean failure + automatic recovery.

#### 4.7 Fallback Telemetry

**Metrics (OTel):**
- `agent.resilience.fallback.activations` — counter per provider switch
- `agent.resilience.circuit.state_changes` — counter per provider per state transition
- `agent.resilience.circuit.state` — gauge per provider (0=closed, 1=half-open, 2=open)
- `agent.resilience.retry.attempts` — counter per provider
- `agent.resilience.provider.duration_ms` — histogram per provider
- `agent.resilience.degradation.events` — counter for full provider exhaustion

**Activities (traces):**
- `agent.resilience.fallback` span wrapping the full chain attempt
- Child spans per provider attempt with outcome tag

---

## Configuration Shape

```json
{
  "AppConfig": {
    "AI": {
      "Governance": {
        "Escalation": {
          "Enabled": true,
          "DefaultTimeoutSeconds": 300,
          "DefaultTimeoutAction": "DenyAndEscalate",
          "DefaultApprovalStrategy": "AnyOf",
          "PriorityLevels": {
            "Informational": { "TimeoutSeconds": 0, "Async": true },
            "Blocking": { "TimeoutSeconds": 300 },
            "Critical": { "TimeoutSeconds": 120, "EscalateToAll": true }
          }
        }
      },
      "Resilience": {
        "Enabled": true,
        "FallbackChain": ["AzureOpenAI", "Anthropic", "OpenAI"],
        "CircuitBreaker": {
          "FailureRatio": 0.5,
          "SamplingDurationSeconds": 30,
          "MinimumThroughput": 5,
          "BreakDurationSeconds": 60
        },
        "Retry": {
          "MaxAttempts": 2,
          "BaseDelaySeconds": 1,
          "BackoffType": "ExponentialWithJitter"
        },
        "Timeout": {
          "PerAttemptSeconds": 30
        },
        "DegradedMode": {
          "RetryQueueTtlSeconds": 300,
          "MaxQueueSize": 100
        }
      }
    }
  }
}
```

---

## Architecture Constraints

- Clean Architecture: Domain records → Application interfaces → Infrastructure implementations → Presentation AG-UI events
- Immutability: records for all DTOs/events, `IReadOnlyList<T>` for public surfaces
- DI: Each layer's `DependencyInjection.cs`, keyed DI for strategies and notifiers
- Options pattern: `IOptionsMonitor<T>` for all config
- OTel: Follow `agent.<domain>.<metric>` naming convention
- MediatR: Pipeline behaviors for cross-cutting escalation logic
- Testing: xUnit + Moq, 80%+ coverage, `Handle_Scenario_ExpectedResult` naming

## Success Criteria

1. Agent exceeding autonomy tier → automatic escalation request with configurable block/queue behavior
2. Escalation delivered via AG-UI events + extensible notifier adapters
3. Multi-approver strategies (any-of, all-of, quorum) with configurable timeout policies
4. Timeout default: auto-deny + escalate to higher tier
5. JSONL audit trail + structured logging for all escalation events
6. LLM calls failover through configured provider chain with per-provider circuit breakers
7. Agent receives fallback metadata and can adapt behavior to degraded capabilities
8. Passive health tracking with pre-warm probes on circuit recovery
9. Graceful error + retry queue when all providers exhausted
10. Full OTel observability: escalation events, provider switches, circuit state changes
11. 80%+ test coverage on all new code
