# Story: OTel Log Export to Azure Event Hub (with in-app PII filtering)

> **Status:** PR1 shipped (OTel logs signal + PII processor + validation, off by default). PR2 (Collector-mode delivery) shipped. PR3 (Direct mode) / PR4 (docs) pending.
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

### 5.6 Structured attribute promotion (decided — §10.2)

Many log events already carry rich structured fields (token counts, tool names, session id, agent id). These are promoted to first-class OTel **log-record attributes** rather than flattened into the message string, so they survive as **queryable dimensions** in Grafana and any downstream SIEM / Event Hub consumer — e.g. "every log for session X", "every tool-failure log". The redaction processor (§5.2) scrubs string attribute values before export, so promotion does **not** widen the PII surface.

## 6. Configuration schema

New `EventHubExporterConfig` under `ExportersConfig.EventHub`, plus a logs-signal toggle. Off by default.

```jsonc
"AppConfig": {
  "Observability": {
    "Logs": {
      "OtelExportEnabled": false,          // master switch for the ILogger→OTel bridge
      "MinExportLevel": "Information"       // export everything by default; set "Warning" to cap volume/cost
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

1. **PR1 — OTel logs signal + PII processor. ✅ SHIPPED.** Bridges `ILogger` → OTel on both host shapes via `AddOpenTelemetry().WithLogging(...)` (uniform for web + console — the bridge is an `ILoggerProvider` the standard factory picks up); adds `LogRecordRedactionProcessor : BaseProcessor<LogRecord>` (reuses `IContentRedactionFilter`); adds `LogsConfig` (`OtelExportEnabled` + `MinExportLevel` + redaction) at `AppConfig:Observability:Logs` with `LogsConfigValidator` + `ValidateOnStart`; OTLP logs exporter (same endpoint as traces/metrics). **Off by default; byte-identical when off.** *This PR alone closes the Grafana-logs gap.*
   - **Deviation 1 (better home):** redaction config lives under **`Logs`**, not `Exporters.EventHub.Redaction`. PII scrub is a *signal-wide* processor — it runs once before **every** log exporter (OTLP now, Event Hub later), so scoping it to one exporter would be wrong. §5.2's "before any exporter" is honored: the redactor is registered pre-build, ahead of the exporter, because the batch exporter snapshots the pooled record (an after-the-exporter processor would leak). PR2's `EventHub.Redaction` is therefore dropped as redundant.
   - **Deviation 2 (YAGNI):** `ITelemetryConfigurator.ConfigureLogging` seam is **deferred to PR2**, when a second log exporter (Azure Monitor / Event Hub Direct) actually needs it. PR1 wires the redactor + OTLP exporter directly; adding an interface method with no implementer would be inert. The ordering constraint (redactor must precede the exporter, and OTLP must register pre-build) also means the deferred-configurator seam couldn't express PR1's pipeline anyway.
2. **PR2 — Collector-mode delivery. ✅ SHIPPED (docs + collector config, no app code, no SDK dependency).** Now that PR1 emits OTLP logs, Collector mode needs no application code — only the Collector, which the reference topology already runs. Added a `logs` pipeline to `scripts/otel-collector/config.yaml` (OTLP receiver → memory_limiter/filter/resource/attributes/batch → `debug`, with an opt-in `kafka/eventhub` exporter to Event Hub's Kafka-compatible endpoint), the `EVENTHUB_*` env plumbing in `docker-compose.yml`, and an operator guide (`scripts/otel-collector/EVENTHUB-LOGS.md`). The exporter is inert by default (defined + commented-out of the pipeline, exactly like the existing `azuremonitor` exporter), so a running Collector is unaffected until an operator opts in.
   - **Deviation vs sketch:** the original PR2 also bundled `EventHubExporterConfig` + validator, but that config only has a consumer in Direct mode (PR3). Shipping a validated config class with nothing consuming it is the "built-but-never-wired" anti-pattern the harness audits reject, so it moves to **PR3**, where the Direct exporter consumes it. Collector auth to Event Hub (connection string or Microsoft Entra) is the Collector's concern, documented in the operator guide — no app config.
3. **PR3 — Direct mode.** Add `Azure.Messaging.EventHubs`; in-process `BaseExporter<LogRecord>` behind `IEventHubLogSink`; both auth modes; bounded queue + drop-on-overflow + dropped/failure counters + recursion guard.
4. **PR4 (optional) — docs.** Security guide (PII flow, auth, recursion guard) + observability blueprint update (logs now a signal; Grafana Loki wiring notes).

## 10. Decisions (settled 2026-07-12)

1. **Log verbosity — export everything by default.** `MinExportLevel` defaults to `Information` (the full routine + problem trail), with a single config knob to dial back to `Warning` when a consumer wants to cap Event Hub volume/cost. See §6.
2. **Promote structured fields to log attributes.** Token counts, tool names, session id, etc. become first-class OTel log attributes rather than being baked into the message text, so they are directly filterable in Grafana and downstream SIEM. See §5.6.
3. **PII redaction — full set on by default.** All content categories (email, phone, SSN, credit-card, IP, AWS key, JWT, generic credentials) are enabled by default; over-redaction is the intended safe posture for a compliance-sensitive template. See §5.2 / §6.
4. **Collector exporter choice — deferred to backlog.** Kafka-endpoint vs. contrib Azure exporter is an ops-tier decision that does not affect app code (both consume OTLP). Tracked as a backlog follow-up, **not** a blocker for PR1–PR3.
```
