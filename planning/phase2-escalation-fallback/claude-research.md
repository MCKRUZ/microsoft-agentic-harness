# Phase 2 Research — Enterprise Trust

## Part 1: Codebase Analysis

### Governance/Approval Workflow Stubs

**Status:** Stub-complete, not fully wired into orchestration.

**Workflow graph:** `CreateApprovalRequestExecutor → [RequestPort: ApprovalPort] → ProcessApprovalOutcomeExecutor`

**ApprovalRequest shape:**
```csharp
record ApprovalRequest(
    string ToolName, string AgentId, string Description,
    string Risk, IReadOnlyList<string> Approvers, DateTimeOffset RequestedAt);
```

**ApprovalResponse shape:**
```csharp
record ApprovalResponse(
    bool Approved, string ApproverName, string? Reason, DateTimeOffset RespondedAt,
    string AgentId = "unknown", string ToolName = "unknown");
```

**GovernanceApprovalOutcome** contains `AuditTrailId`, embeds `ApprovalResponse`, links to `OriginalDecision`.

**Pattern:** Correlation via echoed `AgentId` + `ToolName`. Executors are stateless; message carries all state.

### Autonomy Tiers & Governance Decision Model

**AutonomyLevel:** `Restricted(0)`, `Supervised(1)`, `Autonomous(2)`

**AutonomyTierPolicy:**
```csharp
record AutonomyTierPolicy {
    AutonomyLevel Level { get; init; }
    PermissionBehaviorType DefaultBehavior { get; init; }  // Ask, Allow, Deny
    IReadOnlyDictionary<string, PermissionBehaviorType>? ToolOverrides { get; init; }
}
```

**GovernanceDecision** includes `GovernancePolicyAction` with values: `Allow`, `Deny`, `Warn`, `RequireApproval`, `Log`, `RateLimit`. Has optional `Approvers` list and `MatchedRule`.

**AutonomyExceededResult** embedded in `DelegationResult` and `DelegationRecord` — structured escalation signal with `AttemptedAction`, `CurrentLevel`, `RequiredLevel`, `Reason`.

### IAutonomyTierResolver

```csharp
interface IAutonomyTierResolver {
    AutonomyLevel Resolve(SubagentType agentType);
    AutonomyLevel Resolve(SubagentDefinition definition);
}
```

### IGovernancePolicyEngine

```csharp
interface IGovernancePolicyEngine {
    GovernanceDecision EvaluateToolCall(string agentId, string toolName, 
        IReadOnlyDictionary<string, object>? arguments = null);
    void LoadPolicyFile(string yamlPath);
    bool HasPolicies { get; }
}
```

Wrapped by `AgtPolicyEngineAdapter`. Evaluates against YAML policies loaded at runtime.

### IChatClientFactory & LLM Providers

**Interface methods:** `IsAvailable()`, `GetChatClientAsync()`, `GetAvailableProviders()`, `CreatePersistentAgentAsync()`

**Supported types:** `AzureOpenAI`, `OpenAI`, `AzureAIInference`, `PersistentAgents`, `Anthropic`, `Echo`

**Implementation:** Caches persistent agent lookups via `IMemoryCache`. Azure AI Inference normalizes `.services.ai.azure.com` endpoints. Anthropic created directly (no DI singleton).

### Polly / Resilience

**No Polly usage exists in codebase.** Package imported in `Directory.Packages.props` but never used. Need to build from scratch.

### Event Push Pattern (AG-UI SSE)

**Not classic SignalR Hub.** Uses AG-UI event streaming via SSE as JSON lines.

**Event types:** `RUN_STARTED`, `RUN_FINISHED`, `RUN_ERROR`, `STEP_STARTED`, `STEP_FINISHED`, `TEXT_MESSAGE_*`, `TOOL_CALL_*`, `STATE_SNAPSHOT`, `STATE_DELTA`, `CUSTOM`

**Pattern:** Polymorphic base `AgUiEvent` with `[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]`.

**For Phase 2:** Need custom event types: `APPROVAL_REQUESTED`, `APPROVAL_RESPONSE_RECEIVED`, `ESCALATION_TRIGGERED`, `FALLBACK_ACTIVATED`.

### DI Registration Patterns

**Infrastructure.AI:** `AddSingleton<IChatClientFactory>`, `AddSingleton<IAuditSink>`, conditional AI client registration based on config.

**Application.Core:** MediatR auto-discovery, validator scanning, `AddSingleton<IPermissionRuleProvider, AutonomyTierRuleProvider>`, keyed workflow singletons.

### OTel Conventions

**GovernanceConventions:** `agent.governance.{decisions|violations|evaluation_duration|rate_limit_hits|audit_events|...}`

**SupervisorConventions:** `agent.supervisor.{delegations.total|delegations.duration_ms|autonomy.exceeded_total|selection_score}`

**Pattern:** `agent.<domain>.<metric>` naming. Tags include policy, rule, action, scope, tool. Instruments created via `AppInstrument.Meter.Create*()`.

### Config Hierarchy

```
AppConfig.AI.Governance.{Enabled, PolicyPaths, EnablePromptInjectionDetection, EnableMcpSecurity, EnableAudit, EnableMetrics}
AppConfig.AI.Permissions.{DefaultBehavior, DefaultAutonomyLevel, TierPolicies}
AppConfig.AI.Orchestration.Subagent.{MaxConcurrentSubagents, DefaultMaxTurnsPerSubagent, MaxDelegationDepth}
```

### Testing Patterns

xUnit + Moq. Test naming: `Handle_[Condition]_[ExpectedOutcome]`. Mock IGovernancePolicyEngine, IAuditService, ILogger. Setup returns specific `GovernanceDecision` outcomes. Verify audit service calls with `Times.Once`.

### DelegationResult with Autonomy Exceeded

```csharp
DelegationResult.FailAutonomyExceeded(AutonomyExceededResult exceeded)
```

Factory method returns `IsSuccess=false` with structured `AutonomyExceeded` property — the Phase 2 escalation trigger.

### Key File Paths

**Domain:** `Domain.AI/Governance/`, `Domain.AI/Orchestration/DelegationResult.cs`, `Domain.AI/Telemetry/Conventions/`
**Application:** `Application.Core/Workflows/Governance/`, `Application.AI.Common/Interfaces/Governance/`, `Application.AI.Common/Interfaces/IChatClientFactory.cs`
**Infrastructure:** `Infrastructure.AI/Factories/ChatClientFactory.cs`, `Infrastructure.AI/DependencyInjection.cs`
**Presentation:** `Presentation.AgentHub/AgUi/AgUiEventType.cs`
**Tests:** `Infrastructure.AI.Tests/Governance/`, `Application.AI.Common.Tests/MediatRBehaviors/GovernancePolicyBehaviorTests.cs`

---

## Part 2: Web Research

### Topic 1: Polly v8 Resilience Pipelines

**Builder pattern:** `ResiliencePipelineBuilder<T>` → `.AddFallback()` → `.AddRetry()` → `.AddCircuitBreaker()` → `.AddTimeout()` → `.Build()`.

**Strategy execution order:** Outermost-first in add order. Fallback must be outermost to catch `BrokenCircuitException`. Timeout innermost for per-attempt limits.

**Correct composition for LLM fallback:**
```csharp
new ResiliencePipelineBuilder<HttpResponseMessage>()
    .AddFallback(...)      // outer: catches all failures, triggers secondary provider
    .AddRetry(...)         // retry primary before giving up
    .AddCircuitBreaker(...)// track failure ratio, break circuit
    .AddTimeout(...)       // inner: per-attempt timeout
    .Build();
```

**Circuit breaker states:** Closed → Open → HalfOpen. V8 only supports ratio-based (not consecutive failure count). Use `CircuitBreakerStateProvider` for health reporting, `CircuitBreakerManualControl` for graceful shutdown.

**`AddStandardResilienceHandler`:** Pre-composed 5-strategy pipeline (rate limiter → total timeout → retry → circuit breaker → attempt timeout).

**Anti-patterns:**
- Don't abuse `OnRetry` as fallback — use `AddFallback` with `FallbackAction`
- Don't nest `ExecuteAsync` calls — compose via builder
- Wrong strategy order is the #1 bug source

**Sources:** pollydocs.org, Microsoft Learn, NimblePros blog

### Topic 2: Human-in-the-Loop Escalation

**Core architecture:** Propose-Commit separation. Store structured action payload → present to reviewer → execute only after approval with idempotency keys.

**Risk-based tiering:**
| Tier | Behavior |
|------|----------|
| Auto-approve | Execute + log (read ops, low value) |
| Async review | Execute + flag for audit (medium risk) |
| Sync approval | Block until approved (financial, deletion) |

**Multi-approver strategies:** Any-of (speed), All-of (high-risk), Quorum (balanced), Role-based dual (separation of duties).

**Timeout/expiry policies:**
- No response within SLA → auto-escalate to next tier
- Expired → reject + notify agent to re-plan
- All tiers exhausted → queue for manual processing + alert ops
- Reviewer unavailable → route to backup pool

**Anti-patterns:** Rubber-stamping (automation complacency), approve/deny without context, treating oversight as static, neglecting sampling audits for low-risk actions.

**Sources:** Galileo, StackAI, AppScale, Strata.io, Cloudflare

### Topic 3: Microsoft.Extensions.AI Fallback

**No built-in `FallbackChatClient`.** Must build custom using `DelegatingChatClient` or direct `IChatClient` implementation.

**`DelegatingChatClient` pattern:** Subclass, override `GetResponseAsync`/`GetStreamingResponseAsync`, call `base.*` for delegation.

**Keyed DI for providers:** Register each provider with keyed DI, resolve in fallback chain constructor.

**Degraded mode strategies:**
1. Model downgrade (GPT-4o → GPT-3.5)
2. Feature reduction (disable tool calling, reduce max tokens)
3. Cached responses for common queries
4. Static "service degraded" response

**Gotchas:**
- Streaming: can't switch providers mid-stream
- Different providers have different tool support — validate `ChatOptions` compatibility
- Token counting differs per provider

**Sources:** Microsoft Learn, DevLeader, .NET Blog, Rick Strahl

### Topic 4: SignalR Notification Patterns

**Targeting:** `Clients.Client(connId)`, `Clients.User(userId)`, `Clients.Group(groupName)`

**Typed hubs:** `Hub<IApprovalHubClient>` for compile-time safety.

**Sending from outside hub:** Inject `IHubContext<ApprovalHub>` into background services.

**Critical pattern:** SignalR is notification layer only. Durable store (database) is source of truth. On reconnect, client queries REST API for missed approvals.

**Groups are volatile** — don't persist across restarts. Re-join on connect. Use Azure SignalR Service for scale-out.

**Anti-patterns:**
- Don't rely on SignalR for delivery guarantee
- Send IDs + metadata, not full payloads
- Client `.on()` handler params must match server `SendAsync` property names exactly

**Sources:** Microsoft Learn, Code Maze

### Testing Setup

Existing patterns: xUnit + Moq, Arrange-Act-Assert, `Mock.Of<IOptionsMonitor<T>>()` for config. Governance behavior tests mock `IGovernancePolicyEngine` and `IGovernanceAuditService`. Test naming: `MethodName_Scenario_ExpectedResult`.
