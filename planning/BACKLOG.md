# Backlog

Work items intentionally deferred from the current session. Each item should be actionable — include rationale and acceptance criteria.

---

## Observability / Agent-assisted Debugging

### Telemetry MCP Server (diagnostic surface for in-session debugging)

**Rationale.** AgentHub already emits rich telemetry — OTLP traces/metrics to `localhost:4317`, a `SignalRSpanExporter`, PII filtering, LLM token tracking, rate limiting, tail-based sampling. None of it is currently reachable by a developer assistant (Claude, Copilot, etc.) without the human pasting logs into the chat. The dev-loop tax is real: every troubleshooting turn requires the human to manually shuttle log excerpts.

A log file sink (Serilog → `logs/agenthub-{date}.ndjson`) was added as a tactical fix so assistants can `Read` / `Grep` structured JSON. That's sufficient for ad-hoc debugging but does not surface traces, span relationships, LLM cost data, or tool-invocation timing.

**Proposal.** Build an MCP server that wraps the existing telemetry infrastructure and exposes it as MCP tools. This fits the project's MCP-first posture and lets any MCP-aware assistant self-serve diagnostics.

**Candidate tools to expose:**

| Tool | Purpose |
|------|---------|
| `get_recent_logs(level?, since?, trace_id?)` | Tails the `.ndjson` log file with filters. |
| `get_recent_spans(since?, trace_id?, conversation_id?)` | Returns buffered spans from the `SignalRSpanExporter` pipeline. |
| `get_trace(trace_id)` | Reconstructs a trace tree from span buffer or Jaeger. |
| `get_llm_token_usage(conversation_id?, since?)` | Surfaces output from `LlmTokenTrackingProcessor`. |
| `get_tool_effectiveness(tool_name?, since?)` | Surfaces output from `ToolEffectivenessProcessor`. |
| `get_conversation_timeline(conversation_id)` | Composite view: spans + logs + tool calls + token stream timing for one turn. |

**Acceptance criteria:**
1. New project: `Infrastructure.Observability.McpServer` (keep diagnostic surface separate from `Infrastructure.AI.MCPServer` which serves production tools).
2. Dev-only registration — guarded by `IsDevelopment()` and an explicit config flag (`Diagnostics:ExposeMcp=true`).
3. Auth: reuse the dev-auth handler; never expose unauthenticated in any non-Development environment.
4. Scope: read-only. No endpoint should mutate state or replay traffic.
5. Extend to `Presentation.ConsoleUI` and `Infrastructure.AI.MCPServer` once the shape is proven.
6. Documentation: `docs/diagnostics.md` explaining how to wire it into Claude Code / Cursor / Copilot as an MCP server.

**Non-goals:**
- This is not a replacement for Jaeger, Grafana, or Azure Monitor. It is a programmatic surface for in-session agent debugging.
- No historical storage beyond what already lives in the span buffer and log files.

### Companion: OpenTelemetry Browser SDK → distributed traces

Follow-on to the current browser→`/api/client-logs` pipeline. Replace the hand-rolled log shipper with `@opentelemetry/sdk-trace-web` + `@opentelemetry/exporter-trace-otlp-http`, pointing at the existing OTel collector (`localhost:4317` today, or a CORS-friendly proxy in dev). Benefits:

- W3C trace-context correlation: every `fetch`/SignalR call auto-injects `traceparent`, so a browser interaction ties to the server request and agent turn in one trace view.
- The telemetry MCP tools above (`get_trace`, `get_conversation_timeline`) automatically surface browser spans alongside server spans — no separate "client logs" query needed.
- Replaces `browserLogger.ts`: console wrappers keep shipping events, but they become OTel log records instead of custom POSTs.

Depends on: exposing an OTLP HTTP receiver that the browser can reach (the current gRPC endpoint at 4317 is browser-unfriendly). Likely an `http/protobuf` receiver on the collector plus CORS config.

**Estimated effort.** ~2–3 hours (scaffold new project + implement 3 tools + docs). Remaining tools can land incrementally.

**Why deferred.** Scope too large for the current debugging session. Log file sink (Serilog) provides 80% of the immediate value; this is the "right" answer when a future session focuses on diagnostics tooling rather than fixing a specific bug.
