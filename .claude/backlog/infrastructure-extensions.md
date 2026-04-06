# Backlog: Deferred Ports from ApplicationTemplate

## Deferred from Application.AI.Common audit (2026-04-06)

### ChatClientBuilderExtensions → Infrastructure.AI
- Template has this in Application.AI.Common but it references Azure.AI.ContentSafety, Azure.AI.TextAnalytics, concrete middleware classes
- Should be Infrastructure layer — fails clean architecture litmus test
- Wires ObservabilityMiddleware, RateLimitingMiddleware, AttackDetectionMiddleware, ContentSafetyMiddleware into ChatClientBuilder
- Port when building Infrastructure.AI project

### IServiceCollectionExtensions (AI DI) → Infrastructure.AI
- Template has this in Application.AI.Common but registers AzureOpenAIClient, OpenAIClient, PersistentAgentsClient, ContentSafetyClient
- All concrete Azure SDK types — belongs in Infrastructure
- Methods: AddAgentFrameworkClients, AddEmbeddings, AddAgent365TelemetryServices, AddContentSafety, AddSkillServices
- Port when building Infrastructure.AI project

### SkillDefinitionExtensions → Application.AI.Common
- Pure domain logic (metadata access, tag filtering) extending SkillDefinition
- Deferred because SkillDefinition model doesn't exist yet in Domain.AI
- Port when building Domain.AI Skills model

---

## Deferred Factories (2026-04-06)

### AgentExecutionContextFactory → Application.AI.Common/Factories
- Converts SkillDefinition → AgentExecutionContext
- Only uses abstractions + Ardalis.GuardClauses — correctly Application layer
- Deferred: depends on SkillDefinition, AgentExecutionContext, Domain.AI.Tools (not built yet)

### ChatClientFactory → Infrastructure.AI
- Creates IChatClient from AzureOpenAIClient, OpenAIClient, PersistentAgentsClient
- Concrete Azure SDK types — belongs in Infrastructure
- Port when building Infrastructure.AI

### SkillsContextProviderFactory → Infrastructure.AI
- Creates FileAgentSkillsProvider (concrete Microsoft.Agents.AI type)
- Infrastructure — port when building Infrastructure.AI

### AgentFactory → Infrastructure.AI
- Orchestrates ChatClientFactory + SkillsContextProviderFactory to create agents
- References Microsoft.Agents.AI concrete types
- Infrastructure — port when building Infrastructure.AI

---

## Deferred Helpers (2026-04-06)

### AgentFrameworkHelper → Infrastructure.AI
- Configures AzureOpenAIClientOptions, OpenAIClientOptions, PersistentAgentsAdministrationClientOptions
- Concrete SDK types — belongs in Infrastructure
- Port when building Infrastructure.AI

---

## Deferred Middleware (2026-04-06)

### RateLimitingMiddleware → Infrastructure.AI
- Extends DelegatingChatClient, uses System.Threading.RateLimiting.RateLimiter
- Port when building Infrastructure.AI

### AttackDetectionMiddleware → Infrastructure.AI
- Extends DelegatingChatClient, delegates to PromptShieldService
- Port when building Infrastructure.AI

### ContentSafetyMiddleware → Infrastructure.AI
- Extends DelegatingChatClient, uses Azure.AI.ContentSafety + TextAnalytics clients
- Port when building Infrastructure.AI

### HumanInTheLoopFilterMiddleware → Infrastructure.AI
- Implements IAgentRunMiddleware from Microsoft.Agents.AI
- Port when building Infrastructure.AI

### ConsoleApprovalHandler → Presentation
- Uses Spectre.Console for interactive approval prompts — UI concern
- Port when building Presentation layer

---

## Deferred Services (2026-04-06)

### Application.AI.Common (when Domain.AI models exist):
- **ToolSchemaService** — bridges tool definitions with tool declarations, only abstractions + Domain.AI.Tools
- **SkillLoaderService** — loads/parses SKILL.md files, manages skill lifecycle, only abstractions + Domain.AI.Skills

### Infrastructure.AI:
- **AgentParserService** — parses AGENT.md files, depends on YamlDotNet
- **ToolDefinitionLoader** — loads .tool.md files, depends on YamlDotNet
- **SkillMetadataParser** — parses YAML metadata from SKILL.md, depends on YamlDotNet
- **AIToolConverter** — converts tools to AITool format via reflection (1800+ lines, needs decomposition)
- **McpToolProvider** — manages MCP server connections, depends on ModelContextProtocol SDK
- **PromptShieldService** — prompt injection detection, depends on Azure.AI.ContentSafety
- **ContentSafetyService** — content moderation, depends on Azure.AI.ContentSafety + AspNetCore.Http
