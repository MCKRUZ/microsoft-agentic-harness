# Infrastructure.Observability

LLM-powered agents are notoriously hard to debug. A conversation that goes sideways might involve dozens of turns, multiple tool calls, and branching logic that's invisible from the outside. This project makes the invisible visible.

Infrastructure.Observability is the finalization stage of the OpenTelemetry pipeline. By the time a trace reaches this layer, it's already been instrumented by Application.Common (base harness sources) and Application.AI.Common (AI-specific sources). This project applies the final processing — PII scrubbing, rate limiting, cost tracking, intelligent sampling, and tool effectiveness measurement — before traces and metrics are exported to Jaeger, Azure Monitor, and Prometheus.

---

## The Processor Pipeline

Five processors run in strict order. The order matters — PII must be scrubbed before export, cost must be tracked before sampling drops spans, and effectiveness must be measured on every span regardless of sampling decisions.

### 1. PII Filtering

`PiiFilteringProcessor` runs first because nothing leaves the harness with sensitive data attached. It performs a two-pass scan: first identifying attributes that match PII patterns (auth headers, user identifiers, email addresses), then either removing them or replacing them with SHA-256 hashes.

The matching is case-insensitive to handle SDK variation — Azure SDK tags attributes as `Authorization` while custom code might use `authorization` or `auth_header`. All variations get caught.

### 2. Rate Limiting

`RateLimitingProcessor` prevents trace storms from overwhelming the export backend. It implements a token bucket algorithm: configurable spans-per-second throughput with burst capacity. Excess spans are dropped silently, with periodic summary logs so you know *that* dropping happened without amplifying the log volume.

This matters because a single orchestrated task can generate hundreds of spans — one per tool call, per turn, per sub-agent. Without rate limiting, a complex task could saturate the Jaeger backend.

### 3. LLM Token Cost Tracking

`LlmTokenTrackingProcessor` reads `gen_ai.usage.*` attributes from LLM spans — input tokens, output tokens, cache read/write tokens — and records cost metrics. It uses configurable per-model pricing to estimate dollar costs per turn and tracks prompt cache hit rates for efficiency analysis.

This processor runs before sampling because cost data is too valuable to lose. Even if a trace is sampled out, the cost metrics still get recorded.

### 4. Tail-Based Sampling

`TailBasedSamplingProcessor` makes the keep/drop decision after seeing the complete trace. It buffers spans by trace ID, then applies sampling policies:

- **Always keep** error traces (any span with an error status)
- **Always keep** slow traces (duration exceeds configurable threshold)
- **Always keep** AI agent execution traces (orchestration-level spans)
- **Probabilistically sample** everything else

This means you never lose visibility into failures or performance problems, while routine successful traces are sampled down to control storage costs. The buffer includes overflow eviction to prevent unbounded memory growth during burst traffic.

### 5. Tool Effectiveness

`ToolEffectivenessProcessor` enriches `execute_tool` spans with quality signals: result size in characters, whether the result was empty, and whether it was truncated. It records metrics for tool execution duration, invocation counts, error rates, and empty result frequency.

This data answers questions like: "Is the file search tool actually returning useful results, or is the agent calling it repeatedly and getting nothing?"

## Exporters

The `ObservabilityTelemetryConfigurator` wires all five processors into the OpenTelemetry pipeline, then configures export targets:

- **OTLP (Jaeger/Tempo)** — Distributed traces via gRPC protocol
- **Azure Monitor** — Traces and metrics to Application Insights
- **Prometheus** — Metrics scraping endpoint for Grafana dashboards

Export targets are conditional — if Jaeger isn't configured, it's skipped. If Azure Monitor isn't configured, it's skipped. The harness adapts to whatever observability infrastructure is available.

---

## Project Structure

```
Infrastructure.Observability/
├── Exporters/
│   └── ObservabilityTelemetryConfigurator.cs  Pipeline orchestrator (Order 300)
├── Processors/
│   ├── PiiFilteringProcessor.cs               SHA-256 hashing of sensitive attributes
│   ├── RateLimitingProcessor.cs               Token bucket span throttling
│   ├── LlmTokenTrackingProcessor.cs           Cost estimation + cache hit tracking
│   ├── TailBasedSamplingProcessor.cs          Error/slow/AI-aware trace sampling
│   └── ToolEffectivenessProcessor.cs          Result quality enrichment + metrics
└── DependencyInjection.cs                     Registers configurator at Order 300
```

## Dependencies

- **Application.Common** — `ITelemetryConfigurator` interface
- **Application.AI.Common** — AI-specific metrics and source names
- **Domain.AI** — Telemetry convention constants
- **OpenTelemetry** — Base SDK
- **OpenTelemetry.Exporter.OpenTelemetryProtocol** — OTLP/gRPC export
- **Azure.Monitor.OpenTelemetry.Exporter** — Azure Application Insights
- **OpenTelemetry.Exporter.Prometheus.AspNetCore** — Prometheus metrics endpoint
