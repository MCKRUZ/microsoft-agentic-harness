# Telemetry Verification Suite — Design Spec

**Date:** 2026-05-05
**Status:** Approved
**Branch:** feat/agt-governance-integration

## Problem

The telemetry dashboard shows no data after a real chat in the web UI. Existing E2E tests pass because they emit metrics synthetically (calling `Record()`/`Add()` directly) and scrape the in-process Prometheus exporter — they never exercise the real code paths or the collector/Prometheus pipeline.

The gap: no test verifies that a real user action produces data visible on the dashboard.

## Solution

A 4-layer test suite where each layer independently verifies one segment of the telemetry pipeline:

```
App Handler → [Layer 1] → Prometheus Exporter → [Layer 2] → Collector → [Layer 3] → Dashboard PromQL → [Layer 4] → Full Stack
```

## Layer 1: Integration — Real Chat Flow to Metrics Emission

**File:** `src/Content/Tests/Presentation.AgentHub.Tests/Telemetry/MetricsIntegrationTests.cs`

**What it does:**
- Boots the app via `TestWebApplicationFactory`
- Sends a real `RunConversationCommand` through MediatR (or invokes the SignalR hub method)
- Scrapes the in-process `/metrics` Prometheus endpoint
- Asserts all expected instruments were incremented with correct tag dimensions

**Expected assertions:**
- `agent_session_started_total` > 0
- `agent_session_active` > 0 (then drops after conversation ends)
- `agent_orchestration_turns_total` > 0
- `agent_orchestration_turn_duration_milliseconds_count` > 0
- `agent_orchestration_conversation_duration_milliseconds_count` > 0
- Tool-related metrics if the conversation invokes tools

**What it catches:** Metrics defined but never called from real code paths, DI wiring failures, handler logic that skips metric emission, tag dimension mismatches.

**Dependencies:** TestWebApplicationFactory, mock AI client (returns canned responses), in-process Prometheus exporter.

## Layer 2: Collector Contract — Naming Transform Validation

**File:** `src/Content/Tests/Presentation.AgentHub.Tests/Telemetry/CollectorContractTests.cs`

**What it does:**
- Reads `scripts/otel-collector/config.yaml` and extracts the Prometheus exporter `namespace` value
- Defines the canonical list of app instrument names (from the static metric classes)
- Applies OTel-to-Prometheus naming rules:
  - `.` → `_`
  - Unit suffix: bare `ms` → `_milliseconds`, bare `s` → `_seconds`
  - Counter instruments → `_total` suffix
  - Histogram instruments → `_sum`, `_count`, `_bucket` suffixes
  - Namespace prefix: `{namespace}_`
- Produces the "final Prometheus metric names" list
- Snapshot-tests this list (any drift = test failure)

**Expected behavior:**
- `agent.session.active` (UpDownCounter, no unit) → `agentic_harness_agent_session_active`
- `agent.orchestration.turn.duration` (Histogram, unit `ms`) → `agentic_harness_agent_orchestration_turn_duration_milliseconds_{sum,count,bucket}`
- `agent.tool.invocations` (Counter, no unit) → `agentic_harness_agent_tool_invocations_total`

**What it catches:** Collector namespace changes, instrument renames, unit changes, OTel naming convention surprises (like the `ms` → `_milliseconds` gotcha already documented).

**Dependencies:** File system access to collector config YAML, YamlDotNet for parsing.

## Layer 3: Dashboard Contract — PromQL to Valid Metrics

**File:** `src/Content/Tests/Presentation.AgentHub.Tests/Telemetry/DashboardContractTests.cs`

**What it does:**
- Extracts all metric names from `MetricCatalog` PromQL expressions (regex: captures the metric name from patterns like `rate(metric_name{...}[5m])` or bare `metric_name`)
- Loads the canonical metric name list from Layer 2's transform logic (shared helper)
- Asserts every dashboard metric name exists in the canonical list
- Optionally: asserts every canonical metric has at least one dashboard panel (coverage check)

**What it catches:** Dashboard queries referencing stale/renamed metrics, typos in PromQL, new instruments added without dashboard coverage, metric names that silently diverged from the collector output.

**Dependencies:** Access to MetricCatalog source or its runtime output, shared naming contract from Layer 2.

## Layer 4: Full E2E — Browser to Prometheus Data

**File:** `src/Content/Tests/Presentation.AgentHub.Tests/Telemetry/MetricsE2ETests.cs`

**Infrastructure:** Testcontainers (Docker)
- `otel-collector` container with the project's `config.yaml` mounted
- `prometheus` container configured to scrape the collector
- App under test configured to send OTLP to the collector container

**What it does:**
- Starts the full pipeline: App → Collector → Prometheus (all in Docker via Testcontainers)
- Drives the web UI with Playwright: navigates to chat, sends a message, waits for response
- Polls Prometheus HTTP API (`/api/v1/query`) with known PromQL until data appears (timeout 30s)
- Asserts non-empty result vectors for critical metrics

**Expected assertions (minimum):**
- `agentic_harness_agent_session_started_total` returns non-empty vector
- `agentic_harness_agent_orchestration_turns_total` returns non-empty vector
- `agentic_harness_agent_orchestration_turn_duration_milliseconds_count` > 0

**What it catches:** service.name mismatch (collector filter drops traffic), OTLP endpoint misconfiguration, Prometheus scrape target missing, network connectivity between containers, the full "nothing shows up on dashboard" failure class.

**Dependencies:** Docker, Testcontainers.DotNet, Playwright, a Prometheus config that scrapes the collector's metrics port.

## Shared Infrastructure

### MetricNamingContract (shared helper)

A static class that encodes:
1. All instrument definitions (name, type, unit) scraped from the static metric classes
2. The collector namespace (read from YAML or hardcoded with a test that validates the YAML matches)
3. The OTel→Prometheus transform function
4. The canonical output list

Used by Layers 2, 3, and 4 for consistent naming expectations.

### TestWebApplicationFactory Enhancements

- Register a mock `IChatClient` that returns canned AI responses (so Layer 1 doesn't need a real LLM)
- Ensure `service.name` resource attribute is set to `Presentation.AgentHub` (matching the collector filter)
- Expose the in-process Prometheus scrape endpoint for Layer 1

## Test Categorization

| Layer | Category Trait | CI Gate | Docker Required |
|-------|---------------|---------|-----------------|
| 1 | `[Trait("Category", "Integration")]` | PR merge | No |
| 2 | `[Trait("Category", "Contract")]` | PR merge | No |
| 3 | `[Trait("Category", "Contract")]` | PR merge | No |
| 4 | `[Trait("Category", "E2E")]` | Nightly / manual | Yes |

## Success Criteria

1. Layer 1 test fails if any handler stops emitting metrics (regression guard)
2. Layer 2 test fails if collector namespace or instrument names change without updating the snapshot
3. Layer 3 test fails if dashboard PromQL references a non-existent metric
4. Layer 4 test fails if the full pipeline doesn't deliver data to Prometheus within 30s of a chat

## Out of Scope

- Grafana dashboard rendering verification (visual regression)
- Alert rule validation
- Multi-tenant metric isolation testing
- Performance/load testing of the metrics pipeline
