# Tool Output Compression Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Compress large tool outputs before they enter conversation history, preserving decision-relevant information while reducing context window consumption. Full outputs persist in `IToolResultStore` for on-demand retrieval.

**Architecture:** Post-execution MediatR pipeline behavior (`ToolOutputCompressionBehavior`) runs after `ResponseSanitizationBehavior`. Hybrid classification (tool metadata preferred, content sniffing fallback) routes to keyed compression strategies (JSON, StructuredText, FreeText). Tiered: heuristics first, LLM fallback for FreeText. Dual-store: compressed version in conversation history, full output in `IToolResultStore`.

**Tech Stack:** C# .NET 10, MediatR pipeline behaviors, `System.Text.Json`, `Microsoft.Extensions.AI`, xUnit + Moq + FluentAssertions

---

## File Structure

| Layer | File | Responsibility |
|-------|------|---------------|
| Domain | `Domain.AI/Compression/Enums/ToolOutputCategory.cs` | Output type classification enum |
| Domain | `Domain.AI/Compression/Models/CompressionResult.cs` | Compression outcome record |
| Domain | `Domain.Common/Config/AI/ToolOutputCompressionConfig.cs` | Config POCO with thresholds and toggles |
| Application | `Application.AI.Common/Interfaces/Compression/IToolOutputCompressor.cs` | Entry-point interface for compression orchestration |
| Application | `Application.AI.Common/Interfaces/Compression/ICompressionStrategy.cs` | Strategy interface for category-specific compression |
| Application | `Application.AI.Common/MediatRBehaviors/ToolOutputCompressionBehavior.cs` | Pipeline behavior wiring compression into MediatR |
| Infrastructure | `Infrastructure.AI/Compression/ContentTypeDetector.cs` | Static content sniffing for output classification |
| Infrastructure | `Infrastructure.AI/Compression/ToolOutputCompressor.cs` | Orchestrates strategy dispatch + tiered fallback |
| Infrastructure | `Infrastructure.AI/Compression/Strategies/JsonCompressionStrategy.cs` | JSON array truncation, depth pruning, key filtering |
| Infrastructure | `Infrastructure.AI/Compression/Strategies/StructuredTextCompressionStrategy.cs` | Head/tail preservation, deduplication |
| Infrastructure | `Infrastructure.AI/Compression/Strategies/FreeTextCompressionStrategy.cs` | Sentence truncation + LLM fallback |
| Tests | `Infrastructure.AI.Tests/Compression/ContentTypeDetectorTests.cs` | 6 tests |
| Tests | `Infrastructure.AI.Tests/Compression/Strategies/JsonCompressionStrategyTests.cs` | 7 tests |
| Tests | `Infrastructure.AI.Tests/Compression/Strategies/StructuredTextCompressionStrategyTests.cs` | 5 tests |
| Tests | `Infrastructure.AI.Tests/Compression/Strategies/FreeTextCompressionStrategyTests.cs` | 5 tests |
| Tests | `Infrastructure.AI.Tests/Compression/ToolOutputCompressorTests.cs` | 6 tests |
| Tests | `Application.AI.Common.Tests/MediatRBehaviors/ToolOutputCompressionBehaviorTests.cs` | 7 tests |

**Modifications:**
- `Domain.Common/Config/AI/AIConfig.cs` — add `ToolOutputCompression` property
- `Application.AI.Common/Interfaces/Tools/ITool.cs` — add `OutputCategory` and `CompressionTokenThreshold` properties
- `Infrastructure.AI/DependencyInjection.cs` — register compressor, strategies, config
- `Application.AI.Common/DependencyInjection.cs` — register behavior after `ResponseSanitizationBehavior`

---

### Task 1: Domain Models — ToolOutputCategory Enum + CompressionResult Record

**Files:**
- Create: `src/Content/Domain/Domain.AI/Compression/Enums/ToolOutputCategory.cs`
- Create: `src/Content/Domain/Domain.AI/Compression/Models/CompressionResult.cs`

- [ ] **Step 1: Create ToolOutputCategory enum**

```csharp
// src/Content/Domain/Domain.AI/Compression/Enums/ToolOutputCategory.cs
namespace Domain.AI.Compression.Enums;

/// <summary>
/// Classifies tool output content for compression strategy selection.
/// Tools declare this via <c>ITool.OutputCategory</c>; when not declared,
/// <c>ContentTypeDetector</c> infers it from content structure.
/// </summary>
public enum ToolOutputCategory
{
    /// <summary>Parseable JSON — API responses, structured data.</summary>
    Json = 0,

    /// <summary>Source code, configuration files, documents with line structure.</summary>
    FileContent = 1,

    /// <summary>Multiple results with repeated structure (search hits, log entries).</summary>
    SearchResults = 2,

    /// <summary>Tab/pipe-delimited rows with consistent column count.</summary>
    Tabular = 3,

    /// <summary>Unstructured prose, error output, logs without repeated structure.</summary>
    FreeText = 4
}
```

- [ ] **Step 2: Create CompressionResult record**

```csharp
// src/Content/Domain/Domain.AI/Compression/Models/CompressionResult.cs
namespace Domain.AI.Compression.Models;

/// <summary>
/// Outcome of compressing a tool output. Carries the compressed text,
/// token metrics, and which strategy produced the result.
/// </summary>
public sealed record CompressionResult
{
    /// <summary>The compressed (or original) output text.</summary>
    public required string Output { get; init; }

    /// <summary>Estimated token count of the original output.</summary>
    public required int OriginalTokens { get; init; }

    /// <summary>Estimated token count after compression.</summary>
    public required int CompressedTokens { get; init; }

    /// <summary>Name of the strategy that produced this result (e.g., "Json", "LlmFallback", "HardTruncate").</summary>
    public required string Strategy { get; init; }

    /// <summary>Whether compression was actually applied (false when output was below threshold).</summary>
    public required bool WasCompressed { get; init; }

    /// <summary>Creates a passthrough result for outputs below threshold.</summary>
    public static CompressionResult Passthrough(string output, int tokens) => new()
    {
        Output = output,
        OriginalTokens = tokens,
        CompressedTokens = tokens,
        Strategy = "None",
        WasCompressed = false
    };
}
```

- [ ] **Step 3: Verify build**

Run: `dotnet build src/Content/Domain/Domain.AI/Domain.AI.csproj`
Expected: Build succeeded. 0 errors.

- [ ] **Step 4: Commit**

```bash
git add src/Content/Domain/Domain.AI/Compression/
git commit -m "feat(compression): add ToolOutputCategory enum and CompressionResult record"
```

---

### Task 2: ToolOutputCompressionConfig + AIConfig Property

**Files:**
- Create: `src/Content/Domain/Domain.Common/Config/AI/ToolOutputCompressionConfig.cs`
- Modify: `src/Content/Domain/Domain.Common/Config/AI/AIConfig.cs`

- [ ] **Step 1: Create config POCO**

```csharp
// src/Content/Domain/Domain.Common/Config/AI/ToolOutputCompressionConfig.cs
namespace Domain.Common.Config.AI;

/// <summary>
/// Configuration for the tool output compression subsystem.
/// Bound from <c>AppConfig.AI.ToolOutputCompression</c>.
/// </summary>
public sealed class ToolOutputCompressionConfig
{
    /// <summary>Master toggle. When false, all outputs pass through uncompressed.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Default token threshold that triggers compression. Outputs below this
    /// pass through untouched. Individual tools can override via
    /// <c>ITool.CompressionTokenThreshold</c>.
    /// </summary>
    public int DefaultTokenThreshold { get; set; } = 2000;

    /// <summary>
    /// Whether to use an LLM for summarization when heuristic strategies
    /// produce a result that still exceeds the threshold on FreeText content.
    /// </summary>
    public bool LlmFallbackEnabled { get; set; } = true;

    /// <summary>Timeout in seconds for LLM fallback calls.</summary>
    public int LlmFallbackTimeoutSeconds { get; set; } = 5;

    /// <summary>
    /// Operation name passed to <c>IModelRouter.RouteOperationAsync</c>
    /// to resolve the economy-tier model for LLM compression.
    /// </summary>
    public string LlmRoutingOperation { get; set; } = "output_compression";
}
```

- [ ] **Step 2: Add ToolOutputCompression property to AIConfig**

In `src/Content/Domain/Domain.Common/Config/AI/AIConfig.cs`, add after the `Sandbox` property (line ~150):

```csharp
    /// <summary>
    /// Tool output compression configuration: thresholds, LLM fallback,
    /// and strategy selection for reducing context window consumption.
    /// </summary>
    public ToolOutputCompressionConfig ToolOutputCompression { get; set; } = new();
```

Also add `ToolOutputCompression` to the XML doc hierarchy comment in the `<code>` block, after `└── Sandbox`:

```
    /// ├── Sandbox           — Sandbox execution: resource limits, isolation, containers
    /// └── ToolOutputCompression — Tool output compression: thresholds, LLM fallback, strategies
```

- [ ] **Step 3: Verify build**

Run: `dotnet build src/Content/Domain/Domain.Common/Domain.Common.csproj`
Expected: Build succeeded. 0 errors.

- [ ] **Step 4: Commit**

```bash
git add src/Content/Domain/Domain.Common/Config/AI/ToolOutputCompressionConfig.cs
git add src/Content/Domain/Domain.Common/Config/AI/AIConfig.cs
git commit -m "feat(compression): add ToolOutputCompressionConfig and wire into AIConfig"
```

---

### Task 3: Application Interfaces — ICompressionStrategy + IToolOutputCompressor

**Files:**
- Create: `src/Content/Application/Application.AI.Common/Interfaces/Compression/ICompressionStrategy.cs`
- Create: `src/Content/Application/Application.AI.Common/Interfaces/Compression/IToolOutputCompressor.cs`

- [ ] **Step 1: Create ICompressionStrategy**

```csharp
// src/Content/Application/Application.AI.Common/Interfaces/Compression/ICompressionStrategy.cs
using Domain.AI.Compression.Enums;
using Domain.AI.Compression.Models;

namespace Application.AI.Common.Interfaces.Compression;

/// <summary>
/// Category-specific compression strategy for tool outputs.
/// Implementations are registered via keyed DI by <see cref="ToolOutputCategory"/>
/// and resolved by <see cref="IToolOutputCompressor"/> during compression.
/// </summary>
/// <remarks>
/// Strategies must be deterministic and side-effect-free (except
/// <c>FreeTextCompressionStrategy</c> which may call an LLM as fallback).
/// Return <c>CompressionResult.WasCompressed = false</c> to signal that
/// this strategy could not handle the content and the compressor should
/// fall through to the next strategy.
/// </remarks>
public interface ICompressionStrategy
{
    /// <summary>Returns whether this strategy can handle the given output category.</summary>
    bool CanHandle(ToolOutputCategory category);

    /// <summary>
    /// Compresses the output to fit within the token threshold.
    /// </summary>
    /// <param name="output">The tool output string to compress.</param>
    /// <param name="tokenThreshold">Target maximum token count.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// Compression result. Set <c>WasCompressed = false</c> if the strategy
    /// cannot handle this content (e.g., JSON parse failure) to signal fallthrough.
    /// </returns>
    Task<CompressionResult> CompressAsync(
        string output,
        int tokenThreshold,
        CancellationToken cancellationToken = default);
}
```

- [ ] **Step 2: Create IToolOutputCompressor**

```csharp
// src/Content/Application/Application.AI.Common/Interfaces/Compression/IToolOutputCompressor.cs
using Domain.AI.Compression.Enums;
using Domain.AI.Compression.Models;

namespace Application.AI.Common.Interfaces.Compression;

/// <summary>
/// Orchestrates tool output compression by dispatching to the appropriate
/// <see cref="ICompressionStrategy"/> based on output category, applying
/// tiered fallback (heuristic → LLM → hard truncation).
/// </summary>
public interface IToolOutputCompressor
{
    /// <summary>
    /// Compresses tool output to fit within the token threshold.
    /// </summary>
    /// <param name="output">The raw tool output to compress.</param>
    /// <param name="category">The classified output category.</param>
    /// <param name="tokenThreshold">Maximum token count for the result.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// The compression result. Always returns a valid result — never throws.
    /// On internal failure, returns hard-truncated output.
    /// </returns>
    Task<CompressionResult> CompressAsync(
        string output,
        ToolOutputCategory category,
        int tokenThreshold,
        CancellationToken cancellationToken = default);
}
```

- [ ] **Step 3: Verify build**

Run: `dotnet build src/Content/Application/Application.AI.Common/Application.AI.Common.csproj`
Expected: Build succeeded. 0 errors.

- [ ] **Step 4: Commit**

```bash
git add src/Content/Application/Application.AI.Common/Interfaces/Compression/
git commit -m "feat(compression): add ICompressionStrategy and IToolOutputCompressor interfaces"
```

---

### Task 4: ITool Modification — Add OutputCategory + CompressionTokenThreshold

**Files:**
- Modify: `src/Content/Application/Application.AI.Common/Interfaces/Tools/ITool.cs`

- [ ] **Step 1: Add optional properties to ITool**

In `src/Content/Application/Application.AI.Common/Interfaces/Tools/ITool.cs`, add after the `IsConcurrencySafe` property (line ~62):

```csharp
    /// <summary>
    /// Declares the expected output content type for compression strategy selection.
    /// When null, <c>ContentTypeDetector</c> sniffs the output at runtime.
    /// </summary>
    Domain.AI.Compression.Enums.ToolOutputCategory? OutputCategory => null;

    /// <summary>
    /// Per-tool compression token threshold override. When null, falls back
    /// to <c>ToolOutputCompressionConfig.DefaultTokenThreshold</c>.
    /// </summary>
    int? CompressionTokenThreshold => null;
```

- [ ] **Step 2: Verify build**

Run: `dotnet build src/AgenticHarness.slnx`
Expected: Build succeeded. 0 errors. Default interface implementations ensure no existing ITool implementors break.

- [ ] **Step 3: Commit**

```bash
git add src/Content/Application/Application.AI.Common/Interfaces/Tools/ITool.cs
git commit -m "feat(compression): add OutputCategory and CompressionTokenThreshold to ITool"
```

---

### Task 5: ContentTypeDetector + Tests

**Files:**
- Create: `src/Content/Infrastructure/Infrastructure.AI/Compression/ContentTypeDetector.cs`
- Create: `src/Content/Tests/Infrastructure.AI.Tests/Compression/ContentTypeDetectorTests.cs`

- [ ] **Step 1: Write the tests**

```csharp
// src/Content/Tests/Infrastructure.AI.Tests/Compression/ContentTypeDetectorTests.cs
using Domain.AI.Compression.Enums;
using FluentAssertions;
using Infrastructure.AI.Compression;
using Xunit;

namespace Infrastructure.AI.Tests.Compression;

public sealed class ContentTypeDetectorTests
{
    [Fact]
    public void Detect_ValidJsonObject_ReturnsJson()
    {
        var input = """{"name": "test", "value": 42}""";

        ContentTypeDetector.Detect(input).Should().Be(ToolOutputCategory.Json);
    }

    [Fact]
    public void Detect_ValidJsonArray_ReturnsJson()
    {
        var input = """[{"id": 1}, {"id": 2}]""";

        ContentTypeDetector.Detect(input).Should().Be(ToolOutputCategory.Json);
    }

    [Fact]
    public void Detect_FilePathsWithLineNumbers_ReturnsFileContent()
    {
        var input = """
            src/Program.cs:10: public static void Main()
            src/Program.cs:11: {
            src/Program.cs:12:     Console.WriteLine("Hello");
            src/Program.cs:13: }
            """;

        ContentTypeDetector.Detect(input).Should().Be(ToolOutputCategory.FileContent);
    }

    [Fact]
    public void Detect_TabDelimitedRows_ReturnsTabular()
    {
        var input = "Name\tAge\tCity\nAlice\t30\tSeattle\nBob\t25\tPortland\nCharlie\t35\tDenver\n";

        ContentTypeDetector.Detect(input).Should().Be(ToolOutputCategory.Tabular);
    }

    [Fact]
    public void Detect_RepeatedStructuredLines_ReturnsSearchResults()
    {
        var lines = string.Join('\n', Enumerable.Range(1, 20)
            .Select(i => $"Result {i}: Found match in file{i}.cs at line {i * 10}"));

        ContentTypeDetector.Detect(lines).Should().Be(ToolOutputCategory.SearchResults);
    }

    [Fact]
    public void Detect_PlainProse_ReturnsFreeText()
    {
        var input = "The quick brown fox jumps over the lazy dog. This is a paragraph of unstructured text that doesn't match any pattern.";

        ContentTypeDetector.Detect(input).Should().Be(ToolOutputCategory.FreeText);
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    public void Detect_EmptyOrNull_ReturnsFreeText(string? input)
    {
        ContentTypeDetector.Detect(input!).Should().Be(ToolOutputCategory.FreeText);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test src/Content/Tests/Infrastructure.AI.Tests/Infrastructure.AI.Tests.csproj --filter "FullyQualifiedName~ContentTypeDetectorTests" --no-build 2>&1 || echo "Expected: build failure because ContentTypeDetector does not exist yet"`
Expected: Build failure — `ContentTypeDetector` does not exist.

- [ ] **Step 3: Implement ContentTypeDetector**

```csharp
// src/Content/Infrastructure/Infrastructure.AI/Compression/ContentTypeDetector.cs
using System.Text.Json;
using System.Text.RegularExpressions;
using Domain.AI.Compression.Enums;

namespace Infrastructure.AI.Compression;

/// <summary>
/// Sniffs tool output content to infer <see cref="ToolOutputCategory"/>
/// when the tool does not declare one via <c>ITool.OutputCategory</c>.
/// Detection order: JSON → FileContent → Tabular → SearchResults → FreeText.
/// </summary>
public static partial class ContentTypeDetector
{
    [GeneratedRegex(@"^[\s]*[\w\./\\]+\.\w+:\d+:", RegexOptions.Multiline)]
    private static partial Regex FilePathLineNumberPattern();

    [GeneratedRegex(@"^.+\t.+\t.+$", RegexOptions.Multiline)]
    private static partial Regex TabDelimitedPattern();

    /// <summary>
    /// Detects the output category from content structure.
    /// Returns <see cref="ToolOutputCategory.FreeText"/> for null, empty, or unrecognized content.
    /// </summary>
    public static ToolOutputCategory Detect(string? output)
    {
        if (string.IsNullOrWhiteSpace(output))
            return ToolOutputCategory.FreeText;

        var trimmed = output.AsSpan().Trim();
        if (trimmed.Length == 0)
            return ToolOutputCategory.FreeText;

        if (IsJson(trimmed))
            return ToolOutputCategory.Json;

        var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries);

        if (lines.Length >= 3 && FilePathLineNumberPattern().IsMatch(output))
            return ToolOutputCategory.FileContent;

        if (IsTabular(lines))
            return ToolOutputCategory.Tabular;

        if (IsRepeatedStructure(lines))
            return ToolOutputCategory.SearchResults;

        return ToolOutputCategory.FreeText;
    }

    private static bool IsJson(ReadOnlySpan<char> trimmed)
    {
        if ((trimmed[0] != '{' && trimmed[0] != '[') ||
            (trimmed[^1] != '}' && trimmed[^1] != ']'))
            return false;

        try
        {
            using var doc = JsonDocument.Parse(trimmed.ToString());
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool IsTabular(string[] lines)
    {
        if (lines.Length < 3)
            return false;

        var firstTabCount = lines[0].Count(c => c == '\t');
        if (firstTabCount < 2)
            return false;

        var matchingLines = lines.Count(l => l.Count(c => c == '\t') == firstTabCount);
        return matchingLines >= lines.Length * 0.7;
    }

    private static bool IsRepeatedStructure(string[] lines)
    {
        if (lines.Length < 10)
            return false;

        var prefixPattern = ExtractPrefix(lines[0]);
        if (prefixPattern.Length < 3)
            return false;

        var matchCount = lines.Count(l => l.StartsWith(prefixPattern, StringComparison.Ordinal));
        return matchCount >= lines.Length * 0.6;
    }

    private static string ExtractPrefix(string line)
    {
        var colonIndex = line.IndexOf(':');
        if (colonIndex > 0 && colonIndex < 30)
            return line[..(colonIndex + 1)];

        var spaceIndex = line.IndexOf(' ');
        if (spaceIndex > 0 && spaceIndex < 20)
            return line[..spaceIndex];

        return line.Length > 10 ? line[..10] : line;
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test src/Content/Tests/Infrastructure.AI.Tests/Infrastructure.AI.Tests.csproj --filter "FullyQualifiedName~ContentTypeDetectorTests"`
Expected: All 7 tests pass.

- [ ] **Step 5: Commit**

```bash
git add src/Content/Infrastructure/Infrastructure.AI/Compression/ContentTypeDetector.cs
git add src/Content/Tests/Infrastructure.AI.Tests/Compression/ContentTypeDetectorTests.cs
git commit -m "feat(compression): add ContentTypeDetector with content sniffing + 7 tests"
```

---

### Task 6: JsonCompressionStrategy + Tests

**Files:**
- Create: `src/Content/Infrastructure/Infrastructure.AI/Compression/Strategies/JsonCompressionStrategy.cs`
- Create: `src/Content/Tests/Infrastructure.AI.Tests/Compression/Strategies/JsonCompressionStrategyTests.cs`

- [ ] **Step 1: Write the tests**

```csharp
// src/Content/Tests/Infrastructure.AI.Tests/Compression/Strategies/JsonCompressionStrategyTests.cs
using Domain.AI.Compression.Enums;
using FluentAssertions;
using Infrastructure.AI.Compression.Strategies;
using Xunit;

namespace Infrastructure.AI.Tests.Compression.Strategies;

public sealed class JsonCompressionStrategyTests
{
    private readonly JsonCompressionStrategy _sut = new();

    [Fact]
    public void CanHandle_Json_ReturnsTrue()
    {
        _sut.CanHandle(ToolOutputCategory.Json).Should().BeTrue();
    }

    [Fact]
    public void CanHandle_FreeText_ReturnsFalse()
    {
        _sut.CanHandle(ToolOutputCategory.FreeText).Should().BeFalse();
    }

    [Fact]
    public async Task CompressAsync_LargeArray_TruncatesWithCount()
    {
        var items = Enumerable.Range(1, 20).Select(i => new { id = i, name = $"item{i}" });
        var json = System.Text.Json.JsonSerializer.Serialize(items);

        var result = await _sut.CompressAsync(json, 50);

        result.WasCompressed.Should().BeTrue();
        result.Output.Should().Contain("items omitted");
        result.CompressedTokens.Should().BeLessThan(result.OriginalTokens);
    }

    [Fact]
    public async Task CompressAsync_DeeplyNested_PrunesAtDepth4()
    {
        var json = """{"a":{"b":{"c":{"d":{"e":{"f":"deep"}}}}}}""";

        var result = await _sut.CompressAsync(json, 20);

        result.WasCompressed.Should().BeTrue();
        result.Output.Should().Contain("nested object");
    }

    [Fact]
    public async Task CompressAsync_SmallJson_ReturnsPassthrough()
    {
        var json = """{"name":"test"}""";

        var result = await _sut.CompressAsync(json, 100);

        result.WasCompressed.Should().BeFalse();
        result.Output.Should().Be(json);
    }

    [Fact]
    public async Task CompressAsync_InvalidJson_ReturnsFalseWasCompressed()
    {
        var result = await _sut.CompressAsync("not json {{{", 50);

        result.WasCompressed.Should().BeFalse();
    }

    [Fact]
    public async Task CompressAsync_EmptyString_ReturnsPassthrough()
    {
        var result = await _sut.CompressAsync("", 100);

        result.WasCompressed.Should().BeFalse();
    }

    [Fact]
    public async Task CompressAsync_LowSignalKeys_RemovedWhenOverBudget()
    {
        var json = """{"data":"important","_links":{"self":"http://example.com"},"metadata":{"created":"2024-01-01"},"pagination":{"page":1,"total":100}}""";

        var result = await _sut.CompressAsync(json, 15);

        result.WasCompressed.Should().BeTrue();
        result.Output.Should().Contain("important");
        result.Output.Should().NotContain("_links");
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test src/Content/Tests/Infrastructure.AI.Tests/Infrastructure.AI.Tests.csproj --filter "FullyQualifiedName~JsonCompressionStrategyTests" --no-build 2>&1 || echo "Expected: build failure"`
Expected: Build failure — `JsonCompressionStrategy` does not exist.

- [ ] **Step 3: Implement JsonCompressionStrategy**

```csharp
// src/Content/Infrastructure/Infrastructure.AI/Compression/Strategies/JsonCompressionStrategy.cs
using System.Text.Json;
using System.Text.Json.Nodes;
using Application.AI.Common.Helpers;
using Application.AI.Common.Interfaces.Compression;
using Domain.AI.Compression.Enums;
using Domain.AI.Compression.Models;

namespace Infrastructure.AI.Compression.Strategies;

/// <summary>
/// Compresses JSON output by truncating large arrays, pruning deep nesting,
/// and removing low-signal keys. Returns <c>WasCompressed = false</c> on
/// parse failure to signal fallthrough to the next strategy.
/// </summary>
public sealed class JsonCompressionStrategy : ICompressionStrategy
{
    private const int MaxArrayElements = 10;
    private const int KeepFirst = 3;
    private const int KeepLast = 2;
    private const int MaxDepth = 4;

    private static readonly HashSet<string> LowSignalKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "metadata", "_links", "pagination", "headers", "timestamps",
        "_metadata", "links", "paging", "cursor"
    };

    /// <inheritdoc />
    public bool CanHandle(ToolOutputCategory category) => category == ToolOutputCategory.Json;

    /// <inheritdoc />
    public Task<CompressionResult> CompressAsync(
        string output, int tokenThreshold, CancellationToken cancellationToken = default)
    {
        var originalTokens = TokenEstimationHelper.EstimateTokens(output);

        if (string.IsNullOrEmpty(output) || originalTokens <= tokenThreshold)
            return Task.FromResult(CompressionResult.Passthrough(output, originalTokens));

        JsonNode? node;
        try
        {
            node = JsonNode.Parse(output);
        }
        catch (JsonException)
        {
            return Task.FromResult(new CompressionResult
            {
                Output = output,
                OriginalTokens = originalTokens,
                CompressedTokens = originalTokens,
                Strategy = "Json",
                WasCompressed = false
            });
        }

        if (node is null)
            return Task.FromResult(CompressionResult.Passthrough(output, originalTokens));

        TruncateArrays(node);
        PruneDepth(node, 0);

        var compressed = node.ToJsonString(new JsonSerializerOptions { WriteIndented = false });
        var compressedTokens = TokenEstimationHelper.EstimateTokens(compressed);

        if (compressedTokens > tokenThreshold)
        {
            RemoveLowSignalKeys(node);
            compressed = node.ToJsonString(new JsonSerializerOptions { WriteIndented = false });
            compressedTokens = TokenEstimationHelper.EstimateTokens(compressed);
        }

        return Task.FromResult(new CompressionResult
        {
            Output = compressed,
            OriginalTokens = originalTokens,
            CompressedTokens = compressedTokens,
            Strategy = "Json",
            WasCompressed = true
        });
    }

    private static void TruncateArrays(JsonNode node)
    {
        switch (node)
        {
            case JsonArray array when array.Count > MaxArrayElements:
            {
                var omitted = array.Count - KeepFirst - KeepLast;
                var kept = new List<JsonNode?>();
                for (var i = 0; i < KeepFirst && i < array.Count; i++)
                    kept.Add(array[i]?.DeepClone());
                kept.Add(JsonValue.Create($"... ({omitted} items omitted)"));
                for (var i = array.Count - KeepLast; i < array.Count; i++)
                    kept.Add(array[i]?.DeepClone());

                array.Clear();
                foreach (var item in kept)
                    array.Add(item);
                break;
            }
            case JsonArray array:
            {
                foreach (var item in array)
                    if (item is not null) TruncateArrays(item);
                break;
            }
            case JsonObject obj:
            {
                foreach (var (_, value) in obj)
                    if (value is not null) TruncateArrays(value);
                break;
            }
        }
    }

    private static void PruneDepth(JsonNode node, int depth)
    {
        if (node is not JsonObject obj) return;

        var toPrune = new List<string>();
        foreach (var (key, value) in obj)
        {
            if (value is JsonObject child)
            {
                if (depth >= MaxDepth)
                {
                    toPrune.Add(key);
                }
                else
                {
                    PruneDepth(child, depth + 1);
                }
            }
            else if (value is JsonArray arr)
            {
                foreach (var item in arr)
                    if (item is not null) PruneDepth(item, depth + 1);
            }
        }

        foreach (var key in toPrune)
        {
            var child = obj[key] as JsonObject;
            var keyCount = child?.Count ?? 0;
            obj[key] = JsonValue.Create($"[nested object with {keyCount} keys]");
        }
    }

    private static void RemoveLowSignalKeys(JsonNode node)
    {
        if (node is not JsonObject obj) return;

        var toRemove = obj
            .Where(kv => LowSignalKeys.Contains(kv.Key))
            .Select(kv => kv.Key)
            .ToList();

        foreach (var key in toRemove)
            obj.Remove(key);

        foreach (var (_, value) in obj)
            if (value is not null) RemoveLowSignalKeys(value);
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test src/Content/Tests/Infrastructure.AI.Tests/Infrastructure.AI.Tests.csproj --filter "FullyQualifiedName~JsonCompressionStrategyTests"`
Expected: All 8 tests pass.

- [ ] **Step 5: Commit**

```bash
git add src/Content/Infrastructure/Infrastructure.AI/Compression/Strategies/JsonCompressionStrategy.cs
git add src/Content/Tests/Infrastructure.AI.Tests/Compression/Strategies/JsonCompressionStrategyTests.cs
git commit -m "feat(compression): add JsonCompressionStrategy with array/depth/key pruning + 8 tests"
```

---

### Task 7: StructuredTextCompressionStrategy + Tests

**Files:**
- Create: `src/Content/Infrastructure/Infrastructure.AI/Compression/Strategies/StructuredTextCompressionStrategy.cs`
- Create: `src/Content/Tests/Infrastructure.AI.Tests/Compression/Strategies/StructuredTextCompressionStrategyTests.cs`

- [ ] **Step 1: Write the tests**

```csharp
// src/Content/Tests/Infrastructure.AI.Tests/Compression/Strategies/StructuredTextCompressionStrategyTests.cs
using Domain.AI.Compression.Enums;
using FluentAssertions;
using Infrastructure.AI.Compression.Strategies;
using Xunit;

namespace Infrastructure.AI.Tests.Compression.Strategies;

public sealed class StructuredTextCompressionStrategyTests
{
    private readonly StructuredTextCompressionStrategy _sut = new();

    [Fact]
    public void CanHandle_FileContent_ReturnsTrue()
    {
        _sut.CanHandle(ToolOutputCategory.FileContent).Should().BeTrue();
    }

    [Fact]
    public void CanHandle_SearchResults_ReturnsTrue()
    {
        _sut.CanHandle(ToolOutputCategory.SearchResults).Should().BeTrue();
    }

    [Fact]
    public void CanHandle_Tabular_ReturnsTrue()
    {
        _sut.CanHandle(ToolOutputCategory.Tabular).Should().BeTrue();
    }

    [Fact]
    public async Task CompressAsync_DuplicateLines_DeduplicatesWithCount()
    {
        var lines = string.Join('\n', Enumerable.Repeat("ERROR: Connection refused", 500));

        var result = await _sut.CompressAsync(lines, 50);

        result.WasCompressed.Should().BeTrue();
        result.Output.Should().Contain("similar lines omitted");
        result.CompressedTokens.Should().BeLessThan(result.OriginalTokens);
    }

    [Fact]
    public async Task CompressAsync_LongFile_PreservesHeadAndTail()
    {
        var lines = Enumerable.Range(1, 400).Select(i => $"Line {i}: content here").ToArray();
        var input = string.Join('\n', lines);

        var result = await _sut.CompressAsync(input, 100);

        result.WasCompressed.Should().BeTrue();
        result.Output.Should().Contain("Line 1:");
        result.Output.Should().Contain("Line 400:");
        result.Output.Should().Contain("lines omitted");
    }

    [Fact]
    public async Task CompressAsync_ShortOutput_ReturnsPassthrough()
    {
        var input = "Line 1\nLine 2\nLine 3";

        var result = await _sut.CompressAsync(input, 100);

        result.WasCompressed.Should().BeFalse();
        result.Output.Should().Be(input);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test src/Content/Tests/Infrastructure.AI.Tests/Infrastructure.AI.Tests.csproj --filter "FullyQualifiedName~StructuredTextCompressionStrategyTests" --no-build 2>&1 || echo "Expected: build failure"`
Expected: Build failure — `StructuredTextCompressionStrategy` does not exist.

- [ ] **Step 3: Implement StructuredTextCompressionStrategy**

```csharp
// src/Content/Infrastructure/Infrastructure.AI/Compression/Strategies/StructuredTextCompressionStrategy.cs
using Application.AI.Common.Helpers;
using Application.AI.Common.Interfaces.Compression;
using Domain.AI.Compression.Enums;
using Domain.AI.Compression.Models;

namespace Infrastructure.AI.Compression.Strategies;

/// <summary>
/// Compresses structured text (file content, search results, tabular data)
/// by deduplicating repeated lines and preserving head/tail with a summary
/// of omitted content.
/// </summary>
public sealed class StructuredTextCompressionStrategy : ICompressionStrategy
{
    private const int DefaultHeadLines = 40;
    private const int DefaultTailLines = 10;

    /// <inheritdoc />
    public bool CanHandle(ToolOutputCategory category) =>
        category is ToolOutputCategory.FileContent
            or ToolOutputCategory.SearchResults
            or ToolOutputCategory.Tabular;

    /// <inheritdoc />
    public Task<CompressionResult> CompressAsync(
        string output, int tokenThreshold, CancellationToken cancellationToken = default)
    {
        var originalTokens = TokenEstimationHelper.EstimateTokens(output);

        if (string.IsNullOrEmpty(output) || originalTokens <= tokenThreshold)
            return Task.FromResult(CompressionResult.Passthrough(output, originalTokens));

        var lines = output.Split('\n');

        var deduplicated = DeduplicateLines(lines);
        var deduplicatedText = string.Join('\n', deduplicated);
        var deduplicatedTokens = TokenEstimationHelper.EstimateTokens(deduplicatedText);

        if (deduplicatedTokens <= tokenThreshold)
        {
            return Task.FromResult(new CompressionResult
            {
                Output = deduplicatedText,
                OriginalTokens = originalTokens,
                CompressedTokens = deduplicatedTokens,
                Strategy = "StructuredText",
                WasCompressed = true
            });
        }

        var headTail = HeadTailPreserve(deduplicated, DefaultHeadLines, DefaultTailLines);
        var compressed = string.Join('\n', headTail);
        var compressedTokens = TokenEstimationHelper.EstimateTokens(compressed);

        return Task.FromResult(new CompressionResult
        {
            Output = compressed,
            OriginalTokens = originalTokens,
            CompressedTokens = compressedTokens,
            Strategy = "StructuredText",
            WasCompressed = true
        });
    }

    private static List<string> DeduplicateLines(string[] lines)
    {
        var result = new List<string>();
        var consecutiveCount = 1;
        string? previousLine = null;

        foreach (var line in lines)
        {
            if (line == previousLine)
            {
                consecutiveCount++;
                continue;
            }

            if (consecutiveCount > 1 && previousLine is not null)
            {
                result.Add($"[... {consecutiveCount - 1} similar lines omitted]");
            }

            result.Add(line);
            previousLine = line;
            consecutiveCount = 1;
        }

        if (consecutiveCount > 1)
            result.Add($"[... {consecutiveCount - 1} similar lines omitted]");

        return result;
    }

    private static List<string> HeadTailPreserve(List<string> lines, int headCount, int tailCount)
    {
        if (lines.Count <= headCount + tailCount)
            return lines;

        var result = new List<string>();
        result.AddRange(lines.Take(headCount));

        var omitted = lines.Count - headCount - tailCount;
        result.Add($"[... {omitted} lines omitted]");

        result.AddRange(lines.Skip(lines.Count - tailCount));

        return result;
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test src/Content/Tests/Infrastructure.AI.Tests/Infrastructure.AI.Tests.csproj --filter "FullyQualifiedName~StructuredTextCompressionStrategyTests"`
Expected: All 6 tests pass.

- [ ] **Step 5: Commit**

```bash
git add src/Content/Infrastructure/Infrastructure.AI/Compression/Strategies/StructuredTextCompressionStrategy.cs
git add src/Content/Tests/Infrastructure.AI.Tests/Compression/Strategies/StructuredTextCompressionStrategyTests.cs
git commit -m "feat(compression): add StructuredTextCompressionStrategy with dedup + head/tail + 6 tests"
```

---

### Task 8: FreeTextCompressionStrategy + Tests

**Files:**
- Create: `src/Content/Infrastructure/Infrastructure.AI/Compression/Strategies/FreeTextCompressionStrategy.cs`
- Create: `src/Content/Tests/Infrastructure.AI.Tests/Compression/Strategies/FreeTextCompressionStrategyTests.cs`

- [ ] **Step 1: Write the tests**

```csharp
// src/Content/Tests/Infrastructure.AI.Tests/Compression/Strategies/FreeTextCompressionStrategyTests.cs
using Application.AI.Common.Interfaces.Routing;
using Domain.AI.Compression.Enums;
using Domain.AI.Compression.Models;
using Domain.AI.Routing.Enums;
using Domain.AI.Routing.Models;
using Domain.Common.Config.AI;
using FluentAssertions;
using Infrastructure.AI.Compression.Strategies;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Infrastructure.AI.Tests.Compression.Strategies;

public sealed class FreeTextCompressionStrategyTests
{
    private readonly Mock<IModelRouter> _mockRouter = new();
    private readonly ToolOutputCompressionConfig _config = new() { LlmFallbackEnabled = true, LlmFallbackTimeoutSeconds = 5 };

    private FreeTextCompressionStrategy CreateSut() => new(
        _mockRouter.Object,
        Options.Create(_config),
        Mock.Of<ILogger<FreeTextCompressionStrategy>>());

    [Fact]
    public void CanHandle_FreeText_ReturnsTrue()
    {
        CreateSut().CanHandle(ToolOutputCategory.FreeText).Should().BeTrue();
    }

    [Fact]
    public async Task CompressAsync_LongProse_TruncatesAtSentenceBoundary()
    {
        var sentences = Enumerable.Range(1, 200)
            .Select(i => $"This is sentence number {i} with some additional words to make it longer.");
        var input = string.Join(' ', sentences);

        var result = await CreateSut().CompressAsync(input, 100);

        result.WasCompressed.Should().BeTrue();
        result.Output.Should().EndWith("[... remainder omitted]");
        result.CompressedTokens.Should().BeLessThanOrEqualTo(100);
    }

    [Fact]
    public async Task CompressAsync_ShortText_ReturnsPassthrough()
    {
        var input = "Short text.";

        var result = await CreateSut().CompressAsync(input, 100);

        result.WasCompressed.Should().BeFalse();
        result.Output.Should().Be(input);
    }

    [Fact]
    public async Task CompressAsync_LlmFallbackDisabled_HardTruncatesOnly()
    {
        _config.LlmFallbackEnabled = false;
        var input = string.Join(' ', Enumerable.Repeat("word", 5000));

        var result = await CreateSut().CompressAsync(input, 50);

        result.WasCompressed.Should().BeTrue();
        _mockRouter.Verify(
            r => r.RouteOperationAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task CompressAsync_LlmThrows_FallsBackToHardTruncation()
    {
        _mockRouter
            .Setup(r => r.RouteOperationAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("LLM unavailable"));

        var input = string.Join(' ', Enumerable.Repeat("word", 5000));

        var result = await CreateSut().CompressAsync(input, 50);

        result.WasCompressed.Should().BeTrue();
        result.Strategy.Should().Be("HardTruncate");
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test src/Content/Tests/Infrastructure.AI.Tests/Infrastructure.AI.Tests.csproj --filter "FullyQualifiedName~FreeTextCompressionStrategyTests" --no-build 2>&1 || echo "Expected: build failure"`
Expected: Build failure — `FreeTextCompressionStrategy` does not exist.

- [ ] **Step 3: Implement FreeTextCompressionStrategy**

```csharp
// src/Content/Infrastructure/Infrastructure.AI/Compression/Strategies/FreeTextCompressionStrategy.cs
using Application.AI.Common.Helpers;
using Application.AI.Common.Interfaces.Compression;
using Application.AI.Common.Interfaces.Routing;
using Domain.AI.Compression.Enums;
using Domain.AI.Compression.Models;
using Domain.Common.Config.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Infrastructure.AI.Compression.Strategies;

/// <summary>
/// Compresses unstructured text using sentence-boundary truncation.
/// When heuristic truncation still exceeds the threshold and LLM fallback
/// is enabled, calls an economy-tier model for intelligent summarization.
/// </summary>
public sealed class FreeTextCompressionStrategy : ICompressionStrategy
{
    private readonly IModelRouter _modelRouter;
    private readonly ToolOutputCompressionConfig _config;
    private readonly ILogger<FreeTextCompressionStrategy> _logger;

    public FreeTextCompressionStrategy(
        IModelRouter modelRouter,
        IOptions<ToolOutputCompressionConfig> config,
        ILogger<FreeTextCompressionStrategy> logger)
    {
        _modelRouter = modelRouter;
        _config = config.Value;
        _logger = logger;
    }

    /// <inheritdoc />
    public bool CanHandle(ToolOutputCategory category) => category == ToolOutputCategory.FreeText;

    /// <inheritdoc />
    public async Task<CompressionResult> CompressAsync(
        string output, int tokenThreshold, CancellationToken cancellationToken = default)
    {
        var originalTokens = TokenEstimationHelper.EstimateTokens(output);

        if (string.IsNullOrEmpty(output) || originalTokens <= tokenThreshold)
            return CompressionResult.Passthrough(output, originalTokens);

        var truncated = TruncateAtSentenceBoundary(output, tokenThreshold);
        var truncatedTokens = TokenEstimationHelper.EstimateTokens(truncated);

        if (truncatedTokens <= tokenThreshold)
        {
            return new CompressionResult
            {
                Output = truncated,
                OriginalTokens = originalTokens,
                CompressedTokens = truncatedTokens,
                Strategy = "FreeText",
                WasCompressed = true
            };
        }

        if (_config.LlmFallbackEnabled)
        {
            try
            {
                var llmResult = await SummarizeWithLlm(truncated, tokenThreshold, cancellationToken);
                if (llmResult is not null)
                    return new CompressionResult
                    {
                        Output = llmResult,
                        OriginalTokens = originalTokens,
                        CompressedTokens = TokenEstimationHelper.EstimateTokens(llmResult),
                        Strategy = "LlmFallback",
                        WasCompressed = true
                    };
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "LLM compression fallback failed, using hard truncation");
            }
        }

        var hardTruncated = TokenEstimationHelper.TruncateToTokenBudget(output, tokenThreshold);
        return new CompressionResult
        {
            Output = hardTruncated,
            OriginalTokens = originalTokens,
            CompressedTokens = TokenEstimationHelper.EstimateTokens(hardTruncated),
            Strategy = "HardTruncate",
            WasCompressed = true
        };
    }

    private static string TruncateAtSentenceBoundary(string text, int tokenThreshold)
    {
        var targetChars = (int)(tokenThreshold * 4 * 0.6);
        if (text.Length <= targetChars)
            return text;

        var sentenceEnders = new[] { '. ', '! ', '? ', ".\n", "!\n", "?\n" };
        var lastEnd = 0;

        for (var i = 0; i < text.Length && i < targetChars; i++)
        {
            foreach (var ender in sentenceEnders)
            {
                if (ender is string s)
                {
                    if (i + s.Length <= text.Length && text.Substring(i, s.Length) == s)
                        lastEnd = i + s.Length;
                }
                else if (text[i] == (char)ender)
                {
                    lastEnd = i + 1;
                }
            }
        }

        if (lastEnd == 0)
            lastEnd = Math.Min(targetChars, text.Length);

        return text[..lastEnd] + "[... remainder omitted]";
    }

    private async Task<string?> SummarizeWithLlm(
        string input, int tokenThreshold, CancellationToken cancellationToken)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(_config.LlmFallbackTimeoutSeconds));

        var decision = await _modelRouter.RouteOperationAsync(
            _config.LlmRoutingOperation, cts.Token);

        var prompt = $"Summarize this tool output in under {tokenThreshold} tokens. " +
                     "Preserve actionable information, specific values, and error details. " +
                     "Omit boilerplate.\n\n" + input;

        var response = await decision.Client.GetResponseAsync(prompt, cancellationToken: cts.Token);
        return response.Text;
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test src/Content/Tests/Infrastructure.AI.Tests/Infrastructure.AI.Tests.csproj --filter "FullyQualifiedName~FreeTextCompressionStrategyTests"`
Expected: All 5 tests pass.

- [ ] **Step 5: Commit**

```bash
git add src/Content/Infrastructure/Infrastructure.AI/Compression/Strategies/FreeTextCompressionStrategy.cs
git add src/Content/Tests/Infrastructure.AI.Tests/Compression/Strategies/FreeTextCompressionStrategyTests.cs
git commit -m "feat(compression): add FreeTextCompressionStrategy with LLM fallback + 5 tests"
```

---

### Task 9: ToolOutputCompressor + Tests

**Files:**
- Create: `src/Content/Infrastructure/Infrastructure.AI/Compression/ToolOutputCompressor.cs`
- Create: `src/Content/Tests/Infrastructure.AI.Tests/Compression/ToolOutputCompressorTests.cs`

- [ ] **Step 1: Write the tests**

```csharp
// src/Content/Tests/Infrastructure.AI.Tests/Compression/ToolOutputCompressorTests.cs
using Application.AI.Common.Interfaces.Compression;
using Domain.AI.Compression.Enums;
using Domain.AI.Compression.Models;
using FluentAssertions;
using Infrastructure.AI.Compression;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Infrastructure.AI.Tests.Compression;

public sealed class ToolOutputCompressorTests
{
    private readonly Mock<ICompressionStrategy> _mockJsonStrategy = new();
    private readonly Mock<ICompressionStrategy> _mockFreeTextStrategy = new();

    public ToolOutputCompressorTests()
    {
        _mockJsonStrategy.Setup(s => s.CanHandle(ToolOutputCategory.Json)).Returns(true);
        _mockFreeTextStrategy.Setup(s => s.CanHandle(ToolOutputCategory.FreeText)).Returns(true);
    }

    private ToolOutputCompressor CreateSut() => new(
        [_mockJsonStrategy.Object, _mockFreeTextStrategy.Object],
        Mock.Of<ILogger<ToolOutputCompressor>>());

    [Fact]
    public async Task CompressAsync_JsonCategory_RoutesToJsonStrategy()
    {
        _mockJsonStrategy
            .Setup(s => s.CompressAsync(It.IsAny<string>(), 100, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CompressionResult
            {
                Output = "compressed", OriginalTokens = 500, CompressedTokens = 50,
                Strategy = "Json", WasCompressed = true
            });

        var result = await CreateSut().CompressAsync("big json", ToolOutputCategory.Json, 100);

        result.Strategy.Should().Be("Json");
        _mockJsonStrategy.Verify(s => s.CompressAsync("big json", 100, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task CompressAsync_StrategyFails_FallsBackToFreeText()
    {
        _mockJsonStrategy
            .Setup(s => s.CompressAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CompressionResult
            {
                Output = "original", OriginalTokens = 500, CompressedTokens = 500,
                Strategy = "Json", WasCompressed = false
            });

        _mockFreeTextStrategy
            .Setup(s => s.CompressAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CompressionResult
            {
                Output = "fallback", OriginalTokens = 500, CompressedTokens = 50,
                Strategy = "FreeText", WasCompressed = true
            });

        var result = await CreateSut().CompressAsync("data", ToolOutputCategory.Json, 100);

        result.Strategy.Should().Be("FreeText");
    }

    [Fact]
    public async Task CompressAsync_StrategyThrows_FallsBackToHardTruncate()
    {
        _mockJsonStrategy
            .Setup(s => s.CompressAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("strategy error"));

        _mockFreeTextStrategy
            .Setup(s => s.CompressAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("also fails"));

        var result = await CreateSut().CompressAsync(new string('x', 1000), ToolOutputCategory.Json, 50);

        result.WasCompressed.Should().BeTrue();
        result.Strategy.Should().Be("HardTruncate");
    }

    [Fact]
    public async Task CompressAsync_UnknownCategory_FallsBackToFreeText()
    {
        _mockFreeTextStrategy
            .Setup(s => s.CompressAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CompressionResult
            {
                Output = "compressed", OriginalTokens = 500, CompressedTokens = 50,
                Strategy = "FreeText", WasCompressed = true
            });

        var result = await CreateSut().CompressAsync("data", (ToolOutputCategory)99, 100);

        result.Strategy.Should().Be("FreeText");
    }

    [Fact]
    public async Task CompressAsync_BelowThreshold_ReturnsPassthrough()
    {
        var result = await CreateSut().CompressAsync("short", ToolOutputCategory.Json, 100);

        result.WasCompressed.Should().BeFalse();
        result.Output.Should().Be("short");
        _mockJsonStrategy.Verify(
            s => s.CompressAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task CompressAsync_EmptyOutput_ReturnsPassthrough()
    {
        var result = await CreateSut().CompressAsync("", ToolOutputCategory.Json, 100);

        result.WasCompressed.Should().BeFalse();
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test src/Content/Tests/Infrastructure.AI.Tests/Infrastructure.AI.Tests.csproj --filter "FullyQualifiedName~ToolOutputCompressorTests" --no-build 2>&1 || echo "Expected: build failure"`
Expected: Build failure — `ToolOutputCompressor` does not exist.

- [ ] **Step 3: Implement ToolOutputCompressor**

```csharp
// src/Content/Infrastructure/Infrastructure.AI/Compression/ToolOutputCompressor.cs
using Application.AI.Common.Helpers;
using Application.AI.Common.Interfaces.Compression;
using Domain.AI.Compression.Enums;
using Domain.AI.Compression.Models;
using Microsoft.Extensions.Logging;

namespace Infrastructure.AI.Compression;

/// <summary>
/// Orchestrates tool output compression by dispatching to category-matched
/// strategies with fallback to FreeText and hard truncation as last resort.
/// </summary>
public sealed class ToolOutputCompressor : IToolOutputCompressor
{
    private readonly IReadOnlyList<ICompressionStrategy> _strategies;
    private readonly ILogger<ToolOutputCompressor> _logger;

    public ToolOutputCompressor(
        IEnumerable<ICompressionStrategy> strategies,
        ILogger<ToolOutputCompressor> logger)
    {
        _strategies = strategies.ToList();
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<CompressionResult> CompressAsync(
        string output,
        ToolOutputCategory category,
        int tokenThreshold,
        CancellationToken cancellationToken = default)
    {
        var originalTokens = TokenEstimationHelper.EstimateTokens(output);

        if (string.IsNullOrEmpty(output) || originalTokens <= tokenThreshold)
            return CompressionResult.Passthrough(output, originalTokens);

        var matched = _strategies.FirstOrDefault(s => s.CanHandle(category));

        if (matched is not null)
        {
            try
            {
                var result = await matched.CompressAsync(output, tokenThreshold, cancellationToken);
                if (result.WasCompressed)
                    return result;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex,
                    "Strategy {Strategy} failed for category {Category}, falling back",
                    matched.GetType().Name, category);
            }
        }

        if (category != ToolOutputCategory.FreeText)
        {
            var freeTextStrategy = _strategies.FirstOrDefault(s => s.CanHandle(ToolOutputCategory.FreeText));
            if (freeTextStrategy is not null)
            {
                try
                {
                    var fallbackResult = await freeTextStrategy.CompressAsync(
                        output, tokenThreshold, cancellationToken);
                    if (fallbackResult.WasCompressed)
                        return fallbackResult;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogWarning(ex, "FreeText fallback strategy also failed, hard truncating");
                }
            }
        }

        var truncated = TokenEstimationHelper.TruncateToTokenBudget(output, tokenThreshold);
        return new CompressionResult
        {
            Output = truncated,
            OriginalTokens = originalTokens,
            CompressedTokens = TokenEstimationHelper.EstimateTokens(truncated),
            Strategy = "HardTruncate",
            WasCompressed = true
        };
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test src/Content/Tests/Infrastructure.AI.Tests/Infrastructure.AI.Tests.csproj --filter "FullyQualifiedName~ToolOutputCompressorTests"`
Expected: All 6 tests pass.

- [ ] **Step 5: Commit**

```bash
git add src/Content/Infrastructure/Infrastructure.AI/Compression/ToolOutputCompressor.cs
git add src/Content/Tests/Infrastructure.AI.Tests/Compression/ToolOutputCompressorTests.cs
git commit -m "feat(compression): add ToolOutputCompressor with strategy dispatch + fallback + 6 tests"
```

---

### Task 10: ToolOutputCompressionBehavior + Tests

**Files:**
- Create: `src/Content/Application/Application.AI.Common/MediatRBehaviors/ToolOutputCompressionBehavior.cs`
- Create: `src/Content/Tests/Application.AI.Common.Tests/MediatRBehaviors/ToolOutputCompressionBehaviorTests.cs`

**Critical context:** The `ResponseSanitizationBehavior` at `src/Content/Application/Application.AI.Common/MediatRBehaviors/ResponseSanitizationBehavior.cs` shows the exact pattern for extracting and replacing tool output in the pipeline. The response can be either a bare `IToolResponse` or wrapped in `Result<T>`. The behavior uses `ExtractToolOutput()` and `ReplaceSanitizedOutput()` helper methods with reflection on `Result.Value` and `Result.Success()`. The compression behavior must follow the same pattern.

- [ ] **Step 1: Write the tests**

```csharp
// src/Content/Tests/Application.AI.Common.Tests/MediatRBehaviors/ToolOutputCompressionBehaviorTests.cs
using Application.AI.Common.Interfaces.Agent;
using Application.AI.Common.Interfaces.Compression;
using Application.AI.Common.Interfaces.Context;
using Application.AI.Common.Interfaces.MediatR;
using Application.AI.Common.MediatRBehaviors;
using Domain.AI.Compression.Enums;
using Domain.AI.Compression.Models;
using Domain.AI.Context;
using Domain.Common.Config.AI;
using FluentAssertions;
using MediatR;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Application.AI.Common.Tests.MediatRBehaviors;

public sealed class ToolOutputCompressionBehaviorTests
{
    private readonly Mock<IToolOutputCompressor> _mockCompressor = new();
    private readonly Mock<IToolResultStore> _mockStore = new();
    private readonly Mock<IAgentExecutionContext> _mockContext = new();
    private readonly ToolOutputCompressionConfig _config = new() { Enabled = true, DefaultTokenThreshold = 10 };

    private sealed record NonToolRequest : IRequest<string>;
    private sealed record ToolTestRequest(string ToolName) : IRequest<ToolTestResponse>, IToolRequest;
    private sealed record ToolTestResponse(string ToolOutput) : IToolResponse
    {
        public IToolResponse WithSanitizedOutput(string sanitizedOutput) =>
            new ToolTestResponse(sanitizedOutput);
    }

    public ToolOutputCompressionBehaviorTests()
    {
        _mockContext.Setup(c => c.ConversationId).Returns("conv-1");
    }

    private ToolOutputCompressionBehavior<TRequest, TResponse> CreateSut<TRequest, TResponse>()
        where TRequest : notnull => new(
        _mockCompressor.Object,
        _mockStore.Object,
        _mockContext.Object,
        Options.Create(_config),
        Mock.Of<ILogger<ToolOutputCompressionBehavior<TRequest, TResponse>>>());

    [Fact]
    public async Task Handle_NonToolRequest_PassesThrough()
    {
        var sut = CreateSut<NonToolRequest, string>();
        var result = await sut.Handle(new NonToolRequest(), () => Task.FromResult("hello"), CancellationToken.None);

        result.Should().Be("hello");
        _mockCompressor.Verify(
            c => c.CompressAsync(It.IsAny<string>(), It.IsAny<ToolOutputCategory>(), It.IsAny<int>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task Handle_BelowThreshold_PassesThrough()
    {
        _config.DefaultTokenThreshold = 10000;
        var sut = CreateSut<ToolTestRequest, ToolTestResponse>();
        var response = new ToolTestResponse("short");

        var result = await sut.Handle(
            new ToolTestRequest("tool1"),
            () => Task.FromResult(response),
            CancellationToken.None);

        result.ToolOutput.Should().Be("short");
    }

    [Fact]
    public async Task Handle_AboveThreshold_CompressesAndStoresReference()
    {
        var largeOutput = new string('x', 200);
        var response = new ToolTestResponse(largeOutput);

        _mockStore
            .Setup(s => s.StoreIfLargeAsync("conv-1", "tool1", null, largeOutput, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ToolResultReference
            {
                ResultId = "ref-123",
                ToolName = "tool1",
                PreviewContent = "xxx...",
                SizeChars = 200,
                Timestamp = DateTimeOffset.UtcNow
            });

        _mockCompressor
            .Setup(c => c.CompressAsync(largeOutput, It.IsAny<ToolOutputCategory>(), _config.DefaultTokenThreshold, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CompressionResult
            {
                Output = "compressed",
                OriginalTokens = 50,
                CompressedTokens = 10,
                Strategy = "Json",
                WasCompressed = true
            });

        var sut = CreateSut<ToolTestRequest, ToolTestResponse>();
        var result = await sut.Handle(
            new ToolTestRequest("tool1"),
            () => Task.FromResult(response),
            CancellationToken.None);

        result.ToolOutput.Should().Contain("compressed");
        result.ToolOutput.Should().Contain("[Full output: result://ref-123]");
    }

    [Fact]
    public async Task Handle_DisabledConfig_PassesThrough()
    {
        _config.Enabled = false;
        var sut = CreateSut<ToolTestRequest, ToolTestResponse>();
        var response = new ToolTestResponse(new string('x', 200));

        var result = await sut.Handle(
            new ToolTestRequest("tool1"),
            () => Task.FromResult(response),
            CancellationToken.None);

        result.ToolOutput.Should().Be(response.ToolOutput);
    }

    [Fact]
    public async Task Handle_CompressorThrows_ReturnsOriginalWithWarning()
    {
        var largeOutput = new string('x', 200);
        var response = new ToolTestResponse(largeOutput);

        _mockStore
            .Setup(s => s.StoreIfLargeAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ToolResultReference
            {
                ResultId = "ref-1", ToolName = "tool1",
                PreviewContent = "x", SizeChars = 200,
                Timestamp = DateTimeOffset.UtcNow
            });

        _mockCompressor
            .Setup(c => c.CompressAsync(It.IsAny<string>(), It.IsAny<ToolOutputCategory>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("boom"));

        var sut = CreateSut<ToolTestRequest, ToolTestResponse>();
        var result = await sut.Handle(
            new ToolTestRequest("tool1"),
            () => Task.FromResult(response),
            CancellationToken.None);

        result.ToolOutput.Should().Be(largeOutput);
    }

    [Fact]
    public async Task Handle_NullToolOutput_PassesThrough()
    {
        var sut = CreateSut<ToolTestRequest, ToolTestResponse>();
        var response = new ToolTestResponse(null!);

        var result = await sut.Handle(
            new ToolTestRequest("tool1"),
            () => Task.FromResult(response),
            CancellationToken.None);

        result.Should().Be(response);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test src/Content/Tests/Application.AI.Common.Tests/Application.AI.Common.Tests.csproj --filter "FullyQualifiedName~ToolOutputCompressionBehaviorTests" --no-build 2>&1 || echo "Expected: build failure"`
Expected: Build failure — `ToolOutputCompressionBehavior` does not exist.

- [ ] **Step 3: Implement ToolOutputCompressionBehavior**

```csharp
// src/Content/Application/Application.AI.Common/MediatRBehaviors/ToolOutputCompressionBehavior.cs
using Application.AI.Common.Helpers;
using Application.AI.Common.Interfaces.Agent;
using Application.AI.Common.Interfaces.Compression;
using Application.AI.Common.Interfaces.Context;
using Application.AI.Common.Interfaces.MediatR;
using Domain.AI.Compression.Enums;
using Domain.Common;
using Domain.Common.Config.AI;
using Infrastructure.AI.Compression;
using MediatR;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Application.AI.Common.MediatRBehaviors;

/// <summary>
/// Post-execution pipeline behavior that compresses large tool outputs before
/// they enter conversation history. Full output is persisted to <see cref="IToolResultStore"/>
/// and a compressed version with a retrieval reference replaces the response.
/// </summary>
/// <remarks>
/// <para>Positioned after <c>ResponseSanitizationBehavior</c> in the pipeline.</para>
/// <para>Only activates when the request implements <see cref="IToolRequest"/>
/// and the response implements <see cref="IToolResponse"/> with output exceeding
/// the configured token threshold.</para>
/// <para>On any failure, returns the original response uncompressed.</para>
/// </remarks>
public sealed class ToolOutputCompressionBehavior<TRequest, TResponse>
    : IPipelineBehavior<TRequest, TResponse> where TRequest : notnull
{
    private readonly IToolOutputCompressor _compressor;
    private readonly IToolResultStore _store;
    private readonly IAgentExecutionContext _context;
    private readonly ToolOutputCompressionConfig _config;
    private readonly ILogger<ToolOutputCompressionBehavior<TRequest, TResponse>> _logger;

    public ToolOutputCompressionBehavior(
        IToolOutputCompressor compressor,
        IToolResultStore store,
        IAgentExecutionContext context,
        IOptions<ToolOutputCompressionConfig> config,
        ILogger<ToolOutputCompressionBehavior<TRequest, TResponse>> logger)
    {
        _compressor = compressor;
        _store = store;
        _context = context;
        _config = config.Value;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<TResponse> Handle(
        TRequest request,
        RequestHandlerDelegate<TResponse> next,
        CancellationToken cancellationToken)
    {
        if (request is not IToolRequest toolRequest)
            return await next();

        if (!_config.Enabled)
            return await next();

        var response = await next();

        try
        {
            var toolOutput = ExtractToolOutput(response);
            if (string.IsNullOrEmpty(toolOutput))
                return response;

            var threshold = _config.DefaultTokenThreshold;
            var estimatedTokens = TokenEstimationHelper.EstimateTokens(toolOutput);
            if (estimatedTokens <= threshold)
            {
                _logger.LogDebug(
                    "Tool {ToolName} output ({Tokens} tokens) below threshold ({Threshold}), skipping compression",
                    toolRequest.ToolName, estimatedTokens, threshold);
                return response;
            }

            var sessionId = _context.ConversationId ?? "unknown";
            var reference = await _store.StoreIfLargeAsync(
                sessionId, toolRequest.ToolName, null, toolOutput, cancellationToken);

            var category = ContentTypeDetector.Detect(toolOutput);
            var result = await _compressor.CompressAsync(
                toolOutput, category, threshold, cancellationToken);

            var compressed = result.Output + $"\n[Full output: result://{reference.ResultId}]";

            _logger.LogInformation(
                "Compressed tool {ToolName} output: {OriginalTokens} → {CompressedTokens} tokens ({Strategy})",
                toolRequest.ToolName, result.OriginalTokens, result.CompressedTokens, result.Strategy);

            return ReplaceToolOutput(response, compressed);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex,
                "Tool output compression failed for {ToolName}, returning original output",
                toolRequest.ToolName);
            return response;
        }
    }

    private static string? ExtractToolOutput(TResponse response)
    {
        if (response is Result { IsSuccess: true } resultBase)
        {
            var valueProperty = resultBase.GetType().GetProperty("Value");
            if (valueProperty?.GetValue(resultBase) is IToolResponse toolResponse)
                return toolResponse.ToolOutput;
        }

        if (response is IToolResponse directToolResponse)
            return directToolResponse.ToolOutput;

        return null;
    }

    private static TResponse ReplaceToolOutput(TResponse response, string compressedOutput)
    {
        if (response is Result { IsSuccess: true } resultBase)
        {
            var valueProperty = resultBase.GetType().GetProperty("Value");
            if (valueProperty?.GetValue(resultBase) is IToolResponse toolResponse)
            {
                var replaced = toolResponse.WithSanitizedOutput(compressedOutput);
                var successMethod = resultBase.GetType().GetMethod("Success", [valueProperty.PropertyType]);
                if (successMethod is not null)
                    return (TResponse)successMethod.Invoke(null, [replaced])!;
            }
        }

        if (response is IToolResponse directToolResponse)
            return (TResponse)directToolResponse.WithSanitizedOutput(compressedOutput);

        return response;
    }
}
```

**Important:** This behavior has a `using` directive for `Infrastructure.AI.Compression` to access `ContentTypeDetector`. However, `Application.AI.Common` should not depend on `Infrastructure.AI`. The `ContentTypeDetector.Detect()` call must be moved to the `IToolOutputCompressor` implementation, or `ContentTypeDetector` should be promoted to an interface. The cleanest approach: make `ToolOutputCompressor.CompressAsync` accept a `ToolOutputCategory?` and do the detection internally when null. Let me revise:

**Revised approach:** The behavior passes `null` as category when the tool doesn't declare one, and `ToolOutputCompressor` calls `ContentTypeDetector.Detect()` internally.

Update `IToolOutputCompressor` signature to accept `ToolOutputCategory?`:

```csharp
// In IToolOutputCompressor.cs — change the category parameter type
Task<CompressionResult> CompressAsync(
    string output,
    ToolOutputCategory? category,
    int tokenThreshold,
    CancellationToken cancellationToken = default);
```

Update `ToolOutputCompressor.CompressAsync` to detect when null:

```csharp
// In ToolOutputCompressor.cs — at the start of CompressAsync
var resolvedCategory = category ?? ContentTypeDetector.Detect(output);
// Then use resolvedCategory instead of category throughout
```

Update the behavior to remove the `Infrastructure.AI` dependency:

```csharp
// In ToolOutputCompressionBehavior.cs — remove: using Infrastructure.AI.Compression;
// Change the compress call to:
var result = await _compressor.CompressAsync(
    toolOutput, null, threshold, cancellationToken);
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test src/Content/Tests/Application.AI.Common.Tests/Application.AI.Common.Tests.csproj --filter "FullyQualifiedName~ToolOutputCompressionBehaviorTests"`
Expected: All 6 tests pass.

- [ ] **Step 5: Commit**

```bash
git add src/Content/Application/Application.AI.Common/MediatRBehaviors/ToolOutputCompressionBehavior.cs
git add src/Content/Application/Application.AI.Common/Interfaces/Compression/IToolOutputCompressor.cs
git add src/Content/Infrastructure/Infrastructure.AI/Compression/ToolOutputCompressor.cs
git add src/Content/Tests/Application.AI.Common.Tests/MediatRBehaviors/ToolOutputCompressionBehaviorTests.cs
git commit -m "feat(compression): add ToolOutputCompressionBehavior pipeline + 6 tests"
```

---

### Task 11: DI Registration + Config Binding

**Files:**
- Modify: `src/Content/Application/Application.AI.Common/DependencyInjection.cs`
- Modify: `src/Content/Infrastructure/Infrastructure.AI/DependencyInjection.cs`

- [ ] **Step 1: Register the behavior in Application.AI.Common/DependencyInjection.cs**

After the `ResponseSanitizationBehavior` registration (line ~71), add:

```csharp
            .AddTransient(typeof(IPipelineBehavior<,>), typeof(ToolOutputCompressionBehavior<,>));
```

Update the XML doc `<list>` to include the new behavior after `ResponseSanitizationBehavior`:

```xml
///   <item><description><c>ToolOutputCompressionBehavior</c> — post-execution: compresses large tool output for context savings</description></item>
```

- [ ] **Step 2: Register compressor, strategies, and config in Infrastructure.AI/DependencyInjection.cs**

After the model routing registrations (line ~226), add:

```csharp
        // --- Tool output compression ---

        services.AddSingleton(Options.Create(appConfig.AI.ToolOutputCompression));
        services.AddTransient<ICompressionStrategy, JsonCompressionStrategy>();
        services.AddTransient<ICompressionStrategy, StructuredTextCompressionStrategy>();
        services.AddTransient<ICompressionStrategy, FreeTextCompressionStrategy>();
        services.AddTransient<IToolOutputCompressor, ToolOutputCompressor>();
```

Add the required `using` directives to the top of the file:

```csharp
using Application.AI.Common.Interfaces.Compression;
using Infrastructure.AI.Compression;
using Infrastructure.AI.Compression.Strategies;
```

- [ ] **Step 3: Verify full solution build**

Run: `dotnet build src/AgenticHarness.slnx`
Expected: Build succeeded. 0 errors.

- [ ] **Step 4: Commit**

```bash
git add src/Content/Application/Application.AI.Common/DependencyInjection.cs
git add src/Content/Infrastructure/Infrastructure.AI/DependencyInjection.cs
git commit -m "feat(compression): register ToolOutputCompressionBehavior, strategies, and config in DI"
```

---

### Task 12: Full Build Verification + Test Run

**Files:** None (verification only)

- [ ] **Step 1: Clean build**

Run: `dotnet build src/AgenticHarness.slnx`
Expected: Build succeeded. 0 errors, 0 warnings (or only pre-existing warnings).

- [ ] **Step 2: Run all new tests**

Run: `dotnet test src/AgenticHarness.slnx --filter "FullyQualifiedName~Compression"`
Expected: All ~36 new tests pass.

- [ ] **Step 3: Run full test suite to check for regressions**

Run: `dotnet test src/AgenticHarness.slnx`
Expected: All tests pass except the pre-existing 95 AgentHub failures (unrelated).

- [ ] **Step 4: Final commit if any fixups needed**

If any test fixups were needed, commit them:

```bash
git add -A
git commit -m "fix(compression): address test/build issues from full verification"
```
