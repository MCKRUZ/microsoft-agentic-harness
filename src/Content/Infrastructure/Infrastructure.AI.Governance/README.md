# Infrastructure.AI.Governance

## What This Project Is

Infrastructure.AI.Governance is the security enforcement layer for the agentic harness. It integrates the Microsoft Agent Governance Toolkit (AGT) to provide four critical safety capabilities: policy-based tool access control (can this agent use this tool?), prompt injection detection (is the user trying to hijack the agent?), tamper-evident audit logging (who did what, and can we prove the log hasn't been altered?), and MCP tool security scanning (is this externally-provided tool description trying to attack the agent?).

The problem it solves in business terms: autonomous AI agents can be manipulated, misused, or compromised. Without governance, there is no way to enforce organizational policies on agent behavior, detect attacks on agent inputs, maintain compliance audit trails, or vet dynamically-loaded tools from untrusted MCP servers.

This project depends on Application.AI.Common (for governance interfaces) and the `Microsoft.AgentGovernance` NuGet package (the AGT runtime). It is referenced by Presentation hosts that conditionally wire governance based on configuration. When governance is disabled, no-op implementations satisfy DI without adding overhead.

**Analogy:** If the agent is an employee, this project is the compliance department -- it enforces policies, detects social engineering attacks, maintains audit records, and vets new tools before the employee can use them.

## Architecture Context

```
Microsoft.AgentGovernance (NuGet)
  GovernanceKernel
  PolicyEngine                  Application.AI.Common
  PromptInjectionDetector         IGovernancePolicyEngine
  AuditLogger                     IPromptInjectionScanner
       |                          IGovernanceAuditService
       v                          IMcpSecurityScanner
+----------------------------------------------+
|       Infrastructure.AI.Governance            |
|                                               |
|  GovernanceKernel (singleton)                 |
|       |                                       |
|  Adapters:                                    |
|    AgtPolicyEngineAdapter --> IGovernance...   |
|    AgtPromptInjectionAdapter --> IPrompt...    |
|    AgtAuditAdapter --> IGovernanceAudit...     |
|    McpSecurityScannerAdapter --> IMcpSec...    |
|                                               |
|  NoOp fallbacks (when disabled):              |
|    NoOpPolicyEngine, NoOpInjectionScanner,    |
|    NoOpAuditService, NoOpMcpScanner           |
+----------------------------------------------+
         ^
         |
  Presentation composition root:
    if (config.Governance.Enabled)
        services.AddGovernanceDependencies(config);
    else
        services.AddGovernanceNoOpDependencies();
```

## Key Concepts

### The Adapter Pattern

**What it is:** Each governance capability is wrapped in an adapter class that translates between the AGT SDK's types and the harness-owned interfaces defined in Application.AI.Common.

**Why it exists:** The harness must not depend directly on AGT types throughout the codebase. If AGT changes its API surface in v4, only the adapter layer needs updating. The rest of the system programs against stable harness interfaces.

**The four adapters:**

| Adapter | Wraps (AGT) | Implements (Harness) |
|---------|-------------|---------------------|
| `AgtPolicyEngineAdapter` | `PolicyEngine` | `IGovernancePolicyEngine` |
| `AgtPromptInjectionAdapter` | `PromptInjectionDetector` | `IPromptInjectionScanner` |
| `AgtAuditAdapter` | `AuditLogger` | `IGovernanceAuditService` |
| `McpSecurityScannerAdapter` | (standalone) | `IMcpSecurityScanner` |

### Policy Engine

**What it is:** Evaluates whether a specific agent action (typically a tool call) is permitted under the organization's governance policies.

**Why it exists:** Organizations need to control what agents can do -- blocking destructive operations, requiring approval for sensitive actions, rate-limiting expensive calls, or logging all actions for compliance.

**How it works:**
1. Policies are defined in YAML files and loaded at startup from `GovernanceConfig.PolicyPaths`.
2. When `EvaluateToolCall(agentId, toolName, arguments)` is called, the engine matches against loaded rules.
3. The adapter maps AGT's decision to the harness `GovernanceDecision` type, which includes: Allowed/Denied, action type (Allow/Deny/Warn/RequireApproval/Log/RateLimit), reason, matched rule name, evaluation latency, and optional approver list.
4. Every evaluation emits OpenTelemetry metrics: `GovernanceMetrics.Decisions`, `GovernanceMetrics.Violations`, `GovernanceMetrics.RateLimitHits`, and `GovernanceMetrics.EvaluationDuration`.

```csharp
var decision = _policyEngine.EvaluateToolCall(
    agentId: "agent-main",
    toolName: "file_system",
    arguments: new Dictionary<string, object> { ["operation"] = "delete" });

if (!decision.IsAllowed)
    // Block the tool call, return the reason to the agent
```

### Prompt Injection Detection

**What it is:** Scans user input text for patterns that indicate an attempt to manipulate the agent's instructions.

**Why it exists:** Prompt injection is the #1 attack vector against LLM-powered agents. Users (or compromised data sources) can embed instructions like "ignore previous instructions and..." to hijack agent behavior. This scanner catches these patterns before the input reaches the LLM.

**How it works:**
1. `Scan(input)` passes the text through AGT's `PromptInjectionDetector`.
2. If injection is detected, the result includes: injection type (DirectOverride, IndirectPayload, etc.), threat level, confidence score, matched patterns, and explanation.
3. Detections emit `GovernanceMetrics.InjectionDetections` counter.

```csharp
var result = _injectionScanner.Scan(userMessage);
if (result.IsInjection)
    // Block the message, log the attempt, notify security
```

### MCP Security Scanner

**What it is:** Analyzes MCP tool descriptions and schemas for attack patterns before the agent can use them.

**Why it exists:** MCP tools come from external servers that may be compromised or malicious. A tool's description is sent to the LLM as part of the prompt -- a malicious description can contain hidden instructions (tool poisoning), invisible Unicode characters, base64-encoded payloads, prompt injection patterns, or typosquatting names designed to impersonate trusted tools.

**How it works (standalone, not AGT-backed):**
1. `ScanTool(name, description, schema)` runs four regex-based scans:
   - **Tool Poisoning:** Detects instruction-override language ("ignore previous", "disregard system")
   - **Hidden Instructions:** Detects zero-width Unicode characters and base64-encoded blocks
   - **Description Injection:** Detects prompt injection patterns ("you are", "act as", "system prompt")
   - **Typosquatting:** Detects Cyrillic lookalikes and special Unicode in tool names
2. Each detected threat includes type, severity level, description, and confidence score.
3. Emits `GovernanceMetrics.McpScans` and `GovernanceMetrics.McpThreats` counters.

### Audit Logging

**What it is:** A tamper-evident (hash-chained) log of all governance decisions.

**Why it exists:** Compliance requires proving what decisions were made, by whom, and that the log hasn't been modified after the fact. AGT's `AuditLogger` maintains a hash chain where each entry's hash includes the previous entry's hash.

**How it works:**
- `Log(agentId, action, decision)` appends to the chain
- `VerifyChainIntegrity()` validates the full hash chain is unbroken
- `EntryCount` provides the current log size
- Every log emits `GovernanceMetrics.AuditEvents`

### No-Op Implementations

**What they are:** Lightweight implementations that satisfy DI requirements when governance is disabled.

**Why they exist:** The harness interfaces are consumed throughout the codebase. Code that calls `_policyEngine.EvaluateToolCall()` shouldn't need null checks. No-ops return "allowed" for policy checks, "clean" for injection scans, "safe" for MCP scans, and are no-ops for audit logging.

## Data Flow

```
User message arrives
       |
       v
[IPromptInjectionScanner.Scan(message)]
       |
  (if injection detected) --> Block + log
       |clean
       v
Agent requests tool call
       |
       v
[IGovernancePolicyEngine.EvaluateToolCall(agent, tool, args)]
       |
  (if denied) --> Block + audit log
       |allowed
       v
[IGovernanceAuditService.Log(agent, tool, "allowed")]
       |
       v
Tool executes normally


MCP server connects with new tools
       |
       v
[IMcpSecurityScanner.ScanTools(tools)]
       |
  (if threats detected) --> Quarantine tool + alert
       |safe
       v
Tools added to agent's available set
```

## Project Structure

```
Infrastructure.AI.Governance/
├── Adapters/
│   ├── AgtPolicyEngineAdapter.cs       Wraps AGT PolicyEngine
│   ├── AgtPromptInjectionAdapter.cs    Wraps AGT PromptInjectionDetector
│   ├── AgtAuditAdapter.cs             Wraps AGT AuditLogger
│   ├── McpSecurityScannerAdapter.cs    Standalone MCP security scanning
│   └── NoOpAdapters.cs                All no-op implementations in one file
├── Policies/                           YAML policy files (copied to output)
├── DependencyInjection.cs              Conditional registration (real vs no-op)
└── Infrastructure.AI.Governance.csproj
```

## Key Types Reference

| Type | Purpose | Implements | Lifetime |
|------|---------|-----------|----------|
| `AgtPolicyEngineAdapter` | Policy evaluation with metrics | `IGovernancePolicyEngine` | Singleton |
| `AgtPromptInjectionAdapter` | Injection detection with metrics | `IPromptInjectionScanner` | Singleton |
| `AgtAuditAdapter` | Hash-chained audit logging | `IGovernanceAuditService` | Singleton |
| `McpSecurityScannerAdapter` | MCP tool vetting | `IMcpSecurityScanner` | Singleton |
| `NoOpPolicyEngine` | Passthrough (disabled) | `IGovernancePolicyEngine` | Singleton |
| `NoOpInjectionScanner` | Always clean (disabled) | `IPromptInjectionScanner` | Singleton |
| `NoOpAuditService` | No-op (disabled) | `IGovernanceAuditService` | Singleton |
| `NoOpMcpScanner` | Always safe (disabled) | `IMcpSecurityScanner` | Singleton |

## Configuration

```jsonc
{
  "AppConfig": {
    "AI": {
      "Governance": {
        "Enabled": true,                    // false = register no-ops instead
        "PolicyPaths": [                    // YAML policy files to load
          "Policies/default-policy.yaml",
          "Policies/production-policy.yaml"
        ],
        "EnableAudit": true,                // Enable hash-chained audit logging
        "EnableMetrics": true,              // Emit OTel governance metrics
        "EnablePromptInjectionDetection": true,  // Enable injection scanning
        "ConflictStrategy": "MostRestrictive"    // How to resolve conflicting policies
      }
    }
  }
}
```

Policy YAML files are configured as `<None Include="Policies/**/*.yaml" CopyToOutputDirectory="PreserveNewest" />` in the csproj, so they deploy alongside the binary.

## Common Tasks

### How to Add a New Governance Policy

1. Create a YAML file in the `Policies/` folder following AGT's policy schema.
2. Add the filename to `GovernanceConfig.PolicyPaths` in appsettings.
3. The policy is loaded at startup by `GovernanceKernel` via resolved paths.
4. Or load dynamically at runtime: `_policyEngine.LoadPolicyFile(path)`.

### How to Debug Policy Evaluation

1. Check `GovernanceMetrics.Decisions` counter with the `governance.action` tag to see allow/deny distribution.
2. The `GovernanceDecision` includes `MatchedRuleName` and `Reason` -- log these at the call site.
3. `GovernanceMetrics.EvaluationDuration` histogram reveals if policy evaluation is a latency bottleneck.
4. For injection false positives, check `InjectionScanResult.MatchedPatterns` and `Confidence` to tune thresholds.

### How to Disable Governance for Development

Set `AppConfig.AI.Governance.Enabled = false` in appsettings.Development.json. The composition root will call `AddGovernanceNoOpDependencies()` and all checks become passthrough.

## Dependencies

**Project References:**
- `Application.AI.Common` -- `IGovernancePolicyEngine`, `IPromptInjectionScanner`, `IGovernanceAuditService`, `IMcpSecurityScanner` interfaces; `GovernanceMetrics` OTel instruments

**NuGet Packages:**
- `Microsoft.AgentGovernance` (v3.0.2) -- The Agent Governance Toolkit runtime providing `GovernanceKernel`, `PolicyEngine`, `PromptInjectionDetector`, `AuditLogger`

**Note:** Extension packages for Microsoft.Agents and MCP (`Microsoft.AgentGovernance.Extensions.Microsoft.Agents` / `.ModelContextProtocol`) do not yet exist on NuGet. The adapter wiring for those surfaces is hand-rolled until packages publish.

## Testing

- **Test project:** `Infrastructure.AI.Governance.Tests` (declared via `InternalsVisibleTo`)
- **Run:** `dotnet test --filter "FullyQualifiedName~Infrastructure.AI.Governance.Tests"`
- **Mock guidance:** Use `NoOp` implementations for tests that don't need governance. For integration tests, create a `GovernanceKernel` with test policy YAML files. The `McpSecurityScannerAdapter` is stateless and can be tested directly with crafted tool descriptions containing known attack patterns.
