# Gating Tools By Declared Behaviour

> Date: 2026-08-10. Target: .NET 10 (Microsoft Agentic Harness). Audience: harness engineers and template consumers who run agents with tools they did not all write themselves.

## 1. Executive summary

Tool governance in the harness works from **lists of names** — a plugin's allow and deny lists, per-agent tool authorization, declarative YAML policy. Every one of them requires somebody to enumerate the dangerous tools correctly, and to keep doing so forever, including for tools that arrive at runtime from an MCP server nobody on the team wrote. A tool nobody thought to list is callable by default, and a name carries no behaviour: `create_page` and `search_pages` look identical to a name-based rule, and one of them writes.

The harness can now invert that default. With **`AI:Governance:ToolBehaviorGating:RequireApprovalForNonReadOnlyTools`** switched on, any tool that has not declared itself read-only requires human approval before it runs. A new mutating tool appearing on an upstream server is gated the moment it appears, with nobody editing a list.

The posture is **off by default**, and applies through the existing tool governor — so it also requires `AI:Governance:EnforceToolInvocation`. Turning one on without the other **fails host startup** rather than silently doing nothing.

## 2. Where the declaration comes from

Two sources, and the difference between them is the whole security argument.

**First-party tools** declare themselves through `ITool.IsReadOnly`, which already existed for concurrency classification and graded autonomy. It defaults to `false` — assume it writes — so a tool that never thought about the question is gated rather than exempt.

**External MCP tools** declare themselves through the protocol's tool annotations: `readOnlyHint`, `destructiveHint`, `idempotentHint`, `openWorldHint`. These arrive on the definition a server advertises and were previously discarded. They are captured now at discovery by `BehaviorRecordingMcpToolProvider`, a decorator over `IMcpToolProvider` that sits **outside** the security scanner, so only tools that survived the definition scan are recorded.

Discovery is the only moment the information exists. By the time a tool call arrives, all that remains is a name; the annotations are not reachable from it.

## 3. The trust rule, and why it is not simply "believe the annotation"

A tool annotation is supplied by the party the annotation is used to police. The MCP specification says so directly — annotations "are not guaranteed to provide a faithful description of tool behavior", and clients "should never make tool use decisions based on `ToolAnnotations` received from untrusted servers". A server that wants past an approval gate marks its destructive tool read-only.

The reference implementation this feature was modelled on (`CopilotKit/OpenTag`'s `WriteConfirmationInterceptor`) trusts the hint unconditionally. The harness does not. The rule here is:

> **A declaration is believed when it tightens. It is believed when it loosens only from a source entitled to be believed.**

Nobody lies in the strict direction, so `destructiveHint: true` is honoured from every server. Only `readOnlyHint` — the one that removes friction — needs provenance:

| Source | A read-only claim… |
| --- | --- |
| First-party tool (`ITool.IsReadOnly`) | Exempts. Authored in the host's own codebase. |
| MCP server with `TrustToolAnnotations: true` | Exempts. The operator has assessed this publisher. |
| MCP server without it (**the default**) | Does **not** exempt. Recorded and reported; the tool still needs approval. |
| Nothing on record | Does not exempt. Unknown is never safe. |

`TrustToolAnnotations` is a per-server setting under `AI:McpServers:Servers:<name>`. Connecting a server is a decision about where its tools come from; this is the separate decision about whether to take its word for what they do.

Two further rules the model enforces:

- **A destructive claim outranks a read-only one.** The combination is incoherent, and an incoherent declaration is a reason to distrust the declarer rather than to pick the convenient half.
- **When two *different* sources describe the same tool name, the one that does not exempt wins.** A hostile server cannot loosen a tool by shadowing a name a stricter source already claimed, in either arrival order.
- **A server revising its own tool is always believed, in both directions.** That is what catches a server which advertised a clean read-only tool and later advertises it as a writer — and it is also the only way granting `TrustToolAnnotations` to a server that was already running can take effect. A rule that kept the stricter record unconditionally would pin the earlier untrusted entry forever and the operator's change would silently do nothing until a restart.

## 4. Where the check runs

Inside `IToolInvocationGovernor`, as a third source of "a human must rule on this" alongside the permission layer's `Ask` and the policy engine's `RequireApproval`. It is deliberately **not** a sixth gate on the admission chain.

The governor is stage 2 of `IToolCallAdmissionPipeline`, which every execution path that can reach a tool goes through and which contains the only copy of the admission sequence — so the posture reaches the agent turn, the Execution API, and all three plan step executors without being added anywhere else. Making it a gate of its own would have meant giving it its own route to a human, and two independent approval questions about one call is precisely the failure the governor's single-question design exists to prevent: an approver clears "write tools need sign-off" and thereby silently clears a second question they were never shown.

Reaching a person also requires `AI:Governance:ToolApproval:Enabled` and `AI:Governance:Escalation:Enabled`. Without them the gated call is **refused** rather than asked about — safe, but it will present to users as tools failing for no stated reason.

One consequence worth stating precisely, because the imprecise version is wrong: the governor arms on **either** `EnforceToolInvocation` **or** the presence of a bundle run's capability envelope. So leaving enforcement off does not switch the posture off — it would apply the posture to bundle runs alone while every agent turn and plan step went ungated, which is worse than either consistent answer. That is why the combination fails startup validation rather than being quietly permitted.

## 5. Exemptions

```jsonc
{
  "AppConfig": {
    "AI": {
      "Governance": {
        "EnforceToolInvocation": true,
        "ToolBehaviorGating": {
          "RequireApprovalForNonReadOnlyTools": true,
          "Exemptions": [
            {
              "Tool": "notion_search",
              "Server": "notion",
              "Reason": "POST-based search endpoint; vendor confirmed it does not mutate"
            }
          ]
        }
      },
      "McpServers": {
        "Servers": {
          "our-own-service": { "TrustToolAnnotations": true }
        }
      }
    }
  }
}
```

The exemption list is a name list, deliberately, and it is the honest part of the design. A hint is sometimes wrong in the direction that costs an operator an approval prompt on every call of a tool that plainly only reads — a search endpoint that uses POST, and is therefore assumed to write. Denying that escape hatch does not make annotations more accurate; it makes operators switch the whole posture off. OpenTag keeps the same list for the same reason.

`Reason` is required and a blank one fails startup validation. This list is the first thing a reviewer reads when asking why a tool was never gated, and an entry with no justification is indistinguishable a year later from one added to silence a prompt.

**`Server` is required whenever the tool comes from a server that is not marked trusted**, and this is the part that is easy to get wrong. A tool name belongs to nobody. An operator exempts `notion_search` after checking one vendor's tool; any other configured server can advertise a destructive tool by that same name tomorrow. The registry already refuses to let a shadowing server loosen a record it did not create — and a name-only exemption applied on top would hand that bypass straight back. Naming the server is a far narrower statement than `TrustToolAnnotations`: that accepts every declaration a server makes, present and future, while this accepts one tool the operator has actually looked at.

An exemption is honoured even for a tool that declares itself destructive, provided it covers that tool's actual source. That is a different question from the destructive-outranks-read-only rule: that rule arbitrates between two claims by the *same* party, while an exemption is the operator overruling that party outright, in writing, with a stated reason.

## 6. Known limits

- **Tool names are the key.** Two MCP servers advertising the same tool name share one behaviour record — resolved stricter-wins, so it fails safe, but collision detection and reporting is a separate concern.
- **A tool the registry has never seen is `Unknown`, therefore gated.** That is the intended failure mode, but it means a path that invokes a tool without going through discovery gates every call while the posture is on.
- **The posture asks about a tool, not about a call.** `write_file` needs approval whether it is writing to a scratch directory or to production config. Argument-conditioned rules remain the declarative policy engine's job.
- **Framework-provided tools have no local declaration, so they gate.** `load_skill` and `read_skill_resource` are unaffected — they bypass the governor entirely, because they are how an agent reads the instructions of skills it was already assigned rather than capabilities it is granted. `run_skill_script` *is* governed, and will require approval under the posture: executing a skill's script is a capability, so that is the intended outcome, but it is worth knowing before switching the posture on in a host that relies on it.

## 7. Where it lives

| Concern | Type |
| --- | --- |
| What a tool declared, and who declared it | `Domain.AI.Governance.ToolBehavior` |
| The exemption rule | `ToolBehavior.IsExemptFromApproval` |
| Recording and resolving declarations | `IToolBehaviorRegistry` / `ToolBehaviorRegistry` |
| Capturing MCP annotations at discovery | `BehaviorRecordingMcpToolProvider` |
| Applying the posture | `ToolInvocationGovernor.RequiresApprovalForDeclaredBehavior` |
| Refusing an inert configuration at boot | `GovernanceConfigValidator` |

Related: [`mcp-tool-definition-scanning.md`](./mcp-tool-definition-scanning.md) governs what an MCP server is allowed to put in the model's context. This document governs what a single tool is allowed to do once it is there. [`tool-composition-analysis.md`](./tool-composition-analysis.md) governs what an agent's *combination* of tools may do together — a risk no per-tool control, this one included, can represent.
