/**
 * Content for the Capability Showcase page. Plain browser-loaded data — no build step.
 *
 * Shape: Category { id, number, name, subtitle, boxes: Box[] }
 *        Box { id, name, status, exec, eng?, techs?, docLink? }
 * status: 'built' | 'partial' | 'not-built' | 'external'
 *   built    = real, shipped, verified against the actual source tree
 *   partial  = the underlying capability is real, but not packaged as its own named thing
 *   not-built = genuinely absent — no invented mechanism, just an honest label
 *   external = deliberately NOT built here — an outside system this harness connects to
 *
 * Every 'built'/'partial' claim here was checked against src/ (grep, not assumption) before
 * being written. Where a box's own docs go deeper, docLink points at the real onboarding chapter.
 */
window.SHOWCASE_CATEGORIES = [
    {
        id: 'user-surface',
        number: '01',
        name: 'User Surface',
        subtitle: 'Where requests enter the system',
        boxes: [
            {
                id: 'chat-ui',
                name: 'Chat UI',
                status: 'built',
                exec: 'A full conversational web app — message list, tool inspector, agent picker — not a demo widget.',
                eng: 'Presentation.WebUI: React SPA with ChatPanel/MessageList/ChatInput, an MCP tool/prompt/resource browser, and AG-UI streaming via useAgentHub.',
            },
            {
                id: 'voice',
                name: 'Voice',
                status: 'not-built',
                exec: 'Not built. No speech-to-text or text-to-speech pipeline exists in this repo today.',
            },
            {
                id: 'embedded-copilot',
                name: 'Embedded Copilot',
                status: 'not-built',
                exec: 'Not built as a distinct embeddable widget. The chat UI is a standalone app, not a drop-in component for someone else’s product.',
            },
            {
                id: 'api',
                name: 'API',
                status: 'built',
                exec: 'A real HTTP API for driving the harness programmatically — bundles, workflows, tool discovery.',
                eng: 'Presentation.ExecutionApi, spec-first via bundle-api.yaml, documented in the onboarding guide’s Execution API chapter.',
                docLink: '../17-bundle-api.html',
            },
            {
                id: 'a2a-inbound',
                name: 'A2A Inbound',
                status: 'built',
                exec: 'Other AI agents can call into this harness using the open Agent-to-Agent protocol, with identity carried through, not dropped.',
                eng: 'Infrastructure.AI/A2A/A2AIdentityPropagator.cs propagates caller identity across an inbound A2A call; backed by Domain.AI/Identity/AgentIdentity.cs.',
            },
        ],
    },
    {
        id: 'identity-trust',
        number: '02',
        name: 'Identity & Trust',
        subtitle: 'Who is asking, what are they allowed to do',
        boxes: [
            {
                id: 'sso-oauth',
                name: 'SSO / OAuth',
                status: 'built',
                exec: 'Real login, not a stub — requests are authenticated before anything else happens.',
                eng: 'JWT Bearer authentication via Microsoft Entra ID, wired through Presentation.Common, Presentation.ExecutionApi, and Presentation.AgentHub.',
            },
            {
                id: 'agent-identity',
                name: 'Agent Identity',
                status: 'built',
                exec: 'The agent itself has a verifiable identity, distinct from the human or system that invoked it — needed the moment one agent calls another.',
                eng: 'Domain.AI/Identity/AgentIdentity.cs, consumed by the A2A identity propagator on both inbound and outbound calls.',
            },
            {
                id: 'scoped-credentials',
                name: 'Scoped Credentials',
                status: 'partial',
                exec: 'Real, but not packaged as its own product — it lives inside the egress and tool-governance layer rather than as a standalone credential-scoping service.',
                eng: 'Enforced via SkillManifestEgressPolicyResolver and the tool-invocation governance chain, not a dedicated credentials-scoping component.',
            },
            {
                id: 'policy-engine',
                name: 'Policy Engine',
                status: 'partial',
                exec: 'Policy decisions are real and enforced, but there’s no single named "policy engine" — the logic lives inside the governance layer described under Governance & Control.',
                eng: 'ToolInvocationGovernor and the surrounding governance chain make the actual policy decisions at tool-call time.',
            },
        ],
    },
    {
        id: 'orchestration',
        number: '03',
        name: 'Orchestration',
        subtitle: 'Decides what happens, in what order, by whom',
        boxes: [
            {
                id: 'classifier',
                name: 'Classifier',
                status: 'partial',
                exec: 'A real classifier exists, but today it’s scoped to one job — scoring how complex a retrieval question is — not general-purpose request classification.',
                eng: 'The RAG complexity-routing classifier (Phase A) scores query complexity and routes to a tiered pipeline; not reused elsewhere yet.',
            },
            {
                id: 'router',
                name: 'Router',
                status: 'partial',
                exec: 'Same story as the classifier above — real routing logic exists for retrieval, not as a general request router.',
                eng: 'Complexity-based routing lives inside the RAG pipeline only.',
            },
            {
                id: 'planner',
                name: 'Planner',
                status: 'built',
                exec: 'A real planning engine that breaks work into a dependency graph and runs it with bounded concurrency, checkpointing, and error recovery.',
                eng: 'PlanExecutor orchestrates a PlanGraph via keyed step executors (LlmCall, ToolUse, HumanGate, ConditionalBranch, SubPlanInvocation), with retry/escalate/skip recovery and EF Core-backed checkpoint/resume.',
            },
            {
                id: 'orchestrator',
                name: 'Orchestrator',
                status: 'built',
                exec: 'The assembly line every request runs through: check it’s safe, pick the right skill, get or build the right agent, run it, record what happened.',
                eng: 'ExecuteAgentTurnCommand flows through ~14 ordered MediatR pipeline behaviors before ExecuteAgentTurnCommandHandler resolves skills and calls agent.RunAsync.',
                docLink: '../04-message-journey.html',
            },
        ],
    },
    {
        id: 'agent-runtime',
        number: '04',
        name: 'Agent Runtime',
        subtitle: 'The agents themselves',
        boxes: [
            {
                id: 'supervisor-agent',
                name: 'Supervisor Agent',
                status: 'partial',
                exec: 'One agent can compose several skills in a single conversation, but that’s different from a dedicated "supervisor" that delegates to separate specialist agents — this harness doesn’t have that multi-agent pattern yet.',
            },
            {
                id: 'specialist-agents',
                name: 'Specialist Agents',
                status: 'partial',
                exec: 'Skills act somewhat like specialists — each brings its own instructions and tools — but they run inside one agent’s context, not as independent sub-agents.',
            },
            {
                id: 'skills-system',
                name: 'Skills System',
                status: 'built',
                exec: 'Short instruction documents that tell an agent what role to play and what it’s allowed to do, loaded only as needed so adding more skills never slows things down.',
                eng: 'SKILL.md files loaded via three-tier progressive disclosure; SkillMetadataParser → SkillDefinition; multi-skill agents merge several via AgentExecutionContextFactory with prerequisite ordering.',
                docLink: '../05-skills.html',
                tech: 'SKILL.md · SkillDefinition',
            },
            {
                id: 'context-budget',
                name: 'Context Budget',
                status: 'built',
                exec: 'Tracks how much of the model’s attention and cost budget a conversation has used, and stops things before they run away.',
                eng: 'IConversationBudgetTracker (InProcessConversationBudgetTracker) plus TokenEstimationHelper enforce a cross-turn token ceiling.',
            },
            {
                id: 'meta-harness',
                name: 'Meta-Harness',
                status: 'built',
                exec: 'The system can review its own past failures, propose changes to a skill’s instructions, and test that proposal against a benchmark before adopting it.',
                eng: 'RunHarnessOptimizationCommand: snapshot → propose → evaluate against a benchmark task suite → regression-gate against prior winners → promote.',
                docLink: '../14-skill-training.html',
            },
        ],
    },
    {
        id: 'model-gateway',
        number: '05',
        name: 'Model Gateway',
        subtitle: 'Talks to the underlying LLMs',
        boxes: [
            {
                id: 'multi-model-routing',
                name: 'Multi-Model Routing',
                status: 'partial',
                exec: 'The harness can fail over between providers, but genuinely dynamic per-request "use the cheapest/best model for this job" routing is thinner than the name implies.',
            },
            {
                id: 'fallback-chains',
                name: 'Fallback Chains',
                status: 'built',
                exec: 'If one AI provider fails, the harness automatically tries the next one in line — real resilience, not just a retry loop.',
                eng: 'IProviderErrorClassifier normalizes failures across providers (each SDK throws a different exception type for the same HTTP status); Polly owns retry and circuit-breaking, chain members run with SDK retry disabled.',
            },
            {
                id: 'semantic-cache',
                name: 'Semantic Cache',
                status: 'not-built',
                exec: 'Not built. There’s no cache that recognizes "this is basically the same question as before" and reuses an answer.',
            },
            {
                id: 'pii-redaction',
                name: 'PII Redaction',
                status: 'built',
                exec: 'Personal and sensitive data gets scrubbed out of logs and telemetry before it’s ever written down.',
                eng: 'RedactionCategory + RedactionRule drive redaction in ContentCaptureConfig/LogsConfig, governed by PiiFilteringConfig.',
            },
            {
                id: 'cost-governance',
                name: 'Cost Governance',
                status: 'built',
                exec: 'Spend is tracked and capped per conversation, not just observed after the fact.',
                eng: 'The same IConversationBudgetTracker used for context budgeting enforces a token/cost ceiling per conversation.',
            },
        ],
    },
    {
        id: 'tool-integration-surface',
        number: '06',
        name: 'Tool & Integration Surface',
        subtitle: 'How agents act on the world',
        boxes: [
            {
                id: 'mcp-server',
                name: 'MCP Server',
                status: 'built',
                exec: 'Lets other AI agents call into this harness’s own capabilities over a standard web connection, with real authentication and rate limits.',
                eng: 'ASP.NET Core WebAPI: .WithHttpTransport().LoadTools().LoadPrompts().LoadResources(), JWT Bearer auth via Entra ID.',
                docLink: '../08-mcp.html',
            },
            {
                id: 'mcp-client',
                name: 'MCP Client',
                status: 'built',
                exec: 'Connects out to tools hosted anywhere else and makes them available to the agent exactly like a built-in tool.',
                eng: 'Discovers tools via tools/list at startup; supports HTTP or stdio transport and Bearer/Entra/ApiKey outbound authentication.',
                docLink: '../08-mcp.html',
                techs: ['HTTP Transport', 'stdio Transport'],
            },
            {
                id: 'api-hub',
                name: 'API Hub',
                status: 'built',
                exec: 'The same Execution API listed under User Surface — it’s both an entry point for humans and an integration surface for other systems.',
                docLink: '../17-bundle-api.html',
            },
            {
                id: 'a2a-outbound',
                name: 'A2A Outbound',
                status: 'built',
                exec: 'This harness can call out to other agents using the Agent-to-Agent protocol, carrying its own identity with it.',
                eng: 'A2AIdentityPropagator handles outbound identity propagation, the mirror of the inbound path under User Surface.',
            },
            {
                id: 'code-sandbox',
                name: 'Code Sandbox',
                status: 'built',
                exec: 'When an agent needs to actually run code, it happens in an isolated box that can’t touch anything it wasn’t explicitly allowed to.',
                eng: 'ProcessSandboxExecutor (Windows Job Objects) and DockerSandboxExecutor, both with HMAC attestation and a closed-by-default capability model.',
            },
            {
                id: 'computer-use',
                name: 'Computer Use',
                status: 'not-built',
                exec: 'Not built. No browser/desktop-automation tool exists in this repo.',
            },
        ],
    },
    {
        id: 'memory-knowledge',
        number: '07',
        name: 'Memory & Knowledge',
        subtitle: 'What the platform remembers',
        boxes: [
            {
                id: 'episodic-memory',
                name: 'Episodic Memory',
                status: 'partial',
                exec: 'Real memory of past interactions exists and fades over time, just not organized under this exact name.',
                eng: 'KnowledgeMemoryService’s Remember/Recall/Forget/Improve, with MemoryDecayService applying exponential weight decay (5% per day untouched, by default) and pruning below a threshold.',
                deepDive: {
                    scenario: [
                        {
                            time: 'Mon',
                            text: 'A user mentions, mid-conversation, that their team is migrating off a legacy system. Nothing about the current task depends on it — but it’s worth keeping.',
                        },
                        {
                            time: '+0s',
                            text: 'Before it’s kept anywhere, the fact is screened — the same way an email attachment gets scanned before you trust it. Nothing looks like an attempt to plant a hidden instruction, so it’s marked trustworthy and filed away.',
                        },
                        {
                            time: 'Day 1–20',
                            text: 'Nobody asks about it again. Like an unused muscle, its importance quietly fades a little each day it goes untouched.',
                        },
                        {
                            time: 'Day 21',
                            text: 'In a completely different conversation, the user asks something related. The fact resurfaces automatically — no one had to remind the agent it existed.',
                        },
                        {
                            time: 'Day 21',
                            text: 'Being recalled resets its clock — it’s relevant again, so it stops fading for now.',
                        },
                    ],
                    flow: [
                        { title: 'Remember', body: 'A fact worth keeping shows up in conversation.' },
                        {
                            title: 'Safety Check',
                            body: 'Screened before anything is trusted. If it passes, it’s stored and recallable. If it looks suspicious, it’s quarantined — kept for audit, but never served back to the agent.',
                        },
                        { title: 'Fades Over Time', body: 'Loses roughly 5% of its importance per day it goes untouched, until it’s pruned entirely.' },
                        { title: 'Recall', body: 'A fast cache is checked first; a fuller search runs only if that’s not enough. Being recalled resets the fade.' },
                    ],
                    narrative:
                        'Episodic memory usually means: memory of a specific thing that happened at a specific time — "the user told me X on Tuesday" — as opposed to semantic memory (general facts) or procedural memory (how to do something).\n\n' +
                        'What’s real: a genuine memory system — remember a fact, recall it later, forget it, and improve it based on feedback. Two things make it more than a toy. Every memory is safety-scanned before it’s trusted: before anything gets remembered, it passes through a gate that scans for injected instructions and stamps where the fact came from. If something looks untrustworthy, it still gets written for audit purposes, but it’s quarantined and can never be served back to the agent — a real security property most memory systems skip entirely. And memories fade on purpose: each fact carries a weight that decays smoothly over time and is deleted outright once it drops below a threshold — like human memory, not everything is kept with equal weight.\n\n' +
                        'Why it’s "partial," not "built": the system doesn’t actually distinguish "this happened at a specific moment" from "this is just a general fact worth knowing." It’s one undifferentiated bucket of remembered facts, tagged with a free-text label, not a real timeline of events.',
                    techTable: {
                        columns: ['Mode', 'What It Does', 'Default?'],
                        rows: [
                            ['Legacy recall', 'Substring match against the session cache, then a graph traversal.', 'Yes'],
                            ['Harmonic recall', 'Matches by meaning (an abstraction + cue anchors), and can merge near-duplicate memories instead of creating new ones.', 'No — off by default'],
                        ],
                    },
                    engineeringFacts: [
                        {
                            title: 'Four real operations',
                            body: '<code>KnowledgeMemoryService</code> implements Remember, Recall, Forget, and Improve — not just store-and-fetch.',
                        },
                        {
                            title: 'The safety gate',
                            body: 'Every write passes through <code>IMemoryWriteGate</code> first. An untrusted fact is still saved for audit, but is filtered out of every future recall, permanently.',
                        },
                        {
                            title: 'Two-speed recall',
                            body: 'The in-session cache is checked first (sub-millisecond); a full graph search only runs if that’s not enough.',
                        },
                        {
                            title: 'The decay formula',
                            body: 'Weight decays as <code>weight × (1 − rate)^days</code> since last access (default rate: 5%/day), with a separate pass pruning nodes below a threshold.',
                        },
                        {
                            title: 'Scoped by user',
                            body: 'Keys are namespaced by tenant and user, so recall can never cross a scope boundary even by accident.',
                        },
                    ],
                    whyItMatters:
                        'This is what lets an agent feel like it "remembers you" across sessions — without either forgetting everything the moment a conversation ends, or turning into a liability that will happily remember whatever a bad actor tries to plant. Getting both right at once is the actual hard part; most systems pick one.',
                },
            },
            {
                id: 'semantic-memory',
                name: 'Semantic Memory',
                status: 'partial',
                exec: 'Covered by the knowledge graph and vector store below rather than as its own separate memory type.',
            },
            {
                id: 'procedural-memory',
                name: 'Procedural Memory',
                status: 'not-built',
                exec: 'Not built. The harness doesn’t separately store "how to do X" procedures distinct from its other memory types.',
            },
            {
                id: 'vector-store',
                name: 'Vector Store',
                status: 'built',
                exec: 'Documents are converted into a searchable form based on meaning, not just keywords.',
                eng: 'Dense (vector) retrieval fused with sparse (BM25) via Reciprocal Rank Fusion; backend is swappable (see Retrieval Backend under RAG Pipeline).',
            },
            {
                id: 'knowledge-graph',
                name: 'Knowledge Graph',
                status: 'built',
                exec: 'Beyond documents, the harness can build and remember a map of entities and how they relate — who worked on what, what depends on what — so understanding compounds across sessions.',
                eng: 'Neo4j or PostgreSQL in production (in-memory for dev/test) behind one interface, Leiden community detection for graph-RAG, feedback-weighted retrieval, TenantIsolatedGraphStore for multi-tenant isolation.',
                docLink: '../07-rag.html',
                techs: ['Neo4j', 'Kuzu', 'PostgreSQL', 'In-memory (dev/test)'],
            },
            {
                id: 'rag-pipeline',
                name: 'RAG Pipeline',
                status: 'built',
                exec: 'How the agent answers questions grounded in your real documents instead of guessing — finds the genuinely relevant passages first, then answers from those, with citations.',
                eng: 'Five-stage pipeline: ingestion, query transformation (RAG Fusion, HyDE), hybrid retrieval, CRAG quality evaluation, token-budgeted assembly with citations.',
                docLink: '../07-rag.html',
                techs: ['Dense + BM25 Hybrid (default)', 'Azure AI Search (agentic retrieval)', 'FAISS', 'SQLite FTS5'],
            },
        ],
    },
    {
        id: 'state-persistence',
        number: '08',
        name: 'State & Persistence',
        subtitle: 'Remembering across turns and restarts',
        boxes: [
            {
                id: 'conversation-history',
                name: 'Conversation History',
                status: 'built',
                exec: 'Conversations survive a restart — they’re not held only in memory.',
                eng: 'ConversationOrchestrator + durable conversation transcript, backed by EF Core.',
            },
            {
                id: 'agent-state',
                name: 'Agent State',
                status: 'built',
                exec: 'An in-progress multi-step plan can be checkpointed and resumed rather than starting over.',
                eng: 'EfCorePlanStateStore persists PlanGraph execution state for PlanExecutor’s checkpoint/resume.',
            },
            {
                id: 'agent-registry',
                name: 'Agent Registry',
                status: 'partial',
                exec: 'Agents can be listed and managed through the API, but there’s no dedicated registry service — it’s a controller, not a first-class subsystem.',
                eng: 'AgentsController exposes agent management; no separate IAgentRegistry.',
            },
            {
                id: 'eval-store',
                name: 'Eval Store',
                status: 'built',
                exec: 'Every meta-harness evaluation run — candidate skills, benchmark results, what won and why — is recorded, not thrown away after the run.',
                eng: 'AgentEvaluationService + HarnessCandidate persist eval-run results consumed by the meta-harness promotion gate.',
            },
        ],
    },
    {
        id: 'observability-evaluation',
        number: '09',
        name: 'Observability & Evaluation',
        subtitle: 'Seeing inside the black box',
        boxes: [
            {
                id: 'distributed-tracing',
                name: 'Distributed Tracing',
                status: 'built',
                exec: 'Lets you replay exactly what an agent did, step by step, down to which specific AI call or tool use caused a failure.',
                eng: 'Nested OpenTelemetry spans (command → turn → tool → LLM call) with GenAI semantic-convention attributes.',
                docLink: '../10-observability.html',
                techs: ['Jaeger', 'Prometheus', 'Azure Monitor'],
            },
            {
                id: 'metrics',
                name: 'Metrics',
                status: 'built',
                exec: 'Standard operational metrics — latency, error rates, throughput — exported the same way any production service would.',
                eng: 'OpenTelemetry metrics exported to Prometheus/Grafana, optionally Azure Monitor.',
            },
            {
                id: 'llm-as-judge',
                name: 'LLM-as-Judge',
                status: 'built',
                exec: 'An AI model grades another AI’s output quality as part of the automated pipeline, not just a human eyeballing it.',
                eng: 'The "grader" CI gate and the meta-harness’s benchmark evaluation both use an LLM-as-judge pattern to score candidate outputs.',
            },
            {
                id: 'human-review',
                name: 'Human Review',
                status: 'built',
                exec: 'A person can be pulled into the loop to review or approve something before it goes further.',
                eng: 'HumanFeedbackRelay carries a pending decision to a human reviewer and the response back into the run.',
            },
            {
                id: 'drift-detection',
                name: 'Drift Detection',
                status: 'built',
                exec: 'The system watches for its own behavior quietly changing over time and flags it, instead of finding out from a user complaint.',
                eng: 'EWMA (exponentially weighted moving average) drift detection over governance/quality signals.',
            },
            {
                id: 'learnings-log',
                name: 'Learnings Log',
                status: 'built',
                exec: 'Insights the meta-harness discovers get written down somewhere durable, not lost at the end of the run that found them.',
                eng: 'The meta-harness learnings memory channel, gated the same way other cross-run memory is.',
            },
        ],
    },
    {
        id: 'governance-control',
        number: '10',
        name: 'Governance & Control',
        subtitle: 'Keeping agents safe and accountable',
        boxes: [
            {
                id: 'runtime-guardrails',
                name: 'Runtime Guardrails',
                status: 'built',
                exec: 'Every tool call, no matter where the tool came from, passes through the same three safety checks before anything actually happens.',
                eng: 'GovernedAIFunction.InvokeCoreAsync runs IToolInvocationGovernor → IToolClassificationGate → IProgressEvaluator, in order.',
            },
            {
                id: 'audit-log',
                name: 'Audit Log',
                status: 'built',
                exec: 'A record of what happened that can’t be quietly edited after the fact.',
                eng: 'A durable PostgreSQL audit store backed by a hash-chained JSONL log, where each entry’s hash includes the previous entry’s.',
            },
            {
                id: 'human-escalation',
                name: 'Human Escalation',
                status: 'built',
                exec: 'Risky actions can require a human’s sign-off before they happen, with the caution level matched to how much damage the action could do.',
                eng: 'A tool’s RiskTier (BlastRadius) feeds the permission resolver’s autonomy-tier gate; a PendingApproval decision blocks fail-closed.',
            },
            {
                id: 'autonomy-tiers',
                name: 'Autonomy Tiers',
                status: 'built',
                exec: 'How independently an agent is allowed to act is a graded, configurable setting — not all-or-nothing.',
                eng: 'AutonomyLevel on plugin/tool declarations, resolved alongside RiskTier at invocation time.',
            },
            {
                id: 'compliance-reporting',
                name: 'Compliance Reporting',
                status: 'not-built',
                exec: 'Not built as a distinct feature. The audit log and safety gates exist; a formal compliance-report generator on top of them doesn’t.',
            },
        ],
    },
    {
        id: 'system-of-record',
        number: '11',
        name: 'System of Record',
        subtitle: 'The enterprise truth the platform serves — not something this harness builds',
        boxes: [
            {
                id: 'crm-customer-data',
                name: 'CRM & Customer Data',
                status: 'external',
                exec: 'An outside system this harness would connect to through its RAG/tool/MCP layers — not something built here, deliberately.',
            },
            {
                id: 'erp-operations',
                name: 'ERP & Operations',
                status: 'external',
                exec: 'Same story — a real enterprise system this harness integrates with, never replaces.',
            },
            {
                id: 'identity-hr-systems',
                name: 'Identity & HR Systems',
                status: 'external',
                exec: 'This harness authenticates against your identity provider (see Identity & Trust); it doesn’t become one.',
            },
            {
                id: 'service-management',
                name: 'Service Management',
                status: 'external',
                exec: 'An external ticketing/ITSM system a tool or MCP connector could reach — not built in this repo.',
            },
            {
                id: 'data-platform',
                name: 'Data Platform',
                status: 'external',
                exec: 'Your actual enterprise data warehouse/lake — the RAG pipeline retrieves from sources like this, it doesn’t replace them.',
            },
            {
                id: 'master-data-management',
                name: 'Master Data Management',
                status: 'external',
                exec: 'The authoritative system of record for entities this harness’s knowledge graph might reference — external by design.',
            },
        ],
    },
];

/**
 * Technology comparison entries referenced by any box's `techs` array above.
 * Shape: { blurb, pros: string[], cons: string[], best, maturity }.
 */
window.SHOWCASE_TECH_CATALOG = {
    'Dense + BM25 Hybrid (default)': {
        blurb: "The harness's own default: combines vector similarity search with classic BM25 keyword search, merged via Reciprocal Rank Fusion.",
        pros: [
            'Works out of the box, no extra service to deploy',
            'Catches both paraphrased questions and exact keyword/product-code matches',
            "Runs entirely inside the harness's own process",
        ],
        cons: [
            'Retrieval quality scales with how much tuning goes into chunking and embeddings',
            'No managed relevance-tuning UI — every knob is code',
        ],
        best: 'Teams who want strong retrieval without standing up or paying for a separate search service.',
        maturity: 'Production (default path)',
    },
    'Azure AI Search (agentic retrieval)': {
        blurb: "Microsoft's managed hybrid search service, with an agentic retrieval mode that plans multi-step queries itself.",
        pros: [
            'Fully managed — no infrastructure to run',
            'Built-in agentic query planning reduces manual query engineering',
            'Deep Azure ecosystem integration (identity, networking, monitoring)',
        ],
        cons: [
            'Recurring Azure cost that scales with index size and query volume',
            'Ties the harness to Azure specifically',
            'Less control over the exact ranking algorithm than the in-process default',
        ],
        best: 'Azure-committed teams who want a managed search backend and are fine trading control for less operational burden.',
        maturity: 'GA',
    },
    FAISS: {
        blurb: "Meta's open-source library for fast approximate nearest-neighbor vector search, run in-process.",
        pros: [
            'Extremely fast at large vector counts',
            'No external service, no network hop',
            'Free and widely used in production ML systems',
        ],
        cons: [
            'Vector-only — hybrid search needs a keyword component hand-assembled alongside it',
            'No built-in persistence layer — the indexing lifecycle is your own responsibility',
            'No access control or multi-tenancy built in',
        ],
        best: "High-volume, latency-sensitive retrieval where you're comfortable owning the index lifecycle yourself.",
        maturity: 'Mature / widely adopted',
    },
    'SQLite FTS5': {
        blurb: "SQLite's built-in full-text search extension — keyword search with zero extra infrastructure.",
        pros: [
            'Ships inside a single file, nothing else to deploy or operate',
            'Great fit for local dev, small deployments, or embedded scenarios',
            'Simple, well-understood query syntax',
        ],
        cons: [
            "Keyword-only — no semantic/vector matching on its own",
            "Doesn't scale to the concurrency or index sizes a dedicated search service handles",
            'No ranking sophistication beyond BM25',
        ],
        best: "Local development, small single-tenant deployments, or anywhere a separate search service isn't worth the operational cost.",
        maturity: 'Mature (part of SQLite core)',
    },
    Neo4j: {
        blurb: 'The most widely-used dedicated graph database, with its own query language (Cypher) and a large ecosystem of graph algorithms.',
        pros: [
            'Mature tooling, visualization, and a large community',
            'Rich built-in graph algorithm library, including community detection',
            'Battle-tested at production scale',
        ],
        cons: [
            'Commercial licensing for clustering/enterprise features',
            'Another stateful service to operate and back up',
            'Steeper learning curve (Cypher) for a team new to graph databases',
        ],
        best: 'Teams that expect the knowledge graph to grow large and want a dedicated, well-supported graph engine.',
        maturity: 'Production',
    },
    Kuzu: {
        blurb: 'An embedded, open-source graph database — runs in-process like SQLite, but for graphs.',
        pros: [
            'No separate service to deploy or operate',
            'Free and fully open-source',
            'Fast for single-node workloads',
        ],
        cons: [
            'Younger project — smaller ecosystem and community than Neo4j',
            'Not designed for multi-node horizontal scale',
            'Fewer built-in graph algorithms out of the box',
        ],
        best: 'Teams that want real graph-query capability without taking on a new operated service.',
        maturity: 'Active / early production',
    },
    PostgreSQL: {
        blurb: 'Using Postgres itself as the graph store, reusing infrastructure the team already runs.',
        pros: [
            'No new database technology to introduce — most teams already run Postgres',
            'One less service to secure, back up, and monitor',
            "Transactional guarantees the harness's other relational data already relies on",
        ],
        cons: [
            'Graph traversal queries are less natural and often slower than a purpose-built graph engine at scale',
            'No dedicated graph algorithm library — community detection etc. has to be implemented or bolted on',
        ],
        best: 'Teams that already run Postgres and want to avoid adding a new database technology purely for the knowledge graph.',
        maturity: 'Production (via existing Postgres infrastructure)',
    },
    'In-memory (dev/test)': {
        blurb: 'A non-persistent, in-process graph store used for local development and automated tests.',
        pros: [
            'Zero setup — nothing to install or configure',
            'Fast test runs with no external dependency',
            'Identical interface to the production backends, so tests exercise real code paths',
        ],
        cons: [
            "Data doesn't survive a process restart",
            'Not viable for any real deployment',
            'Not representative of production performance characteristics',
        ],
        best: 'Local development and CI test runs only.',
        maturity: 'Dev/test only — not for production',
    },
    Jaeger: {
        blurb: 'Open-source distributed tracing backend — stores and visualizes the spans OpenTelemetry emits.',
        pros: [
            'Free, open-source, self-hostable',
            'Purpose-built trace visualization (waterfall views, span search)',
            'No vendor lock-in',
        ],
        cons: [
            'You own its operation, storage, and retention policy',
            'No built-in alerting — typically paired with Prometheus/Grafana for that',
        ],
        best: 'Teams that want full control over their tracing backend and are comfortable self-hosting.',
        maturity: 'Production / CNCF graduated project',
    },
    Prometheus: {
        blurb: 'The standard open-source metrics store and query engine (PromQL), typically paired with Grafana for dashboards.',
        pros: [
            'Industry-standard, huge ecosystem of exporters and dashboards',
            'Powerful query language for aggregating metrics over time',
            'Free and self-hostable',
        ],
        cons: [
            'Metrics only — not a substitute for trace or log storage',
            'Long-term retention at scale needs an additional remote-storage backend',
        ],
        best: 'Teams that want standard, dashboard-ready operational metrics alongside tracing.',
        maturity: 'Production / CNCF graduated project',
    },
    'Azure Monitor': {
        blurb: "Microsoft's managed observability platform — collects traces, metrics, and logs into one Azure-native service.",
        pros: [
            'Fully managed — no tracing/metrics infrastructure to run',
            'Single pane of glass alongside other Azure resource monitoring',
            'Built-in alerting and retention without extra setup',
        ],
        cons: [
            'Recurring Azure cost that scales with telemetry volume',
            'Ties observability tooling to Azure specifically',
        ],
        best: 'Azure-hosted deployments that want managed observability without operating Jaeger/Prometheus themselves.',
        maturity: 'GA',
    },
    'HTTP Transport': {
        blurb: "MCP over standard HTTP — the harness's tools are reachable as a normal web endpoint.",
        pros: [
            'Works across process and network boundaries, including to/from other machines',
            'Standard auth patterns apply (JWT bearer, API gateways, load balancers)',
            'Easiest to expose to external MCP clients',
        ],
        cons: [
            "Network latency and failure modes a local transport doesn't have",
            "Needs real authentication/authorization since it's reachable off-box",
        ],
        best: "Exposing this harness's tools to other systems, or consuming tools hosted elsewhere.",
        maturity: 'Production',
    },
    'stdio Transport': {
        blurb: 'MCP over standard input/output — the tool server runs as a local child process with no network involved.',
        pros: [
            'No network stack, no ports to secure — the tightest possible trust boundary',
            'Simplest possible setup for a tool that only ever runs alongside the agent',
            'Lower latency than a network hop',
        ],
        cons: [
            'Only works for same-machine, same-process-tree scenarios',
            'No support for multiple concurrent remote clients',
        ],
        best: 'Tools that only ever need to run alongside the agent process itself, with no remote-access requirement.',
        maturity: 'Production',
    },
};

/**
 * Glossary of jargon that already appears in the copy above. Shape: term -> plain-English definition.
 */
window.SHOWCASE_GLOSSARY = {
    MCP: 'Model Context Protocol — the open standard this harness uses to expose and consume AI tools.',
    A2A: 'Agent-to-Agent — an open protocol for one AI agent to call another, with identity carried along.',
    JWT: 'JSON Web Token — a signed, compact envelope for identity claims.',
    'Entra ID': "Microsoft's cloud identity platform (formerly Azure AD).",
    BM25: 'A classic keyword-ranking algorithm used by search engines.',
    'Reciprocal Rank Fusion': 'A method for merging several ranked result lists into one combined ranking.',
    CRAG: 'Corrective Retrieval-Augmented Generation — scores retrieved results for trustworthiness before answering with them.',
    HyDE: 'Hypothetical Document Embeddings — generates a hypothetical answer first, then searches using that as the query.',
    OpenTelemetry: 'An open standard for collecting distributed traces, metrics, and logs.',
    'Hash-Chained Log': "A tamper-evident log where each entry's hash includes the previous entry's hash.",
    'Blast Radius': 'How much damage a single action could cause if it goes wrong.',
    'Leiden Community Detection': 'A graph algorithm for finding clusters of closely-related nodes.',
    'Progressive Disclosure': "Revealing information only as it's needed, instead of all at once.",
    'Multi-Tenant Isolation': "Keeping different users' or customers' data provably separate on shared infrastructure.",
    'Autonomy Tier': "A graded level of independence an agent is allowed to operate at before requiring a human's sign-off.",
    Sandbox: 'An isolated execution environment that limits what a process can access or affect.',
    EWMA: 'Exponentially Weighted Moving Average — a way of averaging recent data that weights newer points more heavily, good for spotting drift.',
    'LLM-as-Judge': "Using one AI model to grade another AI model's output quality.",
};
