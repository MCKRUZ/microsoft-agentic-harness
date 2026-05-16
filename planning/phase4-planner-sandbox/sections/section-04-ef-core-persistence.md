# Section 04: EF Core Persistence

## Overview

This section introduces Entity Framework Core to the codebase for the first time. It creates `PlannerDbContext` with five entity configurations, RowVersion concurrency tokens, JSON column handling for polymorphic `StepConfiguration`, and code-first migrations targeting SQLite. This is a deliberate architectural decision: plan state requires transactional updates, queryable audit trails, and checkpoint/resume that file-based stores cannot provide.

**Depends on**: section-01-domain-models (PlanGraph, PlanStep, PlanEdge, StepExecutionState, PlanConfiguration, RetryPolicy, ToolExecutionAttestation, StepType, EdgeType, StepExecutionStatus, and all StepConfiguration subtypes)

**Blocks**: section-05-plan-state-store (EfCorePlanStateStore consumes PlannerDbContext)

---

## NuGet Package Additions

### `src/Directory.Packages.props`

Add under the `<!-- Infrastructure.AI -->` comment block:

```xml
<PackageVersion Include="Microsoft.EntityFrameworkCore.Sqlite" Version="10.0.5" />
<PackageVersion Include="Microsoft.EntityFrameworkCore.Design" Version="10.0.5" />
```

Add under the `<!-- Testing -->` comment block:

```xml
<PackageVersion Include="Microsoft.EntityFrameworkCore.InMemory" Version="10.0.5" />
```

### `src/Content/Infrastructure/Infrastructure.AI/Infrastructure.AI.csproj`

```xml
<PackageReference Include="Microsoft.EntityFrameworkCore.Sqlite" />
```

### `src/Content/Tests/Infrastructure.AI.Tests/Infrastructure.AI.Tests.csproj`

```xml
<PackageReference Include="Microsoft.EntityFrameworkCore.Sqlite" />
<PackageReference Include="Microsoft.EntityFrameworkCore.InMemory" />
```

---

## Entity Models

All entity classes go in `src/Content/Infrastructure/Infrastructure.AI/Persistence/Entities/`.

### `PlanGraphEntity.cs`

Properties:
- `Id` (Guid, primary key) -- maps from `PlanId.Value`
- `Name` (string, required)
- `ParentPlanId` (Guid?, nullable FK to self)
- `ConfigurationJson` (string) -- serialized `PlanConfiguration`
- `CreatedAt` (DateTimeOffset)
- `UpdatedAt` (DateTimeOffset)
- `RowVersion` (byte[], concurrency token)

Navigation properties: `Steps`, `Edges`, `ExecutionLogs`, `ParentPlan`, `ChildPlans`

### `PlanStepEntity.cs`

Properties:
- `Id` (Guid, primary key)
- `PlanGraphId` (Guid, FK)
- `Name` (string, required)
- `Type` (StepType enum, stored as string)
- `ConfigurationJson` (string) -- polymorphic JSON with `type` discriminator
- `RetryPolicyJson` (string)
- `TimeoutSeconds` (double)
- `RequiredAutonomyLevel` (int?, nullable)

### `PlanEdgeEntity.cs`

Properties:
- `Id` (Guid, primary key, auto-generated)
- `PlanGraphId` (Guid, FK)
- `FromStepId` (Guid, FK)
- `ToStepId` (Guid, FK)
- `Type` (EdgeType enum, stored as string)
- `Condition` (string?, nullable)

### `StepExecutionStateEntity.cs`

Properties:
- `Id` (Guid, primary key)
- `StepId` (Guid, FK, unique)
- `Status` (StepExecutionStatus enum, stored as string)
- `AttemptCount` (int)
- `StartedAt` (DateTimeOffset?, nullable)
- `CompletedAt` (DateTimeOffset?, nullable)
- `Output` (string?, nullable)
- `ErrorMessage` (string?, nullable)
- `AttestationJson` (string?, nullable)
- `RowVersion` (byte[], concurrency token)

### `PlanExecutionLogEntity.cs`

Append-only audit log:
- `Id` (long, auto-increment)
- `PlanGraphId` (Guid, FK)
- `StepId` (Guid?, nullable)
- `EventType` (string)
- `Timestamp` (DateTimeOffset)
- `DetailsJson` (string?, nullable)

---

## DbContext

### `PlannerDbContext.cs`

Path: `src/Content/Infrastructure/Infrastructure.AI/Persistence/PlannerDbContext.cs`

Inherits from `DbContext`. Five `DbSet<T>` properties. Entity configuration via `IEntityTypeConfiguration<T>` applied in `OnModelCreating` using `modelBuilder.ApplyConfigurationsFromAssembly`.

Key design points:
- **SQLite WAL mode**: Enable via `PRAGMA journal_mode=WAL;` for read concurrency
- **Timestamp handling**: Store as ISO 8601 strings
- **Enum storage**: Store as strings for readability and resilience

---

## Entity Configurations

All in `src/Content/Infrastructure/Infrastructure.AI/Persistence/Configurations/`.

### `PlanGraphEntityConfiguration.cs`
- Primary key on `Id`
- `Name` required, max length 200
- RowVersion via integer `Version` property with `.IsConcurrencyToken()`
- Self-referencing FK: `ParentPlanId` with `DeleteBehavior.SetNull`
- Indexes on `ParentPlanId` and `CreatedAt`

### `PlanStepEntityConfiguration.cs`
- `Type` stored as string via `.HasConversion<string>()`
- `ConfigurationJson` is a plain string column (polymorphic JSON handled by state store)
- One-to-one navigation to `StepExecutionStateEntity`

### `PlanEdgeEntityConfiguration.cs`
- `FromStepId` and `ToStepId` FKs with `DeleteBehavior.Restrict`
- Composite index on `(PlanGraphId, FromStepId, ToStepId)`

### `StepExecutionStateEntityConfiguration.cs`
- `StepId` unique FK (one-to-one)
- RowVersion via integer `Version` with `.IsConcurrencyToken()`
- Index on `Status` for ready-queue queries

### `PlanExecutionLogEntityConfiguration.cs`
- `Id` auto-increment via `.ValueGeneratedOnAdd()`
- Index on `(PlanGraphId, Timestamp)` for chronological queries

---

## SQLite Concurrency Token Strategy

### `SqliteVersionInterceptor.cs`

Path: `src/Content/Infrastructure/Infrastructure.AI/Persistence/SqliteVersionInterceptor.cs`

Overrides `SavingChangesAsync` to find modified entities with a `Version` property and increment them. This emulates optimistic concurrency for SQLite (which lacks native rowversion).

---

## Migration Strategy

### Initial Migration

```powershell
dotnet ef migrations add InitialPlannerSchema --project src/Content/Infrastructure/Infrastructure.AI --startup-project src/Content/Presentation/Presentation.ConsoleUI --context PlannerDbContext --output-dir Persistence/Migrations
```

### Auto-Migration at Startup

Controlled by `Planner:AutoMigrate` config flag (defaults `true` for dev, `false` for prod).

### Database File Location

Configurable via `Planner:DatabasePath`. Default: `{AppContext.BaseDirectory}/data/planner.db`.

---

## DbContext Lifetime and Factory

```csharp
services.AddDbContext<PlannerDbContext>(options =>
    options.UseSqlite(connectionString)
           .AddInterceptors(new SqliteVersionInterceptor()));
services.AddDbContextFactory<PlannerDbContext>();
```

`IDbContextFactory<PlannerDbContext>` for singleton callers.

---

## Tests

All in `src/Content/Tests/Infrastructure.AI.Tests/Persistence/PlannerDbContextTests.cs`.

```csharp
// Test: PlannerDbContext_Migrate_CreatesAllTables
// Test: PlanGraphEntity_Insert_PersistsAllFields
// Test: PlanGraphEntity_ConcurrentUpdate_ThrowsDbUpdateConcurrencyException
// Test: PlanStepEntity_ConfigJson_RoundTripsPolymorphicConfig
// Test: PlanStepEntity_ConfigJson_PreservesDiscriminator
// Test: PlanEdgeEntity_ForeignKeys_EnforcedByDb
// Test: StepExecutionStateEntity_ConcurrentUpdate_ThrowsDbUpdateConcurrencyException
// Test: StepExecutionStateEntity_AttestationJson_RoundTripsNullable
// Test: PlanExecutionLogEntity_AppendOnly_InsertsWithTimestamp
```

**Important test notes**:
1. Enable SQLite FK enforcement via `PRAGMA foreign_keys = ON`
2. Concurrency tests need two separate DbContext instances
3. Polymorphic JSON round-trip must verify all 5 StepConfiguration subtypes

---

## File Summary

### New Files

| File | Purpose |
|------|---------|
| `Persistence/PlannerDbContext.cs` | DbContext with 5 DbSets |
| `Persistence/SqliteVersionInterceptor.cs` | Optimistic concurrency for SQLite |
| `Persistence/Entities/PlanGraphEntity.cs` | Plan graph entity |
| `Persistence/Entities/PlanStepEntity.cs` | Plan step entity |
| `Persistence/Entities/PlanEdgeEntity.cs` | Plan edge entity |
| `Persistence/Entities/StepExecutionStateEntity.cs` | Step execution state entity |
| `Persistence/Entities/PlanExecutionLogEntity.cs` | Append-only audit log entity |
| `Persistence/Configurations/PlanGraphEntityConfiguration.cs` | EF config |
| `Persistence/Configurations/PlanStepEntityConfiguration.cs` | EF config |
| `Persistence/Configurations/PlanEdgeEntityConfiguration.cs` | EF config |
| `Persistence/Configurations/StepExecutionStateEntityConfiguration.cs` | EF config |
| `Persistence/Configurations/PlanExecutionLogEntityConfiguration.cs` | EF config |
| `Tests/Persistence/PlannerDbContextTests.cs` | 9 persistence tests |

### Files to Modify

| File | Change |
|------|--------|
| `src/Directory.Packages.props` | Add EF Core package versions |
| `Infrastructure.AI.csproj` | Add EF Core SQLite reference |
| `Infrastructure.AI.Tests.csproj` | Add EF Core test references |

---

## Domain-to-Entity Mapping Notes

- `PlanId` -> `Guid` via `.Value`
- `PlanConfiguration` -> `ConfigurationJson` (JSON string)
- `StepConfiguration` -> `ConfigurationJson` with `type` discriminator
- `RetryPolicy` -> `RetryPolicyJson` (JSON string)
- `ToolExecutionAttestation` -> `AttestationJson` (nullable)
- `TimeSpan` -> `double` (seconds)
- All enums stored as strings (including `AutonomyLevel?` via `HasConversion<string>()`)

---

## Implementation Notes (Post-Build)

### Deviations from Plan
- `RequiredAutonomyLevel` changed from `int?` to `AutonomyLevel?` with string conversion (code review: consistent with all-enums-as-strings pattern)
- Added `Microsoft.EntityFrameworkCore.Design` package reference with `PrivateAssets="all"` for migration tooling
- PlanEdge composite index changed to unique `(PlanGraphId, FromStepId, ToStepId, Type)` for defense-in-depth
- Added reverse index `(PlanGraphId, ToStepId)` for predecessor lookups in plan executor
- Added `HasMaxLength(200)` to `PlanStepEntity.Name` and `HasMaxLength(100)` to `PlanExecutionLogEntity.EventType`
- `SqliteVersionInterceptor` simplified to only handle `EntityState.Modified` (removed no-op `Added` path)
- Test uses shared `SqliteConnection` for in-memory DB (required for multi-context scenarios)
- Test orders log entries by `Id` instead of `Timestamp` (SQLite doesn't support `DateTimeOffset` in ORDER BY)

### Deferred to Later Sections
- DI registration (`AddDbContext`, `AddDbContextFactory`) → Section 15
- WAL mode PRAGMA → Section 15/16 (connection setup)
- FK enforcement PRAGMA in production → Section 15/16
- Auto-migration config flag → Section 16

### Test Results
- 9/9 tests passing
- Covers: table creation, field persistence, optimistic concurrency (2 tests), polymorphic JSON round-trip (2 tests), FK enforcement, nullable attestation, append-only audit log
