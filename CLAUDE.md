# Project: Microsoft Agentic Harness

## Purpose
Production-grade template for a Microsoft Agent Framework agent with a full agentic harness — skills, MCP, tools, RAG, and knowledge graph systems — modeled after Claude Code's architecture. Built on the ApplicationTemplate Clean Architecture pattern. Designed for enterprise consumers to clone and extend.

## RAG & Knowledge Architecture
The harness includes a full RAG pipeline (`Infrastructure.AI.RAG`) and a planned knowledge graph layer inspired by [Cognee](https://github.com/topoteretes/cognee).

### Current RAG Capabilities (Implemented)
- **Ingestion**: 3 chunking strategies (structure-aware, fixed-size, semantic), contextual enrichment (Anthropic pattern), RAPTOR hierarchical summarization
- **Retrieval**: Hybrid dense+sparse via Reciprocal Rank Fusion, query transformation (RAG Fusion, HyDE), query classification/routing
- **Quality**: CRAG evaluation with refinement loops, configurable accept/refine/reject thresholds
- **Assembly**: Token budget enforcement, pointer expansion (sibling/parent), citation tracking
- **Reranking**: Azure Semantic, Cross-Encoder, NoOp (strategy-keyed DI)
- **Stores**: Azure AI Search + FAISS (vector), Azure AI Search + SQLite FTS5 (BM25)

### Knowledge Graph Enhancements (Planned — from Cognee analysis)
1. **Production Graph Backend** — Replace the teaching-stub `ManagedCodeGraphRagService` with a real graph database (Neo4j/Kuzu/PostgreSQL). Entity extraction with ontology validation, temporal event support, community detection (Leiden algorithm)
2. **Feedback-Weighted Search** — Track retrieval quality scores on graph nodes/edges. Re-rank future retrievals by blending semantic relevance with historical feedback weights. Configurable learning rate (`feedback_alpha`)
3. **Cross-Session Knowledge Persistence** — `Remember()`/`Recall()`/`Forget()`/`Improve()` operations. Session-local fast cache with background sync to permanent graph. Agents learn across conversations
4. **Entity-Level Provenance** — Stamp every extracted node/edge with source pipeline, task, and timestamp. Audit trail for knowledge lineage beyond document-level citations
5. **Multi-Tenant Knowledge Isolation** — Agent scope boundaries (user → dataset → owner) with permission-checked dataset access. Enables multiple agents/users against shared knowledge infrastructure

## Stack
- C# .NET 10, Clean Architecture, CQRS/MediatR, FluentValidation, AutoMapper
- Microsoft.Agents.AI, Microsoft.Extensions.AI, Azure.AI.OpenAI
- MCP (Model Context Protocol) server/client — HTTP transport with JWT auth
- OpenTelemetry (Jaeger + Azure Monitor), Prometheus
- xUnit, Moq, coverlet

## Architecture
Clean Architecture with Domain → Application → Infrastructure → Presentation layers.
Reference implementation: `C:\CodeRepos\ApplicationTemplate` (same layer structure, DI patterns, and conventions).

Key architectural concepts from the reference:
- **Progressive Disclosure Skills**: 3-tier loading (Index Card → Folder → Filing Cabinet) to manage context budget
- **Keyed DI Tools**: Tools registered with string keys (`"file_system"`, `"calculation_engine"`) for lazy resolution from skill declarations
- **Agent Manifest (AGENT.md)**: Declarative agent config with tool declarations, state config, decision frameworks
- **MCP Server**: ASP.NET Core WebAPI exposing tools/prompts/resources via MCP protocol
- **Factory Pattern**: AgentFactory, ChatClientFactory, AgentExecutionContextFactory for consistent agent construction
- **MediatR Pipeline**: Validation → Caching → Performance → Exception handling behaviors

## Commands
- `dotnet build src/AgenticHarness.slnx` — Build
- `dotnet test src/AgenticHarness.slnx` — Run all tests
- `dotnet test --collect:"XPlat Code Coverage"` — Tests with coverage
- `dotnet run --project src/Content/Presentation/Presentation.ConsoleUI` — Run console

## Verification
After changes: `dotnet build src/AgenticHarness.slnx && dotnet test src/AgenticHarness.slnx`

## Code Style
- Immutability: records, `with` expressions, `ImmutableList<T>`, init-only properties
- PascalCase (classes/methods/props), `_camelCase` (private fields), camelCase (locals/params)
- Functions <50 lines, no nesting >4 levels
- Result<T> pattern for error handling, structured logging (no console.log)
- FluentValidation on all DTOs, validate at system boundaries

## Task Approach
1. Check reference implementation at `C:\CodeRepos\ApplicationTemplate` for existing patterns before creating new abstractions
2. Present options when trade-offs exist between agent framework approaches
3. Implement in layers: Domain models first, Application interfaces, Infrastructure implementations, Presentation last
4. Run build + tests after each meaningful change
5. Flag anything that diverges from the ApplicationTemplate patterns

## Common Mistakes
- Creating new abstractions when ApplicationTemplate already has one: check `Application.AI.Common/Interfaces/` first
- Registering tools without keyed DI: always use `AddKeyedSingleton<T>(toolName, ...)` pattern
- Forgetting MediatR pipeline behaviors when adding new commands: register in `DependencyInjection.cs`
- Hardcoding AI model config: use `AppConfig.AI.AgentFramework` section, never inline
- Skipping content safety middleware in agent factory: always wire through `AgentFactory`
