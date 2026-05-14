# Infrastructure.AI.RAG.Tests

Unit tests for the **Infrastructure.AI.RAG** layer — RAG orchestration, hybrid retrieval, CRAG evaluation, context assembly, and citation tracking.

## Framework

- **xUnit** — test framework
- **Moq** — mocking
- **FluentAssertions** — assertion library
- **coverlet** — code coverage

## Key Test Classes

| Test Class | What It Tests |
|------------|---------------|
| `RagOrchestratorTests` | End-to-end RAG pipeline orchestration |
| `HybridRetrieverTests` | Dense + sparse hybrid retrieval with RRF |
| `CragEvaluatorTests` | CRAG quality evaluation (accept/refine/reject) |
| `RagContextAssemblerTests` | Token-budgeted context assembly |
| `CitationTrackerTests` | Source citation tracking and attribution |

## Running Tests

```bash
dotnet test src/Content/Tests/Infrastructure.AI.RAG.Tests
```

## Coverage

```bash
dotnet test src/Content/Tests/Infrastructure.AI.RAG.Tests --collect:"XPlat Code Coverage"
```
