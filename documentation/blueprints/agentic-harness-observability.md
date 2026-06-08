# Blueprint: Microsoft Agentic Harness Observability

Status: Draft v1 (2026-06-03). Modeled after the
[OpenTelemetry Blueprints initiative](https://opentelemetry.io/blog/2026/blueprints-intro/)
(End-User SIG + DevEx SIG, May 2026) and grounded in the published
[reference implementations](https://opentelemetry.io/docs/guidance/reference-implementations/)
from Adobe, Mastodon, and Skyscanner.

This blueprint is intentionally scoped. Anything not listed under
"Common Challenges" is out of scope and should be addressed by a separate
document.

---

## Summary

You are running, extending, or cloning the **Microsoft Agentic Harness** — a
production-grade .NET 10 template for a Microsoft Agent Framework agent that
ships with skills, plugins, MCP server + client, RAG, knowledge graph,
governance, and a SignalR-backed dashboard (AgentHub). You want
end-to-end observability that:

- Works across the local Grafana + Tempo + Prometheus stack **and** Azure
  Monitor in production, without two divergent instrumentation surfaces.
- Captures every signal a multi-skill agent generates — LLM calls, tool
  invocations, skill prerequisites, RAG retrievals, KG queries, plan steps,
  governance decisions — under stable, vendor-neutral semantic conventions.
- Survives AI-assisted code changes without entropy creeping into metric
  names, span attributes, or exporter configuration.
- Stays cheap and high-signal at scale (cardinality control, sampling, error
  filtering) without losing the events the safety/governance subsystem
  depends on.

If you are a **template consumer**, this blueprint tells you what you inherit,
what you can safely change, and where the locked surfaces are.

If you are a **harness maintainer**, this blueprint is the prescriptive rulebook
that every PR touching `Telemetry/`, `Infrastructure.Observability/`, or
metric/span emission must conform to.

---

## Common Challenges

These are the only problems this blueprint solves. Each maps to a real failure
mode this harness has either hit or is at meaningful risk of hitting.

1. **Multi-team / multi-skill drift.** Skills, plugins, and tools are
   contributed independently. Without enforced conventions, each adds its own
   span names, metric names, or attribute schemas, producing the exact
   "disjointed telemetry" failure mode the OTel blueprints initiative was
   created to address.
2. **AI-assisted entropy.** Agent-authored PRs add new `Meter`/`ActivitySource`
   names, new attributes, or new exporter wiring without checking the existing
   registry. Over time this produces incompatible dashboards and broken
   alerts.
3. **Double-prefix and similar accidental-complexity bugs.** App-level metric
   prefixes collide with the collector's namespace processor (see
   `memory/project_telemetry_fix.md`). This is recurring across teams that
   mix SDK-side and Collector-side naming.
4. **Tool-output and prompt cardinality.** Tool result payloads and free-form
   prompts attached as span attributes blow up storage costs and the
   `Tempo`/`Azure Monitor` cardinality budget.
5. **LLM provider chain visibility.** When the harness falls back across
   providers (Polly fallback chain → Azure OpenAI → Anthropic → local),
   spans must clearly attribute the call to the *effective* provider and
   the *intended* provider.
6. **GenAI / Agent semconv gap.** OTel has stabilized `gen_ai.*` and
   AI-agent semantic conventions; our internal `Conventions/` registry must
   map to them so any OTel-aware backend understands our traces without
   custom dashboards.
7. **Dashboard contract regressions.** AgentHub's
   `Tests/Telemetry/DashboardContractTests` enforce the metric naming
   contract today; the blueprint must keep that authoritative as new metrics
   are added.
8. **Local vs. cloud parity.** A developer running `docker compose` against
   Grafana+Tempo+Prom must see the same span shape that Azure Monitor sees
   in prod. Diverging two pipelines is a known anti-pattern.
9. **SignalR span fan-out.** The custom `SignalRSpanExporter` streams spans
   to the dashboard. It must not become a back-pressure source for the OTLP
   exporter, and it must not see signals the user is not authorized for.

### Explicitly out of scope

- Backend selection (Grafana vs. Datadog vs. Honeycomb vs. Azure Monitor).
  Pick what your org needs. The OTLP egress is the only contract.
- Profiles, eBPF, or continuous profiling. Tracked separately when the
  OTel Profiles GA lands in our pipeline.
- Audit logging for compliance (`Infrastructure.AI.KnowledgeGraph.Compliance`
  has its own JSONL store and provenance receipts).
- Cost telemetry for Azure resources (handled by `nexus_budget` and
  FinOps tooling, not the OTel pipeline).
- Client-side browser telemetry from the Dashboard SPA. Future blueprint.

---

## General Guidelines

### G1 — One conventions registry, full stop

All span names, span attributes, metric names, metric attributes, and log
fields **must** be declared in `Domain.AI/Telemetry/Conventions/*.cs` or
`Domain.Common/Telemetry/AppSourceNames.cs`. Nothing emits raw strings.

This is the harness's analog to Adobe's locked sidecar and Mastodon's
"never invent your own conventions" rule.

- Per-domain conventions files already exist (`AgentConventions`,
  `ToolConventions`, `RagConventions`, `GovernanceConventions`, …).
- Adding a new convention requires updating the matching `Conventions/*.cs`
  file. Today `TelemetryConventionsTests` and `TokenAndToolConventionsTests`
  assert that **declared** constants equal their expected literal values —
  they do not scan emit sites and therefore do not fail the build when an
  emitter calls `Activity.SetTag("foo.bar", …)` outside the registry. That
  emit-site enforcement is v2 backlog (Roslyn analyzer + reflection-based
  membership test). Until then this is a PR-review rule.
- `MetricNamingContract` enforces that the **registered** instrument list
  matches its declared `InstrumentDefinition` table — net-new instruments
  added to code without a matching contract row WILL fail the build.
- Where an OTel SemConv attribute exists (`gen_ai.system`, `gen_ai.request.model`,
  `gen_ai.usage.input_tokens`, `service.name`, `http.*`, `db.*`), use it
  verbatim. Do not invent parallels.

### G2 — Map every agent concept to GenAI SemConv

OpenTelemetry's `gen_ai.*` and AI-agent conventions are the universal
vocabulary. Adoption is non-negotiable for new code and required for
existing code on the next touch.

The harness already adopts a substantial slice of `gen_ai.*` across
`AgentConventions`, `ToolConventions`, and `TokenConventions`. To preserve
G1 ("one registry"), the single entry point is
`Domain.AI/Telemetry/Conventions/GenAiSemconvRegistry.cs`, which re-exports
the existing keys and declares the rest. **New code references
`GenAiSemconvRegistry.*` only.**

| Harness concept                        | OTel SemConv key (via registry)                                  | Status |
| -------------------------------------- | ---------------------------------------------------------------- | ------ |
| Agent run / turn                       | `OperationName = "chat"` or `"invoke_agent"`                     | already used |
| LLM provider (effective)               | `System` (re-export of `AgentConventions.GenAiSystem`)           | already used |
| LLM provider (intended, pre-fallback)  | `SystemIntended` (harness-vendored)                              | NEW    |
| Requested model id                     | `RequestModel`                                                   | already used |
| Resolved model id                      | `ResponseModel`                                                  | NEW    |
| Token usage                            | `UsageInputTokens` / `UsageOutputTokens`                         | already used |
| Cache token accounting                 | `UsageCacheReadInputTokens` / `UsageCacheCreationInputTokens`    | already used |
| Tool invocation                        | `ToolName`, `ToolCallId`, `ToolType`                             | partial (Name yes; CallId/Type NEW) |
| Tool I/O                               | `ToolCallArguments` / `ToolCallResult`                           | already used |
| Conversation correlation               | `ConversationId` (parallel-emitted with `agent.conversation.id`) | NEW    |
| Multi-skill agent identity             | `AgentName`, `AgentId`, `HarnessSkillName`, `HarnessSkillMode`   | NEW    |
| Plugin attribution                     | `HarnessPluginId` (harness-vendored)                             | NEW    |
| Plan step                              | `OperationName` + planned `gen_ai.harness.plan.step.*` namespace | NOT YET DECLARED — define under `ToolConventions` or a new `PlanConventions` before first emitter ships |

Anything harness-specific (skills, plugins, RAG sub-pipelines, plan DAG nodes)
sits under `gen_ai.harness.*` or the existing `harness.*` namespace. Never
invent attributes under reserved OTel prefixes.

### G3 — Two-tier emission, locked inner surface

Following Adobe's pattern (locked sidecar + configurable deployment
collector). In this harness the two tiers are:

- **Pre-build / core (locked)**:
  `Presentation.Common/Extensions/OpenTelemetryServiceCollectionExtensions.cs`
  owns the resource builder (`service.name`, `service.namespace`,
  `service.version`, `deployment.environment`), the harness `ActivitySource`
  and `Meter` registration (via `AppInstrument`), default sampler
  (`AlwaysOnSampler` — tail sampling lives at the Collector per G6),
  HTTP client instrumentation, runtime instrumentation, and the OTLP
  exporter (registered pre-build because `AddOtlpExporter` calls
  `ConfigureServices`). The file branches by entry assembly:
  - `AddWebTelemetry` ALSO registers `AddAspNetCoreInstrumentation`,
    Kestrel + ASP.NET Core hosting meters, and the Prometheus exporter.
  - `AddDesktopTelemetry` (used by `Presentation.ConsoleUI`) does NOT add
    those — ASP.NET Core spans/metrics will not appear from a desktop
    entry point, by design.

  **Template consumers do not modify this file.**
- **Finalization / extension (configurable)**:
  `Infrastructure.Observability/Exporters/ObservabilityTelemetryConfigurator.cs`
  (Order 300, runs after domain configurators) owns the harness-specific
  processor chain: PII filter → rate limiter → LLM token tracking →
  tool effectiveness/usefulness → causal span attribution → tail-based
  sampling, then the Azure Monitor exporter behind a config flag.
- **Consumer extension point**: net-new sources are registered by adding an
  `ITelemetryConfigurator` implementation in the appropriate layer. Net-new
  exporters belong at the Collector tier (G7), not in either of the above
  files.

This is also what prevents recurrences of the
[double-prefix bug](../memory/project_telemetry_fix.md). The configurator
owns the prefix; the Collector owns the namespace; neither knows about the
other.

### G4 — Start simple, evolve gradually (Skyscanner rule)

The Collector pipeline ships with **only**:

1. `memorylimiter` (day-one, non-negotiable — Skyscanner's #1 advice)
2. `batch`
3. `otlphttp` exporter to backend(s)
4. `attributes` processor for the **single** purpose of stripping reserved /
   sensitive fields at the egress boundary.

Anything beyond this — tail sampling, span-to-metrics, transform processors,
load-balancing exporters — requires an ADR. We do not preinstall
infrastructure for hypothetical needs.

### G5 — Cardinality budget per signal type

Every new metric MUST be entered in
`Tests/Presentation.AgentHub.Tests/Telemetry/Contracts/MetricNamingContract.cs`.
Today the contract enforces NAME, TYPE, and UNIT — not cardinality. The
cardinality budget is enforced by **PR-review discipline**, with mechanical
enforcement planned in v2 (see backlog: extend `InstrumentDefinition` with
a `MaxCardinality` field plus a metric-pipeline test that fails when an
instrument exceeds its declared budget at runtime).

Three rules to apply in review until the mechanical check lands:

- High-cardinality identifiers (user id, session id, trace id, agent
  conversation id, full prompt, full tool output) **never** appear as
  metric labels.
- They MAY appear as span attributes, capped by an attribute-length limit
  (default: 8 KiB per attribute, configurable in `appsettings.json` →
  `AppConfig.Telemetry.SpanAttributeMaxBytes`).
- Tool outputs above the cap go through the existing Tool Output
  Compression pipeline behavior. The compressed body is referenced from the
  span via a content-hash, not embedded.

### G6 — Sampling strategy: tail at the Collector, head at the SDK

- **SDK head sampling**: `ParentBased(AlwaysOn)` for development,
  `ParentBased(TraceIdRatioBased(1.0))` for prod where the Collector does
  the real work. Never `AlwaysOff` — keeps the option open for the
  Collector to decide.
- **Collector tail sampling** (Mastodon's pattern): keep 100% of
  error/escalation/safety-violation traces, sample successful agent runs at
  a ratio the operator picks. Defaults in
  `infrastructure/collector/tail-sampling.yaml`: 100% for spans with the
  OTel-canonical `error.type` attribute present (exposed via
  `GenAiSemconvRegistry.ErrorType`), 100% for `harness.governance.escalation.*`,
  10% for the rest.

This matches the safety/governance requirement that every escalation must
be diagnosable end-to-end.

### G7 — Vendor-neutral egress is the goal; one tolerated exception today

Match Skyscanner: the preferred egress is OTLP, and any backend-specific
exporter (Datadog, Honeycomb) sits behind a Collector exporter, not inside
the application. This keeps the SDK boundary unchanged when teams swap
backends and isolates vendor-side breakage from agent code.

**Today's state (be precise — the blueprint won't lie about reality):**
`Infrastructure.Observability/Exporters/ObservabilityTelemetryConfigurator.cs`
registers `AddAzureMonitorTraceExporter` and `AddAzureMonitorMetricExporter`
in-process, gated on `AppConfig.Observability.Exporters.AzureMonitor.Enabled`.
This is the single tolerated in-process vendor exporter, and it lives in
`Infrastructure.Observability` (never in `Presentation.*`).

**Rules a new vendor exporter must follow:**
- It MUST live in `Infrastructure.Observability/Exporters/`, never in
  `Presentation.*` or `Application.*`.
- It MUST be gated by an `Enabled` flag on `AppConfig.Observability.Exporters.<Vendor>`.
- It MUST be added via an `ITelemetryConfigurator` implementation (not
  inline in the SC extension).
- Two vendor exporters MUST NOT be enabled simultaneously without an ADR —
  doubled in-process buffering is a known OOM risk.
- The OTLP-via-Collector path is preferred and should be the long-term
  destination for the Azure Monitor exporter once a Collector-side Azure
  Monitor exporter is in place.

### G8 — Sensitive-content boundary

The `SignalRSpanExporter` is a per-user fan-out. Apply the principle from
Adobe's locked sidecar: it must redact before emit, not after observe.

- Redaction list lives in
  `Presentation.AgentHub/Telemetry/SpanData.cs` and is reviewed in the
  same PR cadence as `security-reviewer` runs.
- Attributes prefixed `harness.private.*` are dropped before the exporter
  hands the span to SignalR.
- The dashboard subscription enforces tenant/user scope via the existing
  `IKnowledgeScopeValidator` chain. The exporter never bypasses it.

#### Content Capture Policy (PR-11)

The OTel GenAI conventions allow attaching full prompt / output / tool /
orchestration content to spans for debugging and audit. This is **OFF by
default** and gated behind a closed-by-default policy so a fresh
`appsettings.json` never silently surfaces user content in traces.

**Stability opt-in is mandatory when capture is on.** Content keys live in the
experimental GenAI SemConv tier. When capture is enabled the
`OTEL_SEMCONV_STABILITY_OPT_IN` environment variable **must** equal
`gen_ai_latest_experimental`
(`GenAiSemconvRegistry.SemconvStabilityOptInValue`). The hosted
`ContentCaptureStartupValidator` reads this at boot and **fails the host**
when capture is on but the variable is unset or wrong, and again if no
`IContentRedactionFilter` is registered — content can only leave the domain
through a redaction filter.

**Two-level gate.** A master `Enabled` flag is the single operator decision;
per-attribute toggles then opt individual attributes in. When the master flag
is off, every `ShouldCapture*` check on `IContentCapturePolicy` returns false
regardless of a stale per-attribute toggle. The `ContentCaptureConfigValidator`
(FluentValidation, auto-discovered) enforces that, when enabled,
`RedactionCategories` is non-empty and every entry maps to a known
`RedactionCategory`.

**Redaction before emit.** Every captured string passes through
`DefaultContentRedactionFilter` (a fixed, over-redactive regex rule set) before
it is attached. The eight `RedactionCategory` values:

| Category | Masks |
|---|---|
| `Email` | Email addresses (RFC 5322 simplified) |
| `Phone` | E.164 + North-American phone numbers |
| `Ssn` | US Social Security Numbers |
| `CreditCard` | Credit-card PANs (broad, Luhn-ish) |
| `IpAddress` | IPv4 / IPv6 addresses |
| `AwsKey` | AWS access-key ids (`AKIA…`, `ASIA…`, …) |
| `JwtToken` | JWT tokens (`header.payload.signature`) |
| `Generic` | Catch-all: `Bearer …`, `AccountKey=`, `api_key=`, … |

The filter is intentionally noisy: a false positive that masks a benign string
is acceptable; a false negative that leaks a credit-card number into a span is
not.

**Schema** — bind from `AppConfig.AI.Telemetry.ContentCapture`:

```jsonc
{
  "AI": {
    "Telemetry": {
      "ContentCapture": {
        "Enabled": false,                    // master toggle — OFF by default
        "CapturePromptContent": false,       // gen_ai.input.messages
        "CaptureOutputContent": false,       // gen_ai.output.messages
        "CaptureToolCallArguments": false,   // gen_ai.tool.call.arguments
        "CaptureToolCallResult": false,      // gen_ai.tool.call.result
        "CaptureMagenticPlanContent": false, // ...magentic.plan.content
        "CaptureMagenticReplanReason": false,// ...magentic.replan.reason
        "CaptureMagenticProgressContent": false,
        "CaptureMagenticPlanReviewFeedback": false,
        "RedactionCategories": [
          "Email", "Phone", "Ssn", "CreditCard",
          "IpAddress", "AwsKey", "JwtToken", "Generic"
        ]
      }
    }
  }
}
```

To turn capture on in an environment: set the env var
(`OTEL_SEMCONV_STABILITY_OPT_IN=gen_ai_latest_experimental`), flip `Enabled`
to `true`, and enable only the specific per-attribute toggles you need. Leave
`RedactionCategories` at the full list unless you have a deliberate,
reviewed reason to shrink it.

### G9 — Convention upgrades happen on a fixed cadence

Skyscanner's biggest pain was 6-month upgrade gaps that batched breaking
SemConv changes. Pin the OTel SDK + SemConv NuGet packages on a known
cadence (every minor release of `OpenTelemetry.SemanticConventions`,
~6-week interval). Use Weaver for SemConv generation once it stabilizes
for .NET; track in an ADR.

### G10 — Gradual rollout for telemetry changes

Promote Collector / configurator changes through `dev → staging → prod`
with Argo CD-style PR-based promotion (Skyscanner's `Dev → Alpha → Beta →
Prod` pattern compressed for our smaller footprint). Telemetry PRs ship
in their own commit, never bundled with feature work, so a regression is
trivially revertable.

---

## Implementation

Concrete actions to bring an existing harness clone into compliance, or to
keep a new clone compliant.

### Implementation map (existing code → blueprint guideline)

| Guideline | Owning file(s)                                                                                          |
| --------- | ------------------------------------------------------------------------------------------------------- |
| G1        | `src/Content/Domain/Domain.AI/Telemetry/Conventions/*.cs`, `Domain.Common/Telemetry/AppSourceNames.cs`, `Domain.Common/Telemetry/AppInstrument.cs` |
| G2        | `Domain.AI/Telemetry/Conventions/GenAiSemconvRegistry.cs` (NEW — re-exports existing `gen_ai.*` keys + fills the gaps in one place) |
| G3        | Locked core: `Presentation.Common/Extensions/OpenTelemetryServiceCollectionExtensions.cs`. Finalization: `Infrastructure.Observability/Exporters/ObservabilityTelemetryConfigurator.cs` (Order 300). |
| G4        | `infrastructure/collector/otelcol-config.yaml` (template)                                               |
| G5        | `Tests/Presentation.AgentHub.Tests/Telemetry/Contracts/MetricNamingContract.cs`                         |
| G6        | `infrastructure/collector/tail-sampling.yaml` (template); SDK head config in `ObservabilityTelemetryConfigurator` |
| G7        | All exporter wiring at the Collector layer; never `AddAzureMonitorOpenTelemetry` in app code            |
| G8        | `Presentation.AgentHub/Telemetry/SpanData.cs`, `SignalRSpanExporter.cs`, `IKnowledgeScopeValidator`     |
| G9        | `Directory.Packages.props` pin policy + ADR                                                             |
| G10       | `.github/workflows/` + Argo CD overlay (consumer-supplied)                                              |

### Required actions for a new clone (in order)

1. Set `service.name`, `service.namespace`, `deployment.environment` in
   `appsettings.{Environment}.json` under `AppConfig.Telemetry.Resource`.
2. Set the OTLP endpoint and headers in `AppConfig.Telemetry.Otlp`. Never
   inline a vendor SDK exporter in app code.
3. Stand up a Collector — local docker compose stack ships in
   `infrastructure/collector/`. Use the minimal pipeline (G4) and add the
   tail-sampling overlay (G6) only when going to prod.
4. Add backend-specific exporters in the Collector config. Do not touch
   `OpenTelemetryServiceCollectionExtensions` unless adding a new
   instrumentation source.
5. Run `dotnet test src/Content/Tests/Presentation.AgentHub.Tests` —
   the `DashboardContractTests` and `MetricNamingContract` tests will fail
   loudly if any guideline is violated.

### Required actions for a feature PR

- Adding a new metric, span, or attribute? Update the matching
  `Conventions/*.cs` file in the same PR. CI enforces it.
- Adding a new LLM provider or tool? Wire it through `gen_ai.*` semconv
  (G2) — don't invent parallel attributes.
- Adding a new exporter? Add it to the Collector config, not the SDK.
- Touching `ObservabilityTelemetryConfigurator.cs`? Run `code-review` and
  add an ADR if the inner surface changes.

### Things that must fail review

These are the rules. Some are mechanically enforced today; the rest are
PR-review discipline until the analyzer + reflection tests in v2 land. The
"Enforcement" column states which is which.

| Rule | Enforcement today |
| ---- | ----------------- |
| New `Meter("…")` or `ActivitySource("…")` outside `AppSourceNames.cs` | reviewer (v2: Roslyn analyzer) |
| New `Activity.SetTag` / `Counter.Add` / `Histogram.Record` calls using raw `gen_ai.*` or `harness.*` string literals instead of `GenAiSemconvRegistry.*` or the per-domain `Conventions/*.cs` const | reviewer (v2: Roslyn analyzer) |
| Metric names containing the service name as a prefix (double-prefix rule, [No App Prefix](../memory/feedback_no_app_prefix.md)) | reviewer (recurrence risk — see [Telemetry Fix](../memory/project_telemetry_fix.md)) |
| New instrument registered in code without a matching row in `MetricNamingContract.AllInstruments` | mechanically enforced by `MetricNamingContract` tests |
| Direct vendor-specific exporter (`AddAzureMonitorOpenTelemetry`, `AddDatadogTracing`, `AddHoneycomb…`) anywhere outside `Infrastructure.Observability/Exporters/`, OR a second vendor exporter enabled alongside the existing Azure Monitor exporter without an ADR | reviewer (v2: assembly-scoped analyzer) |
| New high-cardinality metric labels (user id, trace id, full prompt) | reviewer + `MetricNamingContract` catches common shapes; cardinality budget enforcement is v2 backlog (see G5) |
| Tool output emitted raw onto a span attribute without going through the Tool Output Compression behavior | reviewer (v2: span-attribute size assertion in integration tests) |

---

## Lessons learned (adopted from the published reference implementations)

These are the patterns we pulled from Adobe, Mastodon, and Skyscanner and the
guideline each ended up driving.

| Source     | Lesson                                                                                                          | Where it lives here   |
| ---------- | --------------------------------------------------------------------------------------------------------------- | --------------------- |
| Adobe      | Locked, immutable inner surface (sidecar) + configurable outer surface (deployment collector).                   | G3                    |
| Adobe      | Per-signal collector deployments at the managed tier to isolate failure modes.                                   | G10 (future)          |
| Adobe      | Custom Collector distro with only the components you use — avoid Contrib bloat.                                  | G4 (rule: no Contrib component without ADR) |
| Adobe      | Chained-collector error visibility is a known pitfall — backend errors return as 200 to the inner collector.    | Collector readme + alert on `otelcol_exporter_send_failed_*` |
| Mastodon   | One collector per scope is plenty until proven otherwise.                                                        | G4                    |
| Mastodon   | Operator + Argo CD = declarative, auto-recovering, Git-audited Collector lifecycle.                              | G10                   |
| Mastodon   | Strict SemConv adherence — never invent your own vocabulary.                                                     | G1, G2                |
| Mastodon   | Tail-sampling beats resource limits for controlling overhead.                                                    | G6                    |
| Mastodon   | Downstream-operator freedom: env-var-configurable SDK so consumers can disable, route, or change backends.       | G7                    |
| Skyscanner | Start with `memorylimiter` + `batch` + OTLP exporter. Nothing else on day one.                                   | G4                    |
| Skyscanner | Memory limiter from day one prevents a whole class of incidents.                                                 | G4                    |
| Skyscanner | Filter processors early — high-volume "false positive" errors (their 404 cache misses) wreck sampling budgets.   | G5, G6                |
| Skyscanner | Don't over-engineer telemetry resiliency — in-memory batching is enough.                                         | G4                    |
| Skyscanner | Gradual rollouts catch issues — environment tiers + PR-based promotion.                                          | G10                   |
| Skyscanner | Span-to-metrics connector turns existing spans into low-cardinality platform metrics.                            | Candidate for v2 (RAG / KG / Plan step spans → harness metrics) |
| Skyscanner | SDK View config to drop redundant SDK metrics where Collector-derived metrics already exist.                     | G5                    |
| Skyscanner | Stay current with OTel releases or you batch breaking changes.                                                   | G9                    |

---

## Relationships to other blueprints

Following the OTel pattern of explicit overlap / extends / relates-to:

- **Extends**: A future "Microsoft Agent Framework GenAI Observability"
  blueprint, when published, will define the canonical `gen_ai.*` mapping
  for MAF. This blueprint defers to it.
- **Overlaps with**: A "Centralized Telemetry Platform" blueprint (in
  progress at OTel) — both define the OTLP-out, Collector-routes pattern.
  When the official one publishes, we align and shrink this section.
- **Relates to**: The harness's `documentation/design/plugin-system-design.md`
  — plugin boundary governance (AllowedTools / DeniedTools) is the security
  control; this blueprint is the *visibility* control. Both must agree on
  the `harness.plugin.*` and `harness.tool.*` attribute schemas.
- **Out of scope, handled by**: `documentation/architecture/05-observability.html`
  for the Azure topology and dashboard layout; this blueprint handles
  conventions and emission discipline.

---

## Open questions / v2 backlog

- Adopt Weaver for SemConv generation once .NET tooling stabilizes (G9).
- Span-to-metrics connector for plan-step spans → plan-throughput metric.
- Profiles GA pipeline once OTel Profiles ships stable in .NET.
- Dashboard SPA browser telemetry → separate blueprint.
- Migrate emitters away from the duplicated `agent.conversation.id` /
  `gen_ai.conversation.id` parallel emission once dashboards consume the
  `gen_ai.*` form (G2). Co-located in the registry as
  `ConversationId` / `LegacyConversationId` to make the migration mechanical.
- Wire `GenAiSemconvRegistry.System` / `SystemIntended` through the Polly
  provider-fallback chain so spans always attribute the effective and
  intended provider (G2 challenge #5).
- **Roslyn analyzer** that fails on any `"gen_ai."` or `"harness."` string
  literal outside the conventions namespace, and on any
  `new Meter(…)` / `new ActivitySource(…)` outside `AppSourceNames.cs`.
  Replaces the prose enforcement in "Things that must fail review" with
  build-time enforcement.
- **Reflection-based registry membership test** that walks all
  `Conventions/*.cs` constants once and asserts the registry's re-exports
  cover every `gen_ai.*` and `harness.*` key declared anywhere in the
  Conventions namespace.
- **Cardinality budget field** on `InstrumentDefinition` (G5) plus a
  metric-pipeline test that records cardinality during integration runs
  and fails when an instrument exceeds its declared budget.
- **Migrate Azure Monitor in-process exporter to the Collector layer** —
  currently the one tolerated in-process vendor exporter (G7). Removes the
  last app-level vendor wiring and brings the harness fully under the
  OTLP-only egress rule.
- **Declare `gen_ai.harness.plan.step.*` namespace** before the first
  plan-step span is emitted (G2). Either extend `ToolConventions` or add a
  `PlanConventions` file.
- **Span-attribute size assertion** in the Tool Output Compression
  integration tests — fail if any attribute exceeds
  `AppConfig.Telemetry.SpanAttributeMaxBytes` (G5).
- Submit this blueprint + a redacted reference implementation back to the
  OTel End-User SIG once v2 lands. Process: open `sig-end-user` issue,
  collaborate to scope, craft, review, publish.

---

## Change log

- 2026-06-08 — v1.3. PR-11 content capture. Added the "Content Capture Policy
  (PR-11)" subsection under G8: `OTEL_SEMCONV_STABILITY_OPT_IN` requirement,
  the `AppConfig.AI.Telemetry.ContentCapture` schema (master `Enabled` +
  per-attribute toggles + `RedactionCategories`), the eight redaction
  categories, the OFF-by-default safety stance, and an appsettings.json
  example. Backed by `ContentCaptureConfigValidator`,
  `DefaultContentRedactionFilter`, `ContentCapturePolicy`, and
  `ContentCaptureStartupValidator`.
- 2026-06-03 — v1 draft. Initial blueprint, lessons-learned table from
  Adobe / Mastodon / Skyscanner reference implementations, mapping to
  existing harness code.
- 2026-06-03 — v1.1. Audit-driven corrections after reading the actual
  telemetry surface:
  - G3 split now matches reality: SC extension owns resource builder +
    sampler + OTLP; configurator (Order 300) owns processor chain +
    Azure Monitor.
  - G2 acknowledges the harness already adopts many `gen_ai.*` keys;
    introduced `GenAiSemconvRegistry.cs` as the single re-export +
    gap-fill point instead of a parallel mapping file.
  - Added v2 backlog items for `gen_ai.system` fallback wiring and a
    string-literal analyzer.
- 2026-06-03 — v1.2. Code-review-driven corrections (10 findings):
  - G7 rewritten to acknowledge the in-process Azure Monitor exporter
    instead of falsely claiming Collector-only egress.
  - G5 + G1 acknowledge the cardinality-budget and emit-site-scan
    guardrails do not exist today; rephrased as PR-review discipline
    with mechanical enforcement in v2 backlog.
  - G6 references the canonical `error.type` SemConv attribute (now
    declared as `GenAiSemconvRegistry.ErrorType`) instead of the
    non-existent `gen_ai.error.type`.
  - G3 split clarified: `AddAspNetCoreInstrumentation`, Kestrel meters,
    and the Prometheus exporter live on the web branch only; the desktop
    branch (ConsoleUI) does not have them.
  - G2 plan-step row no longer claims `harness.plan.step.*` is an
    existing namespace.
  - "Things that must fail review" now distinguishes mechanical vs.
    reviewer enforcement per rule, and broadens the vendor-exporter
    scope from `Presentation.*` to "anywhere outside
    `Infrastructure.Observability/Exporters/`".
  - Registry namespace consolidation: `gen_ai.tool.call.*` and the
    `gen_ai.operation.*` enum now have a single home in `ToolConventions`
    and are re-exported from `GenAiSemconvRegistry`. `ConversationId`
    co-located with `LegacyConversationId` to enforce parallel-emit
    pair-handling.
