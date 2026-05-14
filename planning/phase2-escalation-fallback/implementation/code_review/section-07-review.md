# Code Review: Section 07 -- Resilience Interfaces

**Reviewer:** claude-code-reviewer
**Date:** 2026-05-09
**Scope:** 2 new files (pure interfaces, 88 lines total, no implementations, no tests)
**Verdict:** **Approve** -- no CRITICAL or HIGH issues. 2 MEDIUM items and 1 LOW suggestion.

---

## MEDIUM -- IResilientChatClientProvider.cs missing using Domain.AI.Resilience directive

**File:** IResilientChatClientProvider.cs:1-2

The file has only one using directive (Microsoft.Extensions.AI), but the XML doc references Domain.AI.Resilience.FallbackMetadata using the fully-qualified namespace path. Compare with IProviderHealthMonitor.cs, which imports using Domain.AI.Resilience at the top and then uses the short form ProviderHealthState in its XML docs.

The section plan explicitly lists both Microsoft.Extensions.AI and Domain.AI.Resilience as required using directives for this file. The fully-qualified see cref will compile, but it is inconsistent with the pattern used across every other interface in this project (e.g., IGovernancePolicyEngine.cs imports Domain.AI.Governance and uses short cref names).

**Fix:** Add using Domain.AI.Resilience and change the fully-qualified cref to the short form FallbackMetadata.

**Recommendation:** Fix for consistency. Every other interface in the codebase imports domain namespaces and uses short cref names. Template consumers will learn the wrong pattern from this file.

---

## MEDIUM -- OnCircuitStateChanged event uses Action of string and ProviderHealthState -- consider a custom delegate

**File:** IProviderHealthMonitor.cs:57

The section plan acknowledges this choice and explains the rationale (avoid EventArgs allocation). However, Action of two generic parameters has a readability issue: at the call site, the lambda parameters give no IDE hint about what they represent. The parameter names are invisible in the delegate signature.

A custom delegate (CircuitStateChangedHandler with named parameters providerName and newState) would be lightweight and self-documenting. The section plan explicitly chose Action for simplicity. The XML doc on the event member does describe the parameters clearly. This is adequate for a template project where docs are teaching material.

**Recommendation:** Acceptable as-is given the plan rationale. A custom delegate would be slightly better for discoverability, but it is not a blocking issue. Decide based on preference.

---

## LOW -- GetProviderHealth returns Healthy for unknown providers

**File:** IProviderHealthMonitor.cs:36

The plan documents this decision: the assume-full-capability-if-not-declared pattern from section-14 (ProviderCapabilityRegistry). This is internally consistent.

However, this differs from the typical unknown-equals-error convention. A caller passing a misspelled provider name will silently get Healthy instead of an indication that the provider is not registered. This could mask configuration bugs.

**Alternative:** Return nullable ProviderHealthState for unknown providers. Callers who want the assume-healthy behavior can coalesce at the call site.

**Counter-argument:** The plan intentionally chose non-nullable to eliminate defensive null checks in the retry queue hot path (section-15). The retry queue calls this on every drain cycle, and a null check there adds noise.

**Recommendation:** Keep as-is. The plan reasoning is sound for the hot-path use case, and the assume-healthy pattern is consistent with section-14. The XML doc clearly states the behavior. Informational only.

---

## Positive Observations

1. **Clean Architecture boundary is respected.** Both interfaces reference only Domain.AI.Resilience types and Microsoft.Extensions.AI. No Polly types, no infrastructure concerns. The Polly mapping is described in XML docs (teaching material) but does not leak into the contract.

2. **XML documentation is excellent.** The remarks sections on both interfaces explain design rationale (why no synthetic probes, why separate from IChatClientFactory, what happens when resilience is disabled). This is exactly the right level for a template project.

3. **Separation from IChatClientFactory is well-justified.** The IResilientChatClientProvider remarks section clearly explains the semantic difference (give me a client for a specific provider vs give me a client spanning all providers with fallback). This prevents the common mistake of trying to decorate the existing factory.

4. **IReadOnlyDictionary return type on GetAllProviderHealth.** Follows the immutability conventions established across the codebase.

5. **Namespace placement is correct.** Application.AI.Common.Interfaces.Resilience matches the folder path and follows the existing subfolder pattern (Governance/, MetaHarness/, Escalation/).

6. **Diff matches the section plan exactly.** Interface signatures, XML docs, and member ordering are 1:1 with the plan. The only deviation is the missing using Domain.AI.Resilience in IResilientChatClientProvider (covered above).

7. **CancellationToken parameter naming** uses ct, consistent with the newer interface convention established in section 06 and the MCP interfaces.

---

## Plan Conformance Check

| Plan requirement | Status |
|-----------------|--------|
| IResilientChatClientProvider in Interfaces/Resilience/ | Met |
| IProviderHealthMonitor in Interfaces/Resilience/ | Met |
| Returns Task of IChatClient (not custom type) | Met |
| Single method on IResilientChatClientProvider | Met |
| Uses Microsoft.Extensions.AI.IChatClient | Met |
| References Domain.AI.Resilience types only | Met (FallbackMetadata via fully-qualified cref, ProviderHealthState via using) |
| No RegisterProvider/SetState on IProviderHealthMonitor | Met |
| Event-based state change notification | Met |
| using Domain.AI.Resilience on IResilientChatClientProvider | **Deviation** -- fully-qualified cref used instead |

---

## Summary

| Priority | Count | Action |
|----------|-------|--------|
| CRITICAL | 0 | -- |
| HIGH | 0 | -- |
| MEDIUM | 2 | Add using Domain.AI.Resilience to IResilientChatClientProvider and use short cref name; Action delegate vs custom delegate is a preference call |
| LOW | 1 | Healthy-for-unknown behavior is documented and intentional |

**Verdict: Approve.** The missing using directive is the only concrete fix needed. The delegate question is a style preference with no functional impact. Both interfaces are clean, well-documented, and correctly positioned in the architecture.
