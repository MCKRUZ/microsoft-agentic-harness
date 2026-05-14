# Phase 3 Interview Transcript

## Q1: Drift detection baseline granularity

**Question:** For drift detection baselines, should we start with per-agent baselines, per-skill baselines, or per-task-type baselines? The spec mentions all three but the scope differs significantly.

**Answer:** All three levels — full hierarchy: agent > skill > task-type. Most complete but most complex.

## Q2: Learning scope and sharing

**Question:** For the Learnings Log cross-session memory, should corrections be scoped to individual agents, shared across all agents, or configurable per-tenant?

**Answer:** Hierarchical (agent < team < global). Three-tier scope — most flexible.

## Q3: Drift time-series storage backend

**Question:** What storage backend should drift time-series data use? The codebase has filesystem traces, in-memory stores, and graph backends.

**Answer:** Knowledge graph nodes. Store drift events as graph entities to enable relationship queries.

## Q4: Drift threshold strategy

**Question:** For drift threshold strategy, research shows SPC/CUSUM self-calibrates from data and avoids manual threshold tuning. Should we go with EWMA or CUSUM + EWMA combo?

**Answer:** EWMA only. Single approach, configurable lambda. Simpler to implement and reason about.

## Q5: Learning decay model

**Question:** For learning decay, research found Weibull-based adaptive decay is state-of-the-art but complex. A simpler 3-category approach with fixed shelf lives is more pragmatic. Which approach?

**Answer:** 3-category fixed decay. Volatile (days), Stable (months), Permanent (corrections never decay). Aligns with existing retention policies.

## Q6: Real-time drift events

**Question:** Should drift detection emit AG-UI SSE events for real-time dashboard updates, or is periodic polling sufficient for v1?

**Answer:** SSE events (like escalation). Real-time push to QualityHubPage. Consistent with Phase 2 patterns.

## Q7: Automatic drift response

**Question:** When drift is detected beyond the alert threshold, what should happen automatically?

**Answer:** Tiered: warn -> alert -> escalate. Three severity bands with different automatic responses. Most nuanced.

## Q8: Learnings API shape

**Question:** For the Remember/Recall/Forget/Improve API, should these be MediatR commands or direct service methods?

**Answer:** MediatR commands. RememberCommand, RecallQuery, ForgetCommand, ImproveLearningCommand. Pipeline behaviors for validation/audit. Consistent with codebase patterns.
