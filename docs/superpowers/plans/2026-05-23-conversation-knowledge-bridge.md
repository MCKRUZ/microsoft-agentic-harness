# Conversation-to-Knowledge Bridge Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Automatically extract notable facts from agent conversations and persist them to the knowledge graph for cross-session recall.

**Architecture:** A MediatR pipeline behavior (`KnowledgeExtractionBehavior`) fires post-turn, calling `IConversationFactExtractor` which uses an economy-tier LLM via `IModelRouter.RouteOperationAsync("fact_extraction")` to extract structured facts, then persists each via `IKnowledgeMemory.RememberAsync()`. Fire-and-forget — zero latency impact on agent responses.

**Tech Stack:** C# .NET 10, MediatR, Microsoft.Extensions.AI (`IChatClient.GetResponseAsync`), System.Text.Json, IModelRouter, IKnowledgeMemory, xUnit + Moq + FluentAssertions

**Spec:** `docs/superpowers/specs/2026-05-22-conversation-knowledge-bridge-design.md`

---

## File Structure

```
NEW FILES:
  src/Content/Domain/Domain.AI/KnowledgeGraph/Models/ConversationFact.cs
  src/Content/Domain/Domain.Common/Config/AI/KnowledgeBridgeConfig.cs
  src/Content/Application/Application.AI.Common/Interfaces/KnowledgeGraph/IConversationFactExtractor.cs
  src/Content/Application/Application.AI.Common/MediatRBehaviors/KnowledgeExtractionBehavior.cs
  src/Content/Infrastructure/Infrastructure.AI.KnowledgeGraph/Memory/ConversationFactExtractor.cs
  src/Content/Tests/Infrastructure.AI.KnowledgeGraph.Tests/Memory/ConversationFactExtractorTests.cs
  src/Content/Tests/Application.AI.Common.Tests/MediatRBehaviors/KnowledgeExtractionBehaviorTests.cs

MODIFIED FILES:
  src/Content/Domain/Domain.Common/Config/AI/AIConfig.cs              — add KnowledgeBridge property
  src/Content/Application/Application.AI.Common/DependencyInjection.cs — register behavior
  src/Content/Infrastructure/Infrastructure.AI.KnowledgeGraph/DependencyInjection.cs — register extractor
  src/Content/Infrastructure/Infrastructure.AI/DependencyInjection.cs  — bind config
```

---

### Task 1: Domain Model — ConversationFact

**Files:**
- Create: `src/Content/Domain/Domain.AI/KnowledgeGraph/Models/ConversationFact.cs`

- [ ] **Step 1: Create the ConversationFact record**

```csharp
// src/Content/Domain/Domain.AI/KnowledgeGraph/Models/ConversationFact.cs
namespace Domain.AI.KnowledgeGraph.Models;

/// <summary>
/// A fact extracted from a conversation turn by the knowledge bridge.
/// Persisted to the knowledge graph via <see cref="Application.AI.Common.Interfaces.KnowledgeGraph.IKnowledgeMemory.RememberAsync"/>.
/// </summary>
public sealed record ConversationFact
{
    /// <summary>
    /// Deterministic key for idempotent upserts. Format: <c>{conversationId}:{turnNumber}:{factIndex}</c>.
    /// </summary>
    public required string Key { get; init; }

    /// <summary>
    /// Human-readable description of the fact (e.g., "User prefers PostgreSQL over SQL Server").
    /// </summary>
    public required string Content { get; init; }

    /// <summary>
    /// Entity type category. One of: Preference, Decision, Fact, Correction.
    /// Defaults to "Fact".
    /// </summary>
    public string EntityType { get; init; } = "Fact";

    /// <summary>
    /// LLM confidence in the extraction (0.0–1.0). Facts below the configured
    /// <c>KnowledgeBridgeConfig.MinConfidence</c> threshold are discarded.
    /// </summary>
    public double Confidence { get; init; }
}
```

- [ ] **Step 2: Verify build**

Run: `dotnet build src/AgenticHarness.slnx`
Expected: Build succeeds with 0 errors.

- [ ] **Step 3: Commit**

```bash
git add src/Content/Domain/Domain.AI/KnowledgeGraph/Models/ConversationFact.cs
git commit -m "feat(bridge): add ConversationFact domain model"
```

---

### Task 2: Configuration POCO — KnowledgeBridgeConfig

**Files:**
- Create: `src/Content/Domain/Domain.Common/Config/AI/KnowledgeBridgeConfig.cs`
- Modify: `src/Content/Domain/Domain.Common/Config/AI/AIConfig.cs`

- [ ] **Step 1: Create the config class**

```csharp
// src/Content/Domain/Domain.Common/Config/AI/KnowledgeBridgeConfig.cs
namespace Domain.Common.Config.AI;

/// <summary>
/// Configuration for the Conversation-to-Knowledge Bridge.
/// Controls whether fact extraction runs, confidence thresholds, and timeout behavior.
/// Bound to <c>AppConfig:AI:KnowledgeBridge</c>.
/// </summary>
public sealed class KnowledgeBridgeConfig
{
    /// <summary>
    /// Master toggle. When false, <c>KnowledgeExtractionBehavior</c> passes through
    /// with zero overhead — no LLM calls, no background tasks.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Minimum LLM confidence (0.0–1.0) required to persist an extracted fact.
    /// Facts below this threshold are silently discarded.
    /// </summary>
    public double MinConfidence { get; set; } = 0.7;

    /// <summary>
    /// Hard timeout in seconds for the background extraction LLM call.
    /// Prevents runaway requests from consuming resources indefinitely.
    /// </summary>
    public int ExtractionTimeoutSeconds { get; set; } = 10;

    /// <summary>
    /// Operation name passed to <c>IModelRouter.RouteOperationAsync</c> to resolve
    /// the extraction LLM client. Should map to the economy tier in
    /// <c>ModelRoutingConfig.OperationOverrides</c>.
    /// </summary>
    public string RoutingOperationName { get; set; } = "fact_extraction";
}
```

- [ ] **Step 2: Add the property to AIConfig**

In `src/Content/Domain/Domain.Common/Config/AI/AIConfig.cs`, add the property after the `ModelRouting` property (around line 118). Add the new property and update the XML doc tree in the class remarks.

Add this property after the `ModelRouting` property:

```csharp
/// <summary>
/// Conversation-to-Knowledge Bridge configuration controlling automatic
/// fact extraction from agent turns into the knowledge graph.
/// </summary>
public KnowledgeBridgeConfig KnowledgeBridge { get; set; } = new();
```

And add this line to the configuration hierarchy XML doc (in the `<code>` block in the class remarks), after the `ModelRouting` line:

```
/// ├── KnowledgeBridge    — Conversation-to-Knowledge Bridge: fact extraction, confidence, timeout
```

- [ ] **Step 3: Verify build**

Run: `dotnet build src/AgenticHarness.slnx`
Expected: Build succeeds with 0 errors.

- [ ] **Step 4: Commit**

```bash
git add src/Content/Domain/Domain.Common/Config/AI/KnowledgeBridgeConfig.cs src/Content/Domain/Domain.Common/Config/AI/AIConfig.cs
git commit -m "feat(bridge): add KnowledgeBridgeConfig and wire into AIConfig"
```

---

### Task 3: Application Interface — IConversationFactExtractor

**Files:**
- Create: `src/Content/Application/Application.AI.Common/Interfaces/KnowledgeGraph/IConversationFactExtractor.cs`

- [ ] **Step 1: Create the interface**

```csharp
// src/Content/Application/Application.AI.Common/Interfaces/KnowledgeGraph/IConversationFactExtractor.cs
using Domain.AI.KnowledgeGraph.Models;

namespace Application.AI.Common.Interfaces.KnowledgeGraph;

/// <summary>
/// Extracts notable facts from a user/assistant message pair using an LLM.
/// Facts are returned as structured <see cref="ConversationFact"/> records
/// for persistence via <see cref="IKnowledgeMemory.RememberAsync"/>.
/// </summary>
/// <remarks>
/// Implementations should:
/// <list type="bullet">
///   <item><description>Use an economy-tier model via <c>IModelRouter.RouteOperationAsync</c></description></item>
///   <item><description>Return an empty list for routine turns (greetings, acknowledgments)</description></item>
///   <item><description>Catch all exceptions internally and return an empty list on failure</description></item>
///   <item><description>Wrap user content in XML tags to defend against prompt injection</description></item>
/// </list>
/// </remarks>
public interface IConversationFactExtractor
{
    /// <summary>
    /// Analyzes a conversation turn and extracts notable facts.
    /// </summary>
    /// <param name="userMessage">The user's message from this turn.</param>
    /// <param name="assistantResponse">The assistant's response from this turn.</param>
    /// <param name="conversationId">Conversation ID for deterministic key generation.</param>
    /// <param name="turnNumber">Turn number for deterministic key generation.</param>
    /// <param name="cancellationToken">Cancellation token with extraction timeout.</param>
    /// <returns>
    /// Extracted facts ordered by confidence descending. Empty list when no notable
    /// facts are found or when extraction fails.
    /// </returns>
    Task<IReadOnlyList<ConversationFact>> ExtractAsync(
        string userMessage,
        string assistantResponse,
        string conversationId,
        int turnNumber,
        CancellationToken cancellationToken = default);
}
```

- [ ] **Step 2: Verify build**

Run: `dotnet build src/AgenticHarness.slnx`
Expected: Build succeeds with 0 errors.

- [ ] **Step 3: Commit**

```bash
git add src/Content/Application/Application.AI.Common/Interfaces/KnowledgeGraph/IConversationFactExtractor.cs
git commit -m "feat(bridge): add IConversationFactExtractor interface"
```

---

### Task 4: Infrastructure Implementation — ConversationFactExtractor + Tests

**Files:**
- Create: `src/Content/Infrastructure/Infrastructure.AI.KnowledgeGraph/Memory/ConversationFactExtractor.cs`
- Create: `src/Content/Tests/Infrastructure.AI.KnowledgeGraph.Tests/Memory/ConversationFactExtractorTests.cs`

This task follows TDD. Write the tests first, then the implementation.

- [ ] **Step 1: Write the test class**

```csharp
// src/Content/Tests/Infrastructure.AI.KnowledgeGraph.Tests/Memory/ConversationFactExtractorTests.cs
using Application.AI.Common.Interfaces.Routing;
using Domain.AI.KnowledgeGraph.Models;
using Domain.AI.Routing.Models;
using Infrastructure.AI.KnowledgeGraph.Memory;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;
using FluentAssertions;

namespace Infrastructure.AI.KnowledgeGraph.Tests.Memory;

public class ConversationFactExtractorTests
{
    private readonly Mock<IModelRouter> _mockRouter = new();
    private readonly Mock<IChatClient> _mockClient = new();
    private readonly ConversationFactExtractor _sut;

    public ConversationFactExtractorTests()
    {
        var routingDecision = new ModelRoutingDecision
        {
            SelectedTier = new ModelTier
            {
                Name = "economy",
                ClientType = Domain.Common.Config.AI.AIAgentFrameworkClientType.OpenAI,
                DeploymentName = "gpt-4o-mini",
                EstimatedCostPer1KTokens = 0.00015m
            },
            Client = _mockClient.Object,
            Complexity = Domain.AI.Routing.Enums.TaskComplexity.Trivial,
            Source = Domain.AI.Routing.Enums.ClassificationSource.Heuristic,
            Confidence = 1.0
        };

        _mockRouter
            .Setup(r => r.RouteOperationAsync("fact_extraction", It.IsAny<CancellationToken>()))
            .ReturnsAsync(routingDecision);

        _sut = new ConversationFactExtractor(
            _mockRouter.Object,
            NullLogger<ConversationFactExtractor>.Instance);
    }

    [Fact]
    public async Task ExtractAsync_ValidJson_ReturnsFactsWithDeterministicKeys()
    {
        SetupLlmResponse("""
            [
              {"key": "user_prefers_postgresql", "content": "User prefers PostgreSQL", "entity_type": "Preference", "confidence": 0.92},
              {"key": "deadline_june", "content": "Deployment deadline is June 15", "entity_type": "Decision", "confidence": 0.88}
            ]
            """);

        var result = await _sut.ExtractAsync("I prefer PostgreSQL", "Noted, I'll use PostgreSQL", "conv-1", 3);

        result.Should().HaveCount(2);
        result[0].Key.Should().Be("conv-1:3:0");
        result[0].Content.Should().Be("User prefers PostgreSQL");
        result[0].EntityType.Should().Be("Preference");
        result[0].Confidence.Should().Be(0.92);
        result[1].Key.Should().Be("conv-1:3:1");
    }

    [Fact]
    public async Task ExtractAsync_EmptyArray_ReturnsEmptyList()
    {
        SetupLlmResponse("[]");

        var result = await _sut.ExtractAsync("run the tests", "Tests passed.", "conv-1", 1);

        result.Should().BeEmpty();
    }

    [Fact]
    public async Task ExtractAsync_MalformedJson_ReturnsEmptyList()
    {
        SetupLlmResponse("this is not json at all");

        var result = await _sut.ExtractAsync("hello", "hi there", "conv-1", 1);

        result.Should().BeEmpty();
    }

    [Fact]
    public async Task ExtractAsync_AllBelowConfidence_ReturnsEmptyList()
    {
        SetupLlmResponse("""
            [
              {"key": "low_conf", "content": "Might prefer dark mode", "entity_type": "Preference", "confidence": 0.3}
            ]
            """);

        var result = await _sut.ExtractAsync("maybe dark mode?", "I can set that up", "conv-1", 1);

        result.Should().BeEmpty();
    }

    [Fact]
    public async Task ExtractAsync_MixedConfidence_ReturnsOnlyAboveThreshold()
    {
        SetupLlmResponse("""
            [
              {"key": "high", "content": "PostgreSQL preferred", "entity_type": "Preference", "confidence": 0.9},
              {"key": "low", "content": "Maybe uses VS Code", "entity_type": "Fact", "confidence": 0.4},
              {"key": "medium", "content": "Project deadline June", "entity_type": "Decision", "confidence": 0.75}
            ]
            """);

        var result = await _sut.ExtractAsync("user msg", "assistant msg", "conv-1", 2);

        result.Should().HaveCount(2);
        result[0].Key.Should().Be("conv-1:2:0");
        result[0].Confidence.Should().Be(0.9);
        result[1].Key.Should().Be("conv-1:2:1");
        result[1].Confidence.Should().Be(0.75);
    }

    [Fact]
    public async Task ExtractAsync_LlmThrows_ReturnsEmptyList()
    {
        _mockClient
            .Setup(c => c.GetResponseAsync(
                It.IsAny<IList<ChatMessage>>(),
                It.IsAny<ChatOptions?>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("rate limited"));

        var result = await _sut.ExtractAsync("msg", "resp", "conv-1", 1);

        result.Should().BeEmpty();
    }

    [Fact]
    public async Task ExtractAsync_JsonWrappedInMarkdown_ExtractsCorrectly()
    {
        SetupLlmResponse("""
            ```json
            [
              {"key": "fact1", "content": "API rate limit is 1000/min", "entity_type": "Fact", "confidence": 0.85}
            ]
            ```
            """);

        var result = await _sut.ExtractAsync("what's the rate limit?", "The API rate limit is 1000 requests per minute.", "conv-1", 5);

        result.Should().HaveCount(1);
        result[0].Content.Should().Be("API rate limit is 1000/min");
    }

    [Fact]
    public async Task ExtractAsync_DefaultConfidenceThreshold_Is07()
    {
        SetupLlmResponse("""
            [
              {"key": "exactly_at", "content": "Exactly at threshold", "entity_type": "Fact", "confidence": 0.7},
              {"key": "just_below", "content": "Just below threshold", "entity_type": "Fact", "confidence": 0.69}
            ]
            """);

        var result = await _sut.ExtractAsync("msg", "resp", "conv-1", 1);

        result.Should().HaveCount(1);
        result[0].Content.Should().Be("Exactly at threshold");
    }

    private void SetupLlmResponse(string responseText)
    {
        var chatResponse = new ChatResponse(new ChatMessage(ChatRole.Assistant, responseText));
        _mockClient
            .Setup(c => c.GetResponseAsync(
                It.IsAny<IList<ChatMessage>>(),
                It.IsAny<ChatOptions?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(chatResponse);
    }
}
```

- [ ] **Step 2: Run tests — verify they fail**

Run: `dotnet test src/AgenticHarness.slnx --filter "FullyQualifiedName~ConversationFactExtractorTests" --no-build`
Expected: Build fails — `ConversationFactExtractor` class doesn't exist yet.

- [ ] **Step 3: Write the ConversationFactExtractor implementation**

```csharp
// src/Content/Infrastructure/Infrastructure.AI.KnowledgeGraph/Memory/ConversationFactExtractor.cs
using System.Text.Json;
using System.Text.Json.Serialization;
using Application.AI.Common.Interfaces.KnowledgeGraph;
using Application.AI.Common.Interfaces.Routing;
using Domain.AI.KnowledgeGraph.Models;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace Infrastructure.AI.KnowledgeGraph.Memory;

/// <summary>
/// Extracts structured facts from conversation turns using an economy-tier LLM.
/// Catches all exceptions internally — callers always receive a valid (possibly empty) list.
/// </summary>
public sealed class ConversationFactExtractor : IConversationFactExtractor
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private const double DefaultMinConfidence = 0.7;

    private readonly IModelRouter _modelRouter;
    private readonly ILogger<ConversationFactExtractor> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="ConversationFactExtractor"/> class.
    /// </summary>
    public ConversationFactExtractor(
        IModelRouter modelRouter,
        ILogger<ConversationFactExtractor> logger)
    {
        _modelRouter = modelRouter;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ConversationFact>> ExtractAsync(
        string userMessage,
        string assistantResponse,
        string conversationId,
        int turnNumber,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var client = (await _modelRouter.RouteOperationAsync("fact_extraction", cancellationToken)).Client;

            var prompt = BuildPrompt(userMessage, assistantResponse);
            var response = await client.GetResponseAsync(prompt, cancellationToken: cancellationToken);

            var json = response.Text ?? "[]";
            var facts = ParseFacts(json, conversationId, turnNumber);

            _logger.LogDebug(
                "Extracted {Count} facts from conversation {ConversationId} turn {Turn}",
                facts.Count, conversationId, turnNumber);

            return facts;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex,
                "Fact extraction failed for conversation {ConversationId} turn {Turn}",
                conversationId, turnNumber);
            return [];
        }
    }

    private static string BuildPrompt(string userMessage, string assistantResponse) =>
        $$"""
        You are a fact extraction system. Analyze the following conversation turn and extract notable facts that would be valuable in a future conversation with a different agent.

        Only extract facts that represent:
        - **Preference**: User likes/dislikes, workflow choices
        - **Decision**: Architectural or design decisions made
        - **Fact**: Stated facts about the project, team, or domain
        - **Correction**: User corrected the assistant

        Routine instructions, greetings, acknowledgments, and tool invocations should return an empty array.

        Return a JSON array (no markdown fencing). Each element:
        {"key": "snake_case_short_key", "content": "Human-readable fact description", "entity_type": "Preference|Decision|Fact|Correction", "confidence": 0.0-1.0}

        Return [] if no notable facts are present.

        <user_message>
        {{userMessage}}
        </user_message>

        <assistant_message>
        {{assistantResponse[..Math.Min(assistantResponse.Length, 2000)]}}
        </assistant_message>
        """;

    private static IReadOnlyList<ConversationFact> ParseFacts(
        string json, string conversationId, int turnNumber)
    {
        // Strip markdown code fences if present
        json = json.Trim();
        if (json.StartsWith("```"))
        {
            var firstNewline = json.IndexOf('\n');
            if (firstNewline >= 0) json = json[(firstNewline + 1)..];
            if (json.EndsWith("```")) json = json[..^3];
            json = json.Trim();
        }

        // Extract JSON array bounds
        var startIndex = json.IndexOf('[');
        var endIndex = json.LastIndexOf(']');
        if (startIndex < 0 || endIndex <= startIndex)
            return [];

        json = json[startIndex..(endIndex + 1)];

        List<RawFact>? rawFacts;
        try
        {
            rawFacts = JsonSerializer.Deserialize<List<RawFact>>(json, JsonOptions);
        }
        catch (JsonException)
        {
            return [];
        }

        if (rawFacts is null or { Count: 0 })
            return [];

        var factIndex = 0;
        return rawFacts
            .Where(f => f.Confidence >= DefaultMinConfidence)
            .Select(f => new ConversationFact
            {
                Key = $"{conversationId}:{turnNumber}:{factIndex++}",
                Content = f.Content,
                EntityType = f.EntityType ?? "Fact",
                Confidence = f.Confidence
            })
            .ToList();
    }

    private sealed record RawFact
    {
        [JsonPropertyName("key")]
        public string Key { get; init; } = string.Empty;

        [JsonPropertyName("content")]
        public string Content { get; init; } = string.Empty;

        [JsonPropertyName("entity_type")]
        public string? EntityType { get; init; }

        [JsonPropertyName("confidence")]
        public double Confidence { get; init; }
    }
}
```

- [ ] **Step 4: Run tests — verify they pass**

Run: `dotnet test src/AgenticHarness.slnx --filter "FullyQualifiedName~ConversationFactExtractorTests"`
Expected: All 8 tests pass.

- [ ] **Step 5: Commit**

```bash
git add src/Content/Infrastructure/Infrastructure.AI.KnowledgeGraph/Memory/ConversationFactExtractor.cs src/Content/Tests/Infrastructure.AI.KnowledgeGraph.Tests/Memory/ConversationFactExtractorTests.cs
git commit -m "feat(bridge): add ConversationFactExtractor with LLM-based extraction + tests"
```

---

### Task 5: Pipeline Behavior — KnowledgeExtractionBehavior + Tests

**Files:**
- Create: `src/Content/Application/Application.AI.Common/MediatRBehaviors/KnowledgeExtractionBehavior.cs`
- Create: `src/Content/Tests/Application.AI.Common.Tests/MediatRBehaviors/KnowledgeExtractionBehaviorTests.cs`

This task follows TDD. Write the tests first, then the implementation.

- [ ] **Step 1: Write the test class**

```csharp
// src/Content/Tests/Application.AI.Common.Tests/MediatRBehaviors/KnowledgeExtractionBehaviorTests.cs
using Application.AI.Common.Interfaces.KnowledgeGraph;
using Application.AI.Common.MediatRBehaviors;
using Application.Core.CQRS.Agents.ExecuteAgentTurn;
using Domain.AI.KnowledgeGraph.Models;
using Domain.Common.Config.AI;
using FluentAssertions;
using MediatR;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Application.AI.Common.Tests.MediatRBehaviors;

public class KnowledgeExtractionBehaviorTests
{
    private readonly Mock<IConversationFactExtractor> _mockExtractor = new();
    private readonly Mock<IKnowledgeMemory> _mockMemory = new();
    private readonly KnowledgeBridgeConfig _config;

    public KnowledgeExtractionBehaviorTests()
    {
        _config = new KnowledgeBridgeConfig { Enabled = true };
    }

    [Fact]
    public async Task Handle_NonAgentTurnRequest_PassesThrough()
    {
        var behavior = CreateBehavior<NonAgentRequest, string>();

        var result = await behavior.Handle(
            new NonAgentRequest(),
            () => Task.FromResult("passthrough"),
            CancellationToken.None);

        result.Should().Be("passthrough");
        _mockExtractor.Verify(
            e => e.ExtractAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task Handle_Disabled_SkipsExtraction()
    {
        _config.Enabled = false;
        var behavior = CreateAgentTurnBehavior();
        var command = CreateCommand("analyze this code");
        var response = CreateSuccessResponse("Here's my analysis...");

        var result = await behavior.Handle(command, () => Task.FromResult(response), CancellationToken.None);

        result.Should().BeSameAs(response);
        _mockExtractor.Verify(
            e => e.ExtractAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task Handle_FailedTurn_SkipsExtraction()
    {
        var behavior = CreateAgentTurnBehavior();
        var command = CreateCommand("do something");
        var response = new AgentTurnResult
        {
            Success = false,
            Response = "",
            UpdatedHistory = [],
            Error = "Agent error"
        };

        var result = await behavior.Handle(command, () => Task.FromResult(response), CancellationToken.None);

        result.Should().BeSameAs(response);
        _mockExtractor.Verify(
            e => e.ExtractAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task Handle_EmptyResponse_SkipsExtraction()
    {
        var behavior = CreateAgentTurnBehavior();
        var command = CreateCommand("hello");
        var response = CreateSuccessResponse("");

        var result = await behavior.Handle(command, () => Task.FromResult(response), CancellationToken.None);

        result.Should().BeSameAs(response);
        _mockExtractor.Verify(
            e => e.ExtractAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task Handle_SuccessfulTurn_ExtractsAndRemembersFacts()
    {
        var facts = new List<ConversationFact>
        {
            new() { Key = "conv-1:3:0", Content = "User prefers PostgreSQL", EntityType = "Preference", Confidence = 0.9 },
            new() { Key = "conv-1:3:1", Content = "Deadline is June 15", EntityType = "Decision", Confidence = 0.85 }
        };

        _mockExtractor
            .Setup(e => e.ExtractAsync("use PostgreSQL", "Noted.", "conv-1", 3, It.IsAny<CancellationToken>()))
            .ReturnsAsync(facts);

        var behavior = CreateAgentTurnBehavior();
        var command = CreateCommand("use PostgreSQL", "conv-1", 3);
        var response = CreateSuccessResponse("Noted.");

        var result = await behavior.Handle(command, () => Task.FromResult(response), CancellationToken.None);

        result.Should().BeSameAs(response);

        // Wait briefly for the fire-and-forget task to complete
        await Task.Delay(200);

        _mockMemory.Verify(m => m.RememberAsync("conv-1:3:0", "User prefers PostgreSQL", "Preference", It.IsAny<CancellationToken>()), Times.Once);
        _mockMemory.Verify(m => m.RememberAsync("conv-1:3:1", "Deadline is June 15", "Decision", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Handle_ExtractorThrows_ResponseStillReturned()
    {
        _mockExtractor
            .Setup(e => e.ExtractAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("LLM down"));

        var behavior = CreateAgentTurnBehavior();
        var command = CreateCommand("analyze this");
        var response = CreateSuccessResponse("Analysis complete.");

        var result = await behavior.Handle(command, () => Task.FromResult(response), CancellationToken.None);

        result.Should().BeSameAs(response);
        // No exception propagated — fire-and-forget absorbed it
    }

    [Fact]
    public async Task Handle_RememberAsyncThrows_ContinuesWithRemainingFacts()
    {
        var facts = new List<ConversationFact>
        {
            new() { Key = "conv-1:1:0", Content = "Fact A", Confidence = 0.9 },
            new() { Key = "conv-1:1:1", Content = "Fact B", Confidence = 0.85 }
        };

        _mockExtractor
            .Setup(e => e.ExtractAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(facts);

        _mockMemory
            .Setup(m => m.RememberAsync("conv-1:1:0", It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("graph full"));

        var behavior = CreateAgentTurnBehavior();
        var command = CreateCommand("msg", "conv-1", 1);
        var response = CreateSuccessResponse("resp");

        await behavior.Handle(command, () => Task.FromResult(response), CancellationToken.None);
        await Task.Delay(200);

        // Second fact should still be remembered despite first one throwing
        _mockMemory.Verify(m => m.RememberAsync("conv-1:1:1", "Fact B", "Fact", It.IsAny<CancellationToken>()), Times.Once);
    }

    // --- Helpers ---

    private KnowledgeExtractionBehavior<TRequest, TResponse> CreateBehavior<TRequest, TResponse>()
        where TRequest : notnull
    {
        return new KnowledgeExtractionBehavior<TRequest, TResponse>(
            _mockExtractor.Object,
            _mockMemory.Object,
            Options.Create(_config),
            NullLogger<KnowledgeExtractionBehavior<TRequest, TResponse>>.Instance);
    }

    private KnowledgeExtractionBehavior<ExecuteAgentTurnCommand, AgentTurnResult> CreateAgentTurnBehavior()
    {
        return new KnowledgeExtractionBehavior<ExecuteAgentTurnCommand, AgentTurnResult>(
            _mockExtractor.Object,
            _mockMemory.Object,
            Options.Create(_config),
            NullLogger<KnowledgeExtractionBehavior<ExecuteAgentTurnCommand, AgentTurnResult>>.Instance);
    }

    private static ExecuteAgentTurnCommand CreateCommand(
        string userMessage, string conversationId = "conv-1", int turnNumber = 1) =>
        new()
        {
            AgentName = "test-agent",
            UserMessage = userMessage,
            ConversationId = conversationId,
            TurnNumber = turnNumber
        };

    private static AgentTurnResult CreateSuccessResponse(string response) =>
        new()
        {
            Success = true,
            Response = response,
            UpdatedHistory = []
        };

    // Test-local request type that is NOT ExecuteAgentTurnCommand
    private record NonAgentRequest : IRequest<string>;
}
```

- [ ] **Step 2: Run tests — verify they fail**

Run: `dotnet test src/AgenticHarness.slnx --filter "FullyQualifiedName~KnowledgeExtractionBehaviorTests" --no-build`
Expected: Build fails — `KnowledgeExtractionBehavior` class doesn't exist yet.

- [ ] **Step 3: Write the KnowledgeExtractionBehavior implementation**

```csharp
// src/Content/Application/Application.AI.Common/MediatRBehaviors/KnowledgeExtractionBehavior.cs
using Application.AI.Common.Interfaces.KnowledgeGraph;
using Application.Core.CQRS.Agents.ExecuteAgentTurn;
using Domain.Common.Config.AI;
using MediatR;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Application.AI.Common.MediatRBehaviors;

/// <summary>
/// Post-turn pipeline behavior that extracts notable facts from agent conversations
/// and persists them to the knowledge graph via <see cref="IKnowledgeMemory"/>.
/// </summary>
/// <remarks>
/// <para>
/// Only activates for <see cref="ExecuteAgentTurnCommand"/> requests that produce
/// a successful <see cref="AgentTurnResult"/> with a non-empty response.
/// All other request types pass through untouched.
/// </para>
/// <para>
/// Extraction runs as fire-and-forget on a background thread. The agent's response
/// is returned immediately — extraction failures are logged but never propagate.
/// </para>
/// </remarks>
public sealed class KnowledgeExtractionBehavior<TRequest, TResponse>
    : IPipelineBehavior<TRequest, TResponse> where TRequest : notnull
{
    private readonly IConversationFactExtractor _extractor;
    private readonly IKnowledgeMemory _knowledgeMemory;
    private readonly KnowledgeBridgeConfig _config;
    private readonly ILogger<KnowledgeExtractionBehavior<TRequest, TResponse>> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="KnowledgeExtractionBehavior{TRequest, TResponse}"/> class.
    /// </summary>
    public KnowledgeExtractionBehavior(
        IConversationFactExtractor extractor,
        IKnowledgeMemory knowledgeMemory,
        IOptions<KnowledgeBridgeConfig> config,
        ILogger<KnowledgeExtractionBehavior<TRequest, TResponse>> logger)
    {
        _extractor = extractor;
        _knowledgeMemory = knowledgeMemory;
        _config = config.Value;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<TResponse> Handle(
        TRequest request,
        RequestHandlerDelegate<TResponse> next,
        CancellationToken cancellationToken)
    {
        var response = await next();

        if (!_config.Enabled)
            return response;

        if (request is not ExecuteAgentTurnCommand command ||
            response is not AgentTurnResult { Success: true, Response.Length: > 0 } turnResult)
            return response;

        _ = Task.Run(async () =>
        {
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                cts.CancelAfter(TimeSpan.FromSeconds(_config.ExtractionTimeoutSeconds));

                var facts = await _extractor.ExtractAsync(
                    command.UserMessage,
                    turnResult.Response,
                    command.ConversationId,
                    command.TurnNumber,
                    cts.Token);

                foreach (var fact in facts)
                {
                    try
                    {
                        await _knowledgeMemory.RememberAsync(
                            fact.Key, fact.Content, fact.EntityType, cts.Token);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        _logger.LogWarning(ex,
                            "Failed to persist fact {Key} for conversation {ConversationId}",
                            fact.Key, command.ConversationId);
                    }
                }

                if (facts.Count > 0)
                {
                    _logger.LogInformation(
                        "Persisted {Count} facts from conversation {ConversationId} turn {Turn}",
                        facts.Count, command.ConversationId, command.TurnNumber);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex,
                    "Knowledge extraction failed for conversation {ConversationId} turn {Turn}",
                    command.ConversationId, command.TurnNumber);
            }
        }, cancellationToken);

        return response;
    }
}
```

- [ ] **Step 4: Run tests — verify they pass**

Run: `dotnet test src/AgenticHarness.slnx --filter "FullyQualifiedName~KnowledgeExtractionBehaviorTests"`
Expected: All 7 tests pass.

- [ ] **Step 5: Commit**

```bash
git add src/Content/Application/Application.AI.Common/MediatRBehaviors/KnowledgeExtractionBehavior.cs src/Content/Tests/Application.AI.Common.Tests/MediatRBehaviors/KnowledgeExtractionBehaviorTests.cs
git commit -m "feat(bridge): add KnowledgeExtractionBehavior pipeline behavior + tests"
```

---

### Task 6: DI Registration — Wire Everything Together

**Files:**
- Modify: `src/Content/Application/Application.AI.Common/DependencyInjection.cs`
- Modify: `src/Content/Infrastructure/Infrastructure.AI.KnowledgeGraph/DependencyInjection.cs`
- Modify: `src/Content/Infrastructure/Infrastructure.AI/DependencyInjection.cs`

- [ ] **Step 1: Register the behavior in Application.AI.Common DI**

In `src/Content/Application/Application.AI.Common/DependencyInjection.cs`, add the behavior registration as the **last** behavior in the pipeline (after `ResponseSanitizationBehavior` on line 71). Add the required using statement.

Add this using at the top:
```csharp
using Domain.Common.Config.AI;
```

Add this line after `.AddTransient(typeof(IPipelineBehavior<,>), typeof(ResponseSanitizationBehavior<,>));` (line 71):

```csharp
            .AddTransient(typeof(IPipelineBehavior<,>), typeof(KnowledgeExtractionBehavior<,>));
```

Update the XML doc `<list>` in the class remarks to include the new behavior as the last item:
```xml
///   <item><description><c>KnowledgeExtractionBehavior</c> — post-turn: extracts facts to knowledge graph (fire-and-forget)</description></item>
```

- [ ] **Step 2: Register the extractor in Infrastructure.AI.KnowledgeGraph DI**

In `src/Content/Infrastructure/Infrastructure.AI.KnowledgeGraph/DependencyInjection.cs`, add the extractor registration after the `IKnowledgeMemory` registration block (after line 93). Add the required using statement.

Add the following after the `// Cross-session knowledge persistence` block:

```csharp
        // Conversation-to-Knowledge Bridge — LLM-based fact extraction from agent turns
        services.AddTransient<IConversationFactExtractor, ConversationFactExtractor>();
```

- [ ] **Step 3: Bind config in Infrastructure.AI DI**

In `src/Content/Infrastructure/Infrastructure.AI/DependencyInjection.cs`, add the config binding. Find the `ModelRouting` config line (line 222):

```csharp
services.AddSingleton(Options.Create(appConfig.AI.ModelRouting));
```

Add this line directly after it:

```csharp
services.AddSingleton(Options.Create(appConfig.AI.KnowledgeBridge));
```

- [ ] **Step 4: Verify build**

Run: `dotnet build src/AgenticHarness.slnx`
Expected: Build succeeds with 0 errors.

- [ ] **Step 5: Run all tests**

Run: `dotnet test src/AgenticHarness.slnx`
Expected: All existing tests still pass. The new tests (8 extractor + 7 behavior = 15) also pass.

- [ ] **Step 6: Commit**

```bash
git add src/Content/Application/Application.AI.Common/DependencyInjection.cs src/Content/Infrastructure/Infrastructure.AI.KnowledgeGraph/DependencyInjection.cs src/Content/Infrastructure/Infrastructure.AI/DependencyInjection.cs
git commit -m "feat(bridge): register KnowledgeExtractionBehavior, extractor, and config in DI"
```

---

### Task 7: Full Build Verification + Operation Override

**Files:**
- No new files — verification and config defaults only

- [ ] **Step 1: Add operation override to appsettings**

Check if `appsettings.json` has an `AI:ModelRouting:OperationOverrides` section. If it does, add `"fact_extraction": "economy"` to it. If no appsettings override exists, skip this step — the code already defaults to the economy tier via the `RoutingOperationName` config.

To find the appsettings file:
```bash
find src -name "appsettings*.json" | head -5
```

If found, verify the `OperationOverrides` section contains existing entries (like `raptor_summarization`, `crag_evaluation`, `graph_entity_extraction`) and add:
```json
"fact_extraction": "economy"
```

- [ ] **Step 2: Full build**

Run: `dotnet build src/AgenticHarness.slnx`
Expected: 0 errors, 0 warnings related to the bridge code.

- [ ] **Step 3: Run complete test suite**

Run: `dotnet test src/AgenticHarness.slnx`
Expected: All tests pass. Note any pre-existing failures (e.g., AgentHub) separately.

- [ ] **Step 4: Verify test count**

Confirm the 15 new tests appear:
```bash
dotnet test src/AgenticHarness.slnx --filter "FullyQualifiedName~ConversationFactExtractor" --list-tests
dotnet test src/AgenticHarness.slnx --filter "FullyQualifiedName~KnowledgeExtractionBehavior" --list-tests
```

Expected: 8 extractor tests + 7 behavior tests = 15 new tests.

- [ ] **Step 5: Final commit (if appsettings changed)**

```bash
git add <appsettings-path>
git commit -m "feat(bridge): add fact_extraction operation override to appsettings"
```

---

## Dependency Order

```
Task 1 (ConversationFact) ──┐
Task 2 (Config) ────────────┤
Task 3 (Interface) ─────────┼──► Task 4 (Extractor + Tests)
                             │
                             └──► Task 5 (Behavior + Tests)
                                         │
Task 4 + Task 5 ─────────────────────────┼──► Task 6 (DI Wiring)
                                                     │
                                                     └──► Task 7 (Verification)
```

Tasks 1, 2, 3 can run in parallel. Tasks 4 and 5 can run in parallel (after 1-3). Task 6 requires 4+5. Task 7 requires 6.
