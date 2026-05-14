# Infrastructure.Observability

LLM-powered agents are notoriously hard to debug. A single conversation can generate hundreds of spans across tool calls, sub-agents, and LLM invocations -- and a trace that goes sideways is invisible from the outside. This project makes the invisible visible by providing the finalization stage of the OpenTelemetry pipeline: PII scrubbing, rate limiting, LLM cost tracking, intelligent tail-based sampling, tool effectiveness measurement, and multi-backend export (Jaeger, Azure Monitor, Prometheus).

By the time a trace reaches this layer, it has already been instrumented by `Application.AI.Common` (AI-specific metrics and sources). This project applies the final processing at Order 300 (Finalization) before export, plus provides services for budget tracking, session health monitoring, and observability data persistence.

## Architecture Context

```
                    OpenTelemetry Pipeline (ordered by ITelemetryConfigurator.Order)
                    ═══════════════════════════════════════════════════════════════

Order 100: Application.AI.Common.AiTelemetryConfigurator
           → Registers AI activity sources, custom metrics (session, orchestration, safety, RAG)

Order 300: Infrastructure.Observability.ObservabilityTelemetryConfigurator  ← THIS PROJECT
           → Adds processors (PII → Rate Limit → Token Track → Tool Effectiveness → Sampling)
           → Configures exporters (OTLP/Jaeger, Azure Monitor)

                         ┌──────────────────────────────┐
        Activity Span    │  PiiFilteringProcessor       │  Scrub sensitive attributes
             │           │  RateLimitingProcessor       │  Token bucket throttling
             ▼           │  LlmTokenTrackingProcessor   │  Cost aggregation + cache hits
        [Processors]  →  │  ToolEffectivenessProcessor  │  Result quality enrichment
             │           │  ToolUsefulnessProcessor     │  Composite 0-1 score
             ▼           │  CausalSpanAttributionProcessor │  Cross-span attribute bridging
        [Sampling]       │  TailBasedSamplingProcessor  │  Keep errors/slow/AI, sample rest
             │           └──────────────────────────────┘
             ▼
        [Exporters]  →  OTLP (Jaeger/Tempo) | Azure Monitor | Prometheus
```

**Additional services:**
- `BudgetTrackingService` -- cost spend state machine with ObservableGauge callbacks
- `AgentConfigInfoService` -- reports agent configurations as metric labels
- `SessionHealthService` -- per-agent health score gauge
- `PostgresObservabilityStore` / `NullObservabilityStore` -- session/message/tool data persistence

## Key Concepts

### Processor Pipeline (Order Matters)

The seven processors execute in strict sequence. PII scrubbing runs first (sensitive data never lingers in memory), cost tracking runs before sampling (cost is always recorded even for sampled-out spans), and tail-based sampling runs last (all metrics are already captured).

#### 1. PII Filtering Processor

Scans span attributes for sensitive patterns (auth headers, emails, user IDs) and replaces them with SHA-256 hashes or removes them entirely. Case-insensitive matching handles SDK variation (`Authorization` vs `authorization` vs `auth_header`).

```csharp
// Config: AppConfig.Observability.PiiFiltering.Enabled = true
```

#### 2. Rate Limiting Processor

Token bucket algorithm preventing trace storms from overwhelming export backends. A complex orchestrated task can generate hundreds of spans -- without throttling, it could saturate Jaeger.

```csharp
// Config: AppConfig.Observability.RateLimiting.Enabled = true
// Config: AppConfig.Observability.RateLimiting.SpansPerSecond = 100
```

#### 3. LLM Token Cost Tracking

Reads `gen_ai.usage.*` attributes (input tokens, output tokens, cache read/write tokens) and records per-model cost estimates. Tracks prompt cache hit rates for efficiency analysis. Feeds the `BudgetTrackingService` state machine.

#### 4. Tool Effectiveness Processor

Enriches `execute_tool` spans with quality signals: result size, empty-result detection, truncation. Records histogram metrics for tool duration, invocation counts, and error rates.

#### 5. Tool Usefulness Processor

Computes a composite 0-1 usefulness score per tool call based on multiple heuristic signals. Enables "is this tool actually helping the agent?" analysis.

#### 6. Causal Span Attribution Processor

Bridges attributes between related spans (e.g., `agent.tool.name` to `gen_ai.tool.name`). Adds input hashes and result categories. Reads eval context from Activity baggage for cross-span correlation.

#### 7. Tail-Based Sampling

Unlike head-based sampling (decided at trace start with incomplete info), tail-based sampling buffers spans by trace ID and evaluates the complete trace before deciding:

- **Always keep** traces containing error spans
- **Always keep** traces exceeding the slow-request threshold
- **Always keep** traces with AI agent execution attributes
- **Probabilistically sample** remaining traces at configurable percentage

```csharp
// Config: AppConfig.Observability.Sampling
{
  "Enabled": true,
  "DefaultSamplingPercentage": 25,
  "SlowRequestThresholdMs": 5000,
  "AlwaysKeepErrors": true,
  "AlwaysKeepAgentExecutions": true,
  "MaxBufferedTraces": 10000,
  "DecisionWait": "00:00:10"
}
```

The buffer includes overflow eviction (oldest traces dropped first) to prevent unbounded memory growth during burst traffic.

### Export Targets

The `ObservabilityTelemetryConfigurator` configures conditional export:

| Target | Protocol | When Active |
|--------|----------|-------------|
| OTLP (Jaeger/Tempo) | gRPC | `Exporters.Otlp.Enabled = true` + endpoint configured |
| Azure Monitor | Application Insights SDK | `Exporters.AzureMonitor.Enabled = true` + connection string |
| Prometheus | HTTP scrape endpoint (`/metrics`) | Always (configured in Presentation.Common) |

### Budget Tracking Service

A state machine that aggregates LLM costs across all agent turns and exposes them via ObservableGauge callbacks. When budget limits are exceeded, it can signal the orchestration layer to degrade gracefully.

### Observability Persistence

`PostgresObservabilityStore` persists session metadata, messages, tool executions, and safety events to PostgreSQL. Falls back to `NullObservabilityStore` (silent no-op with a warning log) when no connection string is configured.

## Project Structure

```
Infrastructure.Observability/
├── Exporters/
│   └── ObservabilityTelemetryConfigurator.cs     Pipeline orchestrator (Order 300)
├── Persistence/
│   ├── PostgresObservabilityStore.cs             Session/message/tool persistence
│   └── NullObservabilityStore.cs                 No-op fallback (logs warning)
├── Processors/
│   ├── PiiFilteringProcessor.cs                  SHA-256 hashing of sensitive attributes
│   ├── RateLimitingProcessor.cs                  Token bucket span throttling
│   ├── LlmTokenTrackingProcessor.cs             Cost estimation + cache hit tracking
│   ├── TailBasedSamplingProcessor.cs            Error/slow/AI-aware trace sampling
│   ├── ToolEffectivenessProcessor.cs            Result quality enrichment + metrics
│   ├── ToolUsefulnessProcessor.cs               Composite 0-1 usefulness score
│   └── CausalSpanAttributionProcessor.cs        Cross-span attribute bridging
├── Services/
│   ├── AgentConfigInfoService.cs                Agent config as metric labels
│   ├── BudgetTrackingService.cs                 Cost state machine + ObservableGauge
│   ├── NullBudgetTrackingService.cs             No-op when budget tracking disabled
│   └── SessionHealthService.cs                  Per-agent health score gauge
└── DependencyInjection.cs                       AddInfrastructureObservabilityDependencies()
```

## Key Types Reference

| Type | Purpose | Lifetime |
|------|---------|----------|
| `ObservabilityTelemetryConfigurator` | Wires processors + exporters into OTel pipeline | Singleton |
| `TailBasedSamplingProcessor` | Deferred trace-level keep/drop decisions | Created by configurator |
| `PiiFilteringProcessor` | Scrub sensitive span attributes | Created by configurator |
| `LlmTokenTrackingProcessor` | Token count → cost metrics | Created by configurator |
| `ToolEffectivenessProcessor` | Tool result quality enrichment | Created by configurator |
| `BudgetTrackingService` | LLM cost aggregation state machine | Singleton |
| `PostgresObservabilityStore` | Observability data persistence | Singleton |
| `SessionHealthService` | Per-agent health score gauge | Singleton |

## Configuration

```json
{
  "AppConfig": {
    "Observability": {
      "PostgresConnectionString": "Host=localhost;Port=5432;Database=observability;Username=observability;Password=observability",
      "EnableSensitiveTelemetry": false,
      "WebTelemetryProjects": ["Infrastructure.AI.MCPServer", "Presentation.AgentHub"],
      "PiiFiltering": { "Enabled": true },
      "RateLimiting": { "Enabled": true, "SpansPerSecond": 100 },
      "BudgetTracking": { "Enabled": false },
      "Sampling": {
        "Enabled": true,
        "DefaultSamplingPercentage": 25,
        "SlowRequestThresholdMs": 5000,
        "AlwaysKeepErrors": true,
        "AlwaysKeepAgentExecutions": true,
        "MaxBufferedTraces": 10000,
        "DecisionWait": "00:00:10"
      },
      "Exporters": {
        "Otlp": {
          "Enabled": true,
          "Endpoint": "http://localhost:4317",
          "Timeout": "00:00:10"
        },
        "AzureMonitor": {
          "Enabled": false,
          "ConnectionString": ""
        }
      }
    }
  }
}
```

## How to Run

This is a class library consumed by Presentation hosts. To see observability in action:

```bash
# Start infrastructure (PostgreSQL, Jaeger, Prometheus)
.\scripts\start-infrastructure.ps1

# Run AgentHub (exports traces to Jaeger on localhost:4317)
dotnet run --project src/Content/Presentation/Presentation.AgentHub

# View traces: http://localhost:16686 (Jaeger UI)
# View metrics: http://localhost:52000/metrics (Prometheus scrape endpoint)
```

## Common Tasks

### Adding a New Processor

1. Create a class extending `BaseProcessor<Activity>` in `Processors/`
2. Override `OnEnd(Activity data)` to process completed spans
3. Register it in `ObservabilityTelemetryConfigurator.ConfigureTracing()` at the correct pipeline position
4. Add a config flag in `ObservabilityConfig` to enable/disable it

### Adding a New Export Target

1. Add config section in `Domain.Common.Config.Observability`
2. Add conditional exporter registration in `ObservabilityTelemetryConfigurator.ConfigureTracing()` and/or `ConfigureMetrics()`

### Debugging Missing Spans

1. Check `AppConfig.Observability.Sampling.Enabled` -- if true, non-error/non-slow traces may be sampled out
2. Check `RateLimiting.SpansPerSecond` -- high-throughput scenarios may be throttled
3. Check the OTLP endpoint is reachable: `curl http://localhost:4317`

## Dependencies

**Project References:**
- `Application.Common` -- `ITelemetryConfigurator` interface, `IObservabilityStore`
- `Application.AI.Common` -- `IBudgetTrackingService`, AI metric types
- `Domain.AI` -- Telemetry convention constants (`AgentConventions`, `GenAiSystem*`)

**NuGet Packages:**
- `OpenTelemetry` -- Base SDK (`BaseProcessor<Activity>`)
- `OpenTelemetry.Exporter.OpenTelemetryProtocol` -- OTLP/gRPC export
- `Azure.Monitor.OpenTelemetry.Exporter` -- Application Insights traces + metrics
- `OpenTelemetry.Exporter.Prometheus.AspNetCore` -- Prometheus scraping
- `Npgsql` -- PostgreSQL persistence
- `Microsoft.Extensions.Options` / `Microsoft.Extensions.Logging`

## Testing

**Test project:** `Tests/Infrastructure.Observability.Tests`

```bash
dotnet test src/AgenticHarness.slnx --filter "FullyQualifiedName~Infrastructure.Observability"
```

**Coverage areas:**
- Tail-based sampling decisions (error keeps, slow keeps, probabilistic)
- Buffer overflow eviction ordering
- PII attribute detection and hashing
- Rate limiter token bucket behavior
- LLM cost calculation with model pricing
- Tool effectiveness metric recording
