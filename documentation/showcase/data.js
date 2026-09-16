/**
 * Content for the Capability Showcase page. Plain browser-loaded data — no build step.
 * Every fact here traces to a real page in ../onboarding/ (linked via docLink). This file
 * is a visual INDEX into that documentation, not a replacement for it — keep entries short;
 * link out for depth.
 *
 * Shape: Layer { id, name, tagline, exec, eng, docLink, components: Component[] }
 *        Component { id, name, exec, eng, tech? }
 * `exec` = plain-English, no jargon. `eng` = real technical detail, names real types/mechanisms.
 */
window.SHOWCASE_LAYERS = [
    {
        id: 'skills',
        name: 'Skills System',
        tagline: 'Markdown files that teach an agent what it knows and when to use it',
        exec: 'Skills are short instruction documents — written in plain text, not code — that tell an AI agent what role to play and what it is allowed to do. The system only loads the parts it needs for the current task, so adding more skills never slows things down or blows up costs.',
        eng: 'Each skill is a SKILL.md file loaded through three tiers of progressive disclosure: a ~100-token index card always in context, a ~5,000-token instruction body loaded only when the skill is selected, and unbounded Tier-3 resources (scripts, references, templates) loaded only when a tool actually opens them. SkillMetadataParser parses each file into a SkillDefinition; multi-skill agents merge several via AgentExecutionContextFactory, with prerequisite ordering enforced across the set.',
        docLink: '../05-skills.html',
        components: [
            {
                id: 'progressive-disclosure',
                name: 'Progressive Disclosure',
                exec: 'Only pulls in as much detail as is actually needed right now, so the system stays fast even with dozens of skills installed.',
                eng: 'Three-tier loading (index card / full instructions / on-demand resources) keeps a 50-skill install to roughly 5K tokens of startup overhead.',
                tech: 'SKILL.md · SkillDefinition',
            },
            {
                id: 'multi-skill-agents',
                name: 'Multi-Skill Agents',
                exec: 'One agent can combine several skills at once, with dependencies handled automatically — a "gather data" skill finishes before an "analyze" skill that needs its output starts.',
                eng: 'AgentExecutionContextFactory merges instructions and tool lists from every referenced skill; Prerequisites + CompletionTool enforce ordering across the merged set.',
                tech: 'AgentExecutionContextFactory',
            },
            {
                id: 'skill-training',
                name: 'Skill Amendments & Learnings',
                exec: 'Skills are not frozen — the system can propose and test small improvements to a skill’s own instructions based on what it learned from real runs.',
                eng: 'The meta-harness proposes SkillAmendments from execution-trace analysis; these flow through the same loader pipeline into the next session’s Tier-2 content.',
                tech: 'SkillAmendment',
            },
        ],
    },
    {
        id: 'plugins',
        name: 'Plugin System',
        tagline: 'Package a skill and its tools into a portable, permission-fenced bundle',
        exec: 'A plugin bundles one or more skills and the specific tools they need into a single, self-contained folder your team can install or share — with an operator-level dial that can allow certain tools and deny others outright, with no exceptions, so a third-party plugin can never quietly do more than it was given permission to.',
        eng: 'Declared in appsettings.json (Path, AllowedTools, DeniedTools, AutonomyLevel), loaded via IPluginLoader/IPluginManifestReader against a plugin.json manifest, tracked by IPluginRegistry. Plugin skills load in Injected mode, receiving all of their plugin’s MCP tools automatically. DeniedTools is bypass-immune — an unresolvable entry denies boot if resolvable at startup, or marks the whole plugin’s boundary faulted (denying every tool from its skills, agent-wide) once every configured server has reported and it’s still unresolved.',
        docLink: '../06-tools.html',
        components: [
            {
                id: 'boundary-governance',
                name: 'Boundary Governance',
                exec: 'Lets an operator grant a plugin real capability while guaranteeing it can never touch what is explicitly off-limits — even if the agent is running in its most autonomous, least-supervised mode.',
                eng: 'AllowedTools/DeniedTools resolved at startup or lazily as MCP servers report in; DeniedTools cannot be overridden by any autonomy tier.',
                tech: 'PluginDeclaration',
            },
            {
                id: 'injected-skills',
                name: 'Injected Mode',
                exec: 'A plugin’s own skills automatically get access to all of that plugin’s tools, so the plugin’s author — not the harness operator — controls exactly what their bundle can do internally.',
                eng: 'SkillMode.Injected passes through a plugin’s MCP tools automatically, bypassing per-skill allowed-tools declarations; the boundary above is the real enforcement layer, not the skill itself.',
                tech: 'SkillMode.Injected',
            },
        ],
    },
    {
        id: 'tools',
        name: 'Tools & Keyed DI',
        tagline: 'How the agent gets hands — and how every action stays sandboxed and governed',
        exec: 'Tools are the concrete actions an agent can take — read a file, search a document, run a calculation. Every single tool call passes through the same safety checkpoint before it runs, and file access is fenced to an explicit allow-list, so even a confused or manipulated agent cannot reach outside its sandbox.',
        eng: 'Tools implement ITool, registered as keyed singletons resolved by the same string name a skill declares in allowed-tools (AddKeyedSingleton<ITool>). Converted to Microsoft.Extensions.AI’s AITool via AIToolConverter, then every tool — internal, MCP, or plugin-provided — is wrapped by GovernedAIFunction, the single invocation-time chokepoint that runs three ordered gates (authorization, DLP classification, spin/progress guard) before execution.',
        docLink: '../06-tools.html',
        components: [
            {
                id: 'keyed-di',
                name: 'Keyed DI Resolution',
                exec: 'A skill just names the tool it needs as a plain word in a text file, and the system looks up the real implementation behind that name at runtime.',
                eng: 'sp.GetRequiredKeyedService<ITool>(toolName) — lets a skill’s allowed-tools list stay a flat string list instead of a hand-written switch statement.',
                tech: 'Keyed DI (.NET 8)',
            },
            {
                id: 'governed-invocation',
                name: 'Governed Invocation',
                exec: 'Every tool call, no matter where the tool came from, passes through the same three safety checks before anything actually happens.',
                eng: 'GovernedAIFunction.InvokeCoreAsync runs IToolInvocationGovernor → IToolClassificationGate → IProgressEvaluator, in order, each ambient and independently opt-in.',
                tech: 'GovernedAIFunction',
            },
            {
                id: 'output-compression',
                name: 'Output Compression & Spill',
                exec: 'Large tool results get automatically trimmed to fit, and if something is still too big, the agent can page through the rest instead of losing it.',
                eng: 'ToolOutputCompressionBehavior applies a content-type-specific strategy above a token threshold; anything still over the hard character ceiling is spilled and retrievable via tool_result_fetch.',
                tech: 'ToolOutputCompressionBehavior',
            },
        ],
    },
    {
        id: 'mcp',
        name: 'MCP — Model Context Protocol',
        tagline: 'The open standard for plugging in tools built by anyone, anywhere',
        exec: 'MCP is a shared language for AI tools, so the harness can call tools built by other teams — and let other AI systems call tools built here — without custom integration work for each one. Every tool coming from outside is automatically screened for hidden or malicious instructions before the agent ever sees it.',
        eng: 'Server side (Infrastructure.AI.MCPServer) exposes this harness’s own tools, prompts, and resources over HTTP with JWT auth and rate limiting. Client side (Infrastructure.AI.MCP) discovers tools from configured external servers at startup, casting the SDK’s McpClientTool directly to AITool — no wrapper class, no keyed DI. EnableMcpSecurity scans every discovered tool’s name, description, and parameter schema for prompt injection and typosquatting before publication; findings at or above McpToolBlockThreshold withhold the tool entirely.',
        docLink: '../08-mcp.html',
        components: [
            {
                id: 'mcp-server',
                name: 'Server — Expose',
                exec: 'Lets other AI agents call into this harness’s own capabilities over a standard web connection, with real authentication and rate limits.',
                eng: 'ASP.NET Core WebAPI: .WithHttpTransport().LoadTools().LoadPrompts().LoadResources(), JWT Bearer auth via Entra ID.',
                tech: 'Infrastructure.AI.MCPServer',
            },
            {
                id: 'mcp-client',
                name: 'Client — Consume',
                exec: 'Connects out to tools hosted anywhere else and makes them available to the agent exactly like a built-in tool — the agent cannot tell the difference.',
                eng: 'Discovers tools via tools/list at startup; supports HTTP or stdio transport and Bearer/Entra/ApiKey outbound authentication.',
                tech: 'Infrastructure.AI.MCP',
                techs: ['HTTP Transport', 'stdio Transport'],
            },
            {
                id: 'mcp-security-scanning',
                name: 'Security Scanning',
                exec: 'Every external tool’s description is checked for hidden instructions before the agent trusts it — the same text that could poison the agent’s behavior is the text this scan inspects.',
                eng: 'Scans name, description, and parameter schema at discovery time for tool poisoning, description injection, and homoglyph typosquatting.',
                tech: 'EnableMcpSecurity',
            },
        ],
    },
    {
        id: 'orchestration',
        name: 'Agent Harness & Orchestration',
        tagline: 'The engine that turns one user message into a governed, observable agent turn',
        exec: 'This is the assembly line every request runs through: check it is safe, figure out which skill applies, build or reuse the right agent, run it, and record exactly what happened. You almost never need to touch this directly — new capabilities get added by writing a skill or a tool, not by changing this pipeline.',
        eng: 'ExecuteAgentTurnCommand flows through roughly 14 ordered MediatR pipeline behaviors (validation, identity resolution, audit, content safety, prompt injection, token budget, hooks, response sanitization, tool-output compression, knowledge extraction) before ExecuteAgentTurnCommandHandler resolves skills, gets-or-builds an agent via AgentConversationCache/AgentFactory, and calls agent.RunAsync — the LLM-call and tool-dispatch loop owned by Microsoft.Agents.AI.',
        docLink: '../04-message-journey.html',
        components: [
            {
                id: 'pipeline-behaviors',
                name: 'Pipeline Behaviors',
                exec: 'A chain of independent safety and logging checks every request passes through — each one can be switched off individually, and none of them know about each other.',
                eng: 'IPipelineBehavior<TRequest,TResponse> wrappers around every MediatR command; registration order is load-bearing (e.g. validation runs before audit, which runs before content safety).',
                tech: 'MediatR',
            },
            {
                id: 'agent-caching',
                name: 'Agent Caching',
                exec: 'Reuses the same configured agent for the rest of a conversation instead of rebuilding it from scratch on every single message.',
                eng: 'AgentConversationCache.GetOrCreateAsync keys by (conversationId, skillId); a cache hit skips skill lookup, tool resolution, and client construction.',
                tech: 'AgentConversationCache',
            },
            {
                id: 'loop-guards',
                name: 'Loop Guards',
                exec: 'Automatically stops an agent that is stuck repeating itself, or that has run up too large a bill across a long conversation.',
                eng: 'IProgressEvaluator (spin detection on repeated identical tool calls) and IConversationBudgetTracker (cross-turn token ceiling) both watch the same RunAsync loop.',
                tech: 'IProgressEvaluator',
            },
        ],
    },
    {
        id: 'observability',
        name: 'Observability & Safety',
        tagline: 'See inside every agent turn, and stop it when it goes wrong',
        exec: 'Every step an agent takes — every AI call, every tool use — is recorded in a searchable timeline, so when something goes wrong you can see exactly where and why. On top of that sits a stack of independent safety checks: filtering harmful content, requiring human sign-off on risky actions, and a tamper-evident audit log nobody can quietly edit after the fact.',
        eng: 'Full OpenTelemetry instrumentation (nested spans per command, turn, tool call, and LLM request) exports to Jaeger, Prometheus, and optionally Azure Monitor, backed by a durable PostgreSQL audit store and a hash-chained JSONL log for tamper-evidence. Safety is eight independent layers — prompt-injection detection, content-safety middleware, GovernedAIFunction’s three-gate tool authorization, permission/autonomy-tier resolution, plugin-boundary governance, the file-system sandbox, MCP security scanning, and response sanitization — plus human escalation for decisions that exceed the agent’s authority.',
        docLink: '../10-observability.html',
        components: [
            {
                id: 'distributed-tracing',
                name: 'Distributed Tracing',
                exec: 'Lets you replay exactly what an agent did, step by step, down to which specific AI call or tool use caused a failure.',
                eng: 'Nested OpenTelemetry spans (command → turn → tool → LLM call) with GenAI semantic-convention attributes, exported to Jaeger.',
                tech: 'OpenTelemetry',
                techs: ['Jaeger', 'Prometheus', 'Azure Monitor'],
            },
            {
                id: 'graded-autonomy',
                name: 'Graded Autonomy & Escalation',
                exec: 'Risky actions can require a human’s sign-off before they happen, with the level of caution automatically matched to how much damage that action could do.',
                eng: 'A tool’s RiskTier (BlastRadius) feeds the permission resolver’s autonomy-tier gate; a PendingApproval decision blocks fail-closed rather than routing to live mid-call escalation.',
                tech: 'IToolInvocationGovernor',
            },
            {
                id: 'meta-harness',
                name: 'Meta-Harness Self-Improvement',
                exec: 'The system can review its own past failures, propose changes to a skill’s instructions, and test that proposal against a benchmark before adopting it — a built-in feedback loop for getting better over time.',
                eng: 'RunHarnessOptimizationCommand: snapshot → propose (an agent reads execution traces) → evaluate against a benchmark task suite → regression-gate against prior winners → promote.',
                tech: 'Meta-harness loop',
            },
        ],
    },
    {
        id: 'rag',
        name: 'RAG Pipeline',
        tagline: 'Grounding agent answers in your real documents, not just training data',
        exec: 'Retrieval-Augmented Generation is how the agent answers questions about your own documents accurately — it finds the genuinely relevant passages first, then has the AI write an answer based on what it found, with citations back to the source.',
        eng: 'A five-stage pipeline (Infrastructure.AI.RAG): ingestion (parse, chunk, enrich, embed, index), query transformation (RAG Fusion, HyDE), hybrid retrieval (dense vector + BM25 fused via Reciprocal Rank Fusion), reranking plus CRAG quality evaluation (accept/refine/reject), and token-budgeted assembly with citation tracking. An optional Azure AI Search agentic-retrieval backend drops in behind the same IHybridRetriever interface. Three advanced phases — complexity routing, multi-hop retrieval, full autonomy — layer on top, each gated behind config.',
        docLink: '../07-rag.html',
        components: [
            {
                id: 'hybrid-retrieval',
                name: 'Hybrid Retrieval',
                exec: 'Combines two different search styles — matching by meaning and matching by exact keyword — so it catches both paraphrased questions and precise terms like product codes.',
                eng: 'Dense (vector) and sparse (BM25) retrieval fused via Reciprocal Rank Fusion, k ≈ 60.',
                tech: 'Reciprocal Rank Fusion',
            },
            {
                id: 'crag',
                name: 'CRAG Quality Gate',
                exec: 'Checks how trustworthy the retrieved information actually is before answering, and can say "I don’t have good sources for this" instead of guessing.',
                eng: 'The Corrective RAG evaluator scores reranked results against configurable accept/refine/reject thresholds.',
                tech: 'CRAG',
            },
            {
                id: 'complexity-routing',
                name: 'Complexity Routing',
                exec: 'Simple questions get a fast, cheap answer path; hard questions automatically get the full, more thorough treatment — without anyone having to say which is which.',
                eng: 'An LLM classifier scores query complexity and routes to a tiered pipeline, saving an estimated 30–50% of retrieval cost on mixed workloads.',
                tech: 'Complexity routing (Phase A)',
            },
            {
                id: 'retrieval-backend',
                name: 'Retrieval Backend',
                exec: 'The actual search engine behind the retrieval step is swappable — a team can start with the built-in option and move to a managed or specialized one later without rewriting the pipeline around it.',
                eng: 'Every backend sits behind the same IHybridRetriever interface, so swapping one in is a configuration change, not a rewrite.',
                techs: ['Dense + BM25 Hybrid (default)', 'Azure AI Search (agentic retrieval)', 'FAISS', 'SQLite FTS5'],
            },
        ],
    },
    {
        id: 'knowledge-graph',
        name: 'Knowledge Graph',
        tagline: 'Structured, persistent memory of entities and relationships, not just text chunks',
        exec: 'Beyond searching documents, the harness can build and remember a map of entities and how they relate — who worked on what, what depends on what — so an agent’s understanding compounds across sessions instead of starting fresh every time, with old or irrelevant facts fading out automatically.',
        eng: 'Infrastructure.AI.KnowledgeGraph is a working graph-backed retrieval layer (Neo4j or PostgreSQL in production, in-memory for dev/tests) behind one interface, with Leiden community detection for graph-RAG, feedback-weighted retrieval that learns which paths were useful, and cross-session memory (Remember/Recall/Forget/Improve) with configurable decay tiers. Every node and edge is provenance-stamped and retention-enforced; TenantIsolatedGraphStore scopes data by user, dataset, and owner so multiple agents share infrastructure without leaking knowledge.',
        docLink: '../07-rag.html',
        components: [
            {
                id: 'cross-session-memory',
                name: 'Cross-Session Memory',
                exec: 'The agent can remember facts from past conversations and bring them back up later, with older or less-useful memories naturally fading rather than piling up forever.',
                eng: 'KnowledgeMemoryService’s Remember/Recall/Forget/Improve, backed by InMemorySessionCache and CRITICAL/STANDARD/EPHEMERAL decay tiers.',
                tech: 'KnowledgeMemoryService',
            },
            {
                id: 'multi-tenant-isolation',
                name: 'Multi-Tenant Isolation',
                exec: 'Different users or agents can safely share the same underlying system without ever seeing each other’s private information.',
                eng: 'TenantIsolatedGraphStore enforces user → dataset → owner scope boundaries on every read and write.',
                tech: 'TenantIsolatedGraphStore',
            },
            {
                id: 'feedback-weighted-retrieval',
                name: 'Feedback-Weighted Retrieval',
                exec: 'The graph learns over time which pieces of information were actually useful in past answers, and favors those paths in future searches.',
                eng: 'GraphFeedbackStore + LlmFeedbackDetector blend historical usefulness weights into graph-RAG ranking.',
                tech: 'GraphFeedbackStore',
            },
            {
                id: 'graph-store-backend',
                name: 'Graph Store Backend',
                exec: 'The underlying database that stores the knowledge graph is swappable — a small deployment can start lightweight and a large one can move to a dedicated graph engine, all behind the same interface.',
                eng: 'One interface backs multiple stores in production (Neo4j or PostgreSQL), with an in-memory implementation for dev/tests.',
                techs: ['Neo4j', 'Kuzu', 'PostgreSQL', 'In-memory (dev/test)'],
            },
        ],
    },
];

/**
 * Technology comparison entries referenced by any component's `techs` array above.
 * Written for what's actually true of each option in an agent-harness context, not
 * generic industry pros/cons. Shape: { blurb, pros: string[], cons: string[], best, maturity }.
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
 * Glossary of jargon that already appears in the exec/eng copy above — defines terms the
 * page uses, introduces nothing new. Shape: term -> plain-English definition.
 */
window.SHOWCASE_GLOSSARY = {
    MCP: 'Model Context Protocol — the open standard this harness uses to expose and consume AI tools.',
    JWT: 'JSON Web Token — a signed, compact envelope for identity claims.',
    'Entra ID': "Microsoft's cloud identity platform (formerly Azure AD).",
    'Keyed DI': 'Dependency Injection where implementations are resolved by a string or enum key, not just by type.',
    MediatR: 'A .NET library implementing the mediator pattern for in-process request/response and pipeline behaviors.',
    BM25: 'A classic keyword-ranking algorithm used by search engines.',
    'Reciprocal Rank Fusion': 'A method for merging several ranked result lists into one combined ranking.',
    CRAG: 'Corrective Retrieval-Augmented Generation — scores retrieved results for trustworthiness before answering with them.',
    HyDE: 'Hypothetical Document Embeddings — generates a hypothetical answer first, then searches using that as the query.',
    OpenTelemetry: 'An open standard for collecting distributed traces, metrics, and logs.',
    JSONL: 'JSON Lines — a text format with one JSON object per line, easy to append to.',
    'Hash-Chained Log': "A tamper-evident log where each entry's hash includes the previous entry's hash.",
    'Prompt Injection': "An attack where malicious text tries to override an AI system's instructions.",
    Typosquatting: 'Registering a name that looks nearly identical to a trusted one, to trick users or systems into using it.',
    'Blast Radius': 'How much damage a single action could cause if it goes wrong.',
    'Spin Detection': 'Noticing when an agent is stuck repeating the same action without making progress.',
    'Leiden Community Detection': 'A graph algorithm for finding clusters of closely-related nodes.',
    'Progressive Disclosure': "Revealing information only as it's needed, instead of all at once.",
    Provenance: 'A record of where a piece of data came from and how it was derived.',
    'Decay Tier': 'A policy for how quickly a stored memory fades or expires.',
    'Multi-Tenant Isolation': "Keeping different users' or customers' data provably separate on shared infrastructure.",
    'Autonomy Tier': "A graded level of independence an agent is allowed to operate at before requiring a human's sign-off.",
    Sandbox: 'An isolated execution environment that limits what a process can access or affect.',
    DAG: 'Directed Acyclic Graph — a dependency structure with no circular references.',
    CQRS: 'Command/Query Responsibility Segregation — separating operations that change state from operations that read it.',
};
