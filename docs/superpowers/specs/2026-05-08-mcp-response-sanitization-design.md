# MCP Response Sanitization Layer

**Date:** 2026-05-08
**Status:** Draft
**Branch:** feat/agt-governance-integration
**OWASP MCP Coverage:** MCP01 (Secret Exposure), MCP06 (Intent Flow Subversion), MCP10 (Context Over-Sharing)

## Problem

The harness governs tool call **inputs** (GovernancePolicyBehavior, PromptInjectionBehavior) but does not sanitize tool **outputs** before they re-enter the LLM. A compromised or misconfigured MCP tool can leak credentials, inject prompt-override instructions, or embed exfiltration URLs in its response. The AGT blog post identifies `McpResponseSanitizer` and `McpCredentialRedactor` as the controls for this gap.

## Approach

Strategy-per-concern with composite pattern. Three focused `IResponseSanitizer` implementations chained by a `CompositeResponseSanitizer`. Integrated at two layers: MediatR post-execution behavior (for CQRS-mediated calls) and MCP client decorator (for direct MCP calls). Configurable threshold determines whether findings cause redact-and-continue or full response blocking.

## 1. Domain Models

**Project:** `Domain.AI/Governance/`

### SanitizationCategory (enum)

```csharp
public enum SanitizationCategory
{
    None,
    CredentialLeak,
    PromptInjection,
    ExfiltrationUrl
}
```

### SanitizationFinding (record)

Individual finding from any sanitizer.

```csharp
public sealed record SanitizationFinding(
    SanitizationCategory Category,
    ThreatLevel ThreatLevel,
    string Description,
    int StartIndex,
    int Length,
    double Confidence);
```

### SanitizationResult (record)

Aggregate result from one or more sanitizers.

```csharp
public sealed record SanitizationResult(
    bool WasSanitized,
    string SanitizedContent,
    string OriginalContent,
    IReadOnlyList<SanitizationFinding> Findings,
    ThreatLevel HighestThreatLevel)
{
    public static SanitizationResult Clean(string content) =>
        new(false, content, content, [], ThreatLevel.None);

    public static SanitizationResult WithFindings(
        string sanitizedContent,
        string originalContent,
        IReadOnlyList<SanitizationFinding> findings) =>
        new(true, sanitizedContent, originalContent, findings,
            findings.Max(f => f.ThreatLevel));
}
```

## 2. Application Interfaces

**Project:** `Application.AI.Common/Interfaces/Governance/`

### IResponseSanitizer

Each concern implements this interface.

```csharp
public interface IResponseSanitizer
{
    SanitizationCategory Category { get; }
    SanitizationResult Sanitize(string content, string? toolName = null);
}
```

### ICompositeResponseSanitizer

Chains multiple sanitizers, merges findings.

```csharp
public interface ICompositeResponseSanitizer
{
    SanitizationResult Sanitize(string content, string? toolName = null);
}
```

### GovernanceConfig additions

Two new properties on the existing `GovernanceConfig`:

```csharp
public bool EnableResponseSanitization { get; init; } = true;
public ThreatLevel ResponseBlockThreshold { get; init; } = ThreatLevel.Critical;
```

`EnableResponseSanitization` defaults to `true` (active whenever governance is enabled). `ResponseBlockThreshold` follows the same pattern as `InjectionBlockThreshold`.

## 3. Infrastructure Implementations

**Project:** `Infrastructure.AI.Governance/Adapters/`

All implementations are `internal sealed partial` classes using `GeneratedRegex`, following `McpSecurityScannerAdapter` conventions.

### CredentialRedactor : IResponseSanitizer

Regex-based secret detection and redaction. Matches are replaced with `[REDACTED:{type}]`.

| Pattern | Type Tag | ThreatLevel | Confidence |
|---------|----------|-------------|------------|
| `AKIA[0-9A-Z]{16}` | `aws_key` | High | 0.95 |
| `DefaultEndpointsProtocol=...AccountKey=...` | `azure_connection_string` | High | 0.95 |
| `eyJ[A-Za-z0-9_-]{10,}\.eyJ[A-Za-z0-9_-]{10,}` | `jwt` | High | 0.90 |
| `ghp_[A-Za-z0-9]{36}` | `github_pat` | High | 0.95 |
| `sk-[A-Za-z0-9]{20,}` | `api_key` | High | 0.90 |
| `xoxb-[0-9]{10,}-[A-Za-z0-9]+` | `slack_token` | High | 0.95 |
| `-----BEGIN (RSA\|EC\|DSA)? ?PRIVATE KEY-----` | `private_key` | High | 0.95 |
| `(password\|secret\|token\|api_key)\s*[=:]\s*\S+` | `generic_secret` | High | 0.70 |
| `Basic [A-Za-z0-9+/]{10,}={0,2}` (Authorization header) | `basic_auth` | High | 0.85 |

### ResponseInjectionScrubber : IResponseSanitizer

Detects prompt injection patterns embedded in tool output. Strips injection content, replaces with `[SANITIZED:injection]`.

| Pattern | ThreatLevel | Confidence |
|---------|-------------|------------|
| Zero-width / invisible Unicode characters | Critical | 0.95 |
| `<system>` / `</system>` tags | Critical | 0.95 |
| Instruction-override language (ignore/override/disregard previous) | High | 0.85 |
| Role-switching (`assistant:`, `system:`, `user:`) at line start | High | 0.80 |
| Markdown-hidden instructions (`<!-- ... -->` with directive language) | High | 0.80 |
| Base64-encoded blocks >40 chars (same as McpSecurityScannerAdapter) | Medium | 0.60 |

Reuses regex families from `McpSecurityScannerAdapter` where applicable.

### ExfiltrationUrlDetector : IResponseSanitizer

Detects URLs designed to exfiltrate data. Replaces with `[REDACTED:exfiltration_url]`.

| Pattern | ThreatLevel | Confidence |
|---------|-------------|------------|
| Known exfiltration services (ngrok, requestbin, pipedream, webhook.site, burpcollaborator) | High | 0.90 |
| URLs with base64-encoded query params >40 chars | Medium | 0.75 |
| IP-address URLs with URL-encoded payloads | Medium | 0.70 |
| `data:` URIs with encoded content | High | 0.85 |

Legitimate URLs (well-known domains, short query strings) pass through untouched.

### CompositeResponseSanitizer : ICompositeResponseSanitizer

- Injected with `IEnumerable<IResponseSanitizer>` via DI
- Fixed execution order: CredentialRedactor -> ResponseInjectionScrubber -> ExfiltrationUrlDetector
- Each sanitizer operates on the previous sanitizer's `SanitizedContent`
- Merges all findings into a single `SanitizationResult`
- `HighestThreatLevel` is the max across all findings
- Measures total duration for OTel histogram

## 4. Pipeline Integration

### Layer 1: MediatR Behavior

**Project:** `Application.AI.Common/MediatRBehaviors/`

`ResponseSanitizationBehavior<TRequest, TResponse>` — pipeline position 9.5.

```
Pipeline order:
  ...
  7.0  GovernancePolicyBehavior  (pre-execution: policy check)
  7.5  PromptInjectionBehavior   (pre-execution: input injection scan)
  8.0  ContentSafetyBehavior     (pre-execution: LLM-based content screening)
  ...  [handler executes]
  9.5  ResponseSanitizationBehavior (post-execution: output sanitization)
```

Activation gates:
- Request implements `IToolRequest`
- `GovernanceConfig.Enabled == true`
- `GovernanceConfig.EnableResponseSanitization == true`

Response handling requires extracting tool output from `TResponse`. New marker interface:

```csharp
public interface IToolResponse
{
    string ToolOutput { get; }
    IToolResponse WithSanitizedOutput(string sanitizedOutput);
}
```

Tool command responses that carry output implement `IToolResponse`. The behavior checks `if (response is IToolResponse toolResponse)`, sanitizes, and returns `toolResponse.WithSanitizedOutput(result.SanitizedContent)` cast back to `TResponse`. Implementations of `WithSanitizedOutput` must return the same concrete type (e.g. `ToolExecutionResult`) so the cast succeeds.

Threshold logic:
- `HighestThreatLevel >= ResponseBlockThreshold` -> return `Result.GovernanceBlocked` with finding details
- Below threshold -> replace content with sanitized version, audit-log findings
- Clean -> pass through

### Layer 2: MCP Client Decorator

For direct MCP tool calls that bypass MediatR. Extension method or decorator on the MCP client call path:

```csharp
public static async Task<string> CallToolSanitizedAsync(
    this IMcpClient client,
    ICompositeResponseSanitizer sanitizer,
    string toolName,
    Dictionary<string, object> args,
    GovernanceConfig config)
```

Same threshold logic as the MediatR behavior. Ensures tool output is sanitized regardless of entry point.

### DI Registration

Extends `Infrastructure.AI.Governance.DependencyInjection`:

```csharp
// In AddGovernanceDependencies:
services.AddSingleton<IResponseSanitizer, CredentialRedactor>();
services.AddSingleton<IResponseSanitizer, ResponseInjectionScrubber>();
services.AddSingleton<IResponseSanitizer, ExfiltrationUrlDetector>();
services.AddSingleton<ICompositeResponseSanitizer, CompositeResponseSanitizer>();

// In AddGovernanceNoOpDependencies:
services.AddSingleton<ICompositeResponseSanitizer, NoOpResponseSanitizer>();
```

`NoOpResponseSanitizer` returns `SanitizationResult.Clean(content)` for all inputs.

### OTel Metrics

New instruments on `GovernanceMetrics`:

| Metric | Type | Tags |
|--------|------|------|
| `agent.governance.response.sanitizations` | Counter | `agent.governance.sanitization.category`, `agent.governance.tool` |
| `agent.governance.response.blocks` | Counter | `agent.governance.threat_level`, `agent.governance.tool` |
| `agent.governance.response.sanitization_duration` | Histogram (ms) | - |

New constants on `GovernanceConventions`:

```csharp
public const string ResponseSanitizations = "agent.governance.response.sanitizations";
public const string ResponseBlocks = "agent.governance.response.blocks";
public const string SanitizationDuration = "agent.governance.response.sanitization_duration";
public const string SanitizationCategoryTag = "agent.governance.sanitization.category";
```

## 5. Testing

### Unit Tests

**`Infrastructure.AI.Governance.Tests/Adapters/`**

| Test Class | Coverage |
|-----------|----------|
| `CredentialRedactorTests` | Each pattern: AWS, Azure, JWT, GitHub PAT, Slack, PEM, generic, basic auth. Non-matches (normal text). Partial matches. Multi-match in single content. |
| `ResponseInjectionScrubberTests` | `<system>` tags, role-switching, zero-width, instruction-override, markdown-hidden, base64. Clean text passthrough. |
| `ExfiltrationUrlDetectorTests` | Known services, base64 query params, IP URLs, `data:` URIs. Legitimate URLs pass through (github.com, docs.microsoft.com). |
| `CompositeResponseSanitizerTests` | Chaining order verified (credential redaction before injection scrub). Finding accumulation. HighestThreatLevel calculation. Empty/null input. All-clean passthrough. |

**`Application.AI.Common.Tests/MediatRBehaviors/`**

| Test Class | Coverage |
|-----------|----------|
| `ResponseSanitizationBehaviorTests` | Skip non-tool requests. Skip when disabled. Redact-and-continue below threshold. Block at/above threshold. Audit logging on findings. Metrics emission. |

### Integration Test

One end-to-end test: tool response containing an AWS key + injection pattern flows through the full MediatR pipeline. Verifies both are caught, response is sanitized, audit is logged, metrics are emitted.

Naming convention: `MethodName_Scenario_ExpectedResult`.

## File Inventory

| Layer | File | Action |
|-------|------|--------|
| Domain | `Domain.AI/Governance/SanitizationCategory.cs` | Create |
| Domain | `Domain.AI/Governance/SanitizationFinding.cs` | Create |
| Domain | `Domain.AI/Governance/SanitizationResult.cs` | Create |
| Application | `Application.AI.Common/Interfaces/Governance/IResponseSanitizer.cs` | Create |
| Application | `Application.AI.Common/Interfaces/Governance/ICompositeResponseSanitizer.cs` | Create |
| Application | `Application.AI.Common/Interfaces/MediatR/IToolResponse.cs` | Create |
| Application | `Application.AI.Common/MediatRBehaviors/ResponseSanitizationBehavior.cs` | Create |
| Application | `Application.AI.Common/OpenTelemetry/Metrics/GovernanceMetrics.cs` | Edit (add 3 instruments) |
| Domain | `Domain.AI/Telemetry/Conventions/GovernanceConventions.cs` | Edit (add 4 constants) |
| Domain | `Domain.Common/Config/AI/GovernanceConfig.cs` | Edit (add 2 properties) |
| Infrastructure | `Infrastructure.AI.Governance/Adapters/CredentialRedactor.cs` | Create |
| Infrastructure | `Infrastructure.AI.Governance/Adapters/ResponseInjectionScrubber.cs` | Create |
| Infrastructure | `Infrastructure.AI.Governance/Adapters/ExfiltrationUrlDetector.cs` | Create |
| Infrastructure | `Infrastructure.AI.Governance/Adapters/CompositeResponseSanitizer.cs` | Create |
| Infrastructure | `Infrastructure.AI.Governance/Adapters/NoOpAdapters.cs` | Edit (add NoOpResponseSanitizer) |
| Infrastructure | `Infrastructure.AI.Governance/DependencyInjection.cs` | Edit (register new services) |
| Tests | `Infrastructure.AI.Governance.Tests/Adapters/CredentialRedactorTests.cs` | Create |
| Tests | `Infrastructure.AI.Governance.Tests/Adapters/ResponseInjectionScrubberTests.cs` | Create |
| Tests | `Infrastructure.AI.Governance.Tests/Adapters/ExfiltrationUrlDetectorTests.cs` | Create |
| Tests | `Infrastructure.AI.Governance.Tests/Adapters/CompositeResponseSanitizerTests.cs` | Create |
| Tests | `Application.AI.Common.Tests/MediatRBehaviors/ResponseSanitizationBehaviorTests.cs` | Create |

**13 new files, 5 edits. Zero new dependencies.**
