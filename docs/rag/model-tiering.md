# Model Tiering for RAG Cost Optimization

## Overview

The RAG pipeline includes several LLM-intensive operations that can be expensive at scale: contextual enrichment (per-chunk), RAPTOR summarization (recursive), CRAG evaluation (per-query), query classification, and RAG-Fusion variant generation. Not all of these need the most capable model.

The `IRagModelRouter` pattern, adapted from [claude-model-switcher](https://github.com/MCKRUZ/claude-model-switcher), routes each operation to a configured model tier — so bulk operations use cheaper models while quality-critical operations use premium ones.

## How It Works

```
Operation Name         OperationOverrides          Tier          DeploymentName
─────────────────── ─► ──────────────────── ─► ─────────── ─► ───────────────
"raptor_summarization"  → "economy"             economy        "gpt-4o-mini"
"contextual_enrichment" → "economy"             economy        "gpt-4o-mini"
"crag_evaluation"       → "standard"            standard       "gpt-4o"
"query_classification"  → "economy"             economy        "gpt-4o-mini"
"rag_fusion"            → "economy"             economy        "gpt-4o-mini"
(unknown operation)     → DefaultTier           standard       "gpt-4o"
```

1. The `RagModelRouter` receives an operation name (e.g., `"raptor_summarization"`)
2. Looks up the tier in `ModelTieringConfig.OperationOverrides`
3. Falls back to `DefaultTier` if no override exists
4. Resolves tier name to `ModelTierDefinition` (deployment name + rate limit)
5. Creates an `IChatClient` via the existing `IChatClientFactory`
6. Records the tier selection to OpenTelemetry span tags (`rag.model.tier`, `rag.model.deployment`)

## Configuration

```json
{
  "AppConfig": {
    "AI": {
      "Rag": {
        "ModelTiering": {
          "Enabled": true,
          "DefaultTier": "standard",
          "OperationOverrides": {
            "contextual_enrichment": "economy",
            "raptor_summarization": "economy",
            "crag_evaluation": "standard",
            "query_classification": "economy",
            "rag_fusion": "economy"
          },
          "Tiers": [
            {
              "Name": "premium",
              "DeploymentName": "gpt-4o",
              "MaxTokensPerMinute": 30000
            },
            {
              "Name": "standard",
              "DeploymentName": "gpt-4o",
              "MaxTokensPerMinute": 60000
            },
            {
              "Name": "economy",
              "DeploymentName": "gpt-4o-mini",
              "MaxTokensPerMinute": 200000
            }
          ]
        }
      }
    }
  }
}
```

## Tier Definitions

| Tier | Typical Model | Use Case | Cost Impact |
|------|--------------|----------|-------------|
| **Premium** | `gpt-4o` | Final answer generation, complex reasoning | Highest quality, highest cost |
| **Standard** | `gpt-4o` | CRAG evaluation, reranking decisions | Balanced |
| **Economy** | `gpt-4o-mini` | Bulk enrichment, RAPTOR summaries, classification | ~10x cheaper than premium |

## Cost Savings Example

For a corpus of 10,000 document chunks:

| Operation | Without Tiering | With Tiering | Savings |
|-----------|----------------|--------------|---------|
| Contextual enrichment (10K calls) | $15.00 (gpt-4o) | $1.50 (gpt-4o-mini) | 90% |
| RAPTOR summaries (500 clusters) | $2.50 (gpt-4o) | $0.25 (gpt-4o-mini) | 90% |
| Query classification (per query) | $0.003 (gpt-4o) | $0.0003 (gpt-4o-mini) | 90% |
| CRAG evaluation (per query) | $0.005 (gpt-4o) | $0.005 (gpt-4o) | 0% (quality-critical) |

Total ingestion savings: ~90% on bulk operations, with no quality loss on retrieval-time operations.

## Disabling Model Tiering

Set `"Enabled": false` — all operations will use `IChatClientFactory` with the default deployment from `AgentFramework.DefaultDeployment`.

## Adding Custom Tiers

Add a new entry to the `Tiers` array and reference it in `OperationOverrides`:

```json
{
  "Tiers": [
    { "Name": "local", "DeploymentName": "phi-3-mini", "MaxTokensPerMinute": 1000000 }
  ],
  "OperationOverrides": {
    "raptor_summarization": "local"
  }
}
```

## Telemetry

Every LLM call routed through `IRagModelRouter` emits:

- `rag.model.tier` — the tier name selected
- `rag.model.deployment` — the actual model deployment used
- `rag.model.operation` — the operation name that triggered the routing

These tags feed into the existing `LlmUsageMetrics` cost tracking, enabling per-tier cost dashboards in Grafana.
