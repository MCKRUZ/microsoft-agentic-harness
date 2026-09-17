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
                        { title: 'Remember', tone: 'info', body: 'A fact worth keeping shows up in conversation.' },
                        {
                            title: 'Safety Check',
                            tone: 'warn',
                            body: 'Screened before anything is trusted. If it passes, it’s stored and recallable. If it looks suspicious, it’s quarantined — kept for audit, but never served back to the agent.',
                        },
                        { title: 'Fades Over Time', tone: 'jargon', body: 'Loses roughly 5% of its importance per day it goes untouched, until it’s pruned entirely.' },
                        { title: 'Recall', tone: 'tip', body: 'A fast cache is checked first; a fuller search runs only if that’s not enough. Being recalled resets the fade.' },
                    ],
                    /* Each paragraph is trusted, hand-authored HTML (not escaped at render time) so
                       key phrases can be wrapped in <mark class="hl"> to guide a skimming reader's
                       eye straight to what matters, instead of a flat wall of text. */
                    narrative: [
                        'Episodic memory usually means: <mark class="hl">memory of a specific thing that happened at a specific time</mark> — "the user told me X on Tuesday" — as opposed to semantic memory (general facts) or procedural memory (how to do something).',
                        'What’s real: a genuine memory system — remember a fact, recall it later, forget it, and improve it based on feedback. Two things make it more than a toy. Every memory is <mark class="hl">safety-scanned before it’s trusted</mark>: before anything gets remembered, it passes through a gate that scans for injected instructions and stamps where the fact came from. If something looks untrustworthy, it still gets written for audit purposes, but it’s <mark class="hl">quarantined and can never be served back to the agent</mark> — a real security property most memory systems skip entirely. And <mark class="hl">memories fade on purpose</mark>: each fact carries a weight that decays smoothly over time and is deleted outright once it drops below a threshold — like human memory, not everything is kept with equal weight.',
                        'Why it’s <mark class="hl">"partial," not "built"</mark>: the system doesn’t actually distinguish "this happened at a specific moment" from "this is just a general fact worth knowing." It’s <mark class="hl">one undifferentiated bucket</mark> of remembered facts, tagged with a free-text label, not a real timeline of events.',
                    ],
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
                exec: 'A background process pulls durable facts out of what you say and remembers them — just not as a separate memory type from episodic events.',
                eng: 'ConversationFactExtractor runs an LLM-based extraction after each turn; facts above a confidence bar are written through the same KnowledgeMemoryService.RememberAsync pipeline Episodic Memory uses, tagged as a fact instead of an event.',
                deepDive: {
                    scenario: [
                        {
                            time: 'Turn 1',
                            text: 'A user mentions, in passing, "I\'m the CTO here, and I really don\'t need the long version — just tell me what changed."',
                        },
                        {
                            time: '+0s',
                            text: 'The turn finishes and the response goes back to the user immediately. Separately, and without holding anything up, a second, cheaper model reads that same exchange looking for facts worth keeping.',
                        },
                        {
                            time: '+0s',
                            text: 'It pulls out two candidates: "user\'s role is CTO" and "user prefers terse updates." A third guess — "may also manage the infrastructure budget" — comes back too uncertain and is thrown away before it\'s ever stored.',
                        },
                        {
                            time: 'Weeks later',
                            text: 'In a completely different conversation, the user asks for a project update. The answer comes back short and to the point — that preference resurfaced automatically, blended in with whatever else the agent remembers.',
                        },
                    ],
                    flow: [
                        { title: 'Extract', tone: 'info', body: 'After every successful turn, a background call to a cheaper model reads the exchange and pulls out discrete, structured facts — not just the raw transcript.' },
                        {
                            title: 'Confidence Filter',
                            tone: 'warn',
                            body: 'Each candidate fact comes back with a confidence score. Anything underneath the line is silently dropped before it\'s ever written down.',
                        },
                        { title: 'Remember', tone: 'jargon', body: 'What survives is filed through the exact same pipeline Episodic Memory uses — the same safety scan, the same decay — just tagged "Fact" instead of an event.' },
                        { title: 'Recall', tone: 'tip', body: 'Later, recall doesn\'t distinguish "a fact about you" from "something that happened" — it returns whichever is relevant, from the same undifferentiated bucket.' },
                    ],
                    narrative: [
                        'Semantic memory usually means <mark class="hl">a durable fact about the world, detached from the moment you learned it</mark> — "the user is a CTO," not "the user told me on Tuesday they\'re a CTO." That second, timestamped version is episodic memory; the two are supposed to be different systems.',
                        'What\'s real: after every successful turn, a <mark class="hl">background call to a cheaper model reads the conversation and pulls out discrete facts</mark> — not the raw text, but short, structured claims, each with a confidence score attached. Anything below the confidence bar is <mark class="hl">thrown away before it\'s ever stored</mark>, and the whole extraction step runs fire-and-forget on its own timer, so it never adds latency to the response the user is waiting for.',
                        'Why it\'s <mark class="hl">"partial," not "built"</mark>: once a fact survives, it isn\'t stored anywhere distinct from an event — it\'s written into <mark class="hl">the exact same bucket as Episodic Memory</mark>, just with a different label. And the dial that\'s supposed to tune how strict the confidence bar is exists in configuration, but <mark class="hl">isn\'t actually wired to the code that filters facts</mark> — turning it up or down currently does nothing.',
                    ],
                    techTable: {
                        columns: ['Source', 'How It Gets In', 'Scope'],
                        rows: [
                            ['Conversation facts', 'Pulled out of your own turns by a background model call, tagged with a type and a confidence score.', 'Yours alone — decays and can be forgotten like any other memory.'],
                            ['Corpus entities', 'Extracted while ingesting documents into the knowledge graph — people, organizations, technologies.', 'Shared across your tenant, deduplicated by name and type.'],
                        ],
                    },
                    engineeringFacts: [
                        {
                            title: 'One extra model call per turn',
                            body: '<code>ConversationFactExtractor</code> runs a dedicated prompt through the cheapest available model tier after each successful turn — fire-and-forget, never blocking the response.',
                        },
                        {
                            title: 'The confidence knob is disconnected',
                            body: 'A configurable minimum-confidence setting exists (<code>KnowledgeBridgeConfig.MinConfidence</code>), but the extractor filters against its own fixed 0.7 constant instead — changing the setting has no effect today.',
                        },
                        {
                            title: 'Facts and events share one bucket',
                            body: 'A kept fact is written through the identical Remember call Episodic Memory uses, just tagged "Fact" — same decay curve, same safety gate, same graph.',
                        },
                        {
                            title: 'Off by default',
                            body: 'The whole pipeline sits behind one switch, disabled out of the box, so a freshly cloned template never starts remembering things about its users silently.',
                        },
                        {
                            title: 'Entities are a separate, already-distinct system',
                            body: 'Facts from conversation and entities from ingested documents both land in the graph, but through two different paths with different sharing rules — one private and per-user, one shared and deduplicated tenant-wide.',
                        },
                    ],
                    whyItMatters:
                        'This is the difference between an agent that remembers you said something and one that actually knows something about you. The gap isn\'t the idea — the extraction, the confidence gate, the decay are all real and running on every turn. It\'s finishing the separation: giving facts their own store instead of folding them into the same bucket as raw events, and actually wiring the confidence knob that\'s already been built.',
                },
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
                deepDive: {
                    scenario: [
                        {
                            time: 'Query',
                            text: 'Someone asks the assistant, "how much time off do I get?" The company\'s actual policy document never uses the words "time off" — it says "PTO accrual."',
                        },
                        {
                            time: '+0s',
                            text: 'Two searches run against the same documents at the same time: one looks for passages that mean the same thing, the other looks for passages that use the same words.',
                        },
                        {
                            time: '+0s',
                            text: 'The meaning-based search finds the PTO policy even though no words match. A separate search, a moment later, is asked something with an exact acronym in it — and the keyword side catches that one instead, because meaning-based search alone can blur past precise terms.',
                        },
                        {
                            time: '+0s',
                            text: 'Both rankings are blended into one list mathematically, rather than picking a winner, so the final answer is grounded in whichever passage actually was the better match — regardless of which of the two searches found it.',
                        },
                    ],
                    flow: [
                        { title: 'Embed', tone: 'info', body: 'The question is converted into the same kind of "meaning fingerprint" the documents were converted into ahead of time.' },
                        { title: 'Search Twice, At Once', tone: 'jargon', body: 'A meaning-based search and a traditional keyword search run concurrently against the same documents — neither waits on the other.' },
                        { title: 'Fuse The Rankings', tone: 'tip', body: 'The two ranked lists are mathematically combined into one, so a passage that scores well on either search counts, not just the ones both agree on.' },
                        { title: 'Degrade, Don\'t Fail', tone: 'warn', body: 'If one of the two searches errors out, the answer still comes back using whichever search still worked — the whole request doesn\'t fail because of one broken piece.' },
                    ],
                    narrative: [
                        'A vector store\'s job is to let search work on <mark class="hl">meaning instead of exact wording</mark> — a question about "time off" should find a document that only ever says "PTO," the way a person would, instead of missing it because the words don\'t literally match.',
                        'What\'s real: every search actually runs <mark class="hl">two retrieval methods at once</mark> — a meaning-based one and a traditional keyword one — and <mark class="hl">mathematically blends their two rankings into one</mark> rather than trusting either alone. That matters because meaning-based search can blur past an exact acronym or ID number a keyword search would catch instantly, and a pure keyword search misses paraphrasing entirely. If either side fails, the system quietly <mark class="hl">falls back to whichever one still works</mark> instead of failing the whole request. The storage layer underneath is also <mark class="hl">swappable behind one setting</mark> — a fully local, self-hosted stack or a managed cloud search service, without touching any calling code.',
                        'This one is marked <mark class="hl">"Built," not "Partial"</mark> — unlike memory, there\'s no undifferentiated bucket or disconnected setting here. The blending math, the concurrent execution, and the graceful fallback are all real, all wired up, and all running on every retrieval call.',
                    ],
                    techTable: {
                        columns: ['Provider', 'Meaning-Based Search', 'Keyword Search', 'Where It Runs'],
                        rows: [
                            ['Azure AI Search', 'Native managed vector index', 'Native managed keyword ranking', 'Microsoft-hosted cloud service'],
                            ['FAISS + SQLite (default)', 'In-process vector index', 'SQLite full-text index', 'Local — no external service required'],
                        ],
                    },
                    engineeringFacts: [
                        {
                            title: 'Both searches run concurrently',
                            body: 'The meaning-based and keyword searches fire at the same time and are awaited together, so blending them costs no extra latency over running just one.',
                        },
                        {
                            title: 'The blending formula is explicit',
                            body: 'Reciprocal Rank Fusion scores each result as <code>1/(k + rank)</code> summed across both searches (default <code>k = 60</code>) — a well-known, tunable formula, not an ad hoc heuristic.',
                        },
                        {
                            title: 'Each side can fail without sinking the request',
                            body: 'If the meaning-based search throws, the system logs a warning and serves keyword-only results, and vice versa — a broken dependency degrades quality, it doesn\'t return an error page.',
                        },
                        {
                            title: 'Over-fetches before fusing',
                            body: 'Each search is asked for 3x the number of results actually needed, so there\'s enough overlap between the two rankings for the fusion math to be meaningful.',
                        },
                        {
                            title: 'One config value swaps the whole backend',
                            body: 'Switching from the local stack to Azure AI Search — or back — is a single provider setting; the retrieval code that calls it never changes.',
                        },
                    ],
                    whyItMatters:
                        'Grounding an answer in real documents only works if the right passage gets found in the first place. A system that only understands meaning misses exact terms; one that only matches keywords misses paraphrasing. Blending both, with one covering for the other when something breaks, is the difference between retrieval that works in a demo and retrieval that holds up when someone asks a question the way people actually talk.',
                },
            },
            {
                id: 'knowledge-graph',
                name: 'Knowledge Graph',
                status: 'built',
                exec: 'Beyond documents, the harness can build and remember a map of entities and how they relate — who worked on what, what depends on what — so understanding compounds across sessions.',
                eng: 'Neo4j or PostgreSQL in production (in-memory for dev/test) behind one interface, Leiden community detection for graph-RAG, feedback-weighted retrieval, TenantIsolatedGraphStore for multi-tenant isolation.',
                docLink: '../07-rag.html',
                techs: ['Neo4j', 'Kuzu', 'PostgreSQL', 'In-memory (dev/test)'],
                deepDive: {
                    scenario: [
                        {
                            time: 'Ingestion',
                            text: 'A batch of internal documents gets pulled in. Along the way, the system notices "Meridian," "Acme Corp," and a person\'s name showing up together across several of them, and quietly records that they\'re connected — not just that they appear in the same paragraph.',
                        },
                        {
                            time: 'Later',
                            text: 'Someone asks, "what\'s the status of Meridian?" Instead of returning one paragraph that happens to mention the word, the system can follow the relationship — Meridian, who owns it, what it depends on — to assemble a fuller answer.',
                        },
                        {
                            time: 'Next message',
                            text: 'The user replies, "no, that\'s the old vendor, we switched in March." Nobody clicked a thumbs-down. But the system reads that reaction and quietly treats whatever it just relied on as a little less trustworthy going forward.',
                        },
                        {
                            time: 'Meanwhile',
                            text: 'A second company using the same deployment has its own "Meridian" — a completely different project. Its data never mixes with the first company\'s, even though both are running through the identical graph.',
                        },
                    ],
                    flow: [
                        { title: 'Extract & Link', tone: 'info', body: 'While ingesting content, entities and the relationships between them are pulled out and written down — not just the documents themselves.' },
                        { title: 'Group Into Communities', tone: 'jargon', body: 'Related entities are clustered together, so the system can reason and summarize at the level of a topic instead of one disconnected fact at a time.' },
                        { title: 'Listen For Reaction', tone: 'tip', body: 'The user\'s very next message is read for satisfaction or frustration — no rating widget required.' },
                        { title: 'Reweight', tone: 'warn', body: 'That implied reaction nudges how much the graph trusts whatever it just used, shifting future answers without anyone touching a setting.' },
                    ],
                    narrative: [
                        'A knowledge graph is <mark class="hl">a map of things and how they relate</mark>, not just a pile of documents — so an answer can follow a connection ("who owns this, what does it depend on") instead of stopping at whichever single paragraph happened to mention the right word.',
                        'What\'s real: entities and relationships are genuinely extracted and linked as content comes in, then <mark class="hl">clustered into higher-level groups</mark> so the system can summarize a whole topic instead of reasoning fact-by-fact. Isolation between tenants is enforced <mark class="hl">record by record, not all-or-nothing</mark> — two customers can run on the same deployment and never see each other\'s private data, while still sharing a common pool of general knowledge. And the feedback loop is genuinely quiet: it <mark class="hl">reads the user\'s next message for signs of satisfaction or frustration</mark> rather than requiring an explicit rating, and nudges the graph\'s confidence accordingly.',
                        'One honest caveat: that feedback signal, by default, lives in <mark class="hl">fast in-memory storage, not the same durable database as the graph itself</mark> — restart the process and the graph survives, but everything it had learned from user reactions resets to neutral. The code says as much in its own comments; swapping in durable storage for it is a deliberate, documented next step, not a hidden gap.',
                    ],
                    techTable: {
                        columns: ['Backend', 'Best For', 'Notes'],
                        rows: [
                            ['Neo4j', 'Production, dedicated graph workloads', 'Purpose-built graph database with native relationship traversal.'],
                            ['Kuzu', 'Production without running a separate server', 'Embedded graph database — ships inside the process.'],
                            ['PostgreSQL', 'Reusing infrastructure you already run', 'One less system to operate if Postgres is already in your stack.'],
                            ['In-memory', 'Local development and tests only', 'Nothing persists across a restart.'],
                        ],
                    },
                    engineeringFacts: [
                        {
                            title: 'Isolation is checked per record',
                            body: 'Every read checks both tenant AND owner on that specific node — a user sees their tenant\'s shared knowledge plus their own private facts, never anyone else\'s.',
                        },
                        {
                            title: 'Feedback comes from conversation, not a rating widget',
                            body: 'An economy-tier model reads the user\'s next message to detect implicit satisfaction or frustration — no explicit thumbs-up/down UI exists or is required.',
                        },
                        {
                            title: 'Reweighting is a fast running average',
                            body: 'Feedback blends into a node or edge\'s trust score via an exponential moving average — a cheap incremental update, not a full recomputation.',
                        },
                        {
                            title: 'Community detection is "Leiden-inspired," not the textbook algorithm',
                            body: 'The clustering step is a simplified approximation of the published Leiden method, documented as such — a reasonable engineering tradeoff, not a case of overclaiming.',
                        },
                        {
                            title: 'The feedback store defaults to in-memory',
                            body: 'Its own code comments flag this: production deployments should back it with the same durable database as the graph, which isn\'t wired up by default.',
                        },
                    ],
                    whyItMatters:
                        'An assistant that only retrieves paragraphs treats every document as unrelated to every other one, and never gets better at knowing which ones to trust. A real graph can follow a relationship to assemble a fuller answer, and can quietly get better over time from how people actually react — not just from an explicit rating nobody bothers to leave. The gap worth watching is that "gets better over time" part currently forgets what it learned on every restart unless someone wires up durable storage for it.',
                },
            },
            {
                id: 'rag-pipeline',
                name: 'RAG Pipeline',
                status: 'built',
                exec: 'How the agent answers questions grounded in your real documents instead of guessing — finds the genuinely relevant passages first, then answers from those, with citations.',
                eng: 'Five-stage pipeline: ingestion, query transformation (RAG Fusion, HyDE), hybrid retrieval, CRAG quality evaluation, token-budgeted assembly with citations.',
                docLink: '../07-rag.html',
                techs: ['Dense + BM25 Hybrid (default)', 'Azure AI Search (agentic retrieval)', 'FAISS', 'SQLite FTS5'],
                deepDive: {
                    scenario: [
                        {
                            time: 'Question asked',
                            text: 'A user asks something that\'s actually answerable from the company\'s documents. The retrieved passages come back strong, and the answer is assembled and returned with citations — the fast path, and the common case.',
                        },
                        {
                            time: 'A harder question',
                            text: 'This time, what comes back is borderline — technically related, but not a confident match. Instead of answering from weak material, the system quietly rewrites the question and tries retrieval again.',
                        },
                        {
                            time: 'Still not enough',
                            text: 'The second attempt still isn\'t good enough, and retries are exhausted. Rather than presenting an answer built on shaky evidence, the system explicitly reports that it couldn\'t find relevant material — instead of guessing anyway.',
                        },
                        {
                            time: 'A different case',
                            text: 'A question turns out to genuinely be about something outside the ingested documents entirely. Here, the same quality check instead triggers a live web search to fill the gap, if that fallback is turned on.',
                        },
                    ],
                    flow: [
                        { title: 'Retrieve & Rerank', tone: 'info', body: 'Candidate passages come back from hybrid search, then get reordered by a more careful relevance pass before anything is judged.' },
                        { title: 'Grade The Evidence', tone: 'jargon', body: 'A dedicated quality check scores how relevant what was retrieved actually is to the question — before any answer gets built from it.' },
                        { title: 'Accept, Retry, Fall Back, or Refuse', tone: 'warn', body: 'Good evidence gets used. Borderline evidence triggers a rewritten retry (capped, so it can\'t loop forever). Weak evidence either falls back to a web search or is explicitly rejected — never silently guessed past.' },
                        { title: 'Assemble With Citations', tone: 'tip', body: 'What survives is packed into a token budget and handed back with citations pointing to exactly which passages the answer came from.' },
                    ],
                    narrative: [
                        'The core promise of this kind of system is <mark class="hl">answering from evidence instead of guessing</mark> — find the genuinely relevant material first, then answer only from that, and show your work with citations.',
                        'What\'s real: retrieval doesn\'t just run once and hope. A dedicated quality check <mark class="hl">grades how relevant the retrieved passages actually are</mark> before anything gets built from them, and that grade drives one of four distinct outcomes — accept the evidence, quietly rewrite the question and try again (capped at a couple of attempts so it can\'t loop forever), fall back to a live web search if that\'s enabled, or <mark class="hl">explicitly refuse to answer rather than guess past weak evidence</mark>. Harder, multi-part questions get their own path — the system can retrieve <mark class="hl">in several rounds instead of one</mark>, gathering more evidence between rounds when a single pass wasn\'t enough.',
                        'This is marked <mark class="hl">"Built," not "Partial"</mark> — the grading step, the four-way branching, the retry cap, and the citation-backed final answer are all real, all wired together, and all running on every question that reaches this pipeline.',
                    ],
                    techTable: {
                        columns: ['Stage', 'What It Does'],
                        rows: [
                            ['Query transformation', 'Rewrites or expands the question before searching — including generating a hypothetical ideal answer and searching for passages that resemble it, which often finds better matches than searching with the raw question.'],
                            ['Hybrid retrieval', 'Meaning-based and keyword search run together and get blended (see Vector Store).'],
                            ['Quality evaluation', 'Grades the retrieved evidence and decides: accept, retry with a better question, fall back to the web, or refuse.'],
                            ['Assembly', 'Packs what survives into a fixed token budget and attaches citations to the specific source passages.'],
                        ],
                    },
                    engineeringFacts: [
                        {
                            title: 'Four distinct outcomes, not pass/fail',
                            body: 'The quality check returns one of Accept, Refine, Reject, or WebFallback — a real decision tree, not a single relevance threshold.',
                        },
                        {
                            title: 'Refinement is capped',
                            body: 'A borderline result triggers a rewritten retry, but only up to a fixed retry limit — it degrades to "best available" rather than retrying indefinitely.',
                        },
                        {
                            title: 'Rejection is a real, visible outcome',
                            body: 'When evidence is judged irrelevant, the pipeline returns an explicit "nothing relevant found" result instead of assembling an answer from weak material anyway.',
                        },
                        {
                            title: 'Complex questions get multi-round retrieval',
                            body: 'Harder queries route to an iterative retriever that can fetch evidence across several rounds instead of a single retrieval pass, checked afterward by a separate faithfulness evaluator.',
                        },
                        {
                            title: 'A missing evaluator fails safe, not open',
                            body: 'If the quality check itself can\'t run, that\'s treated as "no confident judgment" and handled the same as an exhausted retry — never silently treated as a passing grade.',
                        },
                    ],
                    whyItMatters:
                        'The difference between a demo and something people can actually rely on is what happens when the first search doesn\'t find a good answer. A system that always just answers with whatever it retrieved will confidently answer questions it has no real basis for. This one grades its own evidence, tries again when it\'s not sure, reaches further when it genuinely needs to, and says so out loud when it can\'t find anything real — which is what makes the citations on a good answer actually mean something.',
                },
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
                deepDive: {
                    scenario: [
                        {
                            time: 'Mid-conversation',
                            text: 'A user is a few turns into a conversation with the agent. The host process restarts — a deploy, a crash, anything. When they send their next message, the conversation continues exactly where it left off, not from scratch.',
                        },
                        {
                            time: 'At the same time',
                            text: 'Two different requests for that same conversation land on two different servers at almost the same moment. Without something to stop it, both would try to continue the conversation at once — each one blind to what the other just did.',
                        },
                        {
                            time: 'Instead',
                            text: 'One of the two requests waits its turn. The first finishes, writes its messages, and only then does the second one proceed — reading everything the first one just wrote, in order.',
                        },
                        {
                            time: 'If a host dies mid-turn',
                            text: 'Its claim on the conversation automatically expires rather than blocking it forever, so a crashed server can\'t permanently freeze someone else\'s conversation.',
                        },
                    ],
                    flow: [
                        { title: 'Claim The Turn', tone: 'info', body: 'Before touching a conversation, a request has to hold an exclusive claim on it — waiting in line if someone else already holds it.' },
                        { title: 'Read, Then Write', tone: 'jargon', body: 'The prior messages are loaded, the model responds, and the new turn is appended — durably, not just in the running process\'s memory.' },
                        { title: 'Release, Or Expire', tone: 'tip', body: 'The claim is released when the turn finishes. If the host holding it dies instead, the claim times out on its own rather than freezing the conversation.' },
                        { title: 'Ownership Checked Once, Centrally', tone: 'warn', body: 'Every read or write is checked against who actually owns the conversation — enforced in one place, not re-implemented at each call site.' },
                    ],
                    narrative: [
                        'The bar for "conversation history" isn\'t just saving messages — it\'s making sure two things happening on the same conversation at once <mark class="hl">can never silently corrupt it</mark>, whether they land on one server or two.',
                        'What\'s real: conversations are stored durably rather than only in a running process\'s memory, so a restart doesn\'t lose them. On top of that, a real coordination mechanism means <mark class="hl">only one turn can ever run against a conversation at a time</mark> — a second request arriving mid-turn waits, rather than the two interleaving and producing a transcript where messages land out of causal order. And if the host running a turn crashes mid-way, that claim <mark class="hl">expires on its own instead of freezing the conversation</mark> for everyone else. Ownership is enforced the same way everywhere: <mark class="hl">one check, in the storage layer itself</mark>, not something each new piece of code has to remember to re-verify.',
                        'This is marked <mark class="hl">"Built," not "Partial"</mark> — durability, the turn-claiming mechanism, and the ownership check are all real and all exercised on every conversation, on the default local storage.',
                    ],
                    techTable: {
                        columns: ['Storage Backend', 'Safe For', 'Caveat'],
                        rows: [
                            ['SQLite via EF Core (default)', 'Multiple server processes on one machine', 'Its safety comes from the database file\'s own locking — it doesn\'t extend across a network share to multiple machines.'],
                            ['One JSON file per conversation', 'Single-process local development only', 'Its write protection is an in-process lock; two hosts sharing the same file can produce a corrupted record.'],
                        ],
                    },
                    engineeringFacts: [
                        {
                            title: 'The turn-claiming mechanism works across separate processes',
                            body: 'It\'s not just an in-memory lock — a claim taken by one server is visible to a completely different server process talking to the same storage.',
                        },
                        {
                            title: 'A lost claim is made visible, not silent',
                            body: 'If a host\'s claim on a conversation expires while it still believes it holds it (a stall, a crash, a paused process), that\'s surfaced explicitly rather than letting the host keep writing as if nothing changed.',
                        },
                        {
                            title: 'No guaranteed order between two waiting requests',
                            body: 'If two requests are queued for the same conversation at once, the system doesn\'t promise which one goes first across different servers — only that they can never run at the same time.',
                        },
                        {
                            title: 'One ownership check, not several',
                            body: 'The check for "does this caller actually own this conversation" lives in exactly one place in the storage layer. Earlier versions of this codebase had it hand-copied into as many as six separate call sites — now every one of them just relies on the store to refuse an unauthorized read or write.',
                        },
                        {
                            title: 'The storage layer and the turn-claiming mechanism are deliberately paired',
                            body: 'Swapping one without the other breaks: a durable claim expects to find the same record the durable store manages, and pairing a durable store with an in-memory claim only protects one server against itself.',
                        },
                    ],
                    whyItMatters:
                        'A chatbot that just appends messages to a list looks fine until two requests hit it at once, or the server restarts mid-conversation — and then it silently produces a transcript that doesn\'t reflect what actually happened. Making concurrency and durability correct together, instead of bolting durability onto something that already assumed a single process, is exactly the unglamorous work that determines whether a multi-server deployment behaves like one system or like several ones quietly stepping on each other.',
                },
            },
            {
                id: 'agent-state',
                name: 'Agent State',
                status: 'built',
                exec: 'An in-progress multi-step plan can be checkpointed and resumed rather than starting over.',
                eng: 'EfCorePlanStateStore persists PlanGraph execution state for PlanExecutor’s checkpoint/resume.',
                deepDive: {
                    scenario: [
                        {
                            time: 'Step 4 of 7',
                            text: 'A multi-step plan is midway through. Steps 1 through 3 have genuinely finished, step 4 is actively running, and 5 through 7 haven\'t started yet. The process crashes — a deploy, an outage, anything.',
                        },
                        {
                            time: 'On restart',
                            text: 'The plan doesn\'t start over. Steps 1–3 stay marked done. Step 4 — the one that was actually running when everything stopped — is reset to "ready to try again," since nobody can know if it half-finished. Steps 5–7 are untouched.',
                        },
                        {
                            time: 'Resumed',
                            text: 'Execution picks back up from step 4 as if the crash were a hiccup, not a disaster. The plan\'s history records exactly when it resumed, for anyone auditing later.',
                        },
                        {
                            time: 'If step 4 then genuinely fails',
                            text: 'It\'s retried automatically, waiting a little longer between each attempt. Once its retry budget runs out, what happens next is a deliberate policy choice — not a hardcoded crash.',
                        },
                    ],
                    flow: [
                        { title: 'Checkpoint As You Go', tone: 'info', body: 'Each step\'s state is durably saved as it changes — not just held in the memory of whichever process happens to be running the plan.' },
                        { title: 'Crash Doesn\'t Mean Restart', tone: 'jargon', body: 'On resume, finished steps stay finished. Only the step that was actually running when things stopped gets reset to try again.' },
                        { title: 'Retry With Increasing Patience', tone: 'tip', body: 'A step that fails outright is retried automatically, waiting longer between each attempt, up to a configured limit.' },
                        { title: 'Choose What "Give Up" Means', tone: 'warn', body: 'Once retries are exhausted, the plan\'s own policy decides: fail just that step, skip it and move on, fail the whole plan, or hand it to a human.' },
                    ],
                    narrative: [
                        'The point of checkpointing a multi-step plan is that <mark class="hl">a crash costs at most the one step that was mid-flight</mark> — not the entire plan, and not silent data loss either.',
                        'What\'s real: every step\'s state is written durably as the plan runs, not just held in memory. On resume, the system doesn\'t naively replay everything — it specifically <mark class="hl">resets only the step that was still running when the process died</mark>, because that one is genuinely ambiguous, while everything already finished is trusted and skipped. Each resume is <mark class="hl">recorded in the plan\'s own history</mark>, so an interrupted-and-recovered plan is auditable, not indistinguishable from one that ran straight through. And failure handling isn\'t a single behavior: a step gets retried with a growing delay between attempts, and only once that budget runs out does the plan\'s own policy decide whether to <mark class="hl">fail just that step, skip it, fail the whole plan, or escalate to a person</mark>.',
                        'This is marked <mark class="hl">"Built," not "Partial"</mark> — the checkpointing, the resume-only-what-was-interrupted behavior, and all four failure policies are real and exercised on every plan run, not just the happy path.',
                    ],
                    techTable: {
                        columns: ['When Retries Run Out', 'What Happens'],
                        rows: [
                            ['Fail this step', 'Only this step is marked failed; anything that doesn\'t depend on it still runs.'],
                            ['Skip this step', 'The plan continues as if the step had completed.'],
                            ['Fail the whole plan', 'Execution stops immediately — used when the failed step is load-bearing for everything after it.'],
                            ['Escalate', 'Hands off to a human for manual intervention instead of failing automatically.'],
                        ],
                    },
                    engineeringFacts: [
                        {
                            title: 'Only the interrupted step is reset on resume',
                            body: 'Genuinely completed steps are never re-run after a crash — only a step caught mid-execution is put back to "ready," because its true outcome is unknown.',
                        },
                        {
                            title: 'Resume is a distinct, logged event',
                            body: 'Resuming a plan writes its own entry to the plan\'s execution history, separate from loading its current state for a status check.',
                        },
                        {
                            title: 'Three backoff shapes for retries',
                            body: 'A failing step can be retried with a fixed delay, a linearly growing one, or an exponentially growing one — a configurable choice per plan, not a single fixed policy.',
                        },
                        {
                            title: 'Retries counted as attempts, not extra tries',
                            body: 'A "3 retries" policy runs a step at most 4 times total (the original attempt plus 3 retries) — an easy off-by-one that\'s handled deliberately.',
                        },
                        {
                            title: 'Resuming is ownership-checked before anything is touched',
                            body: 'A plan can only be resumed by the caller who owns it — checked before the resume writes anything, not after.',
                        },
                    ],
                    whyItMatters:
                        'Long-running, multi-step work is exactly where a naive "just retry the whole thing" approach falls apart — steps that call real external systems (sending something, charging something, provisioning something) can\'t simply be re-run for free. Being precise about exactly what needs to resume, and giving the plan an explicit say in what "this step keeps failing" should mean, is what makes it safe to let an agent run something that takes minutes or hours instead of seconds.',
                },
            },
            {
                id: 'agent-registry',
                name: 'Agent Registry',
                status: 'partial',
                exec: 'A real registry does discover and cache every agent on disk — but it only loads once, at first use, and there\'s no way to add, remove, or refresh an agent without restarting the process.',
                eng: 'IAgentMetadataRegistry scans AGENT.md manifests, caches them for the process lifetime with no invalidation, and supports lookup by id/category/tags; a decorator layers in bundle-scoped agents without touching the base cache.',
                deepDive: {
                    scenario: [
                        {
                            time: 'Deploy time',
                            text: 'A handful of AGENT.md files sit in a few configured folders, each describing one agent.',
                        },
                        {
                            time: 'First request',
                            text: 'The very first time anything actually needs the list of agents, the folders get scanned, each manifest gets parsed, and the result is cached in memory.',
                        },
                        {
                            time: 'Every request after that',
                            text: 'Looking an agent up by id, category, or tag is instant — answered straight from that cache, no disk access involved.',
                        },
                        {
                            time: 'Ten minutes later',
                            text: 'Someone drops a brand-new AGENT.md file into one of those folders, expecting the new agent to show up right away. It doesn\'t — the running process is still serving its original snapshot. Only a restart makes the new agent visible.',
                        },
                    ],
                    flow: [
                        { title: 'Wait For First Ask', tone: 'info', body: 'Nothing gets scanned at startup — the folders are only read the first time something actually asks what agents exist.' },
                        { title: 'Scan & Cache', tone: 'jargon', body: 'Every configured folder is walked, each manifest is parsed, and the whole result is cached in memory for as long as the process runs.' },
                        { title: 'Serve From Memory', tone: 'tip', body: 'Every lookup after that — by id, category, or tag — is answered from the cache, not by touching the filesystem again.' },
                        { title: 'Frozen Until Restart', tone: 'warn', body: 'A manifest added, edited, or removed on disk is invisible to a running process — there\'s no built-in way to make it notice.' },
                    ],
                    narrative: [
                        'A "registry" for agents is meant to be the directory of what exists and how to find it — the thing everything else asks instead of independently poking around the filesystem.',
                        'Correcting the record here: a genuine, dedicated service does this — <mark class="hl">not just a controller</mark> — with its own tests, its own caching behavior, and lookup by id, category, and tags. There\'s even a decorator that layers in agents scoped to an active bundle without polluting the base list. That part is real and already built.',
                        'What keeps this "partial": the registry is <mark class="hl">read-only and loads exactly once</mark>. Once a process starts serving traffic, its view of "what agents exist" is <mark class="hl">frozen until that process restarts</mark> — there\'s no endpoint to refresh it, and no way to register or retire an agent except by changing files on disk and cycling the host. The piece the name implies — a real registry — exists. The piece a "management" claim implies — adding, removing, or refreshing at runtime — doesn\'t yet.',
                    ],
                    techTable: {
                        columns: ['Capability', 'Status'],
                        rows: [
                            ['Discover agents from their manifest files', 'Real'],
                            ['Look up an agent by id, category, or tag', 'Real'],
                            ['Layer in bundle-scoped agents without touching the base list', 'Real'],
                            ['Add or remove an agent while the process is running', 'Not available — requires a restart'],
                            ['Refresh the cache on demand', 'Not available'],
                        ],
                    },
                    engineeringFacts: [
                        {
                            title: 'Lazy first-load, not startup-load',
                            body: 'Nothing is scanned until the first real call needs it — deliberately mirroring how this codebase\'s skill discovery already works, per its own doc comments.',
                        },
                        {
                            title: 'Bounded, leaf-stopping scan',
                            body: 'Search depth is capped at 3 folder levels, and any folder containing an <code>AGENT.md</code> is treated as a leaf — it isn\'t recursed into further.',
                        },
                        {
                            title: 'A bad manifest degrades quietly',
                            body: 'A malformed <code>AGENT.md</code> is logged and skipped rather than crashing discovery for every other agent.',
                        },
                        {
                            title: 'Bundle agents are layered, not merged in',
                            body: 'The bundle-aware decorator adds scoped agents on top of a lookup; the base registry\'s own cache never stores or sees them.',
                        },
                        {
                            title: 'This entry corrects an earlier, inaccurate description',
                            body: 'An older pass on this page claimed no dedicated registry existed at all. Re-checked directly against the current code for this write-up — it does; the real gap is the lack of runtime refresh.',
                        },
                    ],
                    whyItMatters:
                        '"Knowing what agents exist" sounds trivial until you actually need to add one without downtime, or you change a manifest and can\'t figure out why nothing changed. A real discovery service that\'s frozen until restart is genuinely fine for a small, mostly-static set of agents — but it isn\'t yet the kind of registry an operations team could add or retire agents against live.',
                },
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
