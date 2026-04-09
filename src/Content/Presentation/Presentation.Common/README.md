# Presentation.Common

Every application needs a place where all the layers come together — where Domain interfaces meet Infrastructure implementations, where configuration sources are loaded, and where the middleware pipeline is assembled in the correct order. Presentation.Common is that place.

This is the composition root of the Agentic Harness. It doesn't contain business logic. It doesn't implement domain interfaces. What it does is wire *everything else* together: loading configuration from files, secrets, and Azure; registering services from every layer; configuring the OpenTelemetry pipeline; setting up health checks; and orchestrating the middleware stack.

---

## Configuration Loading

`AppConfigHelper` loads configuration from multiple sources in priority order, with each source overriding the previous:

1. `appsettings.json` — Base configuration
2. `appsettings.{Environment}.json` — Environment-specific overrides
3. **User Secrets** — Developer credentials (never checked in)
4. **Environment Variables** — Container/CI overrides
5. **Azure Key Vault** — Production secrets (non-DEBUG only)
6. **Azure App Configuration** — Centralized config management (non-DEBUG only)

The last two sources are conditionally loaded — in development, secrets come from User Secrets. In production, they come from Key Vault. The application code doesn't know or care which source provided a value.

## Service Registration

`IServiceCollectionExtensions.GetServices()` is the single entry point called from `Program.cs`. It orchestrates registration across every layer:

```
GetServices()
  → Bind AppConfig sections (AI, Azure, Cache, Http, Observability, ...)
  → Register cross-cutting services (options, caching, health checks)
  → AddApplicationCommonDependencies()        Pipeline behaviors, logging, factories
  → AddApplicationAICommonDependencies()      Agent factories, tool conversion, context budget
  → AddApplicationCoreDependencies()          CQRS handlers, validators
  → AddInfrastructureCommonDependencies()     Identity, middleware
  → AddInfrastructureAIDependencies()         Permission resolver, compaction, hooks, prompts
  → AddInfrastructureAIConnectorsDependencies() GitHub, Jira, ADO, Slack connectors
  → AddInfrastructureAIMcpDependencies()      MCP client connections
  → AddInfrastructureApiAccessDependencies()  HTTP pipeline, auth handlers
  → AddInfrastructureObservabilityDependencies() OTel processors, exporters
  → Configure OpenTelemetry pipeline
```

Each layer provides its own `DependencyInjection.cs` with an `Add*Dependencies()` extension method. Presentation.Common calls them all in the correct order.

## OpenTelemetry Bootstrap

`OpenTelemetryServiceCollectionExtensions` configures the tracing and metrics pipelines. It supports both ASP.NET Core web apps and desktop/console applications:

- Discovers `ITelemetryConfigurator` implementations from DI (ordered by priority: Core → Harness → Domain → Finalization)
- Registers Semantic Kernel and Azure SDK instrumentation
- Sets resource attributes (service name, version, environment)
- Configures Prometheus metrics export endpoint
- Uses `IDeferredTracerProviderBuilder` to avoid the `BuildServiceProvider` anti-pattern

## Middleware Orchestration

`IApplicationBuilderExtensions` wires middleware in the correct order — and the order matters:

1. **DynamicCorsMiddleware** — CORS evaluation first (before auth, before anything)
2. **SecurityAuditMiddleware** — Log every request for compliance
3. **SecurityHeadersMiddleware** — Defense-in-depth headers on every response
4. **GlobalExceptionMiddleware** — Exception → HTTP status mapping (outermost catch)

The global error handler maps domain exceptions to structured JSON responses: error message, status code, error details list, and timestamp. Stack traces are included in Development, hidden in Production.

## Health Checks

`HealthCheckServiceCollectionExtensions` registers conditional health probes:

- **SQL Server** — Only if a connection string is configured
- **Azure Blob Storage** — Only if a storage account is configured
- **Azure Key Vault** — Only if Key Vault URI is configured
- **Redis** — Only if a Redis connection is configured
- **Application Insights** — Only if an instrumentation key is configured

Optional HealthChecks UI dashboard available at a configurable endpoint.

---

## Project Structure

```
Presentation.Common/
├── Extensions/
│   ├── IServiceCollectionExtensions.cs               Master DI orchestrator
│   ├── HealthCheckServiceCollectionExtensions.cs     Conditional health probes
│   ├── IApplicationBuilderExtensions.cs              Middleware pipeline + error handler
│   ├── IEndpointRouteBuilderExtensions.cs            Health check UI endpoint
│   └── OpenTelemetryServiceCollectionExtensions.cs   Tracing + metrics bootstrap
├── Filters/
│   └── ExceptionContextExtensions.cs                 Standardized error response shape
├── Helpers/
│   ├── AppConfigHelper.cs                            Multi-source config loading
│   └── ConfigurationHelper.cs                        Kestrel URL resolution, config sync
└── DependencyInjection.cs                            Facade for APIAccess extensions
```

## Dependencies

This project references every layer — it's the composition root:

- **Application**: Application.Common, Application.AI.Common, Application.Core
- **Infrastructure**: Infrastructure.Common, Infrastructure.AI, Infrastructure.AI.Connectors, Infrastructure.AI.MCP, Infrastructure.APIAccess, Infrastructure.Observability
- **NuGet**: OpenTelemetry, AspNetCore.HealthChecks, Azure.Identity, Azure.Security.KeyVault, Azure.Extensions.AspNetCore.Configuration, Microsoft.Identity.Web, Redis
