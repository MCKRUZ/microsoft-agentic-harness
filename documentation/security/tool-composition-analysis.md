# Governing Tool Combinations

> Date: 2026-08-14. Target: .NET 10 (Microsoft Agentic Harness). Audience: harness engineers and template consumers who run agents with tools they did not all write themselves.

## 1. Executive summary

Every existing tool-governance control in the harness — allow/deny lists, per-agent authorization, [behaviour gating](./tool-behavior-gating.md) — asks whether a single tool may run. None of them asks the question that actually matters for indirect prompt injection: **what can this agent do with these tools together?**

A tool that fetches a web page is not dangerous. A tool that sends email is not dangerous. An agent holding both is an exfiltration primitive: an attacker who controls the page controls what it says, the agent reads what it says, and the agent can act on it — including sending data back out. Every individual permission check passes. The composition is the vulnerability.

With a pairing configured under **`AI:Governance:ToolCompositionGating`**, the harness now flags this at agent build time — naming both tools and the capability pairing that implicates them — and, when the pairing's posture is `RequireApproval`, gates the sink tool at call time exactly as [behaviour gating](./tool-behavior-gating.md) does.

The posture is **off by default**, and inert by construction rather than by a switch: every pairing defaults to `Allow`, so a host that configures nothing here reports and enforces nothing.

## 2. The deliberate fail-open, and why it is not negotiable

Every capability classification in the harness that answers "unknown" normally treats that as unsafe — an unknown knowledge scope means global, not private; an unrecorded tool behaviour means gated, not exempt. **This feature inverts that rule, on purpose.**

A tool nothing can classify contributes **no** capability bits — neither source nor sink. The alternative (unknown means "could be either") would flag every agent holding two or more unclassified tools, which is the *universal taint destroys signal* failure this whole design exists to avoid: if everything is tainted, nothing is.

The mitigation is not silence. Every build reports how many tools it could not classify (`agent.governance.tool_composition_unclassified`), so the blind spot is visible rather than hidden. See §7 for what that number tends to look like on a real host.

## 3. The capability vocabulary

Five bits, two source and three sink, on `Domain.Common.Config.AI.Governance.ToolCompositionCapability`:

| Bit | What it means |
| --- | --- |
| `IngestsUntrustedInput` | Brings content into the conversation the agent did not author — a fetched page, an inbound email, a search result, an indexed document. |
| `ReadsCredentials` | Reads secrets, credentials, or access tokens. |
| `WritesFiles` | Writes, modifies, or deletes files or persistent state. |
| `ExecutesCode` | Runs code, shell commands, or scripts. |
| `SendsOutbound` | Sends data outside the process — email, webhooks, chat, HTTP POST. |

A tool can carry more than one bit (`[Flags]`) — a browser-automation tool that reads a page and can also submit a form is genuinely both. The composition check excludes **self-pairs**: a single tool that is both a source and a sink is [behaviour gating](./tool-behavior-gating.md)'s job, not a composition risk, because gating one tool for what it alone can do is exactly what that feature already covers.

**Named `ToolCompositionCapability`, not `ToolCapability`** — a `Domain.AI.Sandbox.ToolCapability` already exists (the sandbox's execution-grant vocabulary: file/network/process access). The two are unrelated concepts that happened to want the same short name; only one of them could keep it.

## 4. Where classification comes from

Four sources, strict precedence, each described in `IToolCapabilityResolver`'s remarks:

1. **A per-tool operator override** (`ToolCompositionGatingConfig.ToolCapabilities`) — authoritative in both directions, and the only way to *clear* a bit another source found. Clearing requires naming the tool's server, for the identical reason `ToolBehaviorExemption.Server` does: a tool name belongs to nobody, and a name-only override would hand back the exact bypass that rule prevents.
2. **A first-party tool's own declaration** (`ITool.Capabilities`) — authoritative in both directions once the tool is registered, *including* an explicit "declares nothing" default. A registered first-party tool never falls through to the keyword heuristic below it, so a tool a maintainer chose to leave undeclared shows up as unclassified rather than being silently guessed at.
3. **An MCP `openWorldHint: true` annotation** — adds `IngestsUntrustedInput`, believed from any server regardless of trust, because it only ever adds friction to this check; a hostile server gains nothing by asserting it. (Contrast [behaviour gating](./tool-behavior-gating.md)'s `readOnlyHint`, which *removes* friction and is trusted only from a vouched-for source.)
4. **The narrow built-in keyword heuristic** — only for a name with no first-party registration at all (a third-party MCP tool with none of the above).

A **per-server operator override** (`ServerCapabilities`) layers on top of all four, **additive only** — it can add bits every tool from that server inherits, never clear one.

## 5. The keyword heuristic, and what it deliberately excludes

Token-based, matching whole tokens (or a small number of adjacent-token pairs) against the tool's published name — never a substring:

| Capability | Tokens / pairs |
| --- | --- |
| `IngestsUntrustedInput` | `fetch`, `browse`, `crawl`, `scrape`; `(web\|http)+search`; `read+(email\|inbox\|mail)` |
| `SendsOutbound` | `send`, `email`, `webhook`, `publish`, `upload`, `notify`; `http+post` |
| `ExecutesCode` | `exec`, `execute`, `shell`, `bash`, `terminal`, `eval`, `spawn`; `run+(command\|script\|code)` |
| `WritesFiles` | `(write\|edit\|save\|delete)+(file\|directory\|path\|fs)` |
| `ReadsCredentials` | `secret`, `credential`, `keyvault`, `vault`, `password` |

**Deliberately excluded — this is the whole design**: `read, get, list, search, query, load, open, write, create, update, sync, call, request, submit, run, key, auth, token`. Each of these tags a large fraction of any real tool set. `read` alone would mark every file, database, and memory lookup as an untrusted source; bare `run` would catch `run_skill_script`. *Universal taint destroys signal, and it arrives one plausible-looking token at a time.* A keyword added here without an equally deliberate argument for why it will not flag ordinary tools is a regression, not an improvement.

The token split handles bundle-owned MCP tools' namespaced names (`{bundleId}:{server}__{tool}`) correctly: splitting on `_`, `-`, `.`, and the `__` separator recovers the original tool's tokens from the tail.

## 6. Where the check runs, and why nowhere else

**Analysis runs at agent build time**, inside `ToolChainBuilder`, at its two whole-agent-set exits: `BuildMergedToolsWithSourcesAsync` (the main agent build) and `BuildToolsByName` (delegated subagents). These are the only two points where the *complete* cross-skill tool set is known — a per-skill check would only ever see one skill's tools and could not confirm or rule out a pairing that spans two, which is exactly the realistic shape (a web-fetch skill plus an email skill on the same agent).

**Not inside `AgentExecutionContextFactory`**, despite that being the more obvious-looking place. A merged test in this repo, `NoFactoryDecidesAPerRunFactAtConstructionTime`, forbids any file under a `Factories/` directory from reading live per-run governance state — because a factory runs once and its output is cached, so anything it decided is frozen before the run that needs it (the exact #347 defect, where a bundle's disclosure tools reached the model ungoverned). `ToolChainBuilder` sits outside that directory, but the same principle governs the design regardless: **the build stamps only the co-residency fact, never the posture verdict.**

**The posture is resolved live, every time it is asked**, by `ToolCompositionPostureResolver` — once at build time (to decide what `ToolCompositionReporter` reports) and independently again at call time (to decide whether `ToolInvocationGovernor` gates). An operator's config change takes effect on an already-built agent's next call without a rebuild, for the same reason behaviour gating's posture does: the *structural* fact (which tools are co-resident) is fixed once discovered, the *policy* response to it is not.

## 7. Enforcement — the carry, and why it lives where it does

Applied **inside** `ToolInvocationGovernor`, alongside behaviour gating's posture — not as a sixth admission gate. A gate of its own would need its own route to a human, and two independent approval questions about one call is exactly the failure the governor's single-question design prevents: an approver clears "write tools need sign-off" and thereby silently clears "this tool is a live exfiltration sink," a question they were never shown.

**The carrier is the `GovernedAIFunction` wrapper**, not the agent execution context. There are two different types named "agent execution context" in this codebase, and neither works: the one holding the tool list is not registered in dependency injection at all, and the one that is (reachable from the governor on every path) carries agent identity but no tools. `GovernedAIFunction` is built fresh per agent by `ToolChainBuilder` — a singleton, but its *output* is per-instance — so per-instance state on the wrapper is exactly the right shape: it survives to call time on every path that reaches a governed tool, including plan steps and delegated subagents, which each open a fresh scope that would otherwise start blank.

## 8. Config

```jsonc
{
  "AppConfig": {
    "AI": {
      "Governance": {
        "EnforceToolInvocation": true,
        "ToolCompositionGating": {
          "DefaultPosture": "Allow",
          "Pairings": [
            { "Source": "IngestsUntrustedInput", "Sink": "SendsOutbound", "Posture": "RequireApproval" }
          ],
          "ToolCapabilities": [
            {
              "Tool": "notion_search",
              "Server": "notion",
              "Capabilities": ["IngestsUntrustedInput"],
              "Reason": "returns page bodies authored by third parties"
            }
          ],
          "ServerCapabilities": [
            {
              "Server": "web",
              "Capabilities": ["IngestsUntrustedInput"],
              "Reason": "every tool on this server returns fetched page content"
            }
          ]
        }
      }
    }
  }
}
```

No `Enabled` flag — `DefaultPosture: Allow` with an empty `Pairings` list *is* the off state. A `RequireApproval` pairing needs `EnforceToolInvocation: true` for the identical reason [behaviour gating](./tool-behavior-gating.md) does: the governor arms on invocation enforcement *or* a bundle run's capability envelope, so leaving enforcement off would apply the posture to bundle runs alone while every agent turn and plan step goes ungated. `GovernanceConfigValidator` refuses that combination at startup.

Every override (`ToolCapabilities`, `ServerCapabilities`) requires a `Reason` — the first thing a reviewer reads when asking why a tool was, or was not, flagged.

## 9. Known limits

- **The `AIContext` channel is a structural blind spot, and its unclassified count is NOT yet incremented.** Tools arriving at turn time through an `AIContextProvider` (`GoverningToolContextProvider`) never pass through `ToolChainBuilder`, so build-time analysis cannot see them — and, as shipped, that provider does not call the composition analyzer or increment `agent.governance.tool_composition_unclassified` either, so this specific gap is currently silent rather than visible. Wiring that provider into the analyzer is a follow-up, not part of this change.
- **The Execution API's direct-invocation path (`DirectToolInvoker`) is out of scope entirely, structurally.** That surface has no concept of an agent's assembled tool set at all — a caller names one tool and it runs, with no sibling tools in view — so there is no set for composition analysis to reason over. A `RequireApproval` pairing is silently inert for any tool called this way.
- **`ConnectorToolAdapter`-provisioned tools are unclassified by default.** External connectors (email, webhook, Notion, Slack, GitHub, …) registered via the generic connector adapter share one `ITool` implementation across every connector instance, so a per-class `Capabilities` override would give every connector — present and future — the identical declaration, which is wrong for most of them. Classify these via `ToolCapabilities`/`ServerCapabilities` operator overrides per deployment; a per-connector first-party declaration is a follow-up.
- **Self-pairs are excluded**, by design — see §3. A future issue may extend enforcement to cover a single tool that is both.
- **Name-keyed**, like behaviour gating: two servers advertising the same tool name share one resolution.
- **No argument awareness.** The posture asks about a tool pairing, not about a call's arguments — an operator wanting `write_file` gated only for production paths needs the declarative policy engine.
- **Bundle-owned MCP tools are invisible to the MCP-annotation source (§4.3) only.** The tool-behaviour registry that carries the `openWorldHint` signal is keyed on the bare tool name, while bundle-owned tools are published under a namespaced name. First-party declarations and the keyword heuristic are unaffected, since both operate on the published name directly. Tracked as a follow-up against the shared registry, not fixed here — fixing the registry's key changes behaviour gating's treatment of those tools too, and needs its own review.
- **Coverage of a real third-party MCP estate is unmeasured.** The keyword vocabulary is deliberately narrow; measure the unclassified fraction on a real host after adopting this, and treat a high fraction as a signal to add first-party declarations or operator overrides, not to widen the keywords.
- **`Origin` describes the profile, not the bit.** A profile carrying two capability bits from two different sources (a keyword-matched bit and an MCP-annotation-added bit, say) reports only the single strongest source's name. A finding's `SourceOrigin`/`SinkOrigin` is therefore "the strongest source that vouches for this tool," not necessarily "the source of the exact bit that made this pairing fire." Precise per-bit provenance would need a richer data model and is a follow-up.

## 10. Where it lives

| Concern | Type |
| --- | --- |
| The capability vocabulary | `Domain.Common.Config.AI.Governance.ToolCompositionCapability` |
| What a tool was classified as, and by what | `Domain.AI.Governance.ToolCapabilityProfile` / `ToolCapabilityOrigin` |
| A co-resident source/sink fact | `Domain.AI.Governance.ToolCompositionFinding` |
| The fact carried from build to call time | `Domain.AI.Governance.ToolCompositionTaint` |
| Resolving a tool's capabilities | `IToolCapabilityResolver` / `ToolCapabilityResolver` |
| The narrow keyword vocabulary | `ToolCapabilityKeywordRules` |
| Analyzing a whole tool set | `IToolCompositionAnalyzer` / `ToolCompositionAnalyzer` |
| Resolving a pairing's live posture | `ToolCompositionPostureResolver` |
| Reporting (audit, metrics, log) | `ToolCompositionReporter` |
| Stamping the enforcement carrier | `ToolChainBuilder.ApplyCompositionTaint` |
| Applying the posture at call time | `ToolInvocationGovernor.RequiresApprovalForToolComposition` |
| Refusing an inert configuration at boot | `GovernanceConfigValidator` |

Related: [`tool-behavior-gating.md`](./tool-behavior-gating.md) governs what a single tool may do. This document governs what an agent's *combination* of tools may do together — the class of risk individual-tool governance cannot represent at all.
