# Swapping RAG Providers

Every RAG component is behind an interface and registered via keyed DI. You can swap implementations through configuration alone — no code changes required for pre-built providers, and a single class + DI registration for custom ones.

## Vector Store

**Interface:** `IVectorStore`
**Config key:** `AppConfig.AI.Rag.VectorStore.Provider`

### Built-in providers

| Provider | Config value | NuGet | Use case |
|----------|-------------|-------|----------|
| Azure AI Search | `AzureAISearch` | `Azure.Search.Documents` | Production — managed, scalable |
| FAISS | `Faiss` | (in-memory) | Local dev — zero infrastructure |

### Switching

```json
{
  "AppConfig": {
    "AI": {
      "Rag": {
        "VectorStore": {
          "Provider": "AzureAISearch",
          "Endpoint": "https://my-search.search.windows.net",
          "ApiKey": "",
          "IndexName": "documents",
          "EmbeddingModel": "text-embedding-3-large",
          "EmbeddingDimensions": 3072
        }
      }
    }
  }
}
```

For local development, use FAISS (the default):

```json
{
  "VectorStore": {
    "Provider": "Faiss",
    "IndexName": "local-index",
    "EmbeddingModel": "text-embedding-3-large",
    "EmbeddingDimensions": 3072
  }
}
```

**ApiKey:** Never put API keys in `appsettings.json`. Use `dotnet user-secrets` for dev, Azure Key Vault for production.

### Adding a custom provider

1. Implement `IVectorStore` in `Infrastructure.AI.RAG/Retrieval/`:

```csharp
public class PineconeVectorStore : IVectorStore
{
    public async Task UpsertAsync(IReadOnlyList<DocumentChunk> chunks, CancellationToken ct) { ... }
    public async Task<IReadOnlyList<RetrievalResult>> SearchAsync(
        IReadOnlyList<float> queryEmbedding, int topK, CancellationToken ct) { ... }
    public async Task DeleteAsync(string documentId, CancellationToken ct) { ... }
}
```

2. Add a new `VectorStoreProvider` enum value in `Domain.AI/RAG/Enums/VectorStoreProvider.cs`
3. Register in `DependencyInjection.cs`:

```csharp
services.AddKeyedSingleton<IVectorStore>("pinecone", (sp, _) =>
    new PineconeVectorStore(sp.GetRequiredService<IOptionsMonitor<AppConfig>>()));
```

4. Set `"Provider": "Pinecone"` in config.

## BM25 Store

**Interface:** `IBm25Store`

| Provider | Class | Use case |
|----------|-------|----------|
| Azure AI Search | `AzureAISearchBm25Store` | Production — uses Search full-text |
| SQLite FTS5 | `SqliteFts5Store` | Local dev — file-based, zero setup |

The `VectorStoreFactory` resolves both `IVectorStore` and `IBm25Store` based on the same `Provider` config value.

## Reranker

**Interface:** `IReranker`
**Config key:** `AppConfig.AI.Rag.Reranker.Strategy`

| Strategy | Config value | Description |
|----------|-------------|-------------|
| Azure Semantic | `azure_semantic` | Uses Azure AI Search semantic ranker |
| Cross-Encoder | `cross_encoder` | HTTP call to hosted BGE-reranker |
| None | `none` | Identity pass-through (skip reranking) |

```json
{
  "Reranker": {
    "Strategy": "cross_encoder",
    "ModelName": "BAAI/bge-reranker-v2-m3"
  }
}
```

## Chunking Strategy

**Interface:** `IChunkingService`
**Config key:** `AppConfig.AI.Rag.Ingestion.DefaultStrategy`

| Strategy | Config value | Description |
|----------|-------------|-------------|
| Structure-Aware | `StructureAware` | Splits by document structure (headings), preserves breadcrumbs |
| Semantic | `Semantic` | Embedding-similarity based splits |
| Fixed Size | `FixedSize` | Token-count windows with overlap |

Override per-document via `IngestDocumentCommand.OverrideStrategy`.

## Embedding Model

**Interface:** `IEmbeddingGenerator<string, Embedding<float>>` (from `Microsoft.Extensions.AI`)

The embedding model is configured via `VectorStore.EmbeddingModel` and `VectorStore.EmbeddingDimensions`. The `EmbeddingService` wraps any `IEmbeddingGenerator` with batching, retry, and OTel spans.

To use a different embedder (e.g., a local ONNX model), register your own `IEmbeddingGenerator` implementation before `AddRagDependencies()` — the RAG DI will detect the existing registration and skip the default.

## GraphRAG Provider

**Interface:** `IGraphRagService`
**Config key:** `AppConfig.AI.Rag.GraphRag.GraphProvider`

| Provider | Config value | Description |
|----------|-------------|-------------|
| ManagedCode.GraphRag | `managed_code` | C# port of Microsoft GraphRAG |
| Custom | `custom` | Your own implementation |

See [graphrag-setup.md](graphrag-setup.md) for detailed setup instructions.
