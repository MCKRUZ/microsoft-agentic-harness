---
paths: src/Content/**/AI/**/*.cs, src/Content/**/AI.Common/**/*.cs, src/Content/Domain/Domain.AI/**/*.cs
---
# AI Agent Layer Rules

## Agent Construction
Always use `AgentFactory` to create agents — it wires OpenTelemetry, caching, content safety, and function invocation limits. Never construct `AIAgent` directly.

## Skills — Progressive Disclosure (CRITICAL)
Skills use 3-tier loading to manage context budget:
- **Tier 1 (Index Card)**: ~100 tokens. Id, Name, Description, Category, Tags. Loaded at startup.
- **Tier 2 (Folder)**: ~5000 tokens. Full Instructions. Loaded on demand when skill is selected.
- **Tier 3 (Filing Cabinet)**: Scripts, References, Templates. Loaded only when skill executes.

When adding skills: define all 3 tiers. Never eager-load Tier 3 content.

## Tool Registration
Tools use keyed DI for lazy resolution:
```csharp
services.AddKeyedSingleton<IMyTool>("tool_name", (sp, key) => sp.GetRequiredService<IMyTool>());
```
Skills declare tool dependencies in SKILL.md frontmatter: `allowed-tools: ["Read", "Write"]`

## Agent Manifest (AGENT.md)
Declarative config with:
- Domain, category, tags for discovery
- AllowedTools list and ToolDeclarations with operations/fallbacks
- StateConfiguration for workflow tracking
- DecisionFramework for validation gates (GO/CONDITIONAL_GO/NO-GO)
- Skills table referencing child SKILL.md files

## MCP Integration
- MCP tools provided via `IMcpToolProvider` → converted to `AITool` format
- MCP server uses HTTP transport with JWT Bearer + MCP auth
- Rate limiting on all MCP endpoints
- Tool schemas generated via `IToolSchemaService`

## Content Safety
Always wire content safety middleware through AgentFactory. Check `AppConfig.AI.ContentSafety` for enabled filters (PII detection, prompt shield, blocked keywords).

## Chat Client Selection
Use `IChatClientFactory` — supports Azure OpenAI, OpenAI, AI Foundry. Never hardcode endpoints or model names; pull from `AppConfig.AI.AgentFramework`.
