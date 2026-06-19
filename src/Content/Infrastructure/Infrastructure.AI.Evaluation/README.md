# Evaluation — LLM Judge & Judge Panel (Jury)

Judge-backed metrics (`llm_judge` and the RAG metric pack) score each case through
`ILlmJudge`. By default that is a **single judge** (`DefaultLlmJudge`) running the
configured model once.

## Judge Panel ("jury")

`ILlmJudge` resolves to `JuryLlmJudge`, which runs a **panel** of judges and reduces them
to a robust aggregate (median by default) plus a consensus signal. A single judge is
noisy — it has blind spots and the occasional confident-but-wrong call; a panel's median
absorbs the outlier, and the spread tells a reviewer where the judges disagreed.

**Off by default.** With no panelists configured, `JuryLlmJudge` delegates straight to the
single judge — byte-identical behavior, no extra model calls. A panel activates only when
you configure one.

### Enabling a panel

Configure `JuryOptions` after `AddEvaluationDependencies(...)` (same pattern as
`JudgeOptions`):

```csharp
services.Configure<JuryOptions>(o =>
{
    // Multi-persona — one model, different "lenses" (works with a single provider):
    o.Panelists.Add(new JuryPanelistSpec { Name = "accuracy",  PersonaPrompt = "Focus only on factual accuracy." });
    o.Panelists.Add(new JuryPanelistSpec { Name = "relevance", PersonaPrompt = "Focus only on whether the answer addresses the question." });
    o.Panelists.Add(new JuryPanelistSpec { Name = "safety",    PersonaPrompt = "Focus only on unsafe or non-compliant content." });

    // Multi-model — different models (needs ≥2 providers configured):
    // o.Panelists.Add(new JuryPanelistSpec { Name = "gpt-4o",  ClientType = AIAgentFrameworkClientType.AzureOpenAI, Deployment = "gpt-4o" });
    // o.Panelists.Add(new JuryPanelistSpec { Name = "o-series", ClientType = AIAgentFrameworkClientType.OpenAI,     Deployment = "o4-mini" });

    o.ScoreAggregation   = JuryScoreAggregation.Median; // Median (default) | Mean | Min
    o.ConsensusMaxSpread = 0.2;  // spread ≤ this ⇒ Consensus
    o.ConflictMinSpread  = 0.5;  // spread ≥ this ⇒ Conflict; between ⇒ Split
});
```

A panelist with no `ClientType`/`Deployment` uses the configured `JudgeOptions` model
(optionally wearing its `PersonaPrompt`); set both to point it at a different model.

### Cost & determinism

- A panel of N runs **N judge calls per case** (in parallel, so latency stays ~1×, but
  spend is N×). Recommended on the scheduled eval suite, not per-PR.
- A fixed panel of distinct models is reproducible across runs (the judge still bypasses
  `IModelRouter`). A panelist whose call fails or returns malformed JSON is excluded from
  the aggregate; if every panelist fails, the single-judge soft-fail contract is preserved.

### Where it shows up

The aggregate score thresholds as usual. The consensus signal surfaces as the
`Consensus` / `Spread` fields on `MetricScore` (forensic per-panelist detail in
`RawOutput`) and renders as a color-coded **Consensus** column on the dashboard's eval
run-detail page: green = agreement, amber = split, red = conflict.
