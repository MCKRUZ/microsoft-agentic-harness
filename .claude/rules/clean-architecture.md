---
paths: src/Content/**/*.cs
---
# Clean Architecture Rules

## Dependency Direction
Domain depends on nothing. Application depends on Domain only. Infrastructure implements Application interfaces. Presentation depends on Application.

Never reference Infrastructure from Domain or Application projects. Use interfaces + DI.

## Layer Responsibilities
- **Domain**: Entities, value objects, enums, domain events. No framework dependencies.
- **Application**: CQRS commands/queries, interfaces, DTOs, validators, mapping profiles, pipeline behaviors.
- **Infrastructure**: EF Core, external API clients, file system, AI service implementations, MCP server/client.
- **Presentation**: Console UI, API endpoints, middleware, DI composition root.

## DI Registration Pattern
Each layer has its own `DependencyInjection.cs` with `Add{Layer}Dependencies(this IServiceCollection, AppConfig)` extension method. Composition root in Presentation calls all layers.

## CQRS Pattern
- Commands mutate state, Queries read state — never mix
- One handler per command/query, registered via MediatR assembly scanning
- Validation via `RequestValidationBehavior<TRequest, TResponse>` pipeline behavior
- Commands return `Result<T>`, never throw for expected failures

## File Placement Rule (MANDATORY)
Before creating ANY file, determine both its **folder** and its **project** by asking:

1. **Does it depend on external services, APIs, SDKs, HTTP, databases, or file system?** → Infrastructure project
2. **Does it expose HTTP endpoints, WebSocket servers, middleware, or DI composition?** → Presentation project
3. **Is it a pure domain concept with zero framework dependencies?** → Domain project
4. **Is it an interface, DTO, validator, pipeline behavior, or implementation using only Microsoft.Extensions abstractions?** → Application project

Within a project, organize by **concern** (Logging/, Exceptions/, Extensions/) not by type (Interfaces/, Classes/, Models/).

### Specific Placement Guidelines
| Type | Project | Folder |
|------|---------|--------|
| Config POCOs / Options | Models/ or DTOs/ in the consuming layer | NOT with the implementation that uses them |
| DI extension methods | Extensions/ | NOT with the service they register |
| ILogger / ILoggerProvider impls (no external deps) | Application.Common/Logging/ | Pure logging infrastructure only |
| Transport-specific logging (WebSocket, HTTP, SignalR) | Infrastructure or Presentation | Depends on transport framework |
| API clients, HTTP handlers | Infrastructure | Never in Application or Domain |
| Interfaces for external services | Application/Interfaces/ | Implemented in Infrastructure |

### The Litmus Test
If you remove the file's project reference, does the code still compile with only `Microsoft.Extensions.*` and Domain references? If yes → Application. If no → Infrastructure or Presentation.

## New Feature Checklist
1. Domain model in `Domain.Core` or `Domain.AI`
2. Interface in `Application.*.Common/Interfaces/`
3. Command/Query + Handler in `Application.Core/CQRS/`
4. FluentValidation validator alongside command
5. Implementation in `Infrastructure.*`
6. Register in appropriate `DependencyInjection.cs`
7. Wire in Presentation layer

## Standing rules (Incident Ledger, onboarding chapter 19)
- Before adding an abstraction, check `Application.AI.Common/Interfaces/` and the ApplicationTemplate for an existing one (INC-01)
- A new command needs its MediatR pipeline behaviors registered in `DependencyInjection.cs` (INC-03)
- AI model config comes from `AppConfig.AI.AgentFramework`, never inline (INC-04)
- Step executors register as keyed services by `StepType` (INC-06)
- `SandboxOptions` (Domain config) and `SandboxExecutionOptions` (Application container config) are different classes; use the one you mean (INC-07)
- Never increment an entity's `Version` by hand; `SqliteVersionInterceptor` does it on save (INC-08)
- Check `Result.IsSuccess` on every state store operation (INC-09)
- Step executors never call `NotifyStepStarted`; `PlanExecutor` owns notification centrally (INC-10)
- A skill with `PluginSource` needs that plugin declared in `PluginsConfig`, or the plugin won't load and its tools won't resolve (INC-11)
- A plugin's `DeniedTools` are bypass-immune: no auto-approve mode overrides them (INC-12)
