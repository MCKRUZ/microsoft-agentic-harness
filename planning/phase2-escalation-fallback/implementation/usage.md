# Phase 2: Enterprise Trust — Usage Guide

## Quick Start

Phase 2 adds two subsystems to the agentic harness: **Escalation** (human-in-the-loop approval) and **Resilience** (LLM provider fallback with circuit breakers). Both are configured via `appsettings.json` and registered automatically through DI.

### Enable Escalation

Escalation is **enabled by default**. When a governance policy includes a `require_approval` action, the escalation pipeline activates:

```json
// appsettings.json — AppConfig.AI.Governance.Escalation
{
  "Enabled": true,
  "DefaultTimeoutSeconds": 300,
  "DefaultTimeoutAction": "DenyAndEscalate",
  "DefaultApprovalStrategy": "AnyOf"
}
```

### Enable Resilience

Resilience is **disabled by default**. To enable provider fallback:

```json
// appsettings.json — AppConfig.AI.Resilience
{
  "Enabled": true,
  "FallbackChain": [
    { "ClientType": "AzureOpenAI", "DeploymentId": "gpt-4o" },
    { "ClientType": "AzureAIInference", "DeploymentId": "claude-sonnet" }
  ]
}
```

When enabled, `IResilientChatClientProvider.GetChatClientAsync()` returns a `ResilientChatClient` that automatically falls back through the chain on provider failure.

---

## Escalation Subsystem

### How It Works

1. **Policy triggers escalation** — `GovernancePolicyBehavior` encounters a `require_approval` action in a YAML policy
2. **Request created** — `IEscalationService.RequestEscalationAsync()` creates an escalation with approvers, strategy, and timeout
3. **Notifications sent** — `CompositeEscalationNotifier` fans out to all `IEscalationNotificationChannel` implementations (no-op Slack/Teams stubs + AG-UI SSE)
4. **Approver responds** — `IEscalationService.SubmitDecisionAsync()` evaluates via the configured approval strategy
5. **Outcome returned** — Approved, Denied, or TimedOut

### Approval Strategies

| Strategy | Behavior | Use Case |
|----------|----------|----------|
| `AnyOf` | First response wins | Quick decisions, single authority |
| `AllOf` | All must approve; single denial = immediate deny | High-risk actions, compliance |
| `Quorum` | N-of-M voting threshold | Committee decisions |

Strategies are registered as keyed DI singletons (`IApprovalStrategy` keyed by `ApprovalStrategyType` enum).

### Priority Levels

| Priority | Timeout | Behavior |
|----------|---------|----------|
| `Informational` | 0s | Fire-and-forget async notification |
| `Blocking` | 300s | Agent blocks awaiting response |
| `Critical` | 600s | All approvers notified, agent blocks |

### Adding Custom Notification Channels

Register additional `IEscalationNotificationChannel` implementations:

```csharp
// In your DependencyInjection.cs
services.AddSingleton<IEscalationNotificationChannel, MySlackNotifier>();
```

The `CompositeEscalationNotifier` automatically picks up all registered channels.

### AG-UI Integration

When running via AgentHub, escalation events are pushed through the SSE stream as AG-UI events:

- `EscalationRequested` — sent when agent needs approval
- `EscalationResolved` — sent when escalation is approved/denied
- `EscalationExpiring` — sent before timeout (warning)

### Audit Trail

All escalation activity is persisted to JSONL files at `AuditStoragePath` (default: `.agent-sessions/escalations/`). Each escalation gets a file named `{escalation-id}.jsonl` with request, decision, and outcome records.

---

## Resilience Subsystem

### How It Works

1. **Fallback chain configured** — `FallbackChain` array in config defines provider priority order
2. **Circuit breakers monitor health** — `PollyProviderHealthMonitor` tracks per-provider circuit state
3. **Requests route through pipeline** — Retry → Circuit Breaker → Timeout per provider
4. **Fallback on failure** — `ResilientChatClient` iterates through healthy providers
5. **Degraded mode queuing** — When all providers fail, requests queue for retry when a circuit recovers

### Key Components

| Component | Purpose |
|-----------|---------|
| `ResilientChatClient` | Wraps fallback iteration logic |
| `ResilientChatClientProvider` | Composition root — builds the chain from config |
| `ProviderResiliencePipelineBuilder` | Creates Polly v8 pipelines (retry + circuit breaker + timeout) |
| `PollyProviderHealthMonitor` | Maps circuit breaker states to `ProviderHealthState` |
| `ProviderCapabilityRegistry` | Tracks what each provider supports (vision, streaming, tool calling) |
| `LlmRetryQueue` | Background service that drains queued requests when providers recover |

### Polly Pipeline Configuration

```json
{
  "CircuitBreaker": {
    "FailureRatio": 0.5,
    "SamplingDurationSeconds": 30,
    "MinimumThroughput": 5,
    "BreakDurationSeconds": 60
  },
  "Retry": {
    "MaxAttempts": 2,
    "BaseDelaySeconds": 1.0,
    "BackoffType": "Exponential"
  },
  "Timeout": {
    "PerAttemptSeconds": 30
  }
}
```

### Capability Diffing

When falling back to a less-capable provider, `FallbackMetadata.DisabledCapabilities` lists what was lost:

```csharp
var result = await resilientClient.GetResponseAsync(messages, options, ct);
if (result.AdditionalProperties.TryGetValue("FallbackMetadata", out var meta))
{
    // meta.DisabledCapabilities: ["Vision"] if fell back from GPT-4o to Claude
}
```

### Health Monitoring

```csharp
// Check if any provider is available
var anyHealthy = await healthMonitor.IsAnyProviderHealthyAsync();

// Get per-provider health
var health = await healthMonitor.GetAllProviderHealthAsync();
// Returns: Dictionary<string, ProviderHealthState> — Healthy, Degraded, Unavailable
```

---

## OpenTelemetry Metrics

### Escalation Metrics

| Metric | Type | Description |
|--------|------|-------------|
| `agent.escalation.requests` | Counter | Total escalation requests |
| `agent.escalation.resolutions` | Counter | Resolved escalations (tags: outcome, strategy) |
| `agent.escalation.duration_ms` | Histogram | Time from request to resolution |
| `agent.escalation.timeouts` | Counter | Timed-out escalations |
| `agent.escalation.pending` | UpDownCounter | Currently pending escalations |
| `agent.escalation.approver_response_ms` | Histogram | Individual approver response time |

### Resilience Metrics

| Metric | Type | Description |
|--------|------|-------------|
| `agent.resilience.fallback_activations` | Counter | Fallback switches (tags: from, to) |
| `agent.resilience.circuit_state_changes` | Counter | Circuit breaker transitions |
| `agent.resilience.circuit_state` | UpDownCounter | Current circuit state per provider |
| `agent.resilience.retry_attempts` | Counter | Retry attempts |
| `agent.resilience.provider_duration_ms` | Histogram | Per-provider call duration |
| `agent.resilience.degradation_events` | Counter | All-providers-down events |
| `agent.resilience.queue_size` | UpDownCounter | Current retry queue depth |
| `agent.resilience.queue_expired` | Counter | TTL-expired queue items |

---

## Testing

```powershell
# Run all Phase 2 tests (185 tests)
dotnet test src/AgenticHarness.slnx --filter "FullyQualifiedName~Escalation|FullyQualifiedName~Resilience|FullyQualifiedName~ApprovalStrategy"

# Run full solution
dotnet test src/AgenticHarness.slnx
```
