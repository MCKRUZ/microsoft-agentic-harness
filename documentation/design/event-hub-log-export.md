# Story: OTel Log Export to Azure Event Hub (with in-app PII filtering)

> **Status:** Draft for review — no code yet.
> **Issue:** [#153](https://github.com/MCKRUZ/microsoft-agentic-harness/issues/153) — "Event logging in Event Hub … make sure there is a filter in place for PII."
> **Author:** design pass, 2026-07-12.

---

## 1. Problem

An enterprise consumer of this template wants application **logs** delivered to **Azure Event Hub** — the standard ingestion pipe into a SIEM, Log Analytics, or Azure Data Explorer — with **PII scrubbed before it leaves the process**.

Investigating the current state surfaced a gap the issue did not name:

- **Logs are not part of the OTel pipeline today.** The harness wires OTel for **traces** (→ OTLP → Tempo, + Azure Monitor) and **metrics** (→ OTLP → Prometheus, + Azure Monitor). There is **no OTel logs signal** — `WithLogging` / an OTLP logs exporter is not registered anywhere (verified across the repo), and there is no Loki or other log backend in the reference stack.
- **Logging runs on a separate pipe.** `ILogger` records fan out to local/process sinks only: console (`ExecutionConsoleFormatter`), per-run **file** logs, **structured JSONL**, an **in-memory ring buffer** (diagnostics endpoint), and an optional **named pipe** to a viewer. None of these reach the collector.
- **Consequence:** in Grafana today you see the trace waterfall and metric charts, but you **cannot** see the application log lines next to them. Logs live in files/console, invisible to the observability backend.

So "we export everything via OTel" has always been true for traces and metrics, and false for logs.

## 2. Goals

1. Make **logs a first-class OTel signal** — bridged from `ILogger` into the OTel logs pipeline, exported through the same collector-facing machinery as traces/metrics. This alone puts logs in Grafana.
2. Deliver logs to **Azure Event Hub**, via two operator-selectable modes (Collector-delivered or Direct in-process).
3. **Filter PII in-app, before export** — reusing the existing content redactor, in one place that covers every destination.
4. Ship **off by default**, fail-closed on misconfiguration, both auth modes supported for the Direct path.

## 3. Non-goals

- No Loki/log-backend provisioning — that is Collector/ops config, not app code. (The bridge makes it *possible*; standing it up is a deployment concern.)
- No change to the existing local sinks (file/JSONL/console/pipe/ring buffer) — they stay as dev/diagnostic outputs.
- No log **sampling**. Unlike traces (tail-sampled at the collector), logs are exported as emitted; volume is controlled by log level, not sampling.
- Traces/metrics export is unchanged.

## 4. Current-state map (verified)

| Concern | Where it lives today |
|---|---|
| `ILogger` provider suite (file/JSONL/pipe/ring/console) | `Application.Common/Logging/*` + `ILoggingBuilderExtensions` — pure, no external deps |
| Host logging composition (`ClearProviders` + provider selection) | `Application.Common/Extensions/IServiceCollectionExtensions.cs` |
| OTel traces/metrics pipeline | `Presentation.Common/Extensions/OpenTelemetryServiceCollectionExtensions.cs` (`AddWebTelemetry` / `AddDesktopTelemetry`) |
| Ordered processor/exporter seam | `ITelemetryConfigurator` (`ConfigureTracing` / `ConfigureMetrics`), impl `Infrastructure.Observability/Exporters/ObservabilityTelemetryConfigurator.cs` (Order 300) |
| Trace PII scrub (attribute-key delete/hash) | `Infrastructure.Observability/Processors/PiiFilteringProcessor : BaseProcessor<Activity>` — **span-only, does not touch log text** |
| **Content-based PII/secret redactor** | `Infrastructure.AI/Telemetry/Redaction/DefaultContentRedactionFilter : IContentRedactionFilter` — regex for email/phone/SSN/credit-card/IPv4/6/AWS key/JWT/`Bearer`/`api_key`/password. Injectable, thread-safe, over-redactive by design. **Reusable for log text.** |
| Exporter config hierarchy | `Domain.Common/Config/Observability/ExportersConfig { Otlp, AzureMonitor, Prometheus }` |
| Managed-identity credential | `Infrastructure.AI/Identity/ManagedIdentityCredentialProvider` |
| Event Hubs SDK | **Not referenced anywhere — greenfield dependency.** |

## 5. Proposed design

### 5.1 Signal bridge (foundation — required for everything)

Wire the OTel logs signal so `ILogger` records become OTel `LogRecord`s:

- **Web hosts** (`AddWebTelemetry`): add `.WithLogging(...)` alongside the existing `.WithTracing(...).WithMetrics(...)` on the `AddOpenTelemetry()` builder.
- **Desktop/console hosts** (`AddDesktopTelemetry`): create a standalone `LoggerProvider` singleton mirroring the existing standalone `TracerProvider`/`MeterProvider`.
- Both drive configuration through the ordered configurator seam — **extend `ITelemetryConfigurator` with `ConfigureLogging(LoggerProviderBuilder)`** so the PII processor and Event Hub exporter register there, exactly as tracing/metrics processors + exporters do. `ObservabilityTelemetryConfigurator` (Order 300) implements it.
- Reuse the shared `ResourceBuilder` so logs carry the same `service.name`/resource attributes as traces (enables trace↔log correlation in Grafana).

The local `ILogger` providers stay wired. The OTel bridge is *additional* — it does not replace file/console logging.

### 5.2 PII scrub as a `BaseProcessor<LogRecord>` (the "filter")

A new `LogRecordRedactionProcessor : BaseProcessor<LogRecord>` in `Infrastructure.Observability`, registered **first** in `ConfigureLogging` (before any exporter), so redaction happens in-process before OTLP or Event Hub sees anything:

- Reuses `IContentRedactionFilter.Redact(text, categories)` — no new redaction engine.
- Scrubs the **formatted message / body** and each **string-valued attribute**.
- Categories come from config (default: the full content set — email, phone, SSN, credit-card, IP, AWS key, JWT, generic credentials).
- Mode-independent: PII is gone before the record reaches *either* delivery mode, so nothing sensitive ever hits the wire — even in Collector mode where the OTLP stream leaves the process.

This mirrors the existing span-side `PiiFilteringProcessor : BaseProcessor<Activity>` — one PII processor per signal, same pattern.

### 5.3 Delivery modes (both shipped, config-selected)

`EventHubExporterConfig.DeliveryMode = Collector | Direct` (default **Collector**).

| Mode | Path | App-side Event Hub code | For |
|---|---|---|---|
| **Collector** | app emits OTLP logs → OTel Collector → Collector's Kafka exporter → Event Hub's **Kafka-compatible endpoint** (or the contrib Azure exporter) | **none** — app only speaks OTLP | consumers running a collector (the reference topology already assumes one) |
| **Direct** | app hosts a custom `BaseExporter<LogRecord>` → `EventHubProducerClient` | the exporter + Event Hubs SDK | consumers who want no collector dependency |

Collector mode is mostly *enable the OTLP logs exporter + document the collector config*; it needs no `Azure.Messaging.EventHubs` reference. Direct mode is where the SDK dependency and the in-process exporter live.

### 5.4 Auth — both options (Direct mode only)

`EventHubExporterConfig.AuthMode = ManagedIdentity | ConnectionString`:

- **ManagedIdentity** (recommended): `FullyQualifiedNamespace` + `EventHubName` + a `TokenCredential` via the existing `ManagedIdentityCredentialProvider` (supports user-assigned via optional `ManagedIdentityClientId`).
- **ConnectionString**: from Key Vault / env — **never inline in appsettings** (the config XML doc states this, mirroring `OtlpExporterConfig.Headers`).

Collector mode's auth to Event Hub is the collector's concern, not the app's.

### 5.5 Backpressure — best-effort, drop-on-overflow

Logging must never block or crash the app:

- Batching via `BatchLogRecordExportProcessor` with a **bounded** queue; on a full queue, records are **dropped**, not buffered unboundedly.
- A `logs.export.dropped` counter (and export-failure counter) makes drops observable.
- **Recursion guard:** the Event Hub exporter's own failure logs must not route back through the Event Hub sink (infinite loop). The exporter logs failures to console only, and/or its own log category is excluded from OTel export.

## 6. Configuration schema

New `EventHubExporterConfig` under `ExportersConfig.EventHub`, plus a logs-signal toggle. Off by default.

```jsonc
"AppConfig": {
  "Observability": {
    "Logs": {
      "OtelExportEnabled": false,          // master switch for the ILogger→OTel bridge
      "MinExportLevel": "Information"       // floor for what is exported (cost control)
    },
    "Exporters": {
      "EventHub": {
        "Enabled": false,
        "DeliveryMode": "Collector",        // Collector | Direct
        "Redaction": {
          "Enabled": true,
          "Categories": [ "Email", "Phone", "Ssn", "CreditCard",
                          "IpAddress", "AwsKey", "JwtToken", "Generic" ]
        },
        // --- Direct mode only ---
        "AuthMode": "ManagedIdentity",      // ManagedIdentity | ConnectionString
        "FullyQualifiedNamespace": "my-ns.servicebus.windows.net",
        "EventHubName": "harness-logs",
        "ManagedIdentityClientId": null,    // optional user-assigned MI
        "ConnectionString": null,           // ConnectionString mode; from Key Vault, never inline
        "MaxQueueLength": 10000,
        "MaxExportBatchSize": 512,
        "FlushInterval": "00:00:05",
        "MaxConcurrentSends": 2
      }
    }
  }
}
```

**Validation** (`EventHubExporterConfigValidator`, FluentValidation + `ValidateOnStart`, wired at the single `RegisterValidatedConfigSections` point — the exact pattern shipped in #177/#173):

- Gated on `Enabled` (like the `GitOps`/`Iac` validators, which require operator-supplied values only when on).
- `DeliveryMode == Direct` ⇒ require the auth fields: `ManagedIdentity` ⇒ non-empty `FullyQualifiedNamespace` + `EventHubName`; `ConnectionString` ⇒ non-empty `ConnectionString`.
- Positivity on `MaxQueueLength`, `MaxExportBatchSize`, `MaxConcurrentSends`, `FlushInterval` (all `> 0`).
- `MinExportLevel` parses to a valid `LogLevel`.

## 7. Security

- **PII scrubbed in-app before export** (§5.2) — covers Collector mode too, so PII never transits the OTLP wire.
- No secrets in appsettings; Managed Identity preferred; connection string from Key Vault.
- Redactor is intentionally over-redactive (false positives acceptable, false negatives are not).
- Recursion guard on exporter self-logging (§5.5).
- Off by default; fail-closed validation on a half-configured section.

## 8. Testing

CI has no live Azure/Event Hub (consistent with the harness's no-live-Kestrel/Docker CI constraints), so the Direct exporter sits behind a seam:

- **Unit** — `LogRecordRedactionProcessor` scrubs message + string attributes across categories (parametrized); `EventHubExporterConfigValidator` (mirror the #173 validator tests + a `ConfigValidationStartupTests` wiring row); Direct exporter batches and **drops on overflow** against a fake `IEventHubLogSink` (no Azure).
- **Integration** — `WithLogging` pipeline emits **redacted** `LogRecord`s end-to-end using an in-memory exporter; both host shapes (web + desktop) wire the logs signal; `ValidateOnStart` aborts boot on a bad section.
- **Not in CI** — a live Event Hub round-trip is a manual/staging check.

## 9. Rollout (one concern per PR)

1. **PR1 — OTel logs signal + PII processor.** Bridge `ILogger` → OTel on both host shapes; add `ITelemetryConfigurator.ConfigureLogging`; add `LogRecordRedactionProcessor` (reusing `IContentRedactionFilter`); `Logs.OtelExportEnabled` + OTLP logs exporter. **Off by default.** *This PR alone closes the Grafana-logs gap.*
2. **PR2 — Event Hub config + Collector mode.** `EventHubExporterConfig` + validator + `ValidateOnStart` wiring; Collector-mode docs (OTLP logs → collector Kafka/Azure exporter → Event Hub). Mostly config + docs; no SDK dependency.
3. **PR3 — Direct mode.** Add `Azure.Messaging.EventHubs`; in-process `BaseExporter<LogRecord>` behind `IEventHubLogSink`; both auth modes; bounded queue + drop-on-overflow + dropped/failure counters + recursion guard.
4. **PR4 (optional) — docs.** Security guide (PII flow, auth, recursion guard) + observability blueprint update (logs now a signal; Grafana Loki wiring notes).

## 10. Open questions

1. **`MinExportLevel` default** — `Information`, or `Warning` to cap Event Hub cost/volume out of the box?
2. **Collector exporter choice** — Kafka exporter against Event Hub's Kafka endpoint vs. a contrib Azure exporter. To confirm at the ops tier; app is unaffected either way (both consume OTLP).
3. **Attribute promotion** — should the existing StructuredJson fields (token counts, tool names, session id) be promoted to OTel log attributes so they're queryable in Grafana/Event Hub, or is the formatted message enough for v1?
4. **Redaction categories default** — ship the full set on (recommended, over-redactive) or a narrower default with docs to widen?
```
