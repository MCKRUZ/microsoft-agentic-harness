# Dashboard-to-Metric Mapping

> Generated 2026-04-22. Cross-references all 14 Grafana dashboards against
> the 12 metric classes and 7 span processors in the codebase.

## Naming Convention

| Layer | Format | Example |
|-------|--------|---------|
| Code (Domain conventions) | `agent.tokens.input` | `TokenConventions.Input` |
| OTel Collector Prometheus exporter | `agentic_harness_agent_tokens_input` | namespace `agentic_harness` + dots→underscores |
| Prometheus histogram suffixes | `_bucket`, `_sum`, `_count` | appended automatically |

---

## Status Legend

| Symbol | Meaning |
|--------|---------|
| LIVE | Metric exists in Prometheus, fires from WebUI path |
| BATCH-ONLY | Metric exists in code but only fires from ConsoleUI/RunConversationCommand (not WebUI) |
| NEEDS-TOOLS | Metric fires only when LLM actually invokes tools (tool execution spans required) |
| NEEDS-MCP | Metric fires only when MCP servers are configured and queried |
| NEEDS-SAFETY | Metric fires only when content safety middleware is enabled + triggered |
| NEEDS-BUDGET | Metric fires only when BudgetTrackingService observable gauges are registered |
| NEEDS-CONTEXT | Metric fires only from TieredContextAssembler during skill loading |
| DEAD | Metric name defined in conventions but no recording code found |

---

## Dashboard 1: Agent Config (`agentConfig.json`)

| Panel | Prometheus Metric | Code Metric | Recorded By | Status |
|-------|-------------------|-------------|-------------|--------|
| Agent Configurations | `agentic_harness_agent_config_info` | `agent.config_info` | `AgentConfigInfoService` (observable gauge) | **LIVE** |
| Models in Use | same | same | same | **LIVE** |
| Unique Models | same | same | same | **LIVE** |
| Tools per Agent | same (label: `agent_config_tools_count`) | same | same | **LIVE** |
| Skills per Agent | same (label: `agent_config_skills_count`) | same | same | **LIVE** |

**Verdict: WORKING.** All panels use one observable gauge that fires on Prometheus scrape.

---

## Dashboard 2: Agent Framework (`agentFramework.json`)

| Panel | Prometheus Metric | Code Metric | Recorded By | Status |
|-------|-------------------|-------------|-------------|--------|
| Active Sessions | `agentic_harness_agent_session_active` | `agent.session.active` | `SessionMetrics.ActiveSessions` | **LIVE** |
| LLM Calls | `agentic_harness_agent_tokens_total_count` | `agent.tokens.total` | `LlmTokenTrackingProcessor` | **LIVE** |
| Total Tokens | `agentic_harness_agent_tokens_total_sum` | `agent.tokens.total` | `LlmTokenTrackingProcessor` | **LIVE** |
| Tool Calls | `agentic_harness_agent_orchestration_tool_call_count_total` | `agent.orchestration.tool_call_count` | `AgentTelemetryHub` (added this session) + `RunConversationCommandHandler` | **LIVE** (after fix) |
| Tokens per Turn p50/p95 | `agentic_harness_agent_tokens_tokens_per_turn_bucket` | `agent.tokens.tokens_per_turn` | `LlmTokenTrackingProcessor` | **LIVE** |
| Token Input vs Output | `agentic_harness_agent_tokens_input_sum` / `output_sum` | `agent.tokens.input` / `output` | `LlmTokenTrackingProcessor` | **LIVE** |
| Tool Calls Over Time | `agentic_harness_agent_orchestration_tool_call_count_total` | `agent.orchestration.tool_call_count` | Same as Tool Calls stat | **LIVE** (after fix) |
| Tool Calls by Agent | same | same | same | **LIVE** (after fix) |
| Subagent Spawns | `agentic_harness_agent_orchestration_subagent_spawns_total` | `agent.orchestration.subagent_spawns` | `RunOrchestratedTaskCommandHandler` | **BATCH-ONLY** |

**Verdict: MOSTLY WORKING.** 8/9 panels fire from WebUI. Subagent Spawns only fires from orchestrated task path.

---

## Dashboard 3: Analytics (`analytics.json`)

| Panel | Prometheus Metric | Code Metric | Recorded By | Status |
|-------|-------------------|-------------|-------------|--------|
| Average Usefulness Score | `agentic_harness_agent_tool_usefulness_score_*` | `agent.tool.usefulness_score` | `ToolUsefulnessProcessor` | **NEEDS-TOOLS** |
| Usefulness Score Trend | same | same | same | **NEEDS-TOOLS** |
| Usefulness by Tool | same | same | same | **NEEDS-TOOLS** |
| LLM Request Rate | `agentic_harness_agent_tool_invocations_total` | `agent.tool.invocations` | `ToolEffectivenessProcessor` | **NEEDS-TOOLS** |
| Tool Call Volume | same | same | same | **NEEDS-TOOLS** |

**Verdict: EMPTY.** All panels depend on `execute_tool` spans. The LLM must actually call tools (file_system, calculation_engine, etc.) for `ToolEffectivenessProcessor` and `ToolUsefulnessProcessor` to fire. If the LLM answers from knowledge without tool calls, these metrics never record.

**Root cause:** The agent needs to be asked questions that require tool use (e.g., "read the file at X", "list files in Y"). The LLM must decide to call a tool, which generates an `execute_tool` span that the processors pick up.

---

## Dashboard 4: Budget Alerts (`budgetAlerts.json`)

| Panel | Prometheus Metric | Code Metric | Recorded By | Status |
|-------|-------------------|-------------|-------------|--------|
| Current Spend | `agentic_harness_agent_budget_current_spend` | `agent.budget.current_spend` | `BudgetTrackingService` (observable gauge) | **NEEDS-BUDGET** |
| Budget Status | `agentic_harness_agent_budget_status` | `agent.budget.status` | `BudgetTrackingService` (observable gauge) | **NEEDS-BUDGET** |
| Warning Threshold | `agentic_harness_agent_budget_threshold_warning` | `agent.budget.threshold_warning` | `BudgetTrackingService` (observable gauge) | **NEEDS-BUDGET** |
| Critical Threshold | `agentic_harness_agent_budget_threshold_critical` | `agent.budget.threshold_critical` | `BudgetTrackingService` (observable gauge) | **NEEDS-BUDGET** |
| Spend Over Time | same metrics | same | same | **NEEDS-BUDGET** |

**Verdict: EMPTY.** Observable gauges registered in `BudgetTrackingService.RegisterGauges()`. The service must be registered in DI AND `LlmTokenTrackingProcessor` must call `IBudgetTrackingService.RecordSpend()` to update state. Likely the service isn't registered or the spend callbacks return no data because no budget periods are configured in `AppConfig.Observability`.

---

## Dashboard 5: Content Safety (`contentSafety.json`)

| Panel | Prometheus Metric | Code Metric | Recorded By | Status |
|-------|-------------------|-------------|-------------|--------|
| Total Evaluations | `agentic_harness_agent_safety_evaluations_total` | `agent.safety.evaluations` | `ContentSafetyBehavior` | **NEEDS-SAFETY** |
| Block Rate | same + `{outcome="block"}` | same | same | **NEEDS-SAFETY** |
| Blocks by Category | `agentic_harness_agent_safety_blocks_total` | `agent.safety.blocks` | `ContentSafetyBehavior` | **NEEDS-SAFETY** |
| Severity Distribution | `agentic_harness_agent_safety_severity_bucket` | `agent.safety.severity` | `ContentSafetyBehavior` | **NEEDS-SAFETY** |

**Verdict: EMPTY.** `ContentSafetyBehavior` is a MediatR pipeline behavior that only fires for commands implementing `IContentScreenable`. It also requires content safety to be enabled in `AppConfig.AI.ContentSafety`. If content safety isn't configured (no Azure Content Safety endpoint), no evaluations are recorded.

---

## Dashboard 6: Context Explorer (`contextExplorer.json`)

| Panel | Prometheus Metric | Code Metric | Recorded By | Status |
|-------|-------------------|-------------|-------------|--------|
| Budget Utilization | `agentic_harness_agent_context_budget_utilization_*` | `agent.context.budget_utilization` | `ContextBudgetMetrics` via `TieredContextAssembler` | **NEEDS-CONTEXT** |
| Compaction Events | `agentic_harness_agent_context_compactions_total` | `agent.context.compactions` | `ContextBudgetMetrics` | **NEEDS-CONTEXT** |
| Skills Loaded Tokens | `agentic_harness_agent_context_skills_loaded_tokens_*` | `agent.context.skills_loaded_tokens` | `ContextBudgetMetrics` via `TieredContextAssembler` | **NEEDS-CONTEXT** |
| System Prompt Tokens | `agentic_harness_agent_context_system_prompt_tokens_*` | `agent.context.system_prompt_tokens` | `ContextBudgetMetrics` via `TieredContextAssembler` | **LIVE** (partial) |
| Tools Schema Tokens | `agentic_harness_agent_context_tools_schema_tokens_*` | `agent.context.tools_schema_tokens` | `ContextBudgetMetrics` via `TieredContextAssembler` | **LIVE** (partial) |
| Source Tokens | `agentic_harness_agent_context_source_tokens_*` | `agent.context.source_tokens` | `ContextSourceMetrics` via `TieredContextAssembler` | **LIVE** (partial) |

**Verdict: PARTIALLY WORKING.** System prompt tokens, tools schema tokens, and source tokens fire when `TieredContextAssembler.AssembleAsync()` runs during agent creation. Budget utilization and compactions require extended conversations that exceed context limits. Skills loaded tokens requires skills to be configured.

---

## Dashboard 7: Cost Analytics (`costAnalytics.json`)

| Panel | Prometheus Metric | Code Metric | Recorded By | Status |
|-------|-------------------|-------------|-------------|--------|
| Total Cost | `agentic_harness_agent_tokens_cost_estimated_total` | `agent.tokens.cost_estimated` | `LlmTokenTrackingProcessor` | **LIVE** |
| Cache Savings | `agentic_harness_agent_tokens_cost_cache_savings_total` | `agent.tokens.cost_cache_savings` | `LlmTokenTrackingProcessor` | **LIVE** (only if model supports caching) |
| Savings Rate | both above | both above | same | **LIVE** |
| Avg Cost/Conversation | cost_estimated + `agent_orchestration_conversation_duration_count` | cost + orchestration | LlmTokenTrackingProcessor + RunConversationCommandHandler | **PARTIAL** (cost LIVE, denominator BATCH-ONLY) |
| Cost/Turn P50 | `agentic_harness_agent_tokens_cost_per_turn_bucket` | `agent.tokens.cost_per_turn` | `LlmTokenTrackingProcessor` | **LIVE** |
| Daily/Weekly/Monthly Spend | `agentic_harness_agent_budget_current_spend` | `agent.budget.current_spend` | `BudgetTrackingService` | **NEEDS-BUDGET** |

**Verdict: PARTIALLY WORKING.** Top-line cost metrics (total, cache savings, cost/turn) work from any LLM call. Avg Cost/Conversation denominator and budget spend panels need batch path or budget service.

---

## Dashboard 8: Mission Control (`missionControl.json`)

| Panel | Prometheus Metric | Code Metric | Recorded By | Status |
|-------|-------------------|-------------|-------------|--------|
| Active Conversations | `agentic_harness_agent_orchestration_conversation_duration_count` | `agent.orchestration.conversation_duration` | `RunConversationCommandHandler` | **BATCH-ONLY** |
| Token Burn Rate | `agentic_harness_agent_tokens_total_sum` | `agent.tokens.total` | `LlmTokenTrackingProcessor` | **LIVE** |
| Cost Rate | `agentic_harness_agent_tokens_cost_estimated_total` | `agent.tokens.cost_estimated` | `LlmTokenTrackingProcessor` | **LIVE** |
| Error Rate | `agent_tool_errors_total / agent_tool_invocations_total` | `agent.tool.errors` / `invocations` | `ToolEffectivenessProcessor` | **NEEDS-TOOLS** |
| Compaction Events | `agentic_harness_agent_context_compactions_total` | `agent.context.compactions` | `ContextBudgetMetrics` | **NEEDS-CONTEXT** |

**Verdict: PARTIALLY WORKING.** Token burn rate and cost rate fire on every LLM call. Active conversations only from batch. Error rate needs tool execution.

---

## Dashboard 9: Observability (`observability.json`)

| Panel | Prometheus Metric | Code Metric | Recorded By | Status |
|-------|-------------------|-------------|-------------|--------|
| MCP Request Rate | `agentic_harness_mcp_server_requests_total` | `mcp.server.requests` | `McpToolProvider` | **NEEDS-MCP** |
| MCP Duration | `agentic_harness_mcp_server_request_duration_*` | `mcp.server.request_duration` | `McpToolProvider` | **NEEDS-MCP** |

**Verdict: EMPTY unless MCP servers are configured.** `McpToolProvider.GetToolsAsync()` records these when it queries an external MCP server for tool listings. If no MCP servers are in `AppConfig.AI.McpServers`, this never fires.

---

## Dashboard 10: Session Audit (`sessionAudit.json`)

| Panel | Prometheus Metric | Code Metric | Recorded By | Status |
|-------|-------------------|-------------|-------------|--------|
| Active Sessions | `agentic_harness_agent_session_active` | `agent.session.active` | `SessionMetrics` | **LIVE** |
| Session Health | `agentic_harness_agent_session_health_score` | `agent.session.health_score` | Observable gauge (registered externally) | **DEAD** |

**Verdict: PARTIALLY WORKING.** Active sessions fires. Health score gauge name is defined in `SessionConventions` but no observable gauge registration was found in the codebase — the callback that would yield measurements doesn't exist.

---

## Dashboard 11: Session Insights (`sessionInsights.json`)

| Panel | Prometheus Metric | Code Metric | Recorded By | Status |
|-------|-------------------|-------------|-------------|--------|
| Total Conversations | `agentic_harness_agent_orchestration_conversation_duration_count` | `agent.orchestration.conversation_duration` | `RunConversationCommandHandler` | **BATCH-ONLY** |
| Avg Turns/Conversation | `agentic_harness_agent_orchestration_turns_per_conversation_*` | `agent.orchestration.turns_per_conversation` | `RunConversationCommandHandler` | **BATCH-ONLY** |
| Avg Cost/Conversation | cost_estimated / conversation_duration_count | mixed | LlmTokenTrackingProcessor + RunConversationCommandHandler | **PARTIAL** |

**Verdict: MOSTLY EMPTY.** All conversation-level aggregates come from `RunConversationCommandHandler`, which is the ConsoleUI batch path. WebUI uses per-turn `ExecuteAgentTurnCommand` which doesn't record conversation-level metrics.

---

## Dashboard 12: Token Audit (`tokenAudit.json`)

| Panel | Prometheus Metric | Code Metric | Recorded By | Status |
|-------|-------------------|-------------|-------------|--------|
| Total Tokens | `agentic_harness_agent_tokens_total_*` | `agent.tokens.total` | `LlmTokenTrackingProcessor` | **LIVE** |
| Input/Output Split | `agent_tokens_input_*` / `output_*` | `agent.tokens.input` / `output` | `LlmTokenTrackingProcessor` | **LIVE** |
| Cache Read/Write | `agent_tokens_cache_read_total` / `cache_write_total` | `agent.tokens.cache_read` / `cache_write` | `LlmTokenTrackingProcessor` | **LIVE** |
| Cache Hit Rate | `agent_tokens_cache_hit_rate_*` | `agent.tokens.cache_hit_rate` | `LlmTokenTrackingProcessor` | **LIVE** |

**Verdict: WORKING.** All panels fire from `LlmTokenTrackingProcessor` on every LLM span completion.

---

## Dashboard 13: Token & Cost (`tokenUsage.json`)

| Panel | Prometheus Metric | Code Metric | Recorded By | Status |
|-------|-------------------|-------------|-------------|--------|
| Token rates | `agent_tokens_input_sum` / `output_sum` / `total_sum` | `agent.tokens.*` | `LlmTokenTrackingProcessor` | **LIVE** |
| Cost metrics | `agent_tokens_cost_estimated_total` / `cost_per_turn_*` | `agent.tokens.cost_*` | `LlmTokenTrackingProcessor` | **LIVE** |
| Cache metrics | `agent_tokens_cache_hit_rate_*` / `cache_read` / `cache_write` | `agent.tokens.cache_*` | `LlmTokenTrackingProcessor` | **LIVE** |

**Verdict: WORKING.** All panels sourced from LLM token tracking, fires on every chat completion.

---

## Dashboard 14: Tool Execution (`toolExecution.json`)

| Panel | Prometheus Metric | Code Metric | Recorded By | Status |
|-------|-------------------|-------------|-------------|--------|
| Duration by Tool | `agentic_harness_agent_tool_duration_*` | `agent.tool.duration` | `ToolEffectivenessProcessor` | **NEEDS-TOOLS** |
| Duration P95 | same | same | same | **NEEDS-TOOLS** |
| Invocations by Tool | `agentic_harness_agent_tool_invocations_total` | `agent.tool.invocations` | `ToolEffectivenessProcessor` | **NEEDS-TOOLS** |
| Errors by Tool | `agentic_harness_agent_tool_errors_total` | `agent.tool.errors` | `ToolEffectivenessProcessor` | **NEEDS-TOOLS** |
| Invocations by Status | same with status label | same | same | **NEEDS-TOOLS** |
| Result Size P50/P95 | `agentic_harness_agent_tool_result_size_bucket` | `agent.tool.result_size` | `ToolEffectivenessProcessor` | **NEEDS-TOOLS** |

**Verdict: EMPTY.** All panels depend on `execute_tool` span completion. Same root cause as Analytics dashboard — the LLM must invoke tools.

---

## Summary Matrix

| # | Dashboard | Total Panels | LIVE | PARTIAL | EMPTY | Primary Blocker |
|---|-----------|-------------|------|---------|-------|-----------------|
| 1 | Agent Config | 5 | 5 | 0 | 0 | None |
| 2 | Agent Framework | 9 | 8 | 0 | 1 | Subagent spawns = batch-only |
| 3 | Analytics | 5 | 0 | 0 | 5 | Needs tool execution spans |
| 4 | Budget Alerts | 5 | 0 | 0 | 5 | Needs BudgetTrackingService + config |
| 5 | Content Safety | 4 | 0 | 0 | 4 | Needs content safety enabled |
| 6 | Context Explorer | 6 | 3 | 0 | 3 | Compactions + skills need extended use |
| 7 | Cost Analytics | 6 | 3 | 1 | 2 | Budget panels + conversation denominator |
| 8 | Mission Control | 5 | 2 | 0 | 3 | Tool errors + active conversations |
| 9 | Observability | 2 | 0 | 0 | 2 | Needs MCP servers configured |
| 10 | Session Audit | 2 | 1 | 0 | 1 | Health score gauge not implemented |
| 11 | Session Insights | 3 | 0 | 1 | 2 | Conversation metrics = batch-only |
| 12 | Token Audit | 4 | 4 | 0 | 0 | None |
| 13 | Token & Cost | 3+ | 3+ | 0 | 0 | None |
| 14 | Tool Execution | 6 | 0 | 0 | 6 | Needs tool execution spans |

**Totals: ~65 panels. ~29 LIVE, ~2 PARTIAL, ~34 EMPTY.**

---

## Root Cause Categories

### Category A: Working (Dashboards 1, 2, 12, 13)
These fire from `LlmTokenTrackingProcessor` (every LLM call) or observable gauges (every scrape). No action needed.

### Category B: Needs Tool Execution (Dashboards 3, 14)
`ToolEffectivenessProcessor` and `ToolUsefulnessProcessor` only fire on `execute_tool` spans. The LLM must decide to call a registered tool. Ask questions that require file system access, calculation, or other tool capabilities.

### Category C: Batch-Only Path (Dashboards 8 partial, 11)
`RunConversationCommandHandler` records conversation-level aggregates (duration, turn count) but WebUI uses per-turn `ExecuteAgentTurnCommand`. Fix: add equivalent recording in `AgentTelemetryHub.DispatchTurnAsync()` or track conversation lifecycle in the hub.

### Category D: Service Not Configured (Dashboards 4, 5, 9)
Budget, content safety, and MCP metrics need their respective services configured in `appsettings.json`. The metrics instrument code exists but the services either aren't registered or have no config to operate on.

### Category E: Not Implemented (Dashboard 10 partial)
`agent.session.health_score` — the metric name constant exists in `SessionConventions` but no `CreateObservableGauge` call registers it. Dead code.

### Category F: Needs Extended Usage (Dashboard 6 partial)
Context compactions and budget utilization only fire during extended conversations that exceed context limits. Normal short conversations won't trigger these.
