# Shipping application logs to Azure Event Hub (Collector mode)

This guide wires the harness's application **logs** to an **Azure Event Hub** — the
standard ingestion pipe into a SIEM, Log Analytics, or Azure Data Explorer — through
the OpenTelemetry Collector you already run. No application code or extra dependency
is involved: the app emits OTLP logs, and the Collector forwards them.

```
  AgentHub (app)                     OTel Collector                    Azure
  ─────────────                      ──────────────                    ─────
  ILogger ──► OTel logs ──OTLP/gRPC──► logs pipeline ──Kafka/SASL_SSL──► Event Hub
  (PII scrubbed in-process)            (this directory)   :9093           (Kafka endpoint)
```

PII is redacted **in the app**, before anything leaves the process (see
`AppConfig:Observability:Logs` and `LogRecordRedactionProcessor`). Nothing sensitive
transits this hop, so the Collector's logs pipeline does not re-run redaction.

> **Direct mode** (an in-process exporter that talks to Event Hub without a Collector)
> is a separate, opt-in path that pulls in the `Azure.Messaging.EventHubs` SDK. It is
> not part of this guide — use Collector mode when you already run a Collector (the
> harness reference topology does).

---

## 1. Prerequisites

**App side** — turn the logs signal on and point it at the Collector (`appsettings.json`):

```jsonc
"AppConfig": {
  "Observability": {
    "Logs": {
      "OtelExportEnabled": true,      // bridge ILogger → OTel logs
      "MinExportLevel": "Information"  // set "Warning" to cap Event Hub volume/cost
    },
    "Exporters": {
      "Otlp": {
        "Enabled": true,
        "Endpoint": "http://localhost:4317"  // the Collector's OTLP/gRPC endpoint
      }
    }
  }
}
```

Logs are **off by default**; the two switches above are what turn Collector delivery on
from the app side. Redaction is on by default with the full category set — leave it on.

**Azure side:**

- An **Event Hubs namespace**, **Standard tier or higher** (the Kafka endpoint is not
  available on Basic).
- An **event hub** (entity) within it to receive the logs, e.g. `harness-logs`.
- Either the namespace **connection string** (with `Send` rights) or a **Microsoft Entra**
  identity granted the **Azure Event Hubs Data Sender** role (see § Authentication).

---

## 2. Enable the Collector exporter

The `logs` pipeline and a `kafka/eventhub` exporter are already defined in
[`config.yaml`](./config.yaml). They ship **inert**: the pipeline exports only to `debug`
(stdout) until you opt in.

1. Set the environment variables the Collector reads (e.g. in `scripts/otel-collector/.env`,
   which `start-collector.ps1` loads, or your orchestrator's secret store — **never inline
   the connection string in a committed file**):

   ```bash
   EVENTHUB_BOOTSTRAP=<your-namespace>.servicebus.windows.net:9093
   EVENTHUB_LOGS_TOPIC=harness-logs                 # the event hub (entity) name
   EVENTHUB_CONNECTION_STRING=Endpoint=sb://<your-namespace>.servicebus.windows.net/;SharedAccessKeyName=...;SharedAccessKey=...
   ```

2. In `config.yaml`, uncomment `kafka/eventhub` in the **logs** pipeline's exporter list:

   ```yaml
   logs:
     receivers: [otlp]
     processors: [memory_limiter, filter/app_only, resourcedetection, attributes, batch]
     exporters:
       - debug
       - kafka/eventhub   # ← uncomment
   ```

3. Restart the Collector: `./start-collector.ps1` (or `docker-compose up -d`).

---

## 3. Authentication

The exporter ships configured for **connection-string** auth (SASL/PLAIN over TLS):

| Kafka setting | Value |
|---|---|
| bootstrap / broker | `<namespace>.servicebus.windows.net:9093` |
| security protocol | `SASL_SSL` (TLS on, `insecure: false`) |
| SASL mechanism | `PLAIN` |
| SASL username | the literal `$ConnectionString` |
| SASL password | the namespace connection string (`Endpoint=sb://…`) |
| topic | the event hub (entity) name |

The username is a literal token, not your identity — in `config.yaml` it is written
`$$ConnectionString` so the Collector's env-var expansion emits a single `$`.

**Microsoft Entra (OAuth) instead of a connection string** — preferred for production, no
long-lived secret. Event Hubs supports OAuth on the Kafka endpoint; switch the exporter's
`auth.sasl.mechanism` to `OAUTHBEARER` and supply a token source, and grant the Collector's
identity the **Azure Event Hubs Data Sender** role on the namespace. Follow the
[Event Hubs Kafka OAuth guidance](https://learn.microsoft.com/azure/event-hubs/apache-kafka-developer-guide#authentication-with-oauth--microsoft-entra-id);
the exact Collector fields are version-specific — check the
[kafka exporter README](https://github.com/open-telemetry/opentelemetry-collector-contrib/blob/main/exporter/kafkaexporter/README.md)
for the version pinned in [`docker-compose.yml`](./docker-compose.yml).

---

## 4. Verify

1. **App is emitting:** with `debug` in the pipeline, the Collector prints each log record
   to its stdout (`docker-compose logs -f otel-collector`). No records → check the app's
   `Logs:OtelExportEnabled` / `Otlp:Endpoint`.
2. **Redaction is working:** the debug output should already show `[REDACTED:…]` in place
   of any PII — it is scrubbed in the app, not here.
3. **Event Hub is receiving:** in the Azure portal, the event hub's **Messages** chart
   should rise once `kafka/eventhub` is enabled. The Collector's own metrics
   (`http://localhost:8888/metrics`) expose `otelcol_exporter_sent_log_records` and
   `otelcol_exporter_send_failed_log_records` for the kafka exporter.

---

## Notes

- **Volume / cost:** logs are not sampled (unlike traces). Control volume with the app's
  `MinExportLevel` (`Information` → `Warning`) rather than at the Collector.
- **Version-specific config:** the `kafka` exporter's fields (`auth`, `tls`, `protocol_version`)
  have shifted across Collector releases. This config targets the `0.123.0` image in
  `docker-compose.yml`; if you bump the image, re-check the exporter README.
- **Why the Kafka exporter:** the Collector has no dedicated Event Hub component; Event Hubs'
  Kafka-compatible endpoint is the supported path, and Microsoft documents Event Hubs as a
  log-aggregation sink for exactly this pattern.
