# Presentation.Common

The composition root of the Agentic Harness. This project does not contain business logic or implement domain interfaces. What it does is wire everything together: loading configuration from files, secrets, and Azure; registering services from every layer in the correct order; bootstrapping the OpenTelemetry pipeline; configuring health checks; and orchestrating the middleware stack.

Every Presentation host (`AgentHub`, `ConsoleUI`) calls `services.GetServices()` as its single DI entry point. That one call registers Domain models, Application handlers, Infrastructure implementations, and all cross-cutting concerns. The host then adds only its own specific services (SignalR, AG-UI, Spectre.Console menu, etc.).

## Architecture Context

```
┌─────────────────────────────────────────────────────────────────────┐
│  Presentation Host (AgentHub Program.cs or ConsoleUI Program.cs)    │
│                                                                     │
│  builder.Services.GetServices();     ← ONE CALL DOES EVERYTHING    │
│  builder.Services.AddAgentHubServices(...);  ← Host-specific only  │
└─────────────────────────────────────────────────────────────────────┘
                                │
                                ▼
┌─────────────────────────────────────────────────────────────────────┐
│  Presentation.Common.Extensions.IServiceCollectionExtensions        │
│                                                                     │
│  GetServices()                                                      │
│    ├── AppConfigHelper.LoadAppConfig()      Load multi-source config│
│    ├── RegisterConfigSections()             Bind IOptionsMonitor<T> │
│    └── BuildGlobalSolutionServices()                                │
│          ├── AddOptions / ProblemDetails / HttpContextAccessor       │
│          ├── AddCacheConfiguration()                                │
│          ├── AddCustomHealthChecks()                                │
│          ├── AddGlobalProjectDependencies()  ← ALL LAYERS          │
│          │     ├── Application.Common (MediatR, validators)         │
│          │     ├── Application.AI.Common (agent factories)          │
│          │     ├── Application.Core (CQRS handlers)                 │
│          │     ├── Infrastructure.Common (identity, middleware)      │
│          │     ├── Infrastructure.AI.RAG (retrieval pipeline)       │
│          │     ├── Infrastructure.AI (tools, state, agents)         │
│          │     ├── Infrastructure.AI.Governance (safety policies)    │
│          │     ├── Infrastructure.AI.Connectors (external APIs)     │
│          │     ├── Infrastructure.AI.MCP (MCP client)               │
│          │     ├── Infrastructure.APIAccess (HTTP pipeline, auth)   │
│          │     └── Infrastructure.Observability (OTel processors)   │
│          └── AddOpenTelemetry()  ← MUST BE LAST                    │
│                (discovers ITelemetryConfigurator from DI)            │
└─────────────────────────────────────────────────────────────────────┘
```

## Key Concepts

### Configuration Loading (Multi-Source)

`AppConfigHelper.LoadAppConfig()` assembles configuration from multiple sources in priority order (later sources override earlier):

1. `appsettings.json` -- base configuration
2. `appsettings.{DOTNET_ENVIRONMENT}.json` -- environment overrides
3. **User Secrets** -- developer credentials (never committed)
4. **Environment Variables** -- container/CI overrides
5. **Azure Key Vault** -- production secrets (non-DEBUG builds only)
6. **Azure App Configuration** -- centralized config management (non-DEBUG only)

```csharp
// In your Program.cs -- this is all you need:
builder.Services.GetServices();
// All config sources are loaded, all sections bound, all services registered.
```

### Service Registration Order

Registration order matters because OpenTelemetry discovers `ITelemetryConfigurator` implementations from DI. If OTel is registered before Infrastructure.Observability, the tail-based sampling processor won't be found.

The order is:
1. **Application layers first** -- MediatR, validators, pipeline behaviors, agent factories
2. **Infrastructure layers** -- implementations of Application interfaces
3. **OpenTelemetry last** -- discovers all registered `ITelemetryConfigurator` implementations

### OpenTelemetry Bootstrap

`OpenTelemetryServiceCollectionExtensions` supports both web and console hosts:

**Web hosts** (AgentHub): uses `AddOpenTelemetry().WithTracing().WithMetrics()` with ASP.NET Core instrumentation, HTTP client instrumentation, and `IDeferredTracerProviderBuilder` to avoid the `BuildServiceProvider()` anti-pattern.

**Console hosts** (ConsoleUI): creates standalone `TracerProvider` and `MeterProvider` singletons with the same instrumentation but without ASP.NET Core specifics.

```csharp
// Auto-detection via assembly name:
var isWebProject = appConfig.Observability.WebTelemetryProjects
    .Contains(entryAssemblyName); // ["Presentation.AgentHub"]
```

Both modes:
- Enable Semantic Kernel and Azure SDK telemetry via AppContext switches
- Register shared `ResourceBuilder` (service name, version, instance)
- Register the harness's `AppInstrument` source and meter
- Discover and apply all `ITelemetryConfigurator` implementations ordered by priority
- Configure Prometheus export and OTLP export (when enabled)

### Caching Strategy

`AddCacheConfiguration()` selects the caching backend based on `CacheConfig.CacheType`:

| CacheType | Behavior |
|-----------|----------|
| `None` / `Memory` | In-process memory cache + distributed memory cache |
| `DistributedMemory` | Distributed memory cache only |
| `RedisCache` | Redis via StackExchange.Redis (endpoint, password, service name) |

### Health Checks

Conditional probes registered only when the corresponding service is configured:

- SQL Server (connection string present)
- Azure Blob Storage (account configured)
- Azure Key Vault (URI configured)
- Redis (connection configured)
- Application Insights (instrumentation key present)
- Application Status (always registered)

Optional HealthChecks UI dashboard with in-memory storage.

### Middleware Pipeline

`IApplicationBuilderExtensions` wires middleware in correct order:

```csharp
app.UseMiddleware<SecurityAuditMiddleware>();    // Log every request
app.UseMiddleware<SecurityHeadersMiddleware>(); // Defense-in-depth headers
app.UseMiddleware<DynamicCorsMiddleware>();     // Runtime CORS evaluation
app.UseMiddleware<GlobalExceptionMiddleware>(); // Exception → HTTP mapping
```

### Authentication

`AddAuthDependencies()` supports two modes:
- **Azure AD B2C configured**: JWT Bearer with strict validation (zero clock skew, issuer/audience/signing key)
- **No Azure AD config**: Basic authentication + authorization scaffolding for local development

## Project Structure

```
Presentation.Common/
├── Extensions/
│   ├── IServiceCollectionExtensions.cs               Master DI orchestrator
│   ├── HealthCheckServiceCollectionExtensions.cs     Conditional health probes
│   ├── IApplicationBuilderExtensions.cs              Middleware pipeline assembly
│   ├── IEndpointRouteBuilderExtensions.cs            Health check + Swagger endpoints
│   └── OpenTelemetryServiceCollectionExtensions.cs   Tracing + metrics bootstrap
├── Filters/
│   └── ExceptionContextExtensions.cs                 Standardized error response shape
├── Helpers/
│   ├── AppConfigHelper.cs                            Multi-source configuration loading
│   └── ConfigurationHelper.cs                        Kestrel URL resolution
├── Security/
│   └── SystemUser.cs                                 IUser implementation for console/workers
└── DependencyInjection.cs                            Facade: AddPresentationCommonDependencies()
```

## Key Types Reference

| Type | Purpose | Called By |
|------|---------|-----------|
| `IServiceCollectionExtensions.GetServices()` | Single-call DI registration for all layers | `Program.cs` in every host |
| `AppConfigHelper.LoadAppConfig()` | Multi-source config assembly | `GetServices()` |
| `OpenTelemetryServiceCollectionExtensions` | OTel pipeline for web and desktop | `BuildGlobalSolutionServices()` |
| `IApplicationBuilderExtensions` | Middleware pipeline wiring | Host `Program.cs` (web only) |
| `SystemUser` | `IUser` for non-HTTP contexts | Console/worker DI |
| `DependencyInjection` | Facade over APIAccess extensions | `GetServices()` |

## Configuration

This project loads ALL configuration. The key sections it binds:

```json
{
  "AppConfig": {
    "Common": { "ApplicationName": "AgenticHarness", "ApplicationVersion": "1.0.0" },
    "Logging": { "PipeName": "AgenticHarnessLogs.AgentHub", "LogsBasePath": "logs" },
    "AI": { "AgentFramework": { "DefaultDeployment": "gpt-4o" } },
    "Http": { "CorsAllowedOrigins": "..." },
    "Infrastructure": { "FileSystem": { "AllowedBasePaths": ["..."] } },
    "Connectors": {},
    "Observability": { "Exporters": { "Otlp": { "Enabled": true } } },
    "Azure": { "ADB2C": { "Instance": "", "Domain": "" } },
    "Cache": { "CacheType": "Memory" }
  }
}
```

## How to Run

This is a class library -- it runs through Presentation hosts:

```bash
# Via AgentHub (web)
dotnet run --project src/Content/Presentation/Presentation.AgentHub

# Via ConsoleUI (terminal)
dotnet run --project src/Content/Presentation/Presentation.ConsoleUI

# Build only
dotnet build src/Content/Presentation/Presentation.Common
```

## Common Tasks

### Adding a New Infrastructure Layer

1. Create the project with its own `DependencyInjection.cs`
2. Add a project reference in `Presentation.Common.csproj`
3. Add the `using` in `IServiceCollectionExtensions.cs`
4. Call `services.AddMyNewDependencies()` in `AddGlobalProjectDependencies()` at the correct position

### Adding a New Configuration Section

1. Create the config POCO in `Domain.Common.Config/`
2. Add the property to `AppConfig`
3. Bind it in `RegisterConfigSections()`:
   ```csharp
   services.Configure<MyConfig>(configuration.GetSection("AppConfig:MySection"));
   ```

### Adding a New Health Check

1. Add the NuGet package to this project
2. Add a conditional registration in `AddCustomHealthChecks()`:
   ```csharp
   if (!string.IsNullOrEmpty(config.MyService.ConnectionString))
       builder.AddMyServiceCheck("myservice", config.MyService.ConnectionString);
   ```

## Dependencies

**Project References (ALL layers -- this is the composition root):**
- `Application.Common`, `Application.AI.Common`, `Application.Core`
- `Infrastructure.Common`, `Infrastructure.AI`, `Infrastructure.AI.Governance`
- `Infrastructure.AI.RAG`, `Infrastructure.AI.Connectors`, `Infrastructure.AI.MCP`
- `Infrastructure.APIAccess`, `Infrastructure.Observability`

**NuGet Packages:**
- `OpenTelemetry.Extensions.Hosting` / `.Instrumentation.AspNetCore` / `.Http` / `.Runtime` -- OTel pipeline
- `OpenTelemetry.Exporter.OpenTelemetryProtocol` / `.Prometheus.AspNetCore` -- export
- `AspNetCore.HealthChecks.*` -- SQL, Blob, KeyVault, Redis, AppInsights, UI
- `Microsoft.Extensions.Caching.StackExchangeRedis` -- Redis caching
- `Microsoft.Identity.Web` -- Azure AD authentication
- `Azure.Identity` / `Azure.Extensions.AspNetCore.Configuration.Secrets` -- Key Vault config
- `Microsoft.Extensions.Configuration.AzureAppConfiguration` -- Azure App Config

## Testing

**Test project:** `Tests/Presentation.Common.Tests`

```bash
dotnet test src/AgenticHarness.slnx --filter "FullyQualifiedName~Presentation.Common"
```

**Coverage areas:**
- Configuration loading from multiple sources
- Service registration completeness (all layers registered)
- OpenTelemetry pipeline assembly (web vs desktop mode)
- Health check conditional registration
- Middleware pipeline ordering
- Cache strategy selection
