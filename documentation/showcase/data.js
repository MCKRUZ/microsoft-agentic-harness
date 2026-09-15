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
        ],
    },
];
