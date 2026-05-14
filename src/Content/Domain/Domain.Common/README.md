# Domain.Common

## What This Is

Domain.Common is the shared vocabulary of the entire Agentic Harness. Think of it as the dictionary that every layer of the application uses to communicate -- it defines how results flow back to callers, how configuration is structured, how workflows transition between states, and how inputs are validated at system boundaries. It solves the problem of coupling: without a shared set of types at the bottom of the stack, every layer would invent its own error handling, its own config shapes, and its own state models, leading to translation code everywhere.

This project sits at the absolute bottom of the dependency graph. Nothing in the solution depends on fewer things than Domain.Common. Every other project -- Domain.AI, all three Application layers, all Infrastructure projects, and all Presentation hosts -- depends on it either directly or transitively. It depends on nothing except two Microsoft.Extensions abstractions packages.

## Architecture Context

```
                    ┌─────────────────────────┐
                    │     Presentation         │  (ConsoleUI, AgentHub, WebUI)
                    └────────────┬────────────┘
                                 │
                    ┌────────────▼────────────┐
                    │     Infrastructure       │  (AI, Observability, Connectors)
                    └────────────┬────────────┘
                                 │
                    ┌────────────▼────────────┐
                    │      Application         │  (Common, AI.Common, Core)
                    └────────────┬────────────┘
                                 │
                    ┌────────────▼────────────┐
                    │       Domain.AI          │
                    └────────────┬────────────┘
                                 │
                ╔════════════════▼════════════════╗
                ║        Domain.Common           ║  ← YOU ARE HERE
                ╚════════════════════════════════╝
```

In Clean Architecture, the Domain layer contains entities, value objects, and enums with zero framework dependencies. Domain.Common takes this further: it contains no domain *behavior* at all -- just the foundational types that every other layer needs to speak the same language. The dependency rule means this project can never reference anything above it. It cannot know about MediatR, FluentValidation, Entity Framework, or any AI SDK. If you need to add something here, it must compile with nothing but `Microsoft.Extensions.Hosting.Abstractions` and `Microsoft.Extensions.Logging.Abstractions`.

## Key Concepts

### The Result Pattern

The `Result<T>` class is the standard return type for every CQRS handler in the harness. Instead of throwing exceptions for expected failures (validation errors, permission denials, content blocks), handlers return a `Result<T>` that callers inspect.

Without this, every caller would need try-catch blocks around every MediatR dispatch, exception types would proliferate across layers, and the pipeline behaviors couldn't intercept failures cleanly.

**Consumer usage:**

```csharp
// In a command handler:
public async Task<Result<Order>> Handle(CreateOrderCommand cmd, CancellationToken ct)
{
    if (!await _inventory.HasStock(cmd.ProductId))
        return Result<Order>.Fail("Product is out of stock.");

    var order = new Order(cmd.ProductId, cmd.Quantity);
    return Result<Order>.Success(order);
}

// In a caller:
var result = await mediator.Send(new CreateOrderCommand { ProductId = "X" });
if (!result.IsSuccess)
    logger.LogWarning("Failed: {Errors}", result.Errors);
else
    Process(result.Value!);
```

**Functional composition:**

```csharp
var final = await mediator.Send(getOrderQuery)
    .ThenMap(order => order with { Status = "confirmed" })
    .ThenBind(order => SaveOrderAsync(order));
```

The `ResultFailureType` enum maps failures to HTTP status codes and agent recovery strategies: `Validation` (400), `Unauthorized` (401), `Forbidden` (403), `NotFound` (404), `ContentBlocked`, `PermissionRequired`, `GovernanceBlocked`.

### The Configuration Hierarchy

`AppConfig` is the root strongly-typed configuration object. Everything in `appsettings.json` under the `"AppConfig"` section binds to this hierarchy via `IOptionsMonitor<AppConfig>`. This gives you runtime reload support, validation, and IntelliSense instead of magic strings.

```csharp
// Binding in DI (done by Presentation layer):
services.Configure<AppConfig>(configuration.GetSection("AppConfig"));

// Consuming in any service:
public class MyService(IOptionsMonitor<AppConfig> config)
{
    public void DoWork()
    {
        var deployment = config.CurrentValue.AI.AgentFramework.DefaultDeployment;
        var timeout = config.CurrentValue.Agent.DefaultRequestTimeoutSec;
    }
}
```

The hierarchy covers: AI (agents, MCP, RAG, permissions, hooks), Azure (Entra, Key Vault, AppInsights), Cache, Connectors (GitHub, Jira, Slack), HTTP (resilience, OpenAPI), Infrastructure (state, content), Observability (sampling, PII, exporters), and MetaHarness (optimization loop).

### Workflow State Machine

The `IStateManager` interface and its supporting types define a generic workflow engine. Agents can move through multi-step processes (research phases, validation gates, approval chains) with checkpointed state.

```csharp
// Transition a workflow node:
await stateManager.TransitionAsync("workflow-1", "analysis-node", "completed");

// Evaluate a decision gate:
var framework = skill.DecisionFramework;
if (framework.IsValidOutcome("go"))
    await stateManager.TransitionAsync(workflowId, nodeId, "go");
```

The `DecisionFramework` class encapsulates rule-based decisions parsed from SKILL.md files -- conditions map to outcomes (go/conditional_go/no_go) making validation gates fully configurable without code changes.

### Input Security

`SecureInputValidatorHelper` is a static class for validating untrusted inputs at system boundaries. It rejects shell injection (`; | $ > <`), path traversal (`..`), and enforces length limits.

```csharp
// Validate a tool name identifier:
if (!SecureInputValidatorHelper.ValidateIdentifier(toolName))
    return Result.Fail("Invalid tool name");

// Sanitize user-provided input:
var safe = SecureInputValidatorHelper.Sanitize(rawInput, maxLength: 512);
```

### Telemetry Instruments

`AppInstrument` provides the singleton `ActivitySource` and `Meter` for all custom OpenTelemetry instrumentation. Every layer that emits traces or metrics references these shared instruments.

```csharp
using var activity = AppInstrument.Source.StartActivity("MyOperation");
activity?.SetTag("operation.type", "ingestion");
```

## Project Structure

```
Domain.Common/
├── Config/
│   ├── AI/                      # Agent framework, A2A, MCP, permissions, hooks, RAG, orchestration
│   ├── Azure/                   # Entra ID, Key Vault, App Insights, databases, Graph API
│   ├── Cache/                   # Redis/memory backend selection
│   ├── Connectors/              # GitHub, Jira, Azure DevOps, Slack credentials
│   ├── Http/                    # Client configs, OpenAPI, resilience policies (retry, circuit breaker)
│   ├── Infrastructure/          # State management, content providers
│   ├── MetaHarness/             # Automated optimization loop config
│   ├── Observability/           # Exporters, sampling, PII filtering, rate limiting, LLM pricing
│   └── AppConfig.cs             # Root configuration object
├── Constants/                   # ClaimConstants, PolicyNameConstants (auth)
├── Enums/                       # AuthPermissions
├── Extensions/
│   ├── EnumExtensions.cs        # Display name from attributes
│   ├── ResultExtensions.cs      # Map, Bind, Ensure, OnSuccess, OnFailure, async variants
│   └── StringExtensions.cs      # Common string utilities
├── Helpers/
│   ├── JsonAlphabetizerHelper.cs      # Deterministic JSON key ordering
│   ├── ResultHelper.cs                # Reflection-based Result<T> factory for pipeline behaviors
│   └── SecureInputValidatorHelper.cs  # Input sanitization (shell injection, path traversal)
├── Logging/
│   ├── ExecutionScope.cs        # Structured context record (traceId, user, agent, step)
│   └── FileLoggerOptions.cs     # File logger configuration
├── MetaHarness/                 # Evaluation, regression, snapshot models (12 types)
├── Middleware/                  # GlobalErrorHandlerOptions
├── Models/
│   ├── Api/EndpointHealthResult.cs  # Health check response shape
│   ├── AuditEntry.cs            # Compliance audit trail record
│   ├── LogEntry.cs              # Structured log record
│   └── RunManifest.cs           # Execution metadata for a run
├── Telemetry/
│   ├── AppInstrument.cs         # Singleton ActivitySource + Meter
│   └── AppSourceNames.cs        # OTel source name constants
├── Workflow/
│   ├── DecisionFramework.cs     # Rule-based decision evaluation
│   ├── DecisionResult.cs        # Outcome of evaluation
│   ├── DecisionRule.cs          # Condition → outcome mapping
│   ├── IStateManager.cs         # Workflow persistence interface
│   ├── NodeState.cs             # Individual node status + metadata
│   ├── StateConfiguration.cs    # Allowed transitions graph
│   ├── WorkflowState.cs         # Complete workflow state graph
│   └── *Exception.cs            # 3 domain exceptions
└── Result.cs                    # Result<T> + ResultFailureType enum
```

## Key Types Reference

| Type | Purpose | Used By |
|------|---------|---------|
| **Error Handling** | | |
| `Result<T>` | Success/failure envelope for all handlers | Every CQRS handler, pipeline behaviors |
| `ResultFailureType` | Categorizes failure (Validation, Forbidden, etc.) | Global error handler, API controllers |
| `ResultExtensions` | Functional composition (Map, Bind, Ensure) | Handler implementations |
| **Configuration** | | |
| `AppConfig` | Root config object | All services via `IOptionsMonitor<AppConfig>` |
| `AIConfig` | AI subsystem config (MCP, RAG, agents, permissions) | AgentFactory, RAG orchestrator, MCP server |
| `ObservabilityConfig` | Sampling, PII, exporters | OTel pipeline setup |
| **Workflow** | | |
| `IStateManager` | Workflow persistence contract | RunOrchestratedTask handler |
| `DecisionFramework` | Configurable decision gates | Validation skills |
| `WorkflowState` | Complete workflow graph | State management implementations |
| **Security** | | |
| `SecureInputValidatorHelper` | Input sanitization | Tool execution, file operations |
| `ClaimConstants` | Auth claim type strings | Authorization behavior |
| **Telemetry** | | |
| `AppInstrument` | Shared ActivitySource + Meter | All instrumentation code |
| `ExecutionScope` | Logging context record | Logger providers |

## Common Tasks

### How to Add a New Configuration Section

1. Create a new POCO class in `Config/` (under the appropriate subdirectory):

```csharp
namespace Domain.Common.Config.AI;

public class MyFeatureConfig
{
    public bool Enabled { get; set; } = false;
    public int MaxRetries { get; set; } = 3;
    public string? Endpoint { get; set; }
}
```

2. Add a property to the appropriate parent config class (usually `AIConfig`, `AzureConfig`, or `AppConfig`):

```csharp
// In AIConfig.cs:
public MyFeatureConfig MyFeature { get; set; } = new();
```

3. Add the corresponding JSON to `appsettings.json`:

```json
{
  "AppConfig": {
    "AI": {
      "MyFeature": { "Enabled": true, "MaxRetries": 5 }
    }
  }
}
```

### How to Add a New ResultFailureType

1. Add the enum value to `ResultFailureType` in `Result.cs`
2. Add factory methods to both `Result` and `Result<T>`:

```csharp
public static Result MyNewFailure(string reason) => new(false, [reason], ResultFailureType.MyNewFailure);
```

3. Update `ResultExtensions.PropagateFailure` switch expression
4. Update the global error handler in Presentation to map the new type to an HTTP status code

## Dependencies

| Reference | Why |
|-----------|-----|
| `Microsoft.Extensions.Hosting.Abstractions` | `IHostedService` marker for background worker models |
| `Microsoft.Extensions.Logging.Abstractions` | `ILogger<T>` usage in helpers without pulling the full logging stack |

No other project references. This is intentional -- Domain.Common is the foundation everything else builds on.

## Testing

Tests for Domain.Common live in the corresponding test project following the naming convention `Domain.Common.Tests`. Test pure logic like `Result<T>` composition, `SecureInputValidatorHelper` validation, `DecisionFramework.Validate()`, and `ResultExtensions` chaining.

```bash
dotnet test src/AgenticHarness.slnx --filter "FullyQualifiedName~Domain.Common"
```

Since this layer has zero external dependencies, all tests are pure unit tests with no mocking required.
