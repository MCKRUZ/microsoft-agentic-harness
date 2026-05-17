# Section 09: Attestation Service

## Overview

This section implements `HmacAttestationService`, the infrastructure service that HMAC-SHA256-signs tool execution results (or failure records) to create tamper-evident proofs. It also implements `EfCoreAttestationStore` for persisting attestations alongside plan step execution state. Key material is sourced from User Secrets (dev) or Azure Key Vault (prod) -- never plaintext in `appsettings.json`. Key rotation is supported via a versioned keychain where old keys remain available for verification while new attestations use the current key.

Both sandbox executors (Section 07: Process, Section 08: Docker) call `IAttestationService.SignAsync` / `SignFailureAsync` after each execution. The `ToolUseStepExecutor` (Section 10) calls `IAttestationService.VerifyAsync` before accepting tool output into the plan. Attestations are persisted as JSON columns on `StepExecutionStateEntity` (Section 04: EF Core Persistence).

### Dependencies

- **Section 01 (Domain Models)**: `ToolExecutionAttestation` record in `Domain.AI/Attestation/`
- **Section 02 (Application Interfaces)**: `IAttestationService` and `IAttestationStore` in `Application.AI.Common/Interfaces/Attestation/`

### What This Section Blocks

- **Section 10 (Step Executors)**: `ToolUseStepExecutor` depends on `IAttestationService.VerifyAsync` to validate tool output before it flows into agent reasoning.

### Parallel With

Sections 03, 04, 05, 06, 07, 08 -- all can proceed independently.

---

## Tests First

All test files in `src/Content/Tests/Infrastructure.AI.Tests/Attestation/`.

### Test File: `src/Content/Tests/Infrastructure.AI.Tests/Attestation/HmacAttestationServiceTests.cs`

```csharp
// Namespace: Infrastructure.AI.Tests.Attestation
// Dependencies: xUnit, FluentAssertions, Moq
// Mocks: IOptionsMonitor<AttestationKeyOptions> for providing key material

// Test: HmacAttestationService_Sign_ProducesValidSignature
//   Arrange: Create service with a known HMAC key (e.g., 32-byte test key, version "v1").
//            Input: toolName = "calculator", input = "{\"a\":1}", output = "{\"result\":2}"
//   Act: Call SignAsync(toolName, input, output, ct)
//   Assert: Returned ToolExecutionAttestation has:
//     - ToolName == "calculator"
//     - InputHash is non-null, 64-char hex string (SHA-256)
//     - OutputHash is non-null, 64-char hex string (SHA-256)
//     - Signature is non-null, non-empty (Base64-encoded HMAC)
//     - KeyVersion == "v1"
//     - IsFailureAttestation == false
//     - FailureReason is null
//     - Timestamp is recent (within last 5 seconds)

// Test: HmacAttestationService_Verify_AcceptsValidSignature
//   Arrange: Sign a tool execution (same as above).
//   Act: Call VerifyAsync(attestation, ct) with the returned attestation
//   Assert: Returns true

// Test: HmacAttestationService_Verify_RejectsTamperedOutput
//   Arrange: Sign a tool execution, then create a new attestation record with a modified OutputHash
//            (change one character in the hex string).
//   Act: Call VerifyAsync(tamperedAttestation, ct)
//   Assert: Returns false

// Test: HmacAttestationService_Verify_RejectsTamperedInput
//   Arrange: Sign a tool execution, then create a new attestation with a modified InputHash.
//   Act: Call VerifyAsync(tamperedAttestation, ct)
//   Assert: Returns false

// Test: HmacAttestationService_Verify_RejectsTamperedTimestamp
//   Arrange: Sign a tool execution, then create a new attestation with Timestamp shifted by 1 second.
//   Act: Call VerifyAsync(tamperedAttestation, ct)
//   Assert: Returns false

// Test: HmacAttestationService_FailureAttestation_SignsWithNullOutputHash
//   Arrange: Create service with known key.
//   Act: Call SignFailureAsync(toolName, input, failureReason: "OOM kill", ct)
//   Assert: Returned attestation has:
//     - OutputHash is null
//     - IsFailureAttestation == true
//     - FailureReason == "OOM kill"
//     - InputHash is non-null (the input WAS provided, we just have no output)
//     - Signature is non-null (HMAC of ToolName|InputHash|null|Timestamp)
//     - VerifyAsync returns true for this attestation

// Test: HmacAttestationService_KeyRotation_OldKeyStillVerifies
//   Arrange: Create service with keychain: [{ key: keyA, version: "v1" }, { key: keyB, version: "v2" }]
//            where "v2" is the current version.
//            Sign an attestation while "v1" was current (manually construct with v1 key).
//   Act: Call VerifyAsync(v1Attestation, ct) -- service now uses v2 for signing but v1 is in the keychain
//   Assert: Returns true (old key is retained for verification)

// Test: HmacAttestationService_KeyRotation_NewAttestationsUseCurrentKey
//   Arrange: Create service with keychain where "v2" is the current version.
//   Act: Call SignAsync(...)
//   Assert: Returned attestation has KeyVersion == "v2"

// Test: HmacAttestationService_KeyFromUserSecrets_NotFromAppsettings
//   This is a design verification test, not a unit test.
//   Arrange: Create AttestationKeyOptions with only HmacKeys populated (no appsettings path).
//   Act: Attempt to resolve HmacAttestationService from DI
//   Assert: Service resolves correctly.
//   NOTE: The actual User Secrets / Key Vault sourcing is validated by the DI/config
//         integration in Section 16. This test verifies the options binding works
//         and that the service does not have a hardcoded key fallback.
//   Alternative: Assert that constructing HmacAttestationService with empty HmacKeys
//                throws ArgumentException("At least one HMAC key must be configured").
```

---

## Implementation Details

### File Inventory

| File | Project | Purpose |
|------|---------|---------|
| `src/Content/Infrastructure/Infrastructure.AI/Attestation/HmacAttestationService.cs` | Infrastructure.AI | `IAttestationService` implementation |
| `src/Content/Infrastructure/Infrastructure.AI/Attestation/EfCoreAttestationStore.cs` | Infrastructure.AI | `IAttestationStore` implementation |
| `src/Content/Infrastructure/Infrastructure.AI/Attestation/AttestationKeyOptions.cs` | Infrastructure.AI | Options POCO for key configuration |
| `src/Content/Infrastructure/Infrastructure.AI/Attestation/AttestationKeyOptionsValidator.cs` | Infrastructure.AI | `IValidateOptions<AttestationKeyOptions>` for hot-reload validation |
| `src/Content/Tests/Infrastructure.AI.Tests/Attestation/HmacAttestationServiceTests.cs` | Tests | Unit tests (11 tests) |

### AttestationKeyOptions

**File: `src/Content/Infrastructure/Infrastructure.AI/Attestation/AttestationKeyOptions.cs`**

Strongly-typed options POCO bound from configuration. Bound via `IOptionsMonitor<AttestationKeyOptions>` for hot-reload support (key rotation without restart).

Properties:
- `HmacKeys: IReadOnlyList<HmacKeyEntry>` -- ordered list of keys, newest first. At least one entry required.
- `CurrentKeyVersion: string` -- version identifier of the key to use for new signatures. Must match a `Version` in `HmacKeys`.

Where `HmacKeyEntry` is:
- `Version: string` -- unique identifier (e.g., "v1", "2024-01-15")
- `Key: string` -- Base64-encoded HMAC-SHA256 key (minimum 32 bytes decoded)

Configuration source: The key values themselves come from User Secrets (`dotnet user-secrets set "Attestation:HmacKeys:0:Key" "<base64>"`) or Azure Key Vault (referenced via the existing `KeyVaultConfig` integration in the codebase). The `appsettings.json` file may contain the structure (versions, which is current) but NEVER the actual key material. This follows the existing project security conventions in `CLAUDE.md` and `security.md`.

Example User Secrets shape:
```json
{
  "Attestation": {
    "CurrentKeyVersion": "v1",
    "HmacKeys": [
      { "Version": "v1", "Key": "base64encodedkey..." }
    ]
  }
}
```

### HmacAttestationService

**File: `src/Content/Infrastructure/Infrastructure.AI/Attestation/HmacAttestationService.cs`**

Implements `IAttestationService` (defined in Section 02 at `Application.AI.Common/Interfaces/Attestation/IAttestationService.cs`).

**Constructor dependencies:**
- `IOptionsMonitor<AttestationKeyOptions>` -- for key material and rotation
- `TimeProvider` -- for deterministic timestamps in tests
- `ILogger<HmacAttestationService>`

**Constructor validation:**
- Throw `ArgumentException` if `HmacKeys` is null or empty
- Throw `ArgumentException` if `CurrentKeyVersion` does not match any entry in `HmacKeys`
- Log warning if any key in `HmacKeys` decodes to fewer than 32 bytes

**`SignAsync` implementation:**

1. Get current key entry from `IOptionsMonitor<AttestationKeyOptions>.CurrentValue` (reads latest on each call for hot-reload support).
2. Compute `InputHash = SHA256(input)` as lowercase hex string (64 chars).
3. Compute `OutputHash = SHA256(output)` as lowercase hex string.
4. Get `Timestamp = TimeProvider.GetUtcNow()`.
5. Build signing payload: `"{ToolName}|{InputHash}|{OutputHash}|{Timestamp:O}"` -- pipe-delimited, ISO 8601 timestamp for deterministic formatting.
6. Compute HMAC-SHA256 of the payload using the current key. Convert to Base64 string.
7. Return `ToolExecutionAttestation` record with all fields populated, `IsFailureAttestation = false`.

Use `System.Security.Cryptography.HMACSHA256` and `System.Security.Cryptography.SHA256` -- both are available in .NET 10 without additional packages. Use the static `SHA256.HashData(byte[])` and `HMACSHA256.HashData(byte[], byte[])` methods for allocation-free hashing where possible.

**`SignFailureAsync` implementation:**

Same as `SignAsync` except:
- `OutputHash` is null (no output to hash).
- Includes a SHA-256 hash of `FailureReason` in the signing payload for tamper-evidence.
- Signing payload: `"{ToolName}|{InputHash}|null|{FailureReasonHash}|{Timestamp:O}"` -- 5 pipe-delimited fields.
- `IsFailureAttestation = true`, `FailureReason` populated.

**`VerifyAsync` implementation:**

1. Look up the key entry matching `attestation.KeyVersion` from the keychain. If not found, log warning and return `false`.
2. Build the same signing payload from the attestation fields:
   - If `attestation.IsFailureAttestation`: `"{ToolName}|{InputHash}|null|{SHA256(FailureReason)}|{Timestamp:O}"`
   - Else: `"{ToolName}|{InputHash}|{OutputHash}|{Timestamp:O}"`
3. Recompute HMAC-SHA256 using the matched key.
4. Compare with `CryptographicOperations.FixedTimeEquals()` to prevent timing side-channel attacks.
5. Return `true` if match, `false` otherwise.

**Key rotation behavior:**
- `IOptionsMonitor` reloads when configuration changes. No restart needed.
- `SignAsync` always uses `CurrentKeyVersion` from the latest options.
- `VerifyAsync` searches all keys in the keychain, matching by `attestation.KeyVersion`.
- Old keys are never removed from the keychain until their attestations expire or are no longer relevant.

### EfCoreAttestationStore

**File: `src/Content/Infrastructure/Infrastructure.AI/Attestation/EfCoreAttestationStore.cs`**

Implements `IAttestationStore` (defined in Section 02).

This is a thin wrapper around `PlannerDbContext` (Section 04). Attestations are stored as JSON columns on `StepExecutionStateEntity`, so this store queries through the existing entity model.

**Constructor dependencies:**
- `IDbContextFactory<PlannerDbContext>` -- factory pattern for singleton-safe context creation
- `ILogger<EfCoreAttestationStore>`

**Method implementations:**

- `SaveAsync(PlanStepId, ToolExecutionAttestation, ct)`: Load `StepExecutionStateEntity` by step ID, set `AttestationJson` column, save changes. Return `Result.Fail` if entity not found.
- `GetByStepAsync(PlanStepId, ct)`: Load entity, deserialize `AttestationJson`. Return null if no attestation.
- `GetByPlanAsync(PlanId, ct)`: Query all `StepExecutionStateEntity` for the plan, filter where `AttestationJson` is not null, deserialize each.

The store uses `IDbContextFactory<PlannerDbContext>` (not injected `PlannerDbContext`) because attestation operations may be called from singleton-scoped services. Each method creates and disposes its own context via `await using var context = await _factory.CreateDbContextAsync(ct)`.

---

## Security Considerations

1. **No hardcoded keys**: The service validates on construction that keys come from options binding. There is no fallback to a default key. If no key is configured, the service throws at construction time, which surfaces immediately at startup.

2. **Timing-safe comparison**: `VerifyAsync` uses `CryptographicOperations.FixedTimeEquals` to compare HMAC values. Standard `==` or `SequenceEqual` on byte arrays leaks timing information that could reveal partial key material.

3. **Key minimum length**: Log a warning (not an error) if a key is shorter than 32 bytes. HMAC-SHA256 works with any key length but shorter keys reduce security margin.

4. **Signing payload is deterministic**: The pipe-delimited format with ISO 8601 timestamps ensures the same inputs always produce the same payload. No ambient state or random nonces -- the HMAC itself provides authentication.

5. **Failure attestations prove attempts**: Even when execution crashes, the attestation records what input was provided and when the attempt was made. This prevents "we never ran that tool" repudiation.

---

## Integration Points

### Consumed By (Downstream)

- **Section 07 (Process Sandbox)**: `ProcessSandboxExecutor` constructor takes `IAttestationService`. Calls `SignAsync` on success, `SignFailureAsync` on timeout/crash.
- **Section 08 (Docker Sandbox)**: `DockerSandboxExecutor` constructor takes `IAttestationService`. Same signing pattern.
- **Section 10 (Step Executors)**: `ToolUseStepExecutor` calls `VerifyAsync` before accepting tool output. If verification fails, the step fails with a tamper-detection error.

### Consumes (Upstream)

- **Section 01**: `ToolExecutionAttestation` record type.
- **Section 02**: `IAttestationService` and `IAttestationStore` interface contracts.
- **Section 04**: `PlannerDbContext` and `StepExecutionStateEntity` for persistence (via `IDbContextFactory`).

### DI Registration (Section 15)

```csharp
services.AddScoped<IAttestationService, HmacAttestationService>();
services.AddScoped<IAttestationStore, EfCoreAttestationStore>();
services.Configure<AttestationKeyOptions>(configuration.GetSection("Attestation"));
```

### Configuration (Section 16)

The `Attestation` section in configuration. Key values from User Secrets or Key Vault, structure from `appsettings.json`:

```json
{
  "Attestation": {
    "CurrentKeyVersion": "v1",
    "HmacKeys": [
      { "Version": "v1", "Key": "FROM_USER_SECRETS_OR_KEYVAULT" }
    ]
  }
}
```

---

## Implementation Order

1. Create `AttestationKeyOptions.cs` with `HmacKeyEntry` (the options POCO).
2. Write all 9 test stubs in `HmacAttestationServiceTests.cs`.
3. Implement `HmacAttestationService` -- `SignAsync` first, then `SignFailureAsync`, then `VerifyAsync`.
4. Run tests: `dotnet test src/Content/Tests/Infrastructure.AI.Tests/ --filter "FullyQualifiedName~Attestation"`.
5. Implement `EfCoreAttestationStore` (depends on Section 04 being complete for `PlannerDbContext`). If Section 04 is not yet implemented, stub the store or defer -- `HmacAttestationService` has no dependency on the store.
6. Verify build: `dotnet build src/AgenticHarness.slnx`.

---

## Deviations from Original Plan

1. **FailureReason included in signed payload** -- Original plan used `"{ToolName}|{InputHash}|null|{Timestamp:O}"` for failure attestations. Changed to include `SHA256(FailureReason)` as a 5th field for tamper-evidence. Without this, FailureReason could be modified post-signing.

2. **Added `AttestationKeyOptionsValidator.cs`** -- Not in original plan. Implements `IValidateOptions<AttestationKeyOptions>` to validate configuration on hot-reload, not just at construction time.

3. **Key material zeroing** -- Added `CryptographicOperations.ZeroMemory` on decoded key bytes after HMAC computation. Defense-in-depth for key hygiene.

4. **Base64 error handling in VerifyAsync** -- Added try-catch around `Convert.FromBase64String` to return `false` instead of throwing on malformed input.

5. **11 tests total** (vs 9 planned) -- Added `Verify_ReturnsFalse_WhenKeyVersionRetired` and `Verify_RejectsTamperedFailureReason`.
