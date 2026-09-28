# Two Authorities, One Agent: How Agent 365 And The Harness's Own Gates Compose

> Date: 2026-09-27. Target: .NET 10 (Microsoft Agentic Harness). Audience: harness engineers, security reviewers, and template consumers deciding whether to adopt an Agent 365 enforcement feature (Conditional Access on the agent identity, Purview DLP, Defender for AI). Resolves #739.

## 1. The question, and the short answer

#734 asked it plainly: if Microsoft Agent 365 can deny an action the harness would allow — or the reverse — which one wins?

**Short answer: there is no vote to arbitrate, because the two systems never adjudicate the same event at the same point in the request path.** They sit at three different checkpoints a request passes through in sequence — identity, the harness's own in-process admission pipeline, and the downstream Microsoft 365 resource the tool call actually reaches — and each checkpoint is the sole authority for the thing it controls. None of them can be bypassed by another being more permissive, so the honest composition rule is **AND**, not **OR**: every checkpoint must allow, and a "no" from any one of them is final for the thing it governs.

This holds for what Agent 365 actually does **today** (verified against Microsoft's own current documentation, 2026-09-27 — see §5 for what would change the answer). It does not require building anything: the three checkpoints already compose correctly, without new code, because of how failures already propagate.

## 2. The three checkpoints, in request order

| # | Checkpoint | Governs | Where it runs | What a "no" looks like to the harness |
| --- | --- | --- | --- | --- |
| 1 | **Microsoft Entra** (Conditional Access, agent identity lifecycle) | Whether this agent identity may authenticate **at all**, right now | Outside the harness's process, at token issuance | A credential/token acquisition failure through `AzureCredentialFactory` — the harness's tool-execution pipeline is never reached |
| 2 | **The harness's own gates** (`GovernedAIFunction`'s admission pipeline) | Whether **this specific tool call** may be attempted | In-process, synchronous, on the live tool-execution path | A governed refusal — `PendingApproval`, a policy `Block`, a progress-guard halt, a call-once refusal (see [Autonomy & Governance](03-autonomy-and-governance.html)) |
| 3 | **Microsoft Purview / Defender**, at the resource the tool call reaches | Whether **that downstream Microsoft 365 API call** (Graph, SharePoint, Teams, Exchange) succeeds | Outside the harness's process, at the M365 service boundary the credential from checkpoint 1 is presented to | A downstream `403`, an empty/redacted result, or a logged-and-allowed interaction, depending on the tenant's DLP/sensitivity-label policy |

A request that reaches checkpoint 2 already cleared checkpoint 1. A request that clears checkpoint 2 is not yet guaranteed to succeed — checkpoint 3 has not run yet, and the harness has no way to ask it in advance.

## 3. Why this is not "two votes on one decision"

Verified directly against Microsoft's current documentation for Agent 365 rather than assumed from how DLP works for other Microsoft 365 workloads, because Agent 365 GA'd in May 2026 and this area moves fast:

- **Purview DLP does not call into the agent's own execution loop.** An agent instance is enrolled in a DLP policy "as you would a user" and the supported interactions are described as **"Block or audit agent-to-human and human-to-agent for Microsoft Teams, OneDrive or SharePoint, and emails"** — i.e. enforcement happens at the Teams/SharePoint/Exchange service boundary, exactly where it already happens for a human user subject to the same policy. There is no API by which Purview pushes a "deny this specific tool call" decision back into the caller. ([Use Microsoft Purview to manage data security & compliance for Microsoft Agent 365](https://learn.microsoft.com/en-us/purview/ai-agent-365))
- **Microsoft Defender's real-time protection** is described as blocking "unsafe behaviors and malicious activity" through Microsoft's own identity/threat-response mechanisms (Conditional Access risk response, session/identity remediation) — again outside the harness's process, at checkpoint 1 or by acting on the underlying resource, not inside checkpoint 2. ([Microsoft Agent 365 overview — "Secure"](https://learn.microsoft.com/en-us/microsoft-agent-365/overview))
- **Insider Risk Management and DSPM for AI's "AI observability"** are risk-scoring and admin-facing remediation-recommendation surfaces — they inform which DLP/Defender policies exist, but are not themselves an in-line blocking call on a specific tool invocation.

So today, nothing in Agent 365 is capable of talking to `IToolInvocationGovernor`, `IToolClassificationGate`, or any other member of the harness's own admission chain — and nothing in the harness needs to ask Agent 365's permission before running a governed tool call, because Agent 365 has no synchronous "may I" API for that. The two systems are blind to each other by construction, not by an oversight this document is patching.

## 4. This is the same pattern the harness already uses for its own Purview integration — deliberately not confused with it

The harness already calls Microsoft Purview today, via `IToolClassificationGate` (see [Data Protection §Data-classification DLP](08-data-protection.html#data-classification-dlp)) — and it is easy to misread that as "the harness already handles this." It doesn't, because the two integrations are opposite shapes:

| | `IToolClassificationGate` (existing, in the harness) | Agent 365's own DLP/Defender enforcement (external, described above) |
| --- | --- | --- |
| **Direction** | **Pull.** The harness asks Purview to classify data a tool is about to touch, then decides for itself (`Off`/`Audit`/`Enforce`) | **Push-at-the-source.** Purview/Defender act on their own policy when the tool's downstream call reaches the M365 service — nothing asks the harness first |
| **Runs inside checkpoint** | 2 (the harness's own admission pipeline) | 1 or 3 (identity or downstream resource) |
| **The harness's role** | Authoritative decision-maker, using Purview's classification as one input | No role — it cannot see this happen, only its effect (a downstream failure) |

Do not treat an existing `Enforce` classification-gate deployment as already covering Agent 365's own enforcement, and do not build a new gate that tries to "ask Purview" the same way `IToolClassificationGate` does for this — that pull model is specific to the harness's own tool-level DLP feature, not a general Agent 365 integration point. There isn't one to call today.

## 5. When this decision needs revisiting

The reasoning in §2–§4 holds only as long as Agent 365's enforcement stays at checkpoints 1 and 3. **The trigger for revisiting this document is specific: Microsoft shipping a mechanism where Purview, Defender, or Entra pushes a live signal *into* the agent's own process or session** — for example, an API the harness would be expected to poll or subscribe to mid-turn, analogous to how `IToolClassificationGate` already pulls from Purview, but initiated from Microsoft's side instead of the harness's.

If that ships and a consumer wants to adopt it, the correct shape (matching every other gate in `GovernedAIFunction`'s admission chain, and the same additive contract this repo already enforces for `AIContextProvider`, `ToolBehaviorGating`, and every other governance surface — see the Common Mistakes entries in the repository's own `CLAUDE.md`) is:

- **A fifth ambient gate**, added to the existing ordered chain inside `GovernedAIFunction.InvokeCoreAsync` (today: `IToolInvocationGovernor`, `IToolClassificationGate`, `IProgressEvaluator`, `ICallOnceGate`) — never a replacement for, override of, or bypass around any of the other four.
- **Opt-in and fail-closed**, matching every other gate here: inert unless explicitly enabled, and an ambiguous or unreachable verdict from the new integration must deny, not allow — the same posture `IToolClassificationGate`'s own `Enforce` mode already takes on an `Unknown` classification being the one case that fails *open* by design (§Data-classification DLP), which was itself a deliberate, documented exception, not the default this new gate should copy.
- **Independent of everything else in the chain** — the new gate answers only "does this external system also allow this specific call," and a `PendingApproval`/`Block` from it composes with the existing four exactly as the existing four already compose with each other: any one refusal is final.

Until that ships, there is nothing to build. The correct posture today is to let the three checkpoints in §2 do what they already do — the harness's tools already treat a downstream Graph/SharePoint/Teams failure as an ordinary tool-execution failure (they have no special case for "blocked by DLP" versus any other upstream error, and need none), and a revoked or denied identity already surfaces as a credential failure before the harness's pipeline is ever reached.

## 6. What this does not decide

- **This is not guidance on whether to adopt Agent 365 enforcement features at all** — that is a tenant policy decision for the operator, informed by the threat model in [Chapter 01](01-threat-model.html).
- **This does not change anything about the harness's existing gates.** `IToolInvocationGovernor`, `IToolClassificationGate`, `IProgressEvaluator`, and `ICallOnceGate` remain exactly as authoritative as before for what they each control.
- **This is not a claim that Agent 365's identity/DLP/Defender features are sufficient on their own.** [Chapter 02's own callout](02-identity-and-access.html#agent-365-identity) already states the harness's gates are not replaced by Agent 365 export; this document explains *why* that is true architecturally, not just that it is true by policy.

Related: [Chapter 02 — Identity in the tenant directory](02-identity-and-access.html#agent-365-identity) is the reader's entry point to this document. [Chapter 03 — Autonomy & Governance](03-autonomy-and-governance.html#governance-behavior) documents the harness's own four-gate admission chain in full. [Chapter 08 — Data Protection §Data-classification DLP](08-data-protection.html#data-classification-dlp) documents the harness's *own* Purview integration, which this document deliberately distinguishes from Agent 365's.
