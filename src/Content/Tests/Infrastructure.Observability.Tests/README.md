# Infrastructure.Observability.Tests

Unit and integration tests for the **Infrastructure.Observability** layer — OpenTelemetry span processors, PostgreSQL-backed telemetry persistence, and Grafana dashboard query validation.

## Framework

- **xUnit** — test framework
- **Moq** — mocking
- **FluentAssertions** — assertion library
- **Npgsql** — PostgreSQL integration testing
- **OpenTelemetry** — span processor testing
- **coverlet** — code coverage

## Key Test Classes

| Test Class | What It Tests |
|------------|---------------|
| `ToolEffectivenessProcessorTests` | Tool success/failure effectiveness tracking |
| `PiiFilteringProcessorTests` | PII redaction from telemetry spans |
| `TailBasedSamplingProcessorTests` | Error-biased tail sampling decisions |
| `EndToEndPipelineTests` | Full write-through to PostgreSQL read-back |
| `OverviewDashboardTests` | Grafana dashboard SQL query correctness |

## Running Tests

```bash
dotnet test src/Content/Tests/Infrastructure.Observability.Tests
```

## Coverage

```bash
dotnet test src/Content/Tests/Infrastructure.Observability.Tests --collect:"XPlat Code Coverage"
```

## Note

Integration tests require a PostgreSQL instance. They use `ICollectionFixture<PostgresFixture>` to manage the connection lifecycle.
