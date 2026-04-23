# Dashboard Testing Plan

> For each dashboard, the specific user actions or scripts needed to generate
> data and validate the panels show metrics.

---

## Tier 1 — Already Working (just send a chat message)

### Test: Agent Config, Token Audit, Token & Cost, Agent Framework

**Action:** Send any chat message through the WebUI.

```
1. Open the WebUI
2. Select any agent from the dropdown
3. Send: "Hello, what can you help me with?"
4. Wait 30s for Prometheus scrape
5. Check dashboards: Agent Config, Token Audit, Token & Cost, Agent Framework
```

**Expected:** All stat panels populate. Token histograms show data points. Agent Config shows the agent's model, tool count, skill count.

**Why it works:** Every LLM call generates `gen_ai.*` spans that `LlmTokenTrackingProcessor` converts to `agent.tokens.*` metrics. `AgentConfigInfoService` emits config info on every Prometheus scrape.

---

## Tier 2 — Needs Tool Execution (Analytics, Tool Execution)

### Test: Make the LLM call tools

The agent must invoke registered tools. This requires:
1. Tools are registered (file_system is registered via keyed DI)
2. The agent's skill declares `allowed-tools: ["file_system"]` or equivalent
3. The user asks something that the LLM decides requires a tool

**Action:**
```
1. Open the WebUI
2. Select an agent that has tools configured
3. Send prompts that REQUIRE tool use:
   - "Read the contents of the file at C:\Users\kruz7\OneDrive\Documents\Code Repos\MCKRUZ\microsoft-agentic-harness\README.md"
   - "List all files in the src directory"
   - "What's in my CLAUDE.md file?"
4. Wait 30s for Prometheus scrape
5. Check dashboards: Analytics, Tool Execution
```

**Expected:** Tool Execution shows invocations, duration, result size. Analytics shows usefulness scores.

**If still empty, debug checklist:**
- [ ] Verify tools appear in agent's tool list: check `AgentFactory.CreateAgentFromSkillAsync()` → `ChatOptions.Tools` is non-empty
- [ ] Verify `UseFunctionInvocation` middleware is configured on the `ChatClientBuilder` (it is, in `AgentFactory` line ~102)
- [ ] Check if the LLM response contains `FunctionCallContent` items (add temporary logging in `ExecuteAgentTurnCommandHandler.ExtractContentAndTools()`)
- [ ] Verify `ToolEffectivenessProcessor` is registered: check `ObservabilityTelemetryConfigurator` registers it at Order 300
- [ ] Check span name matches: `ToolEffectivenessProcessor` filters on `OperationName == "execute_tool"` — verify the span name from `UseFunctionInvocation`

**Deeper diagnosis — is the LLM even seeing tools?**
```csharp
// Temporary logging in AgentFactory.CreateAgentFromSkillAsync(), after building tools:
_logger.LogInformation("Agent {AgentName} has {ToolCount} tools: {ToolNames}",
    skillId, tools.Count, string.Join(", ", tools.Select(t => t.Name)));
```

---

## Tier 3 — Needs Configuration (Budget Alerts, Content Safety, Observability)

### Test: Budget Alerts

**Prerequisite:** Configure budget thresholds in `appsettings.json`:
```json
{
  "Observability": {
    "Budget": {
      "DailyLimit": 10.00,
      "WeeklyLimit": 50.00,
      "MonthlyLimit": 200.00,
      "WarningThresholdPercent": 0.7,
      "CriticalThresholdPercent": 0.9
    }
  }
}
```

**Action:**
```
1. Add budget config to appsettings.json
2. Restart the app
3. Send several chat messages to accumulate cost
4. Wait 30s for Prometheus scrape
5. Check Budget Alerts dashboard
```

**If still empty:**
- [ ] Verify `BudgetTrackingService` is registered in DI (`Infrastructure.Observability/DependencyInjection.cs`)
- [ ] Verify `RegisterGauges()` is called during startup
- [ ] Verify `LlmTokenTrackingProcessor` calls `IBudgetTrackingService.RecordSpend()`
- [ ] Check Prometheus for `agentic_harness_agent_budget_current_spend` — observable gauges only appear after first scrape

### Test: Content Safety

**Prerequisite:** Configure Azure Content Safety in `appsettings.json`:
```json
{
  "AI": {
    "ContentSafety": {
      "Enabled": true,
      "Endpoint": "https://your-content-safety.cognitiveservices.azure.com/",
      "ApiKey": "<from-user-secrets>"
    }
  }
}
```

**Action:**
```
1. Enable content safety in config
2. Restart the app
3. Send a normal message (should pass evaluation)
4. Send a message that might trigger safety (test with benign borderline content)
5. Wait 30s
6. Check Content Safety dashboard
```

**If still empty:**
- [ ] Verify `ContentSafetyBehavior` is registered in the MediatR pipeline
- [ ] Verify `ExecuteAgentTurnCommand` implements `IContentScreenable`
- [ ] Check if the behavior short-circuits when `ContentSafety.Enabled = false`

### Test: Observability (MCP Metrics)

**Prerequisite:** Configure at least one MCP server in `appsettings.json`:
```json
{
  "AI": {
    "McpServers": [
      {
        "Name": "test-mcp",
        "Endpoint": "http://localhost:3001/mcp",
        "Transport": "http"
      }
    ]
  }
}
```

**Action:**
```
1. Configure an MCP server
2. Restart the app
3. Start a conversation (MCP tools are loaded during agent creation)
4. Wait 30s
5. Check Observability dashboard
```

---

## Tier 4 — Needs Code Fix (Session Insights, Mission Control partial, Session Audit partial)

### Problem: Conversation-level metrics don't fire from WebUI

`RunConversationCommandHandler` records `OrchestrationMetrics.ConversationDuration` and `TurnsPerConversation` when a multi-turn conversation completes. But WebUI uses per-turn `ExecuteAgentTurnCommand` — there's no "conversation end" event.

### Fix Option A: Track conversation lifecycle in AgentTelemetryHub

Add conversation start/end tracking to `AgentTelemetryHub`:

```csharp
// In StopConversation or when client disconnects:
var elapsed = DateTimeOffset.UtcNow - conversationStartTime;
OrchestrationMetrics.ConversationDuration.Record(elapsed.TotalMilliseconds, agentTag);
OrchestrationMetrics.TurnsPerConversation.Record(turnCount, agentTag);
```

This requires tracking `conversationStartTime` and `turnCount` per conversation in the hub's state dictionary.

### Fix Option B: Record per-turn metrics that dashboards can aggregate

Instead of conversation-level aggregates, add per-turn counters that Prometheus can `sum()`:
- `agent.turn.completed` (Counter) — increment on each successful turn
- Use existing `agent.tokens.tokens_per_turn` histogram for per-turn analysis

Then update Session Insights and Mission Control dashboards to use these.

**Recommendation:** Fix Option A is more accurate. The hub already tracks conversation state in `_activeConversations`.

### Problem: Session Health Score not implemented

`SessionConventions.HealthScore` defines the metric name but no `CreateObservableGauge` call exists. Need to implement a health score calculation (e.g., based on error rate, token budget remaining, response latency).

---

## Tier 5 — Needs Extended Usage (Context Explorer partial)

### Test: Context Compactions and Budget Utilization

**Action:**
```
1. Start a conversation
2. Send 20+ messages in the same conversation to build up context
3. Continue until the context budget is exceeded (triggers compaction)
4. Check Context Explorer dashboard
```

**If compaction never triggers:**
- The context budget limit may be very large relative to typical conversation size
- Check `AppConfig.AI.AgentFramework.ContextBudget` for the configured limit
- Temporarily lower it to test compaction

---

## Quick Validation Script

Run this after any fix to validate the full pipeline:

```bash
# 1. Check Prometheus is scraping
curl -s http://localhost:9090/api/v1/targets | jq '.data.activeTargets[] | {endpoint: .scrapeUrl, health: .health}'

# 2. Check which agentic_harness metrics exist
curl -s http://localhost:9090/api/v1/label/__name__/values | jq '.data[] | select(startswith("agentic_harness"))'

# 3. Check specific metric has data
curl -s 'http://localhost:9090/api/v1/query?query=agentic_harness_agent_tokens_total_count' | jq '.data.result'

# 4. Check tool metrics specifically
curl -s 'http://localhost:9090/api/v1/query?query=agentic_harness_agent_tool_invocations_total' | jq '.data.result'

# 5. Check OTel collector metrics endpoint directly
curl -s http://localhost:8889/metrics | grep "agentic_harness_agent_tool"
```

---

## Priority Order for Testing

| Priority | Dashboard | Effort | Impact |
|----------|-----------|--------|--------|
| 1 | Token Audit + Token & Cost + Agent Config | Zero (already works) | Validate pipeline |
| 2 | Agent Framework | Zero (already works after session fixes) | Validate tool call tracking |
| 3 | Analytics + Tool Execution | Medium (need to trigger tool calls) | Proves tool pipeline works |
| 4 | Cost Analytics | Low (mostly works, budget panels need config) | Financial visibility |
| 5 | Context Explorer | Low (partial works, rest needs extended usage) | Context health |
| 6 | Mission Control | Medium (needs conversation lifecycle fix) | Ops dashboard |
| 7 | Session Insights | Medium (same fix as Mission Control) | User analytics |
| 8 | Budget Alerts | Medium (needs config + verify DI) | Cost controls |
| 9 | Content Safety | Medium (needs Azure Content Safety setup) | Safety monitoring |
| 10 | Observability | Medium (needs MCP server configured) | MCP health |
| 11 | Session Audit | High (health score not implemented) | Audit compliance |
