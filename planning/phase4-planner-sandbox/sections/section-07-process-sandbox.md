# Section 7: Process Sandbox

## Overview

This section implements `ProcessSandboxExecutor` -- the default (process-tier) sandbox for tool execution. It launches tool code as a subprocess with stdin/stdout JSON communication, enforces OS-level resource limits via Windows Job Objects (behind the `IProcessResourceLimiter` interface), and handles timeout/crash scenarios with failure attestations. On Linux, process execution works but resource limits are skipped with a logged warning (container isolation is the cross-platform answer).

## Dependencies

- **Section 01 (Domain Models)**: `ResourceLimits`, `SandboxIsolationLevel`, `ToolPermissionProfile`, `ToolExecutionAttestation`
- **Section 02 (Application Interfaces)**: `ISandboxExecutor`, `IProcessResourceLimiter`, `IAttestationService`
- **Section 06 (Capability Model)**: `ToolPermissionProfile` resolution logic
- **Section 09 (Attestation)**: `IAttestationService` (can develop in parallel -- mock for testing)

## Architecture Context

Process sandbox is one of two keyed `ISandboxExecutor` implementations (other: `DockerSandboxExecutor` in Section 08). Registered via keyed DI on `SandboxIsolationLevel`:

```csharp
services.AddKeyedScoped<ISandboxExecutor>(SandboxIsolationLevel.Process, (sp, _) => ...);
```

Does NOT replace the existing `BatchedToolExecutionStrategy`. That handles direct tool execution in the agent loop. When tools execute within a plan, `ToolUseStepExecutor` (Section 10) routes through the sandbox.

## File Locations

### Production Code

| File | Project | Purpose |
|------|---------|---------|
| `Infrastructure.AI/Sandbox/ProcessSandboxExecutor.cs` | Infrastructure.AI | Main sandbox executor |
| `Infrastructure.AI/Sandbox/WindowsJobObjectManager.cs` | Infrastructure.AI | Win32 Job Object P/Invoke wrapper |
| `Infrastructure.AI/Sandbox/WindowsProcessResourceLimiter.cs` | Infrastructure.AI | `IProcessResourceLimiter` for Windows |
| `Infrastructure.AI/Sandbox/NoOpProcessResourceLimiter.cs` | Infrastructure.AI | Linux/non-Windows fallback |

### Test Code

| File | Project | Purpose |
|------|---------|---------|
| `Infrastructure.AI.Tests/Sandbox/ProcessSandboxExecutorTests.cs` | Tests | Unit tests |
| `Infrastructure.AI.Tests/Sandbox/WindowsJobObjectManagerTests.cs` | Tests | Platform-specific tests |
| `Infrastructure.AI.Tests/Sandbox/ProcessResourceLimiterTests.cs` | Tests | Interface mockability tests |

## Tests (Write First)

### ProcessSandboxExecutorTests.cs

```csharp
namespace Infrastructure.AI.Tests.Sandbox;

public class ProcessSandboxExecutorTests
{
    // Test: ProcessSandboxExecutor_SuccessfulExecution_ReturnsOutputAndAttestation
    //   Mock IProcessResourceLimiter (no-op), mock IAttestationService.
    //   Assert: Result.Success is true, Output and Attestation not null.

    // Test: ProcessSandboxExecutor_Timeout_KillsProcessAndReturnsFail
    //   Request Timeout = 1 second. Subprocess hangs.
    //   Assert: Result.Success is false, process killed.

    // Test: ProcessSandboxExecutor_ProcessCrash_ReturnsFailureAttestation
    //   Subprocess exits non-zero.
    //   Assert: Result.Success is false, Attestation.IsFailureAttestation is true.

    // Test: ProcessSandboxExecutor_StdinInput_SerializesAsJson
    //   Subprocess echoes stdin to stdout.
    //   Assert: Output contains original input.

    // Test: ProcessSandboxExecutor_WorkspaceCleanup_DeletesTempDir
    //   Assert: Workspace directory removed after ExecuteAsync.
}
```

### WindowsJobObjectManagerTests.cs

```csharp
namespace Infrastructure.AI.Tests.Sandbox;

public class WindowsJobObjectManagerTests
{
    // Test: WindowsJobObjectManager_CreateAndAssign_SetsResourceLimits
    // [Trait("Category", "WindowsOnly")]

    // Test: WindowsJobObjectManager_Dispose_ClosesJobHandle
    // [Trait("Category", "WindowsOnly")]

    // Test: WindowsJobObjectManager_MemoryLimit_EnforcedOnProcess
    // [Trait("Category", "WindowsOnly")]
}
```

### ProcessResourceLimiterTests.cs

```csharp
namespace Infrastructure.AI.Tests.Sandbox;

public class ProcessResourceLimiterTests
{
    // Test: IProcessResourceLimiter_Interface_CanBeMocked

    // Test: ProcessSandboxExecutor_LinuxWithoutJobObjects_ExecutesWithWarning
    //   NoOpProcessResourceLimiter logs warning, execution still succeeds.
}
```

## Implementation Details

### ProcessSandboxExecutor

Constructor: `IProcessResourceLimiter`, `IAttestationService`, `ILogger`, `TimeProvider`

**Execution flow**:
1. Create temporary workspace directory
2. Serialize tool input to JSON for stdin
3. Start subprocess with redirected I/O, `CreateNoWindow=true`
4. Apply resource limits via `IProcessResourceLimiter.ApplyLimitsAsync`
5. Write input to stdin, close stream
6. Read stdout/stderr concurrently with timeout
7. **Timeout**: Kill via Job Object + `Process.Kill(entireProcessTree: true)` backstop, create failure attestation
8. **Crash** (exit code != 0): Create failure attestation with stderr
9. **Success**: Create success attestation, capture resource usage
10. Cleanup workspace directory in finally block

### WindowsJobObjectManager

IDisposable wrapper around Win32 Job Object P/Invoke:
- `CreateJobObject`, `SetInformationJobObject`, `AssignProcessToJobObject`, `QueryInformationJobObject`
- Limit flags: `JOB_OBJECT_LIMIT_PROCESS_MEMORY`, `JOB_OBJECT_LIMIT_PROCESS_TIME`, `JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE`, `JOB_OBJECT_LIMIT_ACTIVE_PROCESS`
- Dispose closes handle, triggering `KILL_ON_JOB_CLOSE`

### WindowsProcessResourceLimiter

Uses `WindowsJobObjectManager`. Stores managers in `ConcurrentDictionary<int, WindowsJobObjectManager>` keyed by process ID.

### NoOpProcessResourceLimiter

Logs warning: "Process resource limits not available on {OS}. Use container isolation for resource enforcement." Returns `Task.CompletedTask` / zeroed `ResourceUsage`.

### Runtime Platform Selection (Section 15 DI)

```csharp
if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
    services.AddSingleton<IProcessResourceLimiter, WindowsProcessResourceLimiter>();
else
    services.AddSingleton<IProcessResourceLimiter, NoOpProcessResourceLimiter>();
```

## Cross-Platform Behavior

| Capability | Windows | Linux/macOS |
|-----------|---------|-------------|
| Subprocess execution | Yes | Yes |
| Memory limits | Job Object | No (warning) |
| CPU time limits | Job Object | No (warning) |
| Kill on close | Job Object | Process.Kill only |
| Resource usage query | QueryInformationJobObject | Zeroed |
| Full enforcement | Yes | Use container (Section 08) |

## NuGet Dependencies

None additional. `System.Diagnostics.Process` and `System.Runtime.InteropServices` are in the base framework.

## Implementation Notes (Post-Build)

### Domain Model Change
Added `Command` (string?) and `Arguments` (string?) properties to `SandboxExecutionRequest`. The executor needs to know what executable to launch; `ToolName` alone is insufficient. Falls back to `ToolName` if `Command` is null.

### Security: Defense-in-Depth AllowedPrograms Check
`ProcessSandboxExecutor.StartProcess` validates the command against `PermissionProfile.AllowedPrograms` before launching. This supplements the upstream `CapabilityEnforcer` (Section 06) — even if the enforcer is bypassed, the executor won't launch unauthorized programs. Empty `AllowedPrograms` list means no restriction (caller hasn't declared allowed programs).

### Review Fixes Applied
- **Handle leak prevention**: `WindowsProcessResourceLimiter.Apply` wraps Job Object creation in try/catch, disposing on P/Invoke failure.
- **Timeout resource tracking**: `BuildTimeoutResultAsync` now captures wall-clock elapsed time via `BuildUsage(elapsed)`.
- **Drain logging**: `DrainOutputAsync` is non-static and logs at Debug level when output drain times out.
- **Deduplicated logging**: Executor delegates warning logging to the limiter itself (NoOp logs its own warning in `Apply`).
- **Platform traits**: All 3 test classes have `[Trait("Category", "WindowsOnly")]`.

### Test Results
14 tests, all passing:
- ProcessSandboxExecutorTests: 5 tests (success, timeout, crash, stdin, cleanup)
- WindowsJobObjectManagerTests: 3 tests (create/assign, dispose, memory query)
- ProcessResourceLimiterTests: 2 tests (mock interface, NoOp behavior)
