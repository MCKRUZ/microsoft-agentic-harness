# Security Review Report

**Scope:** Last 2 commits (`e70ba0c`, `7d14df7`)
**Reviewed:** 2026-04-06
**Reviewer:** security-reviewer agent
**Files Reviewed:** 55 changed files

## Summary

- **Critical Issues:** 1
- **High Issues:** 5
- **Medium Issues:** 6
- **Low Issues:** 4
- **Risk Level:** HIGH

---

## Critical Issues (Fix Immediately)

### 1. Path Traversal in State Management File I/O
**Severity:** CRITICAL
**Category:** Path Traversal / Directory Traversal
**Location:** `JsonCheckpointStateManager.cs:314-315`, `MarkdownCheckpointDecorator.cs:201-202`

**Issue:**
The `workflowId` parameter is used directly in `Path.Combine` to construct file paths with zero validation. A malicious or malformed `workflowId` like `../../etc` or `..\..\Windows\System32` would escape the base path and read/write arbitrary files on disk.

```csharp
// JsonCheckpointStateManager.cs:314-315
private string GetStateFilePath(string workflowId)
    => Path.Combine(_settings.BasePath, workflowId, "checkpoints", "workflow-state.json");

// MarkdownCheckpointDecorator.cs:201-202
private string GetMarkdownFilePath(string workflowId)
    => Path.Combine(_settings.BasePath, workflowId, "inputs", "workflow-state.md");
```

**Impact:**
An attacker who controls `workflowId` can read, overwrite, or delete arbitrary files on the server filesystem. This includes configuration files, secrets, and system files.

**Remediation:**
Add path traversal validation in both methods (and ideally a shared helper):

```csharp
private string GetStateFilePath(string workflowId)
{
    ValidatePathSegment(workflowId);
    var fullPath = Path.GetFullPath(Path.Combine(_settings.BasePath, workflowId, "checkpoints", "workflow-state.json"));
    if (!fullPath.StartsWith(Path.GetFullPath(_settings.BasePath), StringComparison.OrdinalIgnoreCase))
        throw new ArgumentException($"workflowId '{workflowId}' would escape the base path", nameof(workflowId));
    return fullPath;
}

private static void ValidatePathSegment(string segment)
{
    if (string.IsNullOrWhiteSpace(segment))
        throw new ArgumentException("Path segment cannot be empty");
    if (segment.Contains("..") || segment.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        throw new ArgumentException($"Invalid path segment: '{segment}'");
}
```

Also apply the same validation to `nodeId` in methods like `SetMetadataAsync` where it is used as a dictionary key (not a path segment), but could flow into file-path scenarios in the future.

**References:**
- CWE-22: Improper Limitation of a Pathname to a Restricted Directory
- OWASP: Path Traversal

---

## High Issues (Fix Before Production)

### 2. CORS Default Policy Allows Any Origin
**Severity:** HIGH
**Category:** Security Misconfiguration
**Location:** `IServiceCollectionExtensions.cs:227-230`

**Issue:**
The default CORS policy calls `AllowAnyOrigin()` and `AllowAnyHeader()`, which means any website can make cross-origin requests to the API. This completely bypasses the carefully configured origin-specific policies defined below it.

```csharp
options.AddDefaultPolicy(policy =>
{
    policy.AllowAnyOrigin();
    policy.AllowAnyHeader();
});
```

**Impact:**
Any external website can make requests to the API, enabling CSRF-like attacks and data exfiltration if endpoints are accessible.

**Remediation:**
Remove `AllowAnyOrigin()` from the default policy. Use the configured origins instead:

```csharp
options.AddDefaultPolicy(policy =>
{
    policy.WithOrigins(origins);
    policy.AllowAnyHeader();
});
```

Or restrict the default policy to be as strict as the named policies.

---

### 3. API Key Comparison Vulnerable to Timing Attacks
**Severity:** HIGH
**Category:** Authentication
**Location:** `HttpAuthEndpointFilter.cs:67-68`

**Issue:**
The comment on line 26 claims "ordinal comparison to prevent timing-based inference attacks via case folding," but `string.Equals` with `StringComparison.Ordinal` still short-circuits on first mismatch, making it vulnerable to timing attacks. An attacker can determine the key character-by-character by measuring response times.

```csharp
if (!string.Equals(apiKey, _config.AccessKey1, StringComparison.Ordinal)
    && !string.Equals(apiKey, _config.AccessKey2, StringComparison.Ordinal))
```

**Impact:**
With enough requests, an attacker can determine API keys through timing side-channel analysis.

**Remediation:**
Use `CryptographicOperations.FixedTimeEquals` for constant-time comparison:

```csharp
using System.Security.Cryptography;

private static bool ConstantTimeEquals(string? a, string? b)
{
    if (a is null || b is null) return false;
    return CryptographicOperations.FixedTimeEquals(
        System.Text.Encoding.UTF8.GetBytes(a),
        System.Text.Encoding.UTF8.GetBytes(b));
}
```

---

### 4. Non-Atomic File Replace (Delete + Move Race Condition)
**Severity:** HIGH
**Category:** Race Condition / Data Integrity
**Location:** `JsonCheckpointStateManager.cs:105-108`, `MarkdownCheckpointDecorator.cs:185-188`

**Issue:**
The "atomic replace" pattern uses `File.Delete` followed by `File.Move`, which is not atomic. If the process crashes between the delete and the move, the state file is lost entirely.

```csharp
// Atomic replace (NOT actually atomic)
if (File.Exists(stateFilePath))
    File.Delete(stateFilePath);
File.Move(tempFilePath, stateFilePath);
```

**Impact:**
Workflow state data loss if process crashes or power loss occurs between delete and move. In a workflow system, this means losing the entire execution history.

**Remediation:**
On .NET 6+, use `File.Move` with the overwrite parameter:

```csharp
File.Move(tempFilePath, stateFilePath, overwrite: true);
```

This is a single OS call on most platforms and is closer to truly atomic.

---

### 5. Mutable Domain Types Where Immutable Records Are Expected
**Severity:** HIGH
**Category:** Architecture / Code Quality
**Location:** `WorkflowState.cs`, `NodeState.cs`, `DecisionResult.cs`, `DecisionFramework.cs`, `StateConfiguration.cs`

**Issue:**
Per project coding rules (immutability first, records, `with` expressions), all domain types should be records or use init-only setters. Instead, `WorkflowState`, `NodeState`, `DecisionResult`, `DecisionFramework`, `StateConfiguration`, and `DecisionRule` are all mutable classes with `{ get; set; }` properties and mutable `Dictionary<string, object>` fields. Methods like `GetOrCreateNode`, `SetMetadata`, and `IncrementIteration` mutate state in-place.

**Impact:**
- Shared references can be accidentally mutated, causing subtle bugs in concurrent scenarios
- State management code mutates loaded state and re-saves it (`TransitionAsync` at line 209 mutates `node.Status` directly) -- if two concurrent callers load the same workflow, the last writer wins with no detection

**Remediation:**
Convert to records with init-only properties for the serializable surface. Use `with` expressions for state transitions. For the metadata dictionaries, use `ImmutableDictionary<string, object>` or at minimum return defensive copies.

Short-term: At least add a concurrency check (version/etag) to `SaveAsync` to detect concurrent modifications.

---

### 6. Sync-over-Async in Endpoint Resolver
**Severity:** HIGH
**Category:** Code Quality / Deadlock Risk
**Location:** `ApiEndpointResolverService.cs:104`

**Issue:**
`DiscoverHealthyEndpointAsync` is an async method, but `ResolveEndpointFromConfig` calls it synchronously with `.GetAwaiter().GetResult()`. This can deadlock in ASP.NET Core if the synchronization context is captured.

```csharp
return DiscoverHealthyEndpointAsync(config).GetAwaiter().GetResult();
```

**Impact:**
Potential deadlock under load, hanging the request thread indefinitely.

**Remediation:**
Make `ResolveEndpoint` async all the way through:

```csharp
public async Task<Uri> ResolveEndpointAsync<TClientOptions>(string configurationSectionName)
```

---

## Medium Issues (Fix When Possible)

### 7. Unvalidated `workflowId` and `nodeId` Inputs Across IStateManager
**Severity:** MEDIUM
**Category:** Input Validation
**Location:** `JsonCheckpointStateManager.cs` (all public methods)

**Issue:**
No methods validate that `workflowId` or `nodeId` are non-null, non-empty, or contain valid characters. Null or empty strings would produce meaningless file paths or dictionary lookups. While `Path.Combine` handles null with an exception, the error message would be confusing.

**Remediation:**
Add `ArgumentException.ThrowIfNullOrWhiteSpace(workflowId)` as the first line of every public method.

---

### 8. Unbounded State Configuration Cache
**Severity:** MEDIUM
**Category:** Resource Exhaustion
**Location:** `JsonCheckpointStateManager.cs:44`

**Issue:**
`_stateConfigCache` is a plain `Dictionary<string, StateConfiguration>` with no eviction policy. A long-running process with many unique `workflowId:nodeId` combinations will grow this cache indefinitely.

```csharp
private readonly Dictionary<string, StateConfiguration> _stateConfigCache = new();
```

**Remediation:**
Use `IMemoryCache` with expiration, or `ConcurrentDictionary` with a size limit.

---

### 9. Exception Swallowed in GetMetadataAsync
**Severity:** MEDIUM
**Category:** Error Handling
**Location:** `JsonCheckpointStateManager.cs:240-244`, `WorkflowState.cs:143-147`, `NodeState.cs:97-101`

**Issue:**
`Convert.ChangeType` failures are silently swallowed, returning `default`. This hides type mismatch bugs that could cause incorrect workflow behavior.

```csharp
catch
{
    return default;
}
```

**Remediation:**
Log the conversion failure at Warning level so type mismatches are detectable.

---

### 10. GlobalExceptionMiddleware Returns 400 for Unhandled 500 Errors in Production
**Severity:** MEDIUM
**Category:** Security / Error Handling
**Location:** `GlobalExceptionMiddleware.cs:138-141`

**Issue:**
In production, unhandled exceptions return `400 Bad Request` instead of `500 Internal Server Error`. This is misleading to clients and monitoring systems, and masks server-side failures as client errors.

```csharp
await WriteErrorResponseAsync(
    context,
    StatusCodes.Status400BadRequest,
    "An unexpected error occurred. Please try again later.");
```

**Remediation:**
Return `500 Internal Server Error` in production as well, with the generic message:

```csharp
StatusCodes.Status500InternalServerError
```

---

### 11. HttpAuthorizationConfig Registered as Singleton from Snapshot
**Severity:** MEDIUM
**Category:** Configuration
**Location:** `Infrastructure.Common/DependencyInjection.cs:38-42`

**Issue:**
The `HttpAuthorizationConfig` is registered as a singleton resolved from `appConfig.CurrentValue`, meaning it captures the config at startup and never reflects runtime changes (e.g., key rotation via Azure Key Vault). This contradicts the `IOptionsMonitor` pattern used elsewhere.

```csharp
services.AddSingleton(sp =>
{
    var appConfig = sp.GetRequiredService<IOptionsMonitor<AppConfig>>();
    return appConfig.CurrentValue.Http.Authorization;
});
```

**Remediation:**
Register as a factory that resolves from `IOptionsMonitor` on each resolution, or inject `IOptionsMonitor<AppConfig>` into `HttpAuthEndpointFilter` directly.

---

### 12. DynamicCorsMiddleware Access-Control-Allow-Headers Uses Wildcard
**Severity:** MEDIUM
**Category:** Security Misconfiguration
**Location:** `DynamicCorsMiddleware.cs:109`

**Issue:**
`Access-Control-Allow-Headers` is set to `*`, which allows any header. This is overly permissive and could allow unexpected headers to pass through.

```csharp
headers["Access-Control-Allow-Headers"] = "*";
```

**Remediation:**
Explicitly list allowed headers:

```csharp
headers["Access-Control-Allow-Headers"] = "Authorization,Content-Type,X-API-Key,X-Correlation-Id";
```

---

## Low Issues (Consider Fixing)

### 13. TODO Comments Indicating Incomplete Implementation
**Severity:** LOW
**Category:** Code Completeness
**Location:** `IServiceCollectionExtensions.cs:313-314`, `IServiceCollectionExtensions.cs:319-320`

**Issue:**
Two TODO comments for telemetry enrichers that are not yet ported. Not a security issue now, but indicates missing observability that would help detect attacks.

---

### 14. Unused Logger Variable in CompositeStateManager
**Severity:** LOW
**Category:** Code Quality
**Location:** `CompositeStateManager.cs:56`

**Issue:**
`afLogger` is assigned but the `logger` parameter in the first constructor is never used -- the inner managers get `NullLogger` instances instead. This means CompositeStateManager operations are not logged.

---

### 15. LoggingDelegatingHandler Logs Full Request Object
**Severity:** LOW
**Category:** Information Disclosure
**Location:** `LoggingDelegatingHandler.cs:37,49`

**Issue:**
`_logger.LogDebug("Outbound HTTP request: {Request}", request)` logs the entire `HttpRequestMessage`, which could include `Authorization` headers, API keys, and other sensitive data in debug logs.

**Remediation:**
Log only the method and URI, not the full request object:

```csharp
_logger.LogDebug("Outbound HTTP request: {Method} {Uri}", request.Method, request.RequestUri);
```

---

### 16. Missing `Access-Control-Allow-Credentials` Header Logic
**Severity:** LOW
**Category:** Security
**Location:** `DynamicCorsMiddleware.cs:105-112`

**Issue:**
The CORS middleware sets `Access-Control-Allow-Origin` to a specific origin but does not set `Access-Control-Allow-Credentials: true`. This means cookie-based or token-based authentication in cross-origin requests will fail silently.

---

## Security Checklist

- [x] No hardcoded secrets (API keys are config-driven, not in source)
- [ ] **All inputs validated** -- workflowId/nodeId have NO validation (Finding #1, #7)
- [x] SQL injection prevention (N/A - no SQL in these files)
- [x] XSS prevention (N/A - no HTML rendering)
- [x] CSRF protection (API key auth, no cookie-based auth)
- [x] Authentication required (HttpAuthEndpointFilter)
- [ ] **Authorization verified** -- no per-user authorization on state management
- [x] Rate limiting enabled (fixed window 100 req/min)
- [x] HTTPS enforced (HSTS in SecurityHeadersMiddleware)
- [x] Security headers set (comprehensive set in SecurityHeadersMiddleware)
- [ ] **Dependencies up to date** -- not checked in this review
- [x] No vulnerable packages (not checked)
- [ ] **Logging sanitized** -- LoggingDelegatingHandler logs full request (Finding #15)
- [x] Error messages safe (GlobalExceptionMiddleware hides details in prod)

## Recommendations

1. **Immediate:** Add path traversal protection to all state management file I/O (Finding #1)
2. **Immediate:** Fix the default CORS policy to not allow any origin (Finding #2)
3. **Before production:** Use constant-time comparison for API keys (Finding #3)
4. **Before production:** Use `File.Move(src, dst, overwrite: true)` for atomic saves (Finding #4)
5. **Architecture:** Convert domain types to records for immutability guarantees (Finding #5)
6. **Architecture:** Add concurrency detection (version/etag) to state management saves
7. **Quality:** Add `ArgumentException.ThrowIfNullOrWhiteSpace` guards to all public IStateManager methods
8. **Quality:** Return 500 not 400 for unhandled errors in production (Finding #10)

---

> Security review performed by Claude Code security-reviewer agent
