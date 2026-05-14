<!-- PROJECT_CONFIG
runtime: dotnet
test_command: dotnet test src/AgenticHarness.slnx
END_PROJECT_CONFIG -->

<!-- SECTION_MANIFEST
section-01-domain-escalation
section-02-domain-resilience
section-03-otel-conventions
section-04-config-and-validation
section-05-approval-strategies
section-06-escalation-interfaces
section-07-resilience-interfaces
section-08-escalation-service
section-09-audit-store
section-10-notification-adapters
section-11-polly-pipelines
section-12-resilient-chat-client
section-13-health-monitor
section-14-capability-registry
section-15-retry-queue
section-16-resilient-provider
section-17-governance-integration
section-18-agui-events
section-19-di-registration
section-20-appsettings-config
section-21-tests
END_MANIFEST -->

# Phase 2: Enterprise Trust — Implementation Sections

## Dependency Graph

| Section | Depends On | Blocks | Parallelizable |
|---------|------------|--------|----------------|
| section-01-domain-escalation | - | 03, 05, 06, 08 | Yes (with 02) |
| section-02-domain-resilience | - | 03, 07, 11, 12 | Yes (with 01) |
| section-03-otel-conventions | 01, 02 | 08, 11 | Yes (with 04) |
| section-04-config-and-validation | 01, 02 | 08, 11, 16, 19 | Yes (with 03) |
| section-05-approval-strategies | 01 | 08 | Yes (with 03, 04) |
| section-06-escalation-interfaces | 01 | 08, 09, 10 | Yes (with 03, 04, 05) |
| section-07-resilience-interfaces | 02 | 11, 12, 13, 16 | Yes (with 03, 04, 05, 06) |
| section-08-escalation-service | 03, 04, 05, 06 | 17 | No |
| section-09-audit-store | 06 | 19 | Yes (with 08) |
| section-10-notification-adapters | 06 | 18, 19 | Yes (with 08, 09) |
| section-11-polly-pipelines | 02, 03, 04, 07 | 12 | No |
| section-12-resilient-chat-client | 07, 11 | 16 | No |
| section-13-health-monitor | 07, 11 | 15, 16 | Yes (with 12) |
| section-14-capability-registry | 04, 07 | 12 | Yes (with 11) |
| section-15-retry-queue | 07, 13 | 19 | No |
| section-16-resilient-provider | 04, 07, 12, 13, 14 | 17 | No |
| section-17-governance-integration | 08, 16 | 19 | No |
| section-18-agui-events | 01, 10 | 19 | Yes (with 17) |
| section-19-di-registration | 08, 09, 10, 15, 16, 17, 18 | 20 | No |
| section-20-appsettings-config | 04, 19 | 21 | No |
| section-21-tests | all | - | No |

## Execution Order (Batches)

1. **Batch 1** (parallel): section-01-domain-escalation, section-02-domain-resilience
2. **Batch 2** (parallel): section-03-otel-conventions, section-04-config-and-validation
3. **Batch 3** (parallel): section-05-approval-strategies, section-06-escalation-interfaces, section-07-resilience-interfaces
4. **Batch 4** (parallel): section-08-escalation-service, section-09-audit-store, section-10-notification-adapters, section-11-polly-pipelines, section-14-capability-registry
5. **Batch 5** (parallel): section-12-resilient-chat-client, section-13-health-monitor
6. **Batch 6** (parallel): section-15-retry-queue, section-16-resilient-provider
7. **Batch 7** (parallel): section-17-governance-integration, section-18-agui-events
8. **Batch 8** (sequential): section-19-di-registration
9. **Batch 9** (sequential): section-20-appsettings-config
10. **Batch 10** (sequential): section-21-tests

## Section Summaries

### section-01-domain-escalation
Domain records: EscalationRequest, EscalationOutcome, ApproverDecision, EscalationAuditRecord, enums (Priority, WaitBehavior, TimeoutAction, ResolutionType, StrategyType, AuditRecordType), ApprovalEvaluation. All in Domain.AI/Escalation/.

### section-02-domain-resilience
Domain records: ProviderHealthState, FallbackMetadata, ProviderExhaustedException. All in Domain.AI/Resilience/.

### section-03-otel-conventions
EscalationConventions and ResilienceConventions static classes in Domain.AI/Telemetry/Conventions/. EscalationMetrics and ResilienceMetrics in Application.AI.Common/OpenTelemetry/Metrics/.

### section-04-config-and-validation
EscalationConfig, EscalationPriorityConfig in Domain.Common/Config/AI/Governance/. ResilienceConfig, FallbackProviderConfig, ProviderCapabilitiesConfig, CircuitBreakerConfig, RetryConfig, TimeoutConfig, DegradedModeConfig in Domain.Common/Config/AI/Resilience/. FluentValidation validators for both config classes. Add Escalation property to GovernanceConfig, Resilience property to AIConfig.

### section-05-approval-strategies
IApprovalStrategy interface and AnyOfApprovalStrategy, AllOfApprovalStrategy, QuorumApprovalStrategy implementations.

### section-06-escalation-interfaces
IEscalationService, IEscalationNotifier, IEscalationNotificationChannel, IEscalationAuditStore interfaces.

### section-07-resilience-interfaces
IResilientChatClientProvider, IProviderHealthMonitor interfaces.

### section-08-escalation-service
DefaultEscalationService implementation with in-memory state, timeout racing, cancellation propagation, strategy evaluation, notification dispatch, audit logging.

### section-09-audit-store
JsonlEscalationAuditStore implementation — append-only JSONL with file locking, following JsonlDelegationStore pattern.

### section-10-notification-adapters
CompositeEscalationNotifier (implements IEscalationNotifier, injects IEscalationNotificationChannel[]), NoOpSlackNotifier, NoOpTeamsNotifier.

### section-11-polly-pipelines
ProviderResiliencePipelineBuilder — builds ResiliencePipeline<ChatResponse> per provider with retry + circuit breaker + timeout. Add Polly.Core package to Infrastructure.AI.

### section-12-resilient-chat-client
ResilientChatClient : IChatClient — provider iteration loop with per-provider resilience, streaming fallback, FallbackMetadata attachment.

### section-13-health-monitor
PollyProviderHealthMonitor — maps CircuitBreakerStateProvider to ProviderHealthState, exposes state change events.

### section-14-capability-registry
ProviderCapabilityRegistry — config-driven provider capability mapping, capability diffing for FallbackMetadata.DisabledCapabilities.

### section-15-retry-queue
LlmRetryQueue : IHostedService — in-memory queue with TTL, drain on circuit recovery, cancellation token checking.

### section-16-resilient-provider
ResilientChatClientProvider : IResilientChatClientProvider — composes ResilientChatClient from fallback chain config via IChatClientFactory.

### section-17-governance-integration
New RequireApproval branch in GovernancePolicyBehavior. Supervisor autonomy exceeded trigger. Wire EscalationWaitBehavior from tier policy. Update CreateApprovalRequestExecutor.

### section-18-agui-events
New AG-UI event types (ESCALATION_REQUESTED, ESCALATION_RESOLVED, ESCALATION_EXPIRING) + AgUiEscalationNotifier : IEscalationNotificationChannel.

### section-19-di-registration
Wire all services in Infrastructure.AI, Application.Core, and Presentation.AgentHub DependencyInjection.cs files. Conditional LlmRetryQueue registration.

### section-20-appsettings-config
Add Escalation and Resilience config sections to both appsettings.json files with FallbackProviderConfig entries.

### section-21-tests
Full test suite across Application.Core.Tests and Infrastructure.AI.Tests. Approval strategy tests, escalation service tests, resilience tests, config validation tests.
