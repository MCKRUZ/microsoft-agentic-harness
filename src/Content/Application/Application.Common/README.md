# Application.Common

## What This Is

Application.Common is the engine room of cross-cutting concerns for the CQRS (Command Query Responsibility Segregation) pipeline. Every request that flows through the harness -- whether it's an agent turn, a RAG search, or a config lookup -- passes through the pipeline behaviors, validation rules, and logging infrastructure defined here. Think of it as the security checkpoint, speed monitor, and record-keeper that every request passes through before reaching its handler.

It solves the problem of duplicated infrastructure code: without centralized pipeline behaviors, every handler would need its own validation, timeout, caching, and tracing logic. This project does not know about agents or AI -- it knows about *requests* and how to make them reliable.

Application.Common depends on Domain.Common (for the Result pattern and config types) and is depended upon by Application.AI.Common, Application.Core, and all Infrastructure projects.

## Architecture Context

```
                    ┌───────────────���─────────┐
                    │     Presentation         │  (calls AddApplicationCommonDependencies)
                    └────────────┬────────────┘
                                 │
                    ┌────────────▼────────────┐
                    │     Infrastructure       │  (implements IIdentityService, IDirectoryMapper)
                    └────────────┬────────────┘
                                 │
        ┌────────────────────────┼──────────────────────────┐
        │                        │                          │
┌───────▼───────┐   ╔═══════════▼═══════════╗   ┌──────────▼──────────┐
│  App.AI.Common │   ║   Application.Common  ║   │   Application.Core   │
│  (extends      │   ║   ← YOU ARE HERE      ║   │   (uses pipeline)    │
│   pipeline)    │   ╚═══════════╤═══════════╝   └───────────────��─────┘
└───────┬────────┘               │
        └────────────────────────┤
                    ┌────────────▼────────────┐
                    │      Domain.Common       │
                    └─────────────────────────┘
```

In Clean Architecture, the Application layer contains use cases, interfaces, DTOs, validators, and pipeline behaviors. Application.Common contains the *generic* (non-AI-specific) subset: request validation, authorization, caching, timeout enforcement, tracing, and the logging infrastructure that everything shares. It can reference Domain.Common but nothing above it -- no Infrastructure implementations, no HTTP controllers, no AI SDKs.

## Key Concepts

### The MediatR Pipeline (Behavior Chain)

Every command and query dispatched through MediatR passes through an ordered chain of pipeline behaviors. These are concentric rings -- each wraps the next, executing before and/or after the handler:

```
Incoming Request
  → RequestValidationBehavior    Runs FluentValidation, returns Result.Fail() on error
    → AuthorizationBehavior      Checks [Authorize] attributes on the request type
      → CachingBehavior          Reads/writes HybridCache for ICacheableQuery requests
        → RequestTracingBehavior Creates an OpenTelemetry Activity span
          → TimeoutBehavior      Wraps in CancellationTokenSource with deadline
            → YOUR HANDLER       The actual business logic
```

Registration order in DI determines execution order (first registered = outermost wrapper).

**Consumer usage -- making a request participate in validation:**

```csharp
// 1. Define a command with a marker interface:
public record CreateAgentCommand : IRequest<Result<Agent>>, IHasTimeout
{
    public TimeSpan? Timeout => TimeSpan.FromSeconds(30);
    public required string Name { get; init; }
}

// 2. Create a validator (auto-discovered):
public class CreateAgentCommandValidator : AbstractValidator<CreateAgentCommand>
{
    public CreateAgentCommandValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(128);
    }
}

// 3. If validation fails, the behavior returns Result<Agent>.ValidationFailure(errors)
//    without ever reaching your handler.
```

**Making a query cacheable:**

```csharp
public record GetSkillQuery : IRequest<Result<SkillDefinition>>, ICacheableQuery<Result<SkillDefinition>>
{
    public string CacheKey => $"skill:{SkillId}";
    public TimeSpan? AbsoluteExpiration => TimeSpan.FromMinutes(10);
    public required string SkillId { get; init; }
}
```

### Logging Architecture

The harness needs simultaneous log output to multiple destinations. Application.Common provides 6 custom `ILoggerProvider` implementations:

| Provider | Purpose | When to Use |
|----------|---------|-------------|
| `ColorfulConsoleFormatter` | ANSI-colored terminal output | Development |
| `FileLogger` | Dual-format file (structured + human-readable) | Production persistence |
| `StructuredJsonLogger` | One JSON object per line (JSONL) | Automated log analysis |
| `NamedPipeLogger` | Named pipe stream for LoggerUI | Real-time viewer |
| `InMemoryRingBufferLogger` | Circular buffer (500 entries) | Diagnostics endpoints |
| `CallbackLogger` | Lambda-based handler | Testing, custom integrations |

All providers share execution context through `ExecutionScopeProvider`, which tracks a hierarchy: Executor > Correlation > Step > Operation. This propagates through logging scopes and OpenTelemetry Activities.

```csharp
// Structured scope that all providers respect:
using (_logger.BeginScope(new ExecutionScope
{
    TraceId = Activity.Current?.TraceId.ToString(),
    AgentName = "research-agent",
    StepNumber = 3
}))
{
    _logger.LogInformation("Processing turn");
    // All providers see TraceId, AgentName, StepNumber as structured fields
}
```

### Exception Hierarchy

A typed exception taxonomy maps domain errors to HTTP status codes. The global error handler in Presentation catches these and returns appropriate responses:

| Exception | HTTP | Scenario |
|-----------|------|----------|
| `BadRequestException` | 400 | Malformed input structure |
| `ValidationException` | 400 | FluentValidation failures (carries property-level errors) |
| `ForbiddenAccessException` | 403 | Authorization policy denied |
| `EntityNotFoundException` | 404 | Resource not found |
| `NoContentException` | 204 | Success but no body |
| `ConfigurationNotFoundException` | 500 | Missing config values at runtime |
| `DatabaseInteractionException` | 500 | Data access failures |

All extend `ApplicationExceptionBase` for unified catch blocks.

### Marker Interfaces

Requests opt into pipeline behaviors by implementing marker interfaces:

- `IHasTimeout` -- Provides a `TimeSpan? Timeout` property. TimeoutBehavior wraps the handler in a CancellationTokenSource.
- `ICacheableQuery<T>` -- Provides `CacheKey` and expiration. CachingBehavior checks HybridCache before calling the handler.
- `ICacheInvalidation` -- Provides `CacheKeysToInvalidate`. CachingBehavior evicts these keys on successful handler completion.
- `IAuditable` -- Marks the request for audit trail recording (used by AI-layer AuditTrailBehavior).

### Factories and Helpers

- **AzureCredentialFactory** -- Creates `TokenCredential` instances from config (client secret, certificate, or DefaultAzureCredential). Used by Infrastructure for Azure service authentication.
- **EmbeddedResourceHelper** -- Reads `.md` and `.json` files embedded in assemblies (SKILL.md files compiled into Application.Core).
- **YamlFrontmatterHelper** -- Extracts YAML metadata blocks from markdown files (parses the `---` delimited header).
- **CacheOptionsHelper** -- Factory for `HybridCacheEntryOptions` with sensible defaults.
- **ReflectionHelper** -- Dynamic property access with caching, supports dot-notation paths.

## Project Structure

```
Application.Common/
├── Attributes/
│   └── AuthorizeAttribute.cs         # Declarative [Authorize(Roles="...", Policy="...")] for MediatR
├── DependencyInjection.cs            # Central DI: MediatR, validators, behaviors, cache, logging
├── Exceptions/
│   ├── ApplicationExceptionBase.cs   # Base class with Title, StatusCode, Detail
│   └── ExceptionTypes/               # 7 typed exceptions (BadRequest, Validation, etc.)
├── Extensions/
│   ├── ILoggerExtensions.cs          # High-performance logging extensions
│   ├── ILoggingBuilderExtensions.cs  # .ConfigureLogging() registration
│   ├── IServiceCollectionExtensions.cs # DI helpers
│   ├── ObjectExtensions.cs           # Object utility methods
│   └── TimeProviderExtensions.cs     # Testable time abstractions
├── Factories/
│   └── AzureCredentialFactory.cs     # TokenCredential creation from config
├── Helpers/
│   ├── CacheOptionsHelper.cs         # HybridCache entry option factory
│   ├── EmbeddedResourceHelper.cs     # Assembly resource reading
│   ├── ReflectionHelper.cs           # Dynamic property access with caching
│   └── YamlFrontmatterHelper.cs      # YAML metadata extraction from markdown
├── Interfaces/
│   ├── Common/
│   │   ├── HarnessDirectory.cs       # Enum of well-known directories (Root, Config, Skills...)
│   │   └── IDirectoryMapper.cs       # Maps HarnessDirectory → absolute path
│   ├── MediatR/
│   │   ├── IAuditable.cs             # Opt-in for audit trail
│   │   ├── ICacheableQuery.cs        # Opt-in for read caching
│   │   ├── ICacheInvalidation.cs     # Opt-in for cache eviction
│   │   └── IHasTimeout.cs            # Opt-in for timeout enforcement
│   ├── Security/
│   │   ├── IIdentityService.cs       # Identity operations contract
│   │   └── IUser.cs                  # Current user context
│   └── Telemetry/
│       └── ITelemetryConfigurator.cs  # Layer-ordered OTel source registration
├── Logging/
│   ├── AnsiColors.cs                 # Terminal color constants
│   ├── BaseLogger.cs                 # Shared formatting logic
│   ├── CallbackLogger[Provider].cs   # Lambda-based logging for tests
│   ├── ColorfulConsoleFormatter.cs   # ANSI dev console output
│   ├── ExecutionConsoleFormatter.cs  # Scope-aware console formatting
│   ├── ExecutionScopeProvider.cs     # Tracks executor hierarchy
│   ├── FileLogger[Provider].cs       # Dual-format file output
│   ├── InMemoryRingBufferLogger.cs   # Circular buffer for diagnostics
│   ├── LogEntryFactory.cs            # Creates structured LogEntry records
│   ├── LoggingHelper.cs              # Log-level color mapping, formatting utilities
│   ├── NamedPipeLogger[Provider].cs  # Real-time pipe stream
│   └── StructuredJsonLogger.cs       # JSONL output
├── MediatRBehaviors/
│   ├── AuthorizationBehavior.cs      # [Authorize] enforcement
│   ├── CachingBehavior.cs            # HybridCache read/write/invalidate
│   ├── RequestTracingBehavior.cs     # OpenTelemetry Activity per request
│   ├── RequestValidationBehavior.cs  # FluentValidation with parallel execution
│   └── TimeoutBehavior.cs            # CancellationTokenSource wrapping
└── OpenTelemetry/
    └── AppTelemetryConfigurator.cs   # Registers base harness OTel sources and meters
```

## Key Types Reference

| Type | Purpose | Used By |
|------|---------|---------|
| **Pipeline Behaviors** | | |
| `RequestValidationBehavior<,>` | Runs validators, returns Result.Fail on error | All MediatR requests with validators |
| `AuthorizationBehavior<,>` | Checks [Authorize] attributes | Requests with security requirements |
| `CachingBehavior<,>` | HybridCache read/write | ICacheableQuery-marked queries |
| `TimeoutBehavior<,>` | Enforces deadline | IHasTimeout-marked requests |
| `RequestTracingBehavior<,>` | OTel span per request | All requests (automatic) |
| **Logging** | | |
| `ExecutionScopeProvider` | Tracks execution hierarchy | All logger providers |
| `FileLoggerProvider` | Dual-format file output | Production deployments |
| `InMemoryRingBufferLoggerProvider` | Diagnostics endpoint buffer | Health/diagnostics APIs |
| **Interfaces** | | |
| `IDirectoryMapper` | Well-known path resolution | Skill loaders, config discovery |
| `ITelemetryConfigurator` | OTel source registration | Per-layer telemetry setup |
| `IUser` | Current user identity | Authorization, audit |
| **Helpers** | | |
| `EmbeddedResourceHelper` | Assembly resource reading | SKILL.md loading from embedded files |
| `YamlFrontmatterHelper` | YAML frontmatter extraction | Manifest/skill parsing |

## Common Tasks

### How to Add a New Pipeline Behavior

1. Create the behavior in `MediatRBehaviors/`:

```csharp
public sealed class MyBehavior<TRequest, TResponse>
    : IPipelineBehavior<TRequest, TResponse> where TRequest : notnull
{
    public async Task<TResponse> Handle(
        TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken ct)
    {
        // Pre-handler logic
        var response = await next();
        // Post-handler logic
        return response;
    }
}
```

2. Register it in `DependencyInjection.cs` (position in the list determines execution order):

```csharp
services.AddTransient(typeof(IPipelineBehavior<,>), typeof(MyBehavior<,>));
```

3. If it should only activate for specific requests, check for a marker interface:

```csharp
if (request is not IMyMarker myRequest)
    return await next();  // Skip for non-participating requests
```

### How to Add a New Logger Provider

1. Implement `ILogger` in `Logging/`:

```csharp
public class MyLogger : BaseLogger
{
    protected override void WriteLog(LogEntry entry) { /* your output */ }
}
```

2. Implement `ILoggerProvider`:

```csharp
public class MyLoggerProvider : ILoggerProvider
{
    public ILogger CreateLogger(string categoryName) => new MyLogger(categoryName);
    public void Dispose() { }
}
```

3. Register in `ILoggingBuilderExtensions.ConfigureLogging()` conditional on config.

## Dependencies

| Reference | Why |
|-----------|-----|
| `Domain.Common` | Result pattern, config POCOs, constants, ExecutionScope |
| `Azure.Identity` | AzureCredentialFactory for TokenCredential creation |
| `FluentValidation` | Validator discovery and execution in pipeline |
| `MediatR` | Pipeline behavior infrastructure |
| `Microsoft.Extensions.Caching.Hybrid` | L1 memory + L2 distributed cache |
| `Microsoft.Extensions.Caching.Memory` | In-process cache backing |
| `OpenTelemetry` | Activity/span creation in RequestTracingBehavior |
| `Microsoft.Extensions.Logging` | Logger provider contracts and registration |

## Testing

Tests for Application.Common live in `Application.Common.Tests`. Key test areas:

- **Pipeline behaviors**: Mock `RequestHandlerDelegate`, verify short-circuiting on validation failure, verify caching reads/writes, verify timeout cancellation.
- **Logging providers**: Verify log output format, ring buffer overflow, scope propagation.
- **Helpers**: YamlFrontmatterHelper edge cases, EmbeddedResourceHelper missing resources, CacheOptions defaults.

```bash
dotnet test src/AgenticHarness.slnx --filter "FullyQualifiedName~Application.Common"
```

Use `FakeTimeProvider` (from `Microsoft.Extensions.Time.Testing`) for timeout behavior tests. Use `WebApplicationFactory<Program>` for integration tests that exercise the full pipeline.
