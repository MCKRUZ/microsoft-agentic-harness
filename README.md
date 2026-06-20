# Microsoft Agentic Harness

If you've ever used [Claude Code](https://claude.ai/claude-code) and wondered *"how does this thing actually work under the hood?"* — this project is an answer to that question, built on the Microsoft stack.

The Agentic Harness is a proof-of-concept that reconstructs the architecture behind modern AI coding agents: the skills system that decides what an agent knows, the tool system that decides what it can do, the context budget that decides how much it can hold in its head at once, the orchestration loop that ties it all together, and the meta-harness that automatically improves the agent's own configuration over time. It runs on .NET 10, uses Clean Architecture, and speaks the same protocols (MCP, A2A) that the broader agent ecosystem is converging on.

It's not a chatbot wrapper. It's the plumbing that makes agents feel intelligent.

---

## The Problem

Most "agent" implementations are a prompt, an API call, and a prayer. They work for demos. They fall apart when you need an agent that can:

- **Use tools safely** — not just call functions, but do so within a security sandbox where file access is restricted to explicitly allowed paths and every tool call flows through content safety middleware.
- **Manage its own context** — LLMs have finite context windows, and a naive agent that dumps everything into the prompt will drown before it gets useful. The agent needs to know what to load, when to load it, and when to let go.
- **Collaborate with other agents** — not through shared memory or global state, but through real protocols where agents discover each other, negotiate capabilities, and delegate work.
- **Be observable** — when an agent makes a bad decision three tool calls deep in a multi-turn conversation, you need traces, not guesswork.

This harness solves those problems with real engineering, not abstractions on top of abstractions.

---

## Learn How It Works

Six learning resources, depending on what you need:

- **[Developer Onboarding Guide](https://mckruz.github.io/microsoft-agentic-harness/)** — Step-by-step walkthrough of the codebase aimed at engineers who are forking this template to build something. 14 pages covering getting running, every config knob, the Clean Architecture layout, a full message-journey trace, the skills/tools/RAG/MCP systems, observability, the evaluation framework, and the SkillOpt-port skill-training loop. Read this if you're going to write code.

- **[Architecture Guide](https://mckruz.github.io/microsoft-agentic-harness/architecture/)** — Infrastructure playbook for deploying the harness on Azure. 7 pages covering the full Azure topology, compute and AI services (Container Apps, Azure OpenAI), data and retrieval infrastructure (AI Search, knowledge graph backends), networking and security (VNets, Entra ID, Key Vault), observability (OTel to Azure Monitor or Grafana), and operations with cost tiers from $50/month dev to $800+ production. Read this if you're planning a deployment.

- **[Security Guide](https://mckruz.github.io/microsoft-agentic-harness/security/)** — Every runtime security protocol baked into the harness, explained plainly in the same style as the other guides. 11 pages organized as seven defense-in-depth layers plus an assurance chapter: the threat model, identity &amp; access (JWT/Entra, A2A mTLS, roles, CORS, rate limiting), autonomy &amp; governance (tiers, human-approval escalation), tool permissions (the Deny→Ask→Allow resolver, bypass-immune `DeniedTools`), sandbox &amp; execution (Job Objects, capability model, HMAC attestation), egress &amp; SSRF defense, content safety &amp; prompt-injection, data protection (multi-tenant isolation, provenance, right-to-erasure), and the OWASP Agentic Top-10 eval gate — each protocol cited to its real class. Read this if you need to understand, configure, or extend how the harness stays safe.

- **[Patterns & Technologies Reference](https://mckruz.github.io/microsoft-agentic-harness/reference/patterns-and-technologies.html)** — Exhaustive catalogue of every architectural pattern, AI/RAG subsystem, governance behaviour, framework, and dependency the harness ships with — cross-linked to source paths. 11 sections covering the CQRS pipeline (all 14 MediatR behaviours in order), Result&lt;T&gt;, factories, keyed-DI strategies, the skills/plugin system, the full RAG pipeline (3 chunkers, RAPTOR, hybrid + RRF, CRAG, multi-source orchestration), four knowledge-graph backends, drift/learnings/escalation governance, the DAG plan executor, sandbox + HMAC attestation, the frontend stack, and the full NuGet/npm inventory. Read this when you know what you're looking for and just need the index.

- **[Inside the Agentic Harness — Interactive Course](https://mckruz.github.io/microsoft-agentic-harness/agentic-harness-course/)** — A visual, scroll-based course that teaches how the harness works through animated diagrams, plain-English code translations, and interactive quizzes. No coding background required. Read this if you're trying to understand what the system *does* conceptually. (Local copy: `documentation/agentic-harness-course/index.html`.)

- **[Understand Anything — visual codebase map](https://github.com/Egonex-AI/Understand-Anything)** *(optional third-party tool)* — A Claude Code plugin that turns the codebase into an interactive knowledge-graph dashboard: files, classes, and functions become clickable nodes, automatically grouped into architectural layers, with an auto-generated guided tour that walks a newcomer through the system in dependency order. Install with `/plugin marketplace add Egonex-AI/Understand-Anything` then `/plugin install understand-anything`; run `/understand` to build the graph and `/understand-dashboard` to explore it (requires Node&nbsp;&ge;&nbsp;22 and pnpm). Two honest caveats for *this* codebase: **(1)** a full-repo `/understand` is a heavy multi-agent run — on the order of tens of millions of tokens on a tree this size — so scope it to a subdirectory (e.g. `/understand src/Content/Infrastructure/Infrastructure.AI.RAG`) unless you mean it; **(2)** its relationship edges are inferred by the LLM rather than statically resolved, because the plugin's deterministic backbone is path-based imports and C# uses namespace `using` — so the **layers and the guided tour are the payoff here, not the call graph**. Run this if you want to *see* the architecture and onboard visually rather than read about it. Generated output lands in a gitignored `.understand-anything/` and is never committed.

---

## How It Works

### The Orchestration Loop

At its core, the harness runs a conversation loop. A user sends a message. The agent processes it, decides whether to respond directly or use a tool, executes any tool calls, and feeds the results back into the next turn. This repeats until the agent has an answer or hits a configurable turn limit.

This sounds simple, but the devil is in the execution. Every turn flows through a CQRS pipeline:

```
Request --> Validation --> Caching --> Performance Logging --> Tool Output Compression --> Handler --> Response
```

Validation catches malformed requests before they reach the LLM. Performance logging flags turns that take too long. Tool output compression detects content type (JSON, structured data, free text) and applies strategy-specific compression — array pruning, deduplication, sentence-boundary truncation — with an LLM fallback for content that exceeds token thresholds. The handler itself is where the actual AI interaction happens — sending messages to Azure OpenAI (or AI Foundry, or a Semantic Kernel backend), processing tool calls, and managing conversation state.

Three commands drive everything:

- **ExecuteAgentTurn** handles a single turn — one round of messages in, tool calls out, results back.
- **RunConversation** wraps the full loop — calling ExecuteAgentTurn repeatedly until the agent says it's done.
- **RunOrchestratedTask** is where it gets interesting — an orchestrator agent decomposes a complex task into subtasks, spins up sub-agents for each one, runs their conversations in parallel, and synthesizes the results.
- **RunHarnessOptimization** runs the meta-harness outer loop — proposing changes to skill files, evaluating them against a benchmark, and persisting the best candidates.

### Skills: Teaching Agents What They Know

The skills system is inspired directly by how Claude Code loads context. The insight is that an agent doesn't need to know everything all the time — it needs the right knowledge at the right moment, and it needs to stay within its token budget.

Skills use a three-tier progressive disclosure model:

**Tier 1 — The Index Card (~100 tokens).** A name, a description, a few tags. This is all the agent sees at startup. Enough to know the skill exists and decide whether it's relevant. Every skill in the system is loaded at this tier — the overhead is negligible.

**Tier 2 — The Folder (~5,000 tokens).** Full instructions, tool declarations, behavioral guidelines. When the agent selects a skill, Tier 2 loads into context. This is where the skill becomes useful — the agent now knows *how* to use it, not just *that* it exists.

**Tier 3 — The Filing Cabinet (unbounded).** Scripts, reference documents, templates, examples. Only loaded when the skill is actively executing. This is the heavy context that would blow the budget if loaded eagerly.

Skills are declared in `SKILL.md` files — plain Markdown that humans can read and edit without touching code. An orchestrator agent's skill file describes how to decompose tasks. A research agent's describes how to find and analyze information. Drop a new `SKILL.md` into the skills directory and the agent picks it up at runtime.

The `IContextBudgetTracker` watches over all of this, tracking how many tokens are allocated to the system prompt, loaded skills, tool schemas, and conversation history. When budget runs low, the `ITieredContextAssembler` knows to stop loading Tier 2 content and fall back to Index Cards.

**Multi-Skill Agents.** An agent isn't limited to a single skill. `AgentDefinition.Skills` accepts a list — instructions from all active skills are merged into the system prompt, and their tool declarations are combined with namespace prefixes to avoid collisions. Each skill can define an `AllowedTools` whitelist to restrict which tools it exposes to the agent, keeping the tool surface intentional rather than open-ended.

**Skill Prerequisites.** Skills can declare execution ordering through `prerequisites` (a list of skill IDs) and a `completion_tool` in the SKILL.md frontmatter. The `SkillPrerequisiteMiddleware` — a `DelegatingChatClient` — enforces this ordering per-turn: if skill B depends on skill A, the agent cannot invoke skill B's tools until skill A's completion tool has been called. A conversation-scoped `ISkillCompletionTracker` tracks which skills have completed, and cycle detection via topological sort prevents circular dependency declarations from deadlocking the agent.

### Tools: What Agents Can Do

Tools are the agent's hands. The harness treats them as first-class citizens with their own lifecycle:

Tools are registered in the DI container with string keys — `"file_system"`, `"calculation_engine"`, etc. They're not eagerly loaded. When a skill declares that it needs the `file_system` tool, the harness resolves it from the container at that moment. This keeps the tool surface dynamic: different skills can offer different tools to the same underlying agent.

Every internal `ITool` implementation gets converted to the `AITool` contract that Microsoft.Extensions.AI expects. The `AIToolConverter` handles this bridge — reading the tool's schema, generating the JSON function-calling definition, and wiring up the execution callback. From the LLM's perspective, it's just a function it can call. From the harness's perspective, it's a sandboxed, audited, type-safe operation.

The `FileSystemService` is a good example of why this matters. It implements file operations — read, write, list — but only within explicitly allowed base paths. Any attempt to traverse outside those paths is caught and rejected. The agent thinks it has a file system. It actually has a cage.

### MCP: Extending the Tool Surface

The Model Context Protocol is how the harness connects to the outside world. It works in both directions.

As an **MCP Server**, the harness exposes its tools, prompts, and resources over HTTP with JWT Bearer authentication. External systems — other agents, IDEs, automation pipelines — can discover and invoke these capabilities through a standardized protocol. Rate limiting protects against abuse.

As an **MCP Client**, the harness discovers tools hosted on external MCP servers and converts them into native `AITool` instances. This means an agent can seamlessly use tools from third-party services alongside its built-in capabilities. The agent doesn't know or care where a tool lives — it just calls it.

### A2A: Agents Talking to Agents

The Agent-to-Agent protocol handles distributed collaboration. Each agent publishes an Agent Card — a JSON document describing its name, capabilities, and endpoint URL — at a well-known location (`/.well-known/agent.json`). When the orchestrator needs to delegate work, it discovers available agents by querying these endpoints, selects the right one for the job, and sends it a task over HTTP.

This is how the multi-agent orchestration actually works in practice. The orchestrator doesn't have hardcoded knowledge of its sub-agents. It discovers them, reads their capabilities, and makes routing decisions dynamically.

### Plugins: Extending the Skill Surface

The plugin system lets you extend the harness without modifying core code. Declare plugins in `appsettings.json` pointing at local directories, and the harness handles the rest.

Each plugin directory contains a `plugin.json` manifest describing the skills it provides, any MCP servers it exposes, and its governance constraints. At startup, `PluginManifestReader` reads and validates the manifest, `PluginLoader` wires the declared skills and MCP servers into the harness, and `PluginRegistry` tracks loaded state for runtime introspection. Path containment checks prevent directory traversal — a plugin can't escape its declared root.

Skills operate in one of two modes. **Managed** skills are harness-native: they declare explicit tool dependencies, and the harness resolves them through keyed DI like any built-in skill. **Injected** skills come from plugins and pass all their MCP tools straight through — the harness doesn't curate the tool surface, it just forwards what the plugin provides. The `SkillMode` (a computed property on `SkillDefinition`) determines which path a skill takes.

Plugin-boundary governance adds two layers of control. At **provisioning time**, each `PluginDeclaration` can specify `AllowedTools` and `DeniedTools` lists, filtering the tool surface before the agent ever sees it. At **execution time**, `PluginPermissionRuleProvider` emits `ToolPermissionRule` entries into the three-phase permission resolver based on the plugin's declared `AutonomyLevel`. Deny always wins over allow, and denied tools are bypass-immune — no escalation path can override them.

### Observability: Seeing Inside the Black Box

LLM-powered agents are notoriously hard to debug. A conversation that goes sideways might involve dozens of turns, multiple tool calls, and branching logic that's invisible from the outside.

The harness instruments everything with OpenTelemetry. Every agent turn creates a span. Every tool call creates a child span. Conversation IDs, turn indices, and agent names are tagged automatically by a custom span processor that understands AI workloads — it recognizes spans from Microsoft.Extensions.AI, Semantic Kernel, and Azure.AI.OpenAI and enriches them with agentic context.

Traces flow to Jaeger for visualization. Metrics flow to Prometheus. Logs flow through structured JSON logging. When something goes wrong, you don't guess — you trace the exact path the agent took, see what tools it called, and read the token counts at each step.

### Meta-Harness: Self-Improving Agent Configuration

Research shows that harness choice alone — what to store, retrieve, and show to the model — can cause **6x performance gaps** on identical benchmarks, bigger than the difference between model versions. The meta-harness automates what would otherwise be manual tuning: it watches how the agent fails, proposes targeted changes to the skill files that guide it, tests those changes, and keeps the best ones.

The loop runs in five steps per iteration:

1. **Snapshot** — capture the current state of all skill files as a baseline
2. **Propose** — a coding agent reads recent execution traces and the accumulated learnings log, reasons about why failures occurred, and outputs a structured proposal: which skill files to change, how, and what it observed
3. **Evaluate** — run the agent against a benchmark of eval tasks under the proposed skill files and score it by regex match against expected outputs
4. **Regression gate** — before accepting a new best, verify the candidate doesn't regress on tasks that prior winners already solved; if it does, the candidate is rejected and the previous best is kept
5. **Record** — if the score improved and the regression gate passed, promote this candidate as the new best and update the regression suite; otherwise discard it

Three mechanisms keep the loop efficient:

**Self-maintained regression suite.** The first accepted candidate seeds a `regression_suite.json` with every task it passes. Each subsequent winner must pass at least `RegressionSuiteThreshold` (default 80%) of those pinned tasks before being promoted. Tasks that were previously failing and now pass get added to the suite automatically, so the bar continuously rises.

**Persistent learnings log.** After every iteration — successful or failed — the proposer's observations are appended to a `learnings.md` file in the run directory. The next iteration receives this log as context, so the proposer doesn't re-attempt hypotheses that already failed and can build on patterns that worked.

**Consecutive no-improvement early stop.** When N iterations in a row produce no new accepted best (configurable via `ConsecutiveNoImprovementLimit`, default 5), the loop exits rather than burning through remaining iterations. Failed proposer calls and regression-gated rejections both count toward this limit.

The proposer uses a **causal trace** rather than just pass/fail results. OpenTelemetry spans are attributed back to their root causes — so the proposer knows not just that turn 7 failed, but which tool call three turns earlier set it up for failure. This is what makes the proposals targeted rather than random.

Traces are stored as **grep-friendly JSONL files** in `.meta-harness/` rather than a database — because the proposer agent needs to search them with text patterns, exactly as a developer would. Secrets are stripped before any trace touches disk via `PatternSecretRedactor`.

Eval tasks are simple JSON files you drop in `eval/tasks/`:

```json
{ "taskId": "task-01", "prompt": "Write a haiku about recursion.", "expectedPattern": "(?i)(recursion|itself|loop)", "maxPoints": 1.0 }
```

After a run, the winning candidate's skill files land in `.meta-harness/optimizations/{run-id}/_proposed/`. Promoting them is a single copy command.

### Skill Training: Optimizing One Skill Document Like Neural-Net Weights

Where Meta-Harness rewrites whole skill files based on causal traces, **Skill Training** optimizes a single skill document via bounded gradient-style edits — a port of [microsoft/SkillOpt](https://github.com/microsoft/SkillOpt) into the harness. The skill document is the trainable state of a frozen agent: an optimizer model proposes structured `Append | InsertAfter | Replace | Delete` edits against it, candidates are scored on a validation split, and only edits that strictly improve the gate score are accepted.

The training loop runs six stages per step inside `TrainSkillCommandHandler`:

1. **Rollout** — run the current skill against a train batch via `IRolloutRunner`.
2. **Reflect** — `IPatchProposer` (the "optimizer" agent) reads the trajectories and emits a `Patch` of proposed edits.
3. **Aggregate** — `IPatchAggregator` deduplicates equivalent edits across rollouts and sums their `SupportCount`.
4. **Select** — `IEditSelector` clips to the LR budget; `CosineScheduler` / `LinearScheduler` / `ConstantScheduler` decay it over training.
5. **Apply** — the pure `PatchApplier` walks the patch over the markdown and returns a `PatchApplyReport` (which edits landed, which failed).
6. **Gate** — `GateEvaluator` rolls out the candidate on the val split, projects `(hard, soft)` onto the configured `GateMetric` (`Hard | Soft | Mixed`), and decides `AcceptNewBest | Accept | Reject`. Ties Reject.

Two epoch-boundary mechanisms run between epochs:

**SlowUpdate** does a paired longitudinal comparison of the prior-epoch skill's val rollouts vs the current skill's, classifying each item into improved/regressed/persistent_fail/stable_success and synthesizing guidance for the next epoch — the same anti-forgetting mechanism SkillOpt uses to detect catastrophic regression.

**MetaSkillUpdate** accumulates a cross-epoch strategy memory through `IKnowledgeMemory.RememberAsync` under a stable key (`skill-training/meta/{skillId}/{runId}`) and re-injects it as context on subsequent reflection steps.

Operational invariants worth knowing:
- `Result.Fail` codes are stable, scrubbed strings (`skill_training.reflect.proposer_call_failed`, `…persist_failed`) — raw exception text only flows to structured logs, never into orchestrator surfaces.
- `Replace` with empty `Content` is rejected as a failed edit — the optimizer must use `Delete` explicitly. This keeps the audit trail honest about what the LLM actually intended.
- `HasChanges` on `PatchApplyReport` is computed against content equality, not applied-edit count — identity no-ops don't trigger spurious gating or checkpoints.
- Skill content is capped at 256KB per field (`MaxSkillLength`); gate scores are finite-checked to fail fast on NaN aggregations.
- `IPatchProposer` and `IRolloutRunner` ship with `NotConfigured*` fail-fast defaults — template consumers replace them with agent-backed Infrastructure implementations before invoking `TrainSkillCommand`.

`InMemorySkillTrainingCheckpointStore` keeps the best + most-recent 64 checkpoints per run; durable EF Core storage is the natural next step. `Presentation.ConsoleUI/Examples/SkillTrainingExample.cs` wires deterministic stubs and runs the full loop without engaging any LLM, useful for verifying the state machine and as a starting point for real proposer + runner integration.

### Planner: DAG-Based Task Decomposition

The orchestrator can decompose a task into sub-agents, but those sub-agents still work in a flat loop. The planner adds structure: it turns a high-level goal into a directed acyclic graph (DAG) where each node is an executable step with explicit dependencies, and the executor runs them with bounded concurrency while respecting those edges.

Five step types cover the common patterns:

- **LLM Call** — send a prompt to a model, receive a completion
- **Tool Use** — invoke a harness tool inside the sandbox
- **Human Gate** — pause execution until a human approves, with configurable timeout and auto-escalation
- **Conditional Branch** — evaluate a condition against prior step outputs and route execution accordingly
- **Sub-Plan Invocation** — embed an entire plan as a step, enabling recursive decomposition

Each step declares its own error recovery strategy: **Retry** (with configurable backoff — fixed, linear, or exponential), **Fail Step** (mark failed, continue the plan around it), **Escalate** (promote to human gate), or **Skip & Continue** (treat as successful with empty output). The executor applies these automatically — a transient LLM timeout retries three times before failing, while a human gate escalation pauses the plan and notifies the UI.

Plans are fully persistent. The `EfCorePlanStateStore` checkpoints every step transition to SQLite via EF Core, so an interrupted plan — whether from a crash, a deployment, or a deliberate pause — resumes from the last completed step. Individual steps can be cancelled and retried at runtime through `CancelPlanCommand` and `RetryPlanStepCommand`.

The planner also generates plans from natural language. `GeneratePlanCommand` sends a goal description to an LLM with schema constraints, and the `LlmPlanGeneratorService` parses the structured output into a validated `PlanGraph`. The validator enforces DAG properties (no cycles), checks that all edge targets exist, and ensures every step has a valid configuration for its type.

### Sandbox: Isolated Tool Execution

Tools run inside the orchestration loop, which means a misbehaving tool could read files it shouldn't, spawn processes, or consume unbounded resources. The sandbox prevents this by enforcing isolation at execution time.

Two isolation levels are available:

**Process isolation** uses Windows Job Objects to constrain a tool's spawned process. The `WindowsProcessResourceLimiter` sets hard limits on memory, CPU time, and child process count. The tool runs in its own process but cannot exceed its resource envelope. On non-Windows platforms, a no-op limiter allows the same code paths to run without OS-specific APIs.

**Container isolation** runs the tool inside a Docker container with a read-only root filesystem. The `DockerSandboxExecutor` builds a container from a configurable base image, mounts only the paths the tool's capability profile allows, and tears it down after execution. Network access is blocked unless the tool's profile explicitly grants `NetworkAccess`.

Security is closed-by-default. Every tool must declare the capabilities it needs through the `ToolCapabilityAttribute`: `FileRead`, `FileWrite`, `NetworkAccess`, `ProcessSpawn`. A `ToolPermissionProfile` maps these declarations to concrete mount paths and resource limits. If a tool doesn't declare `FileWrite`, it gets read-only mounts — even if the underlying filesystem is writable.

**HMAC attestation** ensures tamper evidence. After every sandboxed execution, the `HmacAttestationService` produces a `ToolExecutionAttestation` containing the tool name, a SHA-256 hash of the input, a SHA-256 hash of the output, a timestamp, and an HMAC signature over the entire record. Attestations are persisted through the `EfCoreAttestationStore`. Any downstream consumer can verify that a tool's output hasn't been modified since execution.

### AG-UI Protocol Events

The harness streams real-time progress to the WebUI through SignalR using the AG-UI event protocol. The `AgUiEventWriter` emits strongly-typed events that the React frontend consumes for live visualization of agent activity.

34 event types span 8 categories: **Run lifecycle** (start, finish, error, step boundaries), **Streaming** (text message deltas), **Tool calls** (start, arguments, end, result), **State** (snapshots and JSON-Patch deltas), **Escalation** (requested, resolved, expiring), **Drift** (warn, alert, escalate, resolved), **Learning** (captured, applied, forgotten), and **Plan** (plan start, step start, step complete, state delta, sandbox status, plan complete, plan failed).

Plan events are the Phase 4 additions. When a plan starts executing, the frontend receives `PLAN_STARTED` with the full DAG structure. As each step runs, `PLAN_STEP_STARTED` and `PLAN_STEP_COMPLETED` fire with status, duration, and error details. `PLAN_STATE_DELTA` streams incremental updates for long-running steps. `SANDBOX_STATUS` reports resource usage and attestation hashes for tool execution steps. `PLAN_COMPLETED` and `PLAN_FAILED` signal terminal states.

### Governance & Quality Loop

Production agents need guardrails that go beyond content safety filters. The harness implements five interconnected governance subsystems that monitor agent behavior, enforce autonomy boundaries, and feed quality signals back into the system.

**Drift Detection** uses EWMA (Exponentially Weighted Moving Average) scoring to monitor agent output quality against established baselines. When quality metrics drift beyond configurable thresholds, the system escalates through three severity levels — warn, alert, escalate — each triggering appropriate notifications via the AG-UI event stream. Baselines and audit records persist to graph or JSONL stores, and a `DriftEscalationBridge` connects drift alerts directly to the escalation system.

**Learnings System** captures knowledge from agent interactions with configurable decay. `LearningEntry` records flow through CQRS commands — `Remember`, `Recall`, `Forget`, `Improve` — and are classified into decay tiers: `CRITICAL` (never expires), `STANDARD` (gradual decay), and `EPHEMERAL` (fast decay). A `LearningsPruningBackgroundService` runs scheduled cleanup, and a `LearningsDriftBridge` feeds learning quality signals into drift scoring. Persistence backends include graph-backed and in-memory stores.

**Escalation System** handles multi-approval workflows with pluggable strategies: `AllOf` (unanimous), `AnyOf` (first responder), and `Quorum` (majority). Each `EscalationRequest` carries priority, risk level, and timeout actions. A `JsonlEscalationAuditStore` provides compliance trails, and the `AgUiEscalationNotifier` streams escalation events to the UI for real-time human-in-the-loop interactions.

**Autonomy Tiers** enforce permission boundaries at three levels — `Manual`, `Supervised`, and `Autonomous` — with tier policies defined in configuration. A `GovernancePolicyBehavior` intercepts every MediatR command to enforce the current autonomy level. Response sanitizers (`CredentialRedactor`, `ExfiltrationUrlDetector`, `ResponseInjectionScrubber`) scrub agent output before it reaches users. MCP security scanning and prompt injection detection protect the tool boundary.

**Resilience & Circuit Breaker** uses Polly-based circuit breakers with retry queues and provider fallback chains. Each provider maintains a health state (`Healthy`, `Degraded`, `Unhealthy`, `Exhausted`), and a capability registry enables automatic failover to alternative providers when the primary degrades.

**Plugin-Boundary Governance** enforces security constraints on externally-loaded plugins. `PluginPermissionRuleProvider` reads `AllowedTools`/`DeniedTools` from each `PluginDeclaration` and emits `ToolPermissionRule` entries into the three-phase permission resolver. The plugin's `AutonomyLevel` (a string in config, parsed to enum at the Application layer) maps to permission behavior — a `Manual` plugin requires human approval for every tool call, while an `Autonomous` plugin executes freely within its allowed tool set. Deny rules are bypass-immune: once a tool is denied at the plugin boundary, no escalation or override can grant access.

All six systems emit OpenTelemetry metrics and are wired into the AG-UI event stream for live frontend visualization.

### Knowledge Graph

The knowledge graph subsystem (`Infrastructure.AI.KnowledgeGraph`) provides structured entity and relationship storage with production-grade backends, cross-session memory, and compliance-aware data lifecycle management.

**Graph Storage** supports three backends — in-memory (development), Neo4j, and PostgreSQL — behind the `IGraphDatabaseBackend` interface. Entity extraction produces `GraphNode` and `GraphEdge` instances organized into `GraphTriplet` records. Hierarchical community detection uses the Leiden algorithm (`ICommunityDetector`) to cluster related entities at configurable granularity levels.

**Cross-Session Memory** implements `Remember()`/`Recall()`/`Forget()`/`Improve()` operations through the `IKnowledgeMemory` interface. A session-local `InMemorySessionCache` provides fast reads with background sync to the permanent graph store. Agents learn across conversations — knowledge captured in one session is available in the next via `ICrossSessionMemoryStore`.

**Feedback-Weighted Search** tracks retrieval quality scores on graph nodes and edges through `IFeedbackStore`. An `LlmFeedbackDetector` automatically identifies positive and negative signals from agent interactions. Future retrievals blend semantic relevance with historical feedback weights, so frequently useful paths rank higher.

**Provenance & Compliance** stamps every extracted node/edge with source pipeline, task, and timestamp via `IProvenanceStamper`. Retention policies (`IRetentionPolicyProvider`) define per-data-class lifetimes. The `DefaultErasureOrchestrator` handles right-to-erasure requests with verifiable `ErasureReceipt` records. A `ComplianceAwareGraphStore` decorator enforces retention and audit policies transparently. All mutations flow through `IMemoryAuditSink` for compliance trails.

**Multi-Tenant Isolation** via `TenantIsolatedGraphStore` enforces scope boundaries (user → dataset → owner) with permission-checked access through `IKnowledgeScopeValidator`. Multiple agents and users can share knowledge infrastructure while maintaining strict data isolation.

### Agentic RAG Pipeline

The RAG pipeline (`Infrastructure.AI.RAG`) has evolved through four phases into a fully autonomous retrieval system that adapts its strategy based on query complexity, decomposes multi-part questions, maintains knowledge across sessions, and orchestrates multiple sources in parallel.

**Phase A — Adaptive Complexity Routing** classifies incoming queries by complexity using an LLM-based few-shot classifier (`IQueryComplexityClassifier`). A `RetrievalDecisionGate` routes each query to the appropriate retrieval tier: simple queries skip expensive operations like reranking and CRAG evaluation, while complex queries get the full pipeline. Configuration via `ComplexityRoutingConfig` targets 30-50% cost reduction on mixed workloads.

**Phase B — Multi-Hop Retrieval & Faithfulness** handles questions that require synthesizing information from multiple documents. The `IQueryDecomposer` breaks complex questions into sub-queries, and the `IIterativeRetriever` executes retrieval rounds until an `ISufficiencyEvaluator` determines enough evidence has been gathered. An `IAnswerFaithfulnessEvaluator` validates that generated answers are grounded in retrieved evidence, flagging hallucinated claims.

**Phase C — Production Graph & Memory** replaces the teaching-stub GraphRAG service with production backends (Neo4j, Kuzu, PostgreSQL). Entity extraction feeds into the knowledge graph with Leiden community detection. Cross-session memory enables agents to learn across conversations. See the Knowledge Graph section above for details.

**Phase D — Full Autonomy** introduces multi-source orchestration and quality gates. The `MultiSourceOrchestrator` executes retrieval across vector stores, BM25 indexes, and knowledge graphs in parallel, merging results via configurable fusion strategies. A `RetrievalCostTracker` monitors per-query costs against budgets. Quality gates evaluate retrieval quality at each stage, and the orchestrator can autonomously decide to retry, expand, or escalate based on quality signals.

The full pipeline flows through the `RagOrchestrator`, which coordinates all four phases: classify → route → transform → retrieve (single-hop or multi-hop) → rerank → evaluate → expand → assemble. See `docs/rag/README.md` for the complete architecture.

---

## Architecture

The project follows Clean Architecture with strict dependency inversion. Each layer depends only inward — Infrastructure implements Application interfaces, never the reverse.

```
┌─────────────────────────────────────────────────────────────────────┐
│                        Presentation Layer                          │
│  ConsoleUI (Spectre.Console)  ·  LoggerUI  ·  AgentHub (SignalR)  │
│  WebUI (React + TypeScript)   ·  MCP Server  ·  AG-UI Events      │
├─────────────────────────────────────────────────────────────────────┤
│                        Application Layer                           │
│  CQRS Commands/Handlers  ·  Agent Factories  ·  Context Budget    │
│  Skill Loader  ·  Tool Converter  ·  FluentValidation Pipeline    │
│  Plugin Interfaces  ·  Skill Prerequisites  ·  Compression        │
├─────────────────────────────────────────────────────────────────────┤
│                       Infrastructure Layer                         │
│  Azure OpenAI / AI Foundry  ·  MCP Client  ·  Connectors          │
│  Planner (DAG executor)  ·  Sandbox (process/container isolation)  │
│  Observability (OTel)  ·  State Management  ·  API Access          │
│  RAG (hybrid retrieval, CRAG, complexity routing, multi-hop)       │
│  Knowledge Graph (Neo4j/PostgreSQL, Leiden, cross-session memory)  │
│  Governance (drift, escalation, autonomy, resilience, learnings)   │
│  Plugin System (loader, manifest reader, registry)                 │
│  Tool Output Compression (content detection, strategy dispatch)    │
├─────────────────────────────────────────────────────────────────────┤
│                          Domain Layer                              │
│  Agent Manifests  ·  Skill Definitions  ·  Tool Declarations      │
│  Plan Graphs  ·  Sandbox Capabilities  ·  Attestation Models      │
│  A2A Agent Cards  ·  Workflow State  ·  Configuration Hierarchy    │
│  Knowledge Graph Models  ·  Governance Policies  ·  RAG Models     │
│  Plugin Config  ·  Compression Models  ·  Skill Mode               │
└─────────────────────────────────────────────────────────────────────┘
```

### Solution Structure

```
src/
├── AgenticHarness.slnx
│
├── Content/Domain/
│   ├── Domain.Common/
│   │   ├── Config/AI/                  AppConfig, AIConfig, A2AConfig, AIFoundryConfig
│   │   ├── Config/AI/Plugins/          PluginDeclaration, PluginManifest, PluginsConfig
│   │   ├── Constants/                  ClaimConstants, PolicyNameConstants
│   │   └── Workflow/                   IStateManager, WorkflowState
│   └── Domain.AI/
│       ├── Agents/                     AgentManifest, AgentExecutionContext, SkillReference
│       ├── Skills/                     SkillDefinition, SkillMode (Managed/Injected)
│       ├── Tools/                      ToolDeclaration
│       ├── Compression/               ToolOutputCategory, CompressionResult
│       ├── A2A/                        AgentCard
│       ├── Planner/                    PlanGraph, PlanStep, PlanEdge, StepType, ErrorRecovery
│       ├── Sandbox/                    SandboxExecutionRequest, ToolCapability, ResourceLimits
│       ├── Attestation/               ToolExecutionAttestation
│       ├── RAG/                        DocumentChunk, RetrievalResult, QueryComplexity, Faithfulness
│       ├── KnowledgeGraph/             GraphNode, GraphEdge, Community, MemoryRecord, Provenance
│       ├── Governance/                 AutonomyLevel, AutonomyTierPolicy
│       ├── Orchestration/              AgentCandidate, AgentSelection, DelegationRecord
│       ├── SkillTraining/              Edit, Patch, GateResult, RolloutResult, SkillTrainingCheckpoint, TrainSkillConfig
│       └── Permissions/                SafetyGate
│
├── Content/Application/
│   ├── Application.Common/
│   │   ├── Behaviors/                  MediatR pipeline behaviors
│   │   ├── Extensions/                 Guard clauses, string helpers
│   │   ├── Factories/                  AzureCredentialFactory
│   │   └── Interfaces/                 Cross-cutting contracts
│   ├── Application.AI.Common/
│   │   ├── Factories/                  AgentFactory, AgentExecutionContextFactory
│   │   ├── Interfaces/                 IAgentFactory, IChatClientFactory, ISkillLoaderService
│   │   ├── Interfaces/Plugins/         IPluginLoader, IPluginManifestReader, IPluginRegistry
│   │   ├── Interfaces/Skills/          ISkillCompletionTracker
│   │   ├── Interfaces/Compression/     ICompressionStrategy, IToolOutputCompressor
│   │   ├── Middleware/                 SkillPrerequisiteMiddleware
│   │   ├── Interfaces/RAG/            IVectorStore, IReranker, IRagOrchestrator + 19 more
│   │   ├── Interfaces/KnowledgeGraph/  IKnowledgeGraphStore, IKnowledgeMemory, IFeedbackStore + 14 more
│   │   ├── Interfaces/Governance/      IAutonomyTierResolver, ISafetyGateRegistry
│   │   ├── Models/Context/             ContextModels (tier enums, budget types)
│   │   ├── OpenTelemetry/              RagIngestionMetrics, RagRetrievalMetrics
│   │   ├── Interfaces/SkillTraining/   IGateEvaluator, IPatchProposer, IPatchAggregator, IEditSelector, ILrScheduler, IRolloutRunner, ISkillTrainingCheckpointStore
│   │   ├── Services/SkillTraining/     PatchApplier, GateEvaluator, PatchAggregator, TopKEditSelector, schedulers (Cosine/Linear/Constant), InMemoryCheckpointStore, NotConfigured proposer/runner
│   │   ├── CQRS/SkillTraining/         TrainSkill, GateCandidateSkill, ReflectOnFailures, SlowUpdate, MetaSkillUpdate
│   │   └── Services/                   ContextBudgetTracker, TieredContextAssembler, AIToolConverter
│   └── Application.Core/
│       ├── Agents/Skills/              SKILL.md files per agent
│       ├── CQRS/Agents/               ExecuteAgentTurn, RunConversation, RunOrchestratedTask
│       ├── CQRS/MetaHarness/          RunHarnessOptimization (propose→evaluate outer loop)
│       ├── CQRS/Planner/             GeneratePlan, CreatePlan, ExecutePlan, CancelPlan, RetryPlanStep
│       ├── CQRS/RAG/                  IngestDocument, SearchDocuments (commands + handlers)
│       └── Workflows/                  KG ingestion, orchestration, and RAG search workflows
│
├── Content/Infrastructure/
│   ├── Infrastructure.Common/          Identity service, claim extensions
│   ├── Infrastructure.AI/
│   │   ├── Planner/                    PlanExecutor (4 partials), EfCorePlanStateStore, step executors
│   │   ├── Sandbox/                    ProcessSandboxExecutor, DockerSandboxExecutor, Job Objects
│   │   ├── Attestation/               HmacAttestationService, EfCoreAttestationStore
│   │   ├── Persistence/               PlannerDbContext, EF Core entities, SQLite migrations
│   │   ├── Plugins/                    PluginLoader, PluginManifestReader, PluginRegistry
│   │   ├── Compression/               ContentTypeDetector, strategies, ToolOutputCompressor
│   │   └── ...                        ChatClientFactory, A2AAgentHost, state management
│   ├── Infrastructure.AI.Connectors/   Unified external API adapters with ITool bridge
│   ├── Infrastructure.AI.Governance/   Autonomy tiers, response sanitizers, AGT adapters
│   ├── Infrastructure.AI.KnowledgeGraph/ Graph stores (Neo4j/PostgreSQL/in-memory), memory,
│   │                                     compliance, feedback, provenance, scoping
│   ├── Infrastructure.AI.MCP/          MCP client — discover and invoke remote tools
│   ├── Infrastructure.AI.MCPServer/    MCP server — expose tools/prompts/resources via HTTP
│   ├── Infrastructure.AI.RAG/          Full RAG pipeline — ingestion, retrieval, evaluation,
│   │   ├── Ingestion/                    parsers, chunkers, enricher, RAPTOR, embeddings
│   │   ├── Retrieval/                    vector stores, BM25, hybrid, rerankers, iterative
│   │   ├── QueryTransform/               classifier, RAG-Fusion, HyDE, complexity routing
│   │   ├── Evaluation/                   CRAG, sufficiency, faithfulness evaluators
│   │   ├── Assembly/                     pointer expansion, citation tracking
│   │   ├── GraphRag/                     Kuzu/ManagedCode backends, Leiden, cross-session memory
│   │   ├── Orchestration/                RagOrchestrator, multi-source, decision gate, cost tracker
│   │   └── CostControl/                 RagModelRouter (model tiering)
│   ├── Infrastructure.APIAccess/       HTTP resilience policies, security middleware
│   └── Infrastructure.Observability/   OTel pipeline, Prometheus, Jaeger, LLM span processor
│
└── Content/Presentation/
    ├── Presentation.Common/            DI composition root
    ├── Presentation.ConsoleUI/         Interactive menu + 6 runnable examples
    ├── Presentation.LoggerUI/          Named pipe log viewer
    ├── Presentation.AgentHub/          SignalR hub — real-time streaming to the WebUI
    │   ├── Auth/                       DevAuthHandler (dev bypass), Azure AD integration
    │   └── AgUi/                       AG-UI event protocol (34 event types, SSE streaming)
    └── Presentation.WebUI/             React + TypeScript SPA
        ├── src/features/chat/          Streaming chat panel with message history
        ├── src/features/telemetry/     Live span tree for observability
        ├── src/features/mcp/           MCP tool browser + direct invocation
        ├── src/stores/                 Zustand stores (chat, telemetry, app)
        ├── src/hooks/                  useAgentHub (SignalR), useAuth (MSAL)
        └── src/lib/                    Axios API client, MSAL auth config, dev bypass
```

### Dependency Flow

```
Domain.Common <--- Domain.AI
     ^                  ^
     |                  |
Application.Common <-- Application.AI.Common <-- Application.Core
     ^                  ^                              ^
     |                  |                              |
Infrastructure.*  ----->|<---- Infrastructure.AI ------'
                        |
Presentation.Common ----' (composition root — references all layers)
```

---

## Getting Started

### Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
- [Node.js 20+](https://nodejs.org/) (for the WebUI)
- An Azure OpenAI resource (or OpenAI API key)
- Optional: Azure AD app registration (for WebUI auth — see `docs/azure-ad-setup.md`)
- Optional: [Jaeger](https://www.jaegertracing.io/) for distributed tracing
- Optional: Azure AI Foundry project for persistent agents

### Clone & Build

```bash
git clone https://github.com/MCKRUZ/microsoft-agentic-harness.git
cd microsoft-agentic-harness
dotnet build src/AgenticHarness.slnx

# WebUI
cd src/Content/Presentation/Presentation.WebUI
npm install
```

### Configure

The harness uses a strongly-typed `AppConfig` hierarchy bound through the Options pattern. The defaults in `appsettings.json` get you started:

```json
{
  "AppConfig": {
    "Agent": {
      "MaxTurnsPerConversation": 10,
      "DefaultTokenBudget": 128000
    },
    "AI": {
      "AgentFramework": {
        "DefaultDeployment": "gpt-4o",
        "ClientType": "AzureOpenAI"
      },
      "Skills": {
        "BasePath": "skills"
      }
    },
    "Observability": {
      "EnableTracing": true,
      "EnableMetrics": true,
      "SamplingRatio": 1.0
    }
  }
}
```

Secrets go in User Secrets — never in config files:

```bash
cd src/Content/Presentation/Presentation.ConsoleUI
dotnet user-secrets set "AppConfig:AI:AgentFramework:Endpoint" "https://your-resource.openai.azure.com/"
dotnet user-secrets set "AppConfig:AI:AgentFramework:ApiKey" "your-api-key"
```

### Start Observability Infrastructure

The harness uses Docker containers for its observability backend. You need [Docker Desktop](https://www.docker.com/products/docker-desktop) installed and running.

```powershell
# Start everything (OTel Collector, Tempo, PostgreSQL, Prometheus, Grafana)
.\scripts\start-infrastructure.ps1

# Verify containers are running
docker ps --format "table {{.Names}}\t{{.Status}}\t{{.Ports}}"
```

This brings up:

| Service | URL | Credentials |
|---------|-----|-------------|
| PostgreSQL | `localhost:5432` | `observability` / `observability` |
| Prometheus | http://localhost:9090 | — |
| Grafana | http://localhost:3000 | `admin` / `admin` |
| OTLP gRPC | `localhost:4317` | — |
| OTLP HTTP | `localhost:4318` | — |
| Tempo (traces) | http://localhost:3200 | — |

The PostgreSQL database is automatically initialized with the observability schema on first start. Data persists across container restarts via Docker volumes.

To tear down:

```powershell
.\scripts\start-infrastructure.ps1 -Down
```

> **Without Docker:** The harness still runs, but session data, metrics, and traces won't be captured. You'll see a warning at startup: *"Session, message, and tool execution data will NOT be persisted."*

### Run

```bash
# Interactive menu (console)
dotnet run --project src/Content/Presentation/Presentation.ConsoleUI

# Run a specific example directly
dotnet run --project src/Content/Presentation/Presentation.ConsoleUI -- --example research
dotnet run --project src/Content/Presentation/Presentation.ConsoleUI -- --example orchestrator
dotnet run --project src/Content/Presentation/Presentation.ConsoleUI -- --example a2a

# AgentHub + WebUI (two terminals)
dotnet run --project src/Content/Presentation/Presentation.AgentHub
cd src/Content/Presentation/Presentation.WebUI && npm run dev
```

For local development without Azure AD, set `Auth:Disabled=true` in `appsettings.Development.json` and `VITE_AUTH_DISABLED=true` in `Presentation.WebUI/.env.local`. See `docs/azure-ad-setup.md` for full Azure AD setup.

### Run Tests

```bash
dotnet test src/AgenticHarness.slnx
dotnet test src/AgenticHarness.slnx --collect:"XPlat Code Coverage"
```

---

## Try It Out

The ConsoleUI launches an interactive [Spectre.Console](https://spectreconsole.net/) menu with six examples that demonstrate the harness at different levels of complexity:

**Start here:** The **Research Agent** runs a standalone conversation — one agent, a few tools, a question to answer. It shows the basic loop: user message in, tool calls out, synthesized answer back.

**Then try:** The **Orchestrator Agent** adds multi-agent coordination. Give it a complex task and watch it decompose the work, spin up sub-agents, and merge their results into a coherent output.

**Go deeper:** **MCP Tools Discovery** connects to external MCP servers and pulls in remote tools at runtime. **Tool Converter Demo** shows the `ITool` to `AITool` bridge that makes keyed DI tools visible to the LLM. **Persistent Agent** creates a long-running agent on Azure AI Foundry with threads that survive across sessions. **A2A Agent-to-Agent** demonstrates the full discovery-and-delegation protocol between distributed agents.

**For structured execution:** The **Plan Execution** pipeline lets you create a multi-step plan from a natural-language goal, validate its DAG structure, and execute it with dependency ordering, bounded concurrency, and checkpoint/resume. Steps that fail retry automatically or escalate to a human gate. Interrupt a running plan and resume it later — the SQLite checkpoint store picks up exactly where it left off.

**For self-improvement:** The **Meta-Harness Optimizer** runs the propose→evaluate loop against your own skill files. Drop eval tasks in `eval/tasks/`, run `--example optimize`, and it will suggest targeted improvements to your agent's skills based on causal trace analysis.

**For skill-document optimization:** The **Skill Training Demo** runs the SkillOpt-style training loop (rollout → reflect → aggregate → select → apply → gate) end-to-end against deterministic stubs — no LLM calls required. It demonstrates the per-step audit trail, patience-based early stop, and epoch-boundary mechanisms; use it as a starting point for plugging in real `IPatchProposer` + `IRolloutRunner` implementations.

---

## Configuration Reference

| Section | What it controls |
|---------|-----------------|
| `AppConfig.Agent` | Turn limits (`MaxTurnsPerConversation`), token budget (`DefaultTokenBudget`) |
| `AppConfig.AI.AgentFramework` | Model deployment (`DefaultDeployment`), provider (`ClientType`: AzureOpenAI, OpenAI, AIFoundry) |
| `AppConfig.AI.Skills` | Skill discovery paths (`BasePath`, `AdditionalPaths`) |
| `AppConfig.AI.AIFoundry` | Persistent agent endpoint (`ProjectEndpoint`) |
| `AppConfig.AI.A2A` | Agent-to-Agent settings (`Enabled`, `AgentName`, `BaseUrl`, `DiscoveryEndpoints`) |
| `AppConfig.AI.MCP` | MCP server identity (`ServerName`) |
| `AppConfig.AI.McpServers` | External MCP server connections (`Servers[]`) |
| `AppConfig.Observability` | Tracing, metrics, and sampling (`EnableTracing`, `SamplingRatio`) |
| `AppConfig.Cache` | Cache backend (`CacheType`: Memory or Redis) |
| `AppConfig.Logging` | Log output (`LogsBasePath`, `PipeName` for named pipe streaming) |
| `AppConfig.AI.Rag` | RAG pipeline: `VectorStore` (provider, endpoint, embedding model), `Ingestion` (chunking strategy, overlap), `Reranker` (strategy), `Crag` (thresholds), `GraphRag` (enabled, provider, connection string) |
| `AppConfig.AI.Rag.ModelTiering` | Cost optimization: `Enabled`, `DefaultTier`, `OperationOverrides` (per-operation tier), `Tiers[]` (name, deployment, rate limit). See `docs/rag/model-tiering.md` |
| `AppConfig.AI.Rag.ComplexityRouting` | Adaptive routing: tier thresholds, cost weights, bypass rules for simple queries |
| `AppConfig.AI.Rag.Faithfulness` | Answer grounding: evaluation model, hallucination thresholds, citation requirements |
| `AppConfig.AI.Rag.CrossSessionMemory` | Memory persistence: decay rates, retention policies, sync intervals |
| `AppConfig.AI.Rag.GraphDatabase` | Graph backend: provider (InMemory/Neo4j/PostgreSQL), connection string, community level |
| `AppConfig.AI.Plugins` | Plugin declarations: `Declarations[]` (name, local path, AllowedTools, DeniedTools, AutonomyLevel) |
| `AppConfig.AI.ToolOutputCompression` | Compression: `Enabled`, `DefaultTokenThreshold`, per-category thresholds |
| `AppConfig.AI.Permissions` | Autonomy tier policies: per-tier allowed operations, escalation triggers |
| `AppConfig.AI.Orchestration` | Multi-agent: capability match weights, streaming execution, subagent config |
| `MetaHarness` | Optimization settings: `EvalTasksPath`, `SeedCandidatePath`, `MaxIterations`, `ProposerModel`, `EvaluatorModel`, `ScoreImprovementThreshold`, `RegressionSuiteThreshold` (0.8), `ConsecutiveNoImprovementLimit` (5) |
| `Planner` | Plan generation: `GenerationModel`, `ClientType`, `GenerationTemperature` (0.3), `GenerationMaxTokens` (4096) |
| `Attestation` | HMAC attestation keys: `HmacKeys[]` (version + Base64 key), `CurrentKeyVersion` — keys in User Secrets (dev) or Key Vault (prod) |

---

## Tech Stack

| | |
|---|---|
| **Runtime** | .NET 10, C# 14 |
| **AI** | Microsoft.Agents.AI, Microsoft.Extensions.AI, Semantic Kernel |
| **LLM Providers** | Azure OpenAI, OpenAI, Azure AI Foundry |
| **Architecture** | Clean Architecture, CQRS (MediatR), FluentValidation |
| **RAG** | Hybrid retrieval (dense + BM25/RRF), CRAG, RAPTOR, HyDE, RAG-Fusion, GraphRAG, multi-hop |
| **Knowledge Graph** | Neo4j, PostgreSQL, Kuzu, Leiden community detection, cross-session memory |
| **Governance** | Drift detection (EWMA), escalation workflows, autonomy tiers, Polly circuit breakers |
| **Protocols** | MCP (HTTP transport, JWT auth), A2A (agent discovery + delegation) |
| **Observability** | OpenTelemetry, Prometheus, Grafana, Tempo, Azure Monitor |
| **Security** | Azure Identity, JWT Bearer, CORS allowlists, sandboxed tool execution, response sanitization |
| **Testing** | xUnit, Moq, coverlet, Vitest, React Testing Library |
| **WebUI** | React 19, TypeScript, Vite, Tailwind CSS, shadcn/ui, Zustand, TanStack Query, MSAL |

---

## License

This project is provided as-is for educational and reference purposes.
