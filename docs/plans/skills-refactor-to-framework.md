# Skills Refactor: Align with Microsoft Agent Framework

## SDK Reality Check

**Package**: `Microsoft.Agents.AI` 1.0.0-rc4 (already installed)

**Available C# types:**

| Type | Purpose |
|------|---------|
| `FileAgentSkill` | Parsed SKILL.md: Frontmatter (name+desc), Body, SourcePath, ResourceNames |
| `FileAgentSkillLoader` | Directory discovery, YAML parsing, resource scanning, path-traversal protection |
| `FileAgentSkillsProvider` | `AIContextProvider` — progressive disclosure via `load_skill` / `read_skill_resource` tools |
| `SkillFrontmatter` | Record: Name + Description |
| `FileAgentSkillsProviderOptions` | SkillsInstructionPrompt, AllowedResourceExtensions |

**NOT in C# RC4** (Python-only or unreleased): `AgentClassSkill<T>`, `[AgentSkillResource]`, `[AgentSkillScript]`, `run_skill_script`

## Current State

We already use `FileAgentSkillsProvider` at runtime (`AgentExecutionContextFactory:214`). The framework handles agent-facing progressive disclosure correctly. The problem is duplicated supporting infrastructure, not a wrong architecture.

### What's Duplicated (Remove/Replace)

| Our Code | Framework Equivalent | Action |
|----------|---------------------|--------|
| `SkillMetadataParser` | `FileAgentSkillLoader.DiscoverAndLoadSkills()` | Replace: use loader, then enrich |
| `FileSystemSkillContentProvider` | `FileAgentSkillLoader` (directory scanning + resource reading) | Remove |
| `CandidateSkillContentProvider` | In-memory `FileAgentSkill` instances | Remove |
| `ITieredContextAssembler` / `TieredContextAssembler` | `FileAgentSkillsProvider` tool-based disclosure | Remove (conflicts with framework) |
| `ContextLoading` / `ContextContract` (domain types) | Framework manages disclosure via agent tool calls | Remove |
| `SkillCacheStatistics` | Unused anywhere | Remove |

### What's Custom Value-Add (Keep)

| Our Code | Why It Stays |
|----------|-------------|
| `ISkillMetadataRegistry` | Framework has no query-by-category/tag/type. WebUI, MCP, orchestrator need this. |
| `SkillDefinition` (slimmed) | Wraps `FileAgentSkill` + adds: category, tags, skill_type, tool declarations, hierarchy |
| `IContextBudgetTracker` | Framework has no token accounting or diminishing returns detection |
| `ToolPermissionFilter` | Framework advertises `allowed-tools` but doesn't enforce at runtime |
| `SkillAgentOptions` (slimmed) | Deployment override, additional middleware, additional context |
| `SkillReference` | Agent manifest links to skills — lightweight, no duplication |
| `SkillChangedEventArgs` | Hot-reload for dev-time iteration |

## Phased Plan

### Phase 1: Remove Tiered Context Assembly (~1 day, MEDIUM risk)

**IMPORTANT**: `FileAgentSkillLoader` is `internal` in the 1.0.0-rc4 SDK. We CANNOT use it directly.
Only `FileAgentSkillsProvider`, `FileAgentSkillsProviderOptions`, `FileAgentSkill`, and `SkillFrontmatter` are public.
This means `SkillMetadataParser` and `ISkillMetadataRegistry` MUST stay — they serve a purpose the framework can't fulfill.

**Goal**: Remove `ITieredContextAssembler` which conflicts with the framework's tool-based disclosure
(we're injecting context programmatically AND offering it via tools — potential double-loading).

**Delete:**
- [ ] `ITieredContextAssembler.cs` (Application.AI.Common/Interfaces/)
- [ ] `TieredContextAssembler.cs` (Application.AI.Common/Services/Context/)
- [ ] `ContextLoading.cs` (Domain.AI/Skills/) — if separate file
- [ ] `ContextContract.cs` (Domain.AI/Skills/) — if separate file
- [ ] `SkillCacheStatistics.cs` (Domain.AI/Skills/) — unused

**Update:**
- [ ] `Application.AI.Common/DependencyInjection.cs` — remove `ITieredContextAssembler` registration
- [ ] `AgentExecutionContextFactory.cs` — remove tiered assembly calls, keep `FileAgentSkillsProvider` wiring
- [ ] `SkillDefinition.cs` — remove `ContextLoading`/`ContextContract` properties if they exist
- [ ] `SkillAgentOptions.cs` — remove `LoadResources`/`LoadReferences`/`LoadTemplates` flags

**Validation**: `dotnet build && dotnet test` — all existing tests pass

### Phase 2: Slim SkillDefinition to Wrap FileAgentSkill (~0.5 day, LOW risk)

**Goal**: `SkillDefinition` becomes a thin wrapper around `FileAgentSkill` + our extensions.

```csharp
// Domain.AI/Skills/SkillDefinition.cs — AFTER
public sealed class SkillDefinition
{
    // Framework backing — the parsed SKILL.md
    public FileAgentSkill FrameworkSkill { get; }

    // Pass-through to framework
    public string Name => FrameworkSkill.Frontmatter.Name;
    public string Description => FrameworkSkill.Frontmatter.Description;
    public string Instructions => FrameworkSkill.Body;
    public string SourcePath => FrameworkSkill.SourcePath;
    public IReadOnlyList<string> ResourceNames => FrameworkSkill.ResourceNames;

    // Our extensions (parsed from extended YAML frontmatter)
    public string? Category { get; init; }
    public IReadOnlyList<string> Tags { get; init; } = [];
    public string? SkillType { get; init; }
    public IReadOnlyList<string> AllowedTools { get; init; } = [];
    public IReadOnlyList<ToolDeclaration> ToolDeclarations { get; init; } = [];

    // Hierarchy (our concept)
    public string? ParentId { get; init; }
    public IReadOnlyList<SkillDefinition> Children { get; init; } = [];

    // Token estimation (our concept)
    public int EstimateLevel1Tokens() => /* name + desc */ ;
    public int EstimateLevel2Tokens() => /* instructions */ ;
}
```

**Delete:**
- [ ] `SkillResource.cs` — framework's `ResourceNames` + `ReadSkillResourceAsync` replaces this
- [ ] `SkillCacheStatistics.cs` — unused
- [ ] `ContextLoading.cs` — framework manages disclosure
- [ ] `ContextContract.cs` — framework manages disclosure

**Update:**
- [ ] `SkillDefinition.cs` — slim to wrapper pattern above
- [ ] All consumers of removed properties (grep + fix)

### Phase 3: Remove Tiered Context Assembly (~1 day, MEDIUM risk)

**Goal**: Stop programmatically injecting context in tiers. Let `FileAgentSkillsProvider` handle it via `load_skill` / `read_skill_resource` tools.

**The conflict today**: `ITieredContextAssembler` injects skill context programmatically into the system prompt. But `FileAgentSkillsProvider` ALSO advertises skills and provides `load_skill`/`read_skill_resource` as tools. This means:
- The agent may get the same content twice (injected + tool-loaded)
- Token budgets tracked by `IContextBudgetTracker` don't account for tool-loaded content
- The agent can't decline context that was programmatically injected

**Delete:**
- [ ] `ITieredContextAssembler.cs` interface
- [ ] `TieredContextAssembler.cs` implementation
- [ ] `Application.AI.Common/DependencyInjection.cs` — remove `ITieredContextAssembler` registration

**Update:**
- [ ] `AgentExecutionContextFactory.MapToAgentContextAsync()` — simplify to:
  1. Resolve deployment name
  2. Build tools (MCP + keyed DI)
  3. Wire `FileAgentSkillsProvider` as context provider (already doing this)
  4. Wire `ToolPermissionFilter` as context provider
  5. Attach `IContextBudgetTracker` as monitoring middleware
  6. ~~Assemble tiered context~~ (removed)
- [ ] `SkillAgentOptions.cs` — remove `LoadResources`, `LoadReferences`, `LoadTemplates` flags (framework decides)

**Keep `IContextBudgetTracker`** — but change it from "gatekeeping what gets injected" to "monitoring what the agent loads via tools." Hook it into the `load_skill` / `read_skill_resource` tool calls to track token usage.

### Phase 4: Verify & Clean Up Tests (~0.5 day, LOW risk)

- [ ] Update test projects that reference deleted types
- [ ] Replace `CandidateSkillContentProvider` test patterns with in-memory `FileAgentSkill` construction
- [ ] Verify `FileAgentSkillsProvider` progressive disclosure works end-to-end (manual + E2E test)
- [ ] Run full suite: `dotnet test src/AgenticHarness.slnx`

## Post-Refactor Architecture

```
SKILL.md files on disk
        │
        ▼
┌───────────────────────────────────┐
│  FileAgentSkillLoader (FRAMEWORK) │ ← Discovers, parses YAML, validates
│  DiscoverAndLoadSkills(paths)     │   paths, scans resources
│  ReadSkillResourceAsync()         │
└───────────────┬───────────────────┘
                │ Dictionary<string, FileAgentSkill>
                ▼
┌───────────────────────────────────┐
│  SkillMetadataRegistry (OURS)     │ ← Enriches with extended YAML:
│  EnrichFromBody(FileAgentSkill)   │   category, tags, tools, hierarchy
│  GetByCategory(), GetByTags()     │ ← Query API for UI/MCP/orchestrator
│  TryGet(), GetAll()               │
└───────────────┬───────────────────┘
                │ SkillDefinition (wraps FileAgentSkill)
                ▼
┌───────────────────────────────────┐
│  AgentExecutionContextFactory     │ ← Resolves deployment, MCP tools
│  (SIMPLIFIED)                     │   Wires providers below ↓
└───────────────┬───────────────────┘
                │
    ┌───────────┼────────────┐
    ▼           ▼            ▼
┌────────┐ ┌─────────┐ ┌──────────────┐
│FileAgt │ │ToolPerm │ │ContextBudget │
│Skills  │ │ission   │ │Tracker       │
│Provider│ │Filter   │ │(monitoring)  │
│(FRAME- │ │(OURS)   │ │(OURS)        │
│WORK)   │ │         │ │              │
│        │ │Enforces │ │Tracks tokens │
│Provides│ │allowed- │ │loaded via    │
│load_   │ │tools at │ │tool calls    │
│skill & │ │runtime  │ │              │
│read_   │ │         │ │Diminishing   │
│resource│ │         │ │returns       │
│tools   │ │         │ │detection     │
└────────┘ └─────────┘ └──────────────┘
```

## SKILL.md Migration

All existing SKILL.md files stay as-is. The framework parser reads `name` and `description` from frontmatter; our enrichment step reads the rest. No file changes needed.

Extended fields we parse ourselves:

| Field | Location | Consumed By |
|-------|----------|-------------|
| `category` | YAML frontmatter | `SkillMetadataRegistry.GetByCategory()` |
| `tags` | YAML frontmatter | `SkillMetadataRegistry.GetByTags()` |
| `skill_type` | YAML frontmatter | `SkillMetadataRegistry.GetBySkillType()` |
| `allowed-tools` | YAML frontmatter (spec-standard) | `ToolPermissionFilter` |
| `tools` | YAML frontmatter | `AgentExecutionContextFactory` (MCP resolution) |
| `version` | YAML frontmatter | Metadata display |

## Effort Summary

| Phase | Scope | Files | Effort | Risk |
|-------|-------|-------|--------|------|
| 1. Replace parsing | Use `FileAgentSkillLoader`, delete custom parser + content providers | ~8 | 1 day | Low |
| 2. Slim domain model | `SkillDefinition` wraps `FileAgentSkill`, delete dead types | ~6 | 0.5 day | Low |
| 3. Remove tiered assembly | Delete `ITieredContextAssembler`, simplify factory | ~5 | 1 day | Medium |
| 4. Test cleanup | Update test projects, verify E2E | ~10 | 0.5 day | Low |
| **Total** | | **~29 files** | **3 days** | **Low-Medium** |

## What Gets Deleted (Net Code Reduction)

| File | Lines (approx) |
|------|---------------|
| `SkillMetadataParser.cs` | ~150 |
| `FileSystemSkillContentProvider.cs` | ~80 |
| `CandidateSkillContentProvider.cs` | ~40 |
| `ISkillContentProvider.cs` | ~20 |
| `TieredContextAssembler.cs` | ~200 |
| `ITieredContextAssembler.cs` | ~30 |
| `SkillResource.cs` | ~40 |
| `SkillCacheStatistics.cs` | ~15 |
| `ContextLoading.cs` | ~60 |
| `ContextContract.cs` | ~40 |
| **Total removed** | **~675 lines** |

Plus ~200 lines simplified in `AgentExecutionContextFactory` and `SkillDefinition`.

**Net result**: ~875 fewer lines of custom skill infrastructure, framework handles the heavy lifting, our extensions layer cleanly on top.

## Open Questions

1. **Extended frontmatter parsing** — `FileAgentSkillLoader` ignores fields beyond name/description. Do we parse the raw SKILL.md body for our YAML extensions, or read the file separately for just the frontmatter? Recommendation: read the file once at startup to extract extended fields, store in `SkillDefinition`.
2. **Budget tracker integration** — How do we hook `IContextBudgetTracker` into framework tool calls (`load_skill`, `read_skill_resource`)? Options: (a) middleware on `AIAgent` tool invocation pipeline, (b) custom `FileAgentSkillsProvider` subclass that wraps calls with budget tracking.
3. **Hot-reload** — `FileAgentSkillLoader` doesn't watch files. Keep our `SkillChangedEventArgs` pattern or add a `FileSystemWatcher` in the registry? Only matters for dev-time.

## Prerequisites

- [x] Verify `FileAgentSkillLoader` exists in installed SDK — **confirmed in 1.0.0-rc4**
- [x] Verify `FileAgentSkillsProvider` exists — **confirmed, already in use**
- [ ] Spike: confirm `FileAgentSkillLoader.DiscoverAndLoadSkills()` successfully parses our SKILL.md files with extended frontmatter (it should — it only reads name+desc and ignores the rest)
- [ ] Spike: confirm `ReadSkillResourceAsync` works with our resource directory structure
