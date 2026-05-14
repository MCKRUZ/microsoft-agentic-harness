# Phase 3: Quality Loop — Drift Detection + Learnings Log

## Context

This is Phase 3 of the 4-phase platform gaps roadmap for the Microsoft Agentic Harness. Phases 1 (Autonomy Tiers + Supervisor Agent) and Phase 2 (Human Escalation + Fallback Chains) are complete on `feat/agt-governance-integration` with PR #1 open.

The harness is a production-grade template for Microsoft Agent Framework agents with Clean Architecture, CQRS/MediatR, and keyed DI throughout.

## Goal

Close the feedback loop: agents should detect when their outputs degrade over time and learn from corrections across sessions. Two subsystems that feed each other — drift detection identifies quality regressions, learnings log captures what worked and amplifies it.

## Subsystem 1: Drift Detection (~65% remaining)

### What exists today
- `IEvaluationService` — interface for scoring agent outputs
- `ExecutionTraceStore` — persists execution traces
- `HarnessScores` — scoring model with quality dimensions
- `QualityHubPage.tsx` — frontend shell for quality metrics
- MetaHarness evaluation loop — runs agents against test cases

### What's missing
- **Baseline capture** — snapshot "known good" output quality per agent/skill/task type so we have something to compare against
- **Drift scoring** — compare current outputs against baselines using multiple dimensions (semantic similarity, structural conformance, factual consistency, tool usage patterns)
- **Threshold-based alerting** — configurable thresholds that trigger warnings or escalations when drift exceeds tolerance
- **Temporal tracking** — time-series storage of quality scores to detect gradual degradation, not just point-in-time failures
- **Integration with escalation** — when drift is detected beyond threshold, feed into the Phase 2 escalation system (human review of degraded outputs)
- **Dashboard integration** — wire drift metrics into QualityHubPage.tsx

## Subsystem 2: Learnings Log (~55% remaining)

### What exists today
- `GraphFeedbackStore` — EMA-weighted feedback on graph nodes
- `IFeedbackStore` — interface for persisting feedback
- `GraphSkillEffectivenessTracker` — tracks which skills perform well
- Knowledge graph infrastructure with entity/relationship storage

### What's missing
- **Feedback-weighted retrieval** — blend semantic relevance with historical feedback scores when ranking retrieval results (configurable `feedback_alpha` learning rate)
- **Cross-session memory improvement** — `Remember()` / `Recall()` / `Forget()` / `Improve()` operations that persist learnings across agent conversations
- **Correction capture** — when a human corrects an agent output (via escalation or direct feedback), store the correction as a learning with source provenance
- **Learning propagation** — when a learning applies to a skill or tool pattern, propagate the quality signal to related knowledge graph nodes
- **Decay and freshness** — learnings should decay over time if not reinforced, preventing stale corrections from dominating
- **Learning categories** — distinguish between factual corrections, style preferences, tool usage patterns, and domain-specific knowledge

## Cross-Cutting Concerns

- **Drift informs Learnings**: when drift is detected and corrected, the correction becomes a learning entry
- **Learnings reduce Drift**: high-confidence learnings adjust baselines, preventing false-positive drift alerts on intentional changes
- **AG-UI events**: both subsystems should emit SSE events for real-time frontend updates (reuse Phase 2 AG-UI patterns)
- **Observability**: OpenTelemetry traces and metrics for both subsystems
- **Configuration**: `appsettings.json` blocks for drift thresholds, learning rates, decay parameters
- **Testing**: maintain 80%+ coverage, TDD approach, integration tests with in-memory stores

## Architecture Constraints

- Clean Architecture layers: Domain models first, Application interfaces, Infrastructure implementations
- CQRS/MediatR for commands and queries
- Keyed DI for strategy registration (drift scorers, learning stores)
- FluentValidation on all DTOs
- Result<T> pattern for error handling
- Immutability: records, init-only properties, IReadOnlyList<T>

## Dependencies

- Phase 2 escalation system (for drift -> escalation integration)
- MetaHarness evaluation loop (foundation for drift scoring)
- Knowledge graph infrastructure (foundation for learnings storage)
- AG-UI SSE infrastructure (for real-time events)
