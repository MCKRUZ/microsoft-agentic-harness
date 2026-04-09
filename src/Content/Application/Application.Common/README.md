# Application.Common

This is the engine room of cross-cutting concerns. Every request that flows through the harness — whether it's an agent turn, a tool invocation, or a cache lookup — passes through the pipeline behaviors, validation rules, and logging infrastructure defined here.

Application.Common doesn't know about agents or AI. It knows about *requests*: how to validate them, authorize them, cache them, trace them, time them, and log what happened. It's the plumbing that makes the CQRS pipeline reliable before any domain logic runs.

---

## The MediatR Pipeline

Every command and query in the harness flows through an ordered chain of pipeline behaviors. These are the middleware of the CQRS world — each one wraps the next, forming concentric rings around the actual handler:

```
Request
  → TimeoutBehavior (position 2)        Kill requests that take too long
    → AuthorizationBehavior (position 5) Check roles and policies
      → RequestValidationBehavior (7)    Run FluentValidation rules
        → RequestTracingBehavior (8)     Create OpenTelemetry spans
          → CachingBehavior (position 9) Read/write HybridCache
            → Handler                    The actual business logic
```

**TimeoutBehavior** wraps every request in a `CancellationTokenSource`. Commands can implement `IHasTimeout` to override the default, or the global `AgentConfig.DefaultRequestTimeoutSec` applies.

**AuthorizationBehavior** reads `[Authorize]` attributes from the request type. Roles use OR logic (any matching role passes), policies use AND logic (all must pass). If authorization fails, it returns a `Result.Fail()` for Result-typed responses or throws `ForbiddenAccessException` for everything else.

**RequestValidationBehavior** discovers FluentValidation validators for the request type and runs them in parallel. Failures aggregate into a `ValidationException` with property-level error grouping — or a `Result.Fail()` if the response type supports it.

**RequestTracingBehavior** creates an OpenTelemetry `Activity` span for every request, tagged with the request type name. This is how individual handler executions show up in Jaeger traces.

**CachingBehavior** handles two marker interfaces: `ICacheableQuery<T>` for reads (check cache first, store on miss) and `ICacheInvalidation` for writes (evict specified keys on success). Uses `HybridCache` for L1 memory + L2 distributed caching.

## Logging Architecture

The harness needs to send logs to multiple destinations simultaneously — the console for development, files for persistence, named pipes for the LoggerUI viewer, a ring buffer for diagnostics endpoints, and structured JSON for machine parsing. Application.Common provides all of these as custom `ILoggerProvider` implementations:

- **ColorfulConsoleFormatter** / **ExecutionConsoleFormatter** — ANSI-colored console output with execution scope awareness (agent name, step number, operation indentation)
- **FileLogger** — Dual-format file output: structured `log.txt` + human-readable `console.txt`, with background threading and bounded queues
- **StructuredJsonLogger** — One JSON object per line (JSONL format) for automated analysis
- **NamedPipeLogger** — Streams logs over a named pipe for the real-time LoggerUI viewer, with auto-reconnect
- **InMemoryRingBufferLogger** — Fixed-size circular buffer (default 500 entries) for diagnostics endpoints, lock-free
- **CallbackLogger** — Lambda-based log handler for tests and custom integrations

All providers share execution context through `ExecutionScopeProvider`, which tracks the executor hierarchy (Executor > Correlation > Step > Operation) and propagates it through logging scopes and OpenTelemetry Activities.

## Exceptions

A typed exception hierarchy maps domain errors to HTTP status codes:

| Exception | HTTP | When |
|-----------|------|------|
| `BadRequestException` | 400 | Malformed input |
| `ValidationException` | 400 | FluentValidation failures |
| `ForbiddenAccessException` | 403 | Authorization denied |
| `EntityNotFoundException` | 404 | Resource not found |
| `ConfigurationNotFoundException` | 500 | Missing config values |
| `DatabaseInteractionException` | 500 | Data access failures |

All extend `ApplicationExceptionBase` for unified catch blocks in the global error handler.

## Factories & Helpers

- **AzureCredentialFactory** — Creates `TokenCredential` instances from config (client secret, client certificate, or DefaultAzureCredential fallback)
- **EmbeddedResourceHelper** — Reads embedded resources (AGENT.md, SKILL.md, prompt templates) from assemblies
- **ReflectionHelper** — Dynamic property access with caching, supports nested dot-notation paths
- **YamlFrontmatterHelper** — Extracts YAML frontmatter from markdown files
- **CacheOptionsHelper** — Factory methods for HybridCache entry options with sensible defaults

## Interfaces

Key abstractions that other layers implement:

- **IDirectoryMapper** / **HarnessDirectory** — Maps well-known directory names (Root, Config, Skills, Manifests, Logs, Temp) to absolute paths
- **IUser** / **IIdentityService** — Current user context and identity operations
- **ITelemetryConfigurator** — Extension point for layers to register their own OpenTelemetry sources and meters (ordered: 0-99 Core, 100-199 Harness, 200-299 Domain, 300+ Finalization)

---

## Project Structure

```
Application.Common/
├── Attributes/                  AuthorizeAttribute for declarative MediatR security
├── Exceptions/                  Typed exception hierarchy (7 exception types)
├── Extensions/                  DI registration, logging builders, Result/string/time helpers
├── Factories/                   AzureCredentialFactory
├── Helpers/                     Cache options, embedded resources, reflection, YAML frontmatter
├── Interfaces/                  IDirectoryMapper, IUser, IIdentityService, ITelemetryConfigurator
│                                Marker interfaces: IAuditable, ICacheableQuery, ICacheInvalidation, IHasTimeout
├── Logging/                     6 logger providers, 2 console formatters, scope provider, helpers
├── MediatRBehaviors/            5 pipeline behaviors (timeout, auth, validation, tracing, caching)
├── OpenTelemetry/               AppTelemetryConfigurator (base harness OTel registration)
└── DependencyInjection.cs       Central DI bootstrap
```

## Dependencies

- **Domain.Common** — Result pattern, config POCOs, constants
- **Azure.Identity** — Credential factory
- **FluentValidation** — Request validation pipeline
- **MediatR** — CQRS pipeline and behaviors
- **Microsoft.Extensions.Caching.Hybrid** — L1+L2 caching
- **OpenTelemetry** — Trace and metric instrumentation
