# Section 08: Docker Sandbox Executor

## Overview

This section implements `DockerSandboxExecutor`, a container-based sandbox executor using the Docker.DotNet SDK. It provides the elevated isolation tier for tools that require stronger security boundaries than process-level isolation can offer. The executor manages the full container lifecycle: creation, execution, timeout enforcement, output collection, and cleanup. It enforces a critical security invariant: tools declaring `MinimumIsolation = Container` must never be downgraded to process isolation when Docker is unavailable.

**Key file paths:**

| File | Project | Purpose |
|------|---------|---------|
| `src/Content/Infrastructure/Infrastructure.AI/Sandbox/DockerSandboxExecutor.cs` | Infrastructure.AI | Container-based ISandboxExecutor implementation |
| `src/Content/Application/Application.AI.Common/Models/Sandbox/SandboxOptions.cs` | Application.AI.Common | Configuration model for sandbox |
| `src/Content/Tests/Infrastructure.AI.Tests/Sandbox/DockerSandboxExecutorTests.cs` | Infrastructure.AI.Tests | Unit tests (14 tests) |

## Dependencies

- **Section 01 (Domain Models)**: `SandboxIsolationLevel`, `ToolCapability`, `ToolPermissionProfile`, `ResourceLimits`, `ToolExecutionAttestation`, `SandboxExecutionRequest`, `SandboxExecutionResult`, `ResourceUsage`
- **Section 02 (Application Interfaces)**: `ISandboxExecutor`, `IAttestationService`
- **Section 06 (Capability Model)**: `ToolPermissionProfile` resolution; `MinimumIsolation` determines container requirement
- **Section 07 (Process Sandbox)**: `ProcessSandboxExecutor` is the fallback tier
- **Section 09 (Attestation)**: `IAttestationService` consumed for signing results

## NuGet Dependency

Added to `src/Directory.Packages.props`:
```xml
<PackageVersion Include="Docker.DotNet" Version="3.125.15" />
```

Added to `Infrastructure.AI.csproj`:
```xml
<PackageReference Include="Docker.DotNet" />
```

---

## Implementation Details

### Class: `DockerSandboxExecutor`

Implements `ISandboxExecutor`, keyed DI with `SandboxIsolationLevel.Container`.

**Constructor dependencies:**
- `IDockerClient` -- Docker.DotNet SDK, auto-discovers daemon
- `IAttestationService` -- signing execution results
- `IOptionsMonitor<SandboxOptions>` -- default image, per-tool overrides, allowed image prefixes
- `ILogger<DockerSandboxExecutor>`

### Execution Flow

1. **Docker availability check** -- `PingAsync()`. If unavailable:
   - `MinimumIsolation = Container`: refuse with hard error (security boundary)
   - Otherwise: return descriptive error for caller to handle fallback

2. **Workspace preparation** -- Temp directory, write `input.json`, bind-mount into container

3. **Image resolution + pull** -- Resolve image from per-tool override or default. Validate against `AllowedImagePrefixes`. Pull if not available locally (`InspectImage` + conditional `CreateImage`).

4. **Container creation** -- `CreateContainerAsync` with:
   - Image from per-tool override or `SandboxOptions.Container.DefaultImage`
   - `User = "65534:65534"` (nobody — prevents running as root)
   - `HostConfig.Memory` from `ResourceLimits.MemoryLimitBytes`
   - `HostConfig.NetworkMode = "none"` (default) or `"bridge"` if `NetworkAccess` capability
   - `HostConfig.ReadonlyRootfs = true`
   - `HostConfig.AutoRemove = false` (manual removal after log collection)
   - `HostConfig.SecurityOpt = ["no-new-privileges:true"]`
   - `HostConfig.CapDrop = ["ALL"]`
   - `Binds: ["{workspaceDir}:/workspace:rw"]`
   - `PidsLimit` from `ResourceLimits.MaxSubprocesses`
   - **NanoCPUs removed** -- timeout enforces wall-clock CPU limit

5. **Container start** -- `StartContainerAsync`

6. **Wait with timeout** -- `WaitContainerAsync` with linked CancellationTokenSource. On timeout: `StopContainerAsync` with grace period

7. **Output collection** -- `GetContainerLogsAsync` (stdout + stderr via `ReadOutputToEndAsync` tuple). Check for `output.json` in workspace

8. **Attestation** -- `SignAsync` on success, `SignFailureAsync` on crash/timeout

9. **Cleanup** -- Explicit `RemoveContainerAsync(force: true)` in finally block, then delete workspace

### Security Invariant: No Downgrade from Container

When `MinimumIsolation = Container` and Docker is unavailable, execution is REFUSED. Not downgraded. Not retried at process tier. This is a hard security boundary. The caller (ToolUseStepExecutor, Section 10) must handle this refusal.

### Container Hardening

- `User = "65534:65534"` — runs as nobody, not root
- `CapDrop = ["ALL"]` — drops all Linux capabilities
- `SecurityOpt = ["no-new-privileges:true"]` — prevents privilege escalation
- `ReadonlyRootfs = true` — prevents writes outside bind mount
- `NetworkMode = "none"` — default no network access

### Image Resolution + Validation

1. Per-tool override: `SandboxOptions.ToolOverrides[toolName].ContainerImage`
2. Default: `SandboxOptions.Container.DefaultImage`
3. Validation: If `AllowedImagePrefixes` is non-empty, image must match at least one prefix
4. Pull: `InspectImageAsync` to check local availability, `CreateImageAsync` if missing

### Configuration Model: `SandboxOptions`

```csharp
public sealed class SandboxOptions
{
    public const string SectionName = "AI:Sandbox";
    public ContainerSandboxOptions Container { get; init; } = new();
    public IReadOnlyDictionary<string, ToolSandboxOverride> ToolOverrides { get; init; } = new Dictionary<string, ToolSandboxOverride>();
}

public sealed class ContainerSandboxOptions
{
    public string DefaultImage { get; init; } = "mcr.microsoft.com/dotnet/runtime:10.0";
    public string? DockerEndpoint { get; init; }
    public int StopGracePeriodSeconds { get; init; } = 10;
    public IReadOnlyList<string> AllowedImagePrefixes { get; init; } = [];
}

public sealed class ToolSandboxOverride
{
    public string? ContainerImage { get; init; }
}
```

### IDockerClient Registration (Section 15)

```csharp
services.AddSingleton<IDockerClient>(_ =>
    new DockerClientConfiguration(
        string.IsNullOrEmpty(sandboxOptions.Container?.DockerEndpoint)
            ? null
            : new Uri(sandboxOptions.Container.DockerEndpoint))
    .CreateClient());
```

Auto-discovers: `npipe://./pipe/docker_engine` (Windows), `unix:///var/run/docker.sock` (Linux).

### Error Handling

| Docker Error | Result |
|-------------|--------|
| Image not found (local) | Pull attempted, fail if registry unavailable |
| Image not in allowlist | `InvalidOperationException` with descriptive message |
| Daemon error | `Success = false`, daemon error message |
| Daemon unreachable | Docker unavailable path (security invariant) |
| Timeout | Stop container, failure attestation |
| Exit code != 0 | `Success = false`, stderr, failure attestation |

## Tests (14 passing)

| Test | Verifies |
|------|----------|
| SuccessfulExecution_ReturnsOutputAndAttestation | Happy path |
| Timeout_StopsContainer | Timeout → StopContainerAsync called |
| NetworkNone_DefaultConfig | Default network isolation |
| NetworkAccess_OverridesNetworkMode | Bridge when NetworkAccess capability |
| MemoryLimit_PassedToHostConfig | Memory limit mapping |
| ReadonlyRootfs_Enabled | Filesystem security |
| SecurityHardening_Applied | User, CapDrop, SecurityOpt |
| DockerUnavailable_MinIsolationContainer_Refuses | No-downgrade invariant |
| DockerUnavailable_NoMinIsolation_ReturnsUnavailable | Fallback signal |
| WorkspaceMount_BindsCorrectly | Bind mount format |
| CommandAndArguments_MappedToCmd | Cmd construction |
| ToolOverrideImage_UsesOverrideImage | Per-tool image config |
| ImageNotLocal_PullsImage | Image pull on cache miss |
| ContainerRemoved_AfterExecution | Manual cleanup (not AutoRemove) |

## Implementation Notes (Post-Build)

### Review Fixes Applied
- **AutoRemove removed**: Switched to manual `RemoveContainerAsync(force: true)` in finally block. Prevents race between Docker auto-cleanup and log retrieval.
- **Container hardening**: Added `User="65534:65534"`, `CapDrop=["ALL"]`, `SecurityOpt=["no-new-privileges:true"]` to prevent container-as-root escapes.
- **NanoCPUs removed**: `CpuTimeSeconds` is a time budget, not a CPU quota. NanoCPUs conversion was semantically wrong. Timeout alone enforces wall-clock limit.
- **Image pull with cache check**: `InspectImageAsync` → catch `DockerImageNotFoundException` → `CreateImageAsync`. Prevents opaque failures on fresh hosts.
- **Image allowlist validation**: `AllowedImagePrefixes` config property. When non-empty, only matching images can be used (defense against config injection).
- **Cmd construction cleanup**: Removed redundant null/empty filtering with sloppy list; use conditional construction instead.

## Integration Points

- **Section 10**: `ToolUseStepExecutor` resolves via keyed DI, handles Docker unavailable fallback
- **Section 15**: DI registration with `SandboxIsolationLevel.Container` key
- **Section 16**: `SandboxOptions.Container` configuration section
- **Section 09**: `IAttestationService` called after execution
