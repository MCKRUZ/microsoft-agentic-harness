# Tool Output Compression — Design Spec

## Goal

Compress large tool outputs before they enter conversation history, preserving decision-relevant information while reducing context window consumption. Full outputs persist in `IToolResultStore` for on-demand retrieval.

## Architecture

### Compression Point

Post-execution MediatR pipeline behavior (`ToolOutputCompressionBehavior`), positioned after `ResponseSanitizationBehavior`. Dual-store model: compressed version enters conversation history as `FunctionResultContent`, full output persisted to `IToolResultStore` with a retrieval reference appended to the compressed text.

### Output Classification

Hybrid approach: tools declare their output type via an `OutputCategory` property on `ITool` (preferred). When not declared, `ContentTypeDetector` sniffs the output string to infer the category at runtime.

### Tiered Compression

1. **Heuristic strategies** — deterministic, zero-cost, applied first. Strategy selected by `ToolOutputCategory`.
2. **LLM fallback** — economy-tier model via `IModelRouter.RouteOperationAsync("output_compression")`. Only invoked when heuristic result still exceeds threshold AND content is unstructured text (`FreeText`). Configurable timeout (default 5s), falls back to hard truncation on failure.

## Components

### Domain Layer

**`ToolOutputCategory` enum:**

| Value | Description |
|-------|-------------|
| `Json` | Parseable JSON (API responses, structured data) |
| `FileContent` | Source code, config files, documents |
| `SearchResults` | Multiple results with repeated structure |
| `Tabular` | Tab/pipe-delimited rows with consistent columns |
| `FreeText` | Unstructured prose, logs, error output |

**`CompressionResult` record:**
- `Output` (string) — compressed text
- `OriginalTokens` (int) — estimated token count before compression
- `CompressedTokens` (int) — estimated token count after compression
- `Strategy` (string) — name of strategy that produced the result (e.g., `"Json"`, `"LlmFallback"`)
- `WasCompressed` (bool) — false when output was below threshold

### Application Layer

**`IToolOutputCompressor` interface:**
- `CompressAsync(string output, ToolOutputCategory category, int tokenThreshold, CancellationToken ct)` returns `CompressionResult`
- Orchestrates strategy selection, tiered fallback, and hard truncation as last resort

**`ICompressionStrategy` interface:**
- `CanHandle(ToolOutputCategory category)` — returns whether this strategy handles the given category
- `CompressAsync(string output, int tokenThreshold, CancellationToken ct)` returns `CompressionResult`
- Multiple implementations registered via keyed DI by `ToolOutputCategory`

**`ToolOutputCompressionBehavior<TRequest, TResponse>` (MediatR pipeline behavior):**
- Activates for requests implementing `IToolRequest` that produce a response implementing `IToolResponse` (existing marker interfaces in `Application.AI.Common.Interfaces.MediatR`)
- Injects `AgentExecutionContext` (scoped) for session ID when storing full output
- Guards: `config.Enabled`, `response.ToolOutput` token estimate > threshold
- On success: stores full output via `IToolResultStore.StoreIfLargeAsync()`, calls `IToolResponse.WithSanitizedOutput()` to replace output with compressed text + `[Full output: result://{resultId}]` footer
- On failure: logs warning, returns original response uncompressed

**`ITool` modification:**
- Add `ToolOutputCategory? OutputCategory { get; }` — default `null` (triggers content sniffing)
- Add `int? CompressionTokenThreshold { get; }` — nullable, falls back to config default

### Infrastructure Layer

**`JsonCompressionStrategy`:**
- Array truncation: arrays >10 elements → first 3 + last 2, insert `"... (N items omitted)"`
- Depth pruning: objects nested >4 levels → replace subtree with `"[nested object with N keys]"`
- Key filtering: remove low-signal keys (`metadata`, `_links`, `pagination`, `headers`, `timestamps`) when still over budget
- Parse failure → return `WasCompressed = false` to signal fallthrough

**`StructuredTextCompressionStrategy`:**
- Line deduplication: detect repeated patterns → `[... N similar lines omitted]`
- Head/tail preservation: first 40 / last 10 lines, structural summary of omitted middle
- Summary header: `"File: path (N lines, language with M methods)"` from heuristics

**`FreeTextCompressionStrategy`:**
- Sentence-boundary truncation: keep first N sentences fitting 60% of threshold
- LLM fallback (if enabled, still over budget): economy-tier model summarizes the heuristically-reduced text
- LLM prompt: *"Summarize this tool output in under {targetTokens} tokens. Preserve actionable information, specific values, and error details. Omit boilerplate."*
- LLM failure/timeout → hard-truncate at threshold with `[... truncated]` marker

**`ContentTypeDetector` (static helper):**
- `JsonDocument.Parse()` success → `Json`
- Regex for file path + line number patterns → `FileContent`
- Tab/pipe-delimited with consistent column count → `Tabular`
- Repeated structured lines → `SearchResults`
- Default → `FreeText`

**`ToolOutputCompressor` (implementation of `IToolOutputCompressor`):**
- Resolves matching `ICompressionStrategy` by `ToolOutputCategory`
- Runs strategy → checks result against threshold
- If still over and `LlmFallbackEnabled` and category is `FreeText` → LLM summarization
- If still over after all strategies → hard-truncate to threshold

## Data Flow

### Happy Path (output exceeds threshold)

1. Tool executes → `ToolResult` with output string
2. `ResponseSanitizationBehavior` screens for credentials/injection (existing)
3. `ToolOutputCompressionBehavior` activates (pattern-matches on `IToolRequest` + `IToolResponse`):
   - `TokenEstimationHelper.EstimateTokens(response.ToolOutput)` → above threshold
   - Determine category: `ITool.OutputCategory` ?? `ContentTypeDetector.Detect(output)`
   - `IToolResultStore.StoreIfLargeAsync(context.ConversationId, request.ToolName, ...)` → `resultId`
   - `IToolOutputCompressor.CompressAsync()` → `CompressionResult`
   - `response.WithSanitizedOutput(compressed + footer)` → replace response output
4. Compressed output enters conversation history as `FunctionResultContent`

### Agent Retrieval

Agent sees `[Full output: result://{resultId}]` footer → calls `IToolResultStore.RetrieveFullContentAsync(resultId)` via existing retrieval mechanism. No new retrieval tool needed.

### Tiered Fallback Chain

1. Category-matched heuristic strategy
2. If still over + `FreeText` + LLM enabled → LLM summarization of heuristically-reduced text
3. If still over or any failure → hard-truncate at threshold

## Configuration

```yaml
AI:
  ToolOutputCompression:
    Enabled: true
    DefaultTokenThreshold: 2000
    LlmFallbackEnabled: true
    LlmFallbackTimeoutSeconds: 5
    LlmRoutingOperation: "output_compression"
```

Per-tool overrides via `ITool.OutputCategory` and `ITool.CompressionTokenThreshold` (nullable, fall back to config defaults when null).

## Error Handling

- `ToolOutputCompressionBehavior` wraps all compression in try/catch — on failure, returns original output uncompressed with a warning log. Compression must never break tool execution.
- `ContentTypeDetector` failures → default to `FreeText`
- Individual strategy failures → fall through to `FreeTextCompressionStrategy` hard truncation
- LLM timeout/failure → hard-truncate at threshold with retrieval reference

## Observability

- `Debug`: output below threshold, skipped compression
- `Information`: compressed successfully — original tokens, compressed tokens, strategy, ratio
- `Warning`: strategy failed with fallback, LLM timeout
- `CompressionResult` metrics available for `ContextBudgetTracker.RecordUsage()`
- No new OpenTelemetry spans — existing `ToolDiagnosticsMiddleware` traces cover tool output

## Testing Strategy

### ContentTypeDetectorTests (~6 tests)
- Valid JSON → `Json`
- File paths with line numbers → `FileContent`
- Tab-delimited rows → `Tabular`
- Repeated structured lines → `SearchResults`
- Plain prose → `FreeText`
- Malformed/empty → `FreeText`

### JsonCompressionStrategyTests (~7 tests)
- Array >10 elements truncated with count
- Deep nesting pruned at depth 4
- Low-signal keys removed when over budget
- Small JSON → passthrough
- Invalid JSON → `WasCompressed = false`
- Empty/null → passthrough
- Mixed-type arrays preserved

### StructuredTextCompressionStrategyTests (~5 tests)
- Duplicate lines → deduplicated with count
- Long file → head/tail with summary
- Structural summary header generated
- Short output → passthrough
- No structure detected → passthrough

### FreeTextCompressionStrategyTests (~5 tests)
- Long prose → sentence-boundary truncation
- LLM fallback invoked when heuristic still over
- LLM timeout → hard truncation
- LLM disabled → hard truncation only
- Short text → passthrough

### ToolOutputCompressorTests (~6 tests)
- Routes category to correct strategy
- Strategy failure → FreeText fallback
- Tiered: heuristic under budget → no LLM
- Tiered: heuristic over → LLM called
- Unknown category → FreeText default
- Below threshold → passthrough

### ToolOutputCompressionBehaviorTests (~7 tests)
- Non-tool request → passthrough
- Below threshold → passthrough
- Above threshold → compressed + stored + reference footer
- Disabled in config → passthrough
- Compression throws → original output, warning logged
- Tool with custom OutputCategory → used over sniffing
- Tool with custom threshold → overrides config default

**Total: ~36 tests.** Strategy tests are pure unit tests (deterministic string-in/string-out). Behavior and compressor tests mock `IToolResultStore`, `IModelRouter`, `ICompressionStrategy`.

## File Manifest

| Layer | File | Action |
|-------|------|--------|
| Domain | `Domain.AI/Models/ToolOutputCategory.cs` | Create |
| Domain | `Domain.AI/Models/CompressionResult.cs` | Create |
| Domain | `Domain.Common/Config/AI/ToolOutputCompressionConfig.cs` | Create |
| Domain | `Domain.Common/Config/AI/AIConfig.cs` | Modify — add `ToolOutputCompression` property |
| Application | `Application.AI.Common/Interfaces/Compression/IToolOutputCompressor.cs` | Create |
| Application | `Application.AI.Common/Interfaces/Compression/ICompressionStrategy.cs` | Create |
| Application | `Application.AI.Common/MediatRBehaviors/ToolOutputCompressionBehavior.cs` | Create |
| Application | `Application.AI.Common/Interfaces/Tools/ITool.cs` | Modify — add `OutputCategory`, `CompressionTokenThreshold` |
| Infrastructure | `Infrastructure.AI/Compression/ContentTypeDetector.cs` | Create |
| Infrastructure | `Infrastructure.AI/Compression/ToolOutputCompressor.cs` | Create |
| Infrastructure | `Infrastructure.AI/Compression/Strategies/JsonCompressionStrategy.cs` | Create |
| Infrastructure | `Infrastructure.AI/Compression/Strategies/StructuredTextCompressionStrategy.cs` | Create |
| Infrastructure | `Infrastructure.AI/Compression/Strategies/FreeTextCompressionStrategy.cs` | Create |
| Infrastructure | `Infrastructure.AI/DependencyInjection.cs` | Modify — register compressor, strategies, config |
| Application | `Application.AI.Common/DependencyInjection.cs` | Modify — register behavior |
| Tests | `Infrastructure.AI.Tests/Compression/ContentTypeDetectorTests.cs` | Create |
| Tests | `Infrastructure.AI.Tests/Compression/Strategies/JsonCompressionStrategyTests.cs` | Create |
| Tests | `Infrastructure.AI.Tests/Compression/Strategies/StructuredTextCompressionStrategyTests.cs` | Create |
| Tests | `Infrastructure.AI.Tests/Compression/Strategies/FreeTextCompressionStrategyTests.cs` | Create |
| Tests | `Infrastructure.AI.Tests/Compression/ToolOutputCompressorTests.cs` | Create |
| Tests | `Application.AI.Common.Tests/MediatRBehaviors/ToolOutputCompressionBehaviorTests.cs` | Create |
