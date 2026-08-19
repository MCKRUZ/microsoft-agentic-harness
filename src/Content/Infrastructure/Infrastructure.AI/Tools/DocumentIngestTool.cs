using System.Text.Json;
using Application.AI.Common.Interfaces.Tools;
using Application.Core.CQRS.RAG.IngestDocument;
using Domain.AI.Changes;
using Domain.Common.Config.AI.Governance;
using Domain.AI.Models;
using Domain.AI.Sandbox;
using MediatR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Infrastructure.AI.Tools;

/// <summary>
/// Agent tool for ingesting documents into the RAG index. Sends an
/// <see cref="IngestDocumentCommand"/> through MediatR to trigger the full
/// ingestion pipeline: parse, chunk, enrich, embed, and index.
/// </summary>
/// <remarks>
/// <para>
/// Register via keyed DI:
/// <code>
/// services.AddKeyedSingleton&lt;ITool&gt;("document_ingest", (sp, _) =&gt;
///     new DocumentIngestTool(
///         sp.GetRequiredService&lt;IServiceScopeFactory&gt;(),
///         sp.GetRequiredService&lt;ILogger&lt;DocumentIngestTool&gt;&gt;()));
/// </code>
/// </para>
/// <para>
/// This tool is write-oriented and not concurrency-safe because ingestion
/// modifies vector store and BM25 index state. The batched tool execution
/// strategy will serialize calls to this tool.
/// </para>
/// <para>
/// The tool is a keyed SINGLETON, but a mediator dispatch constructs pipeline
/// behaviors that ctor-inject the SCOPED <c>IAgentExecutionContext</c>, so
/// each ingestion resolves <see cref="IMediator"/> from a fresh DI scope via
/// <see cref="IServiceScopeFactory"/> — a root-bound mediator would be a
/// captive dependency rejected by scope validation.
/// </para>
/// </remarks>
public sealed class DocumentIngestTool : ITool
{
    /// <summary>The tool name matching keyed DI registration and SKILL.md declarations.</summary>
    public const string ToolName = "document_ingest";

    private static readonly IReadOnlyList<string> Operations = ["ingest"];

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<DocumentIngestTool> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="DocumentIngestTool"/> class.
    /// </summary>
    /// <param name="scopeFactory">Scope factory used to resolve <see cref="IMediator"/> per ingestion dispatch.</param>
    /// <param name="logger">
    /// Logs a scope-creation or dispatch failure before it is mapped to a failed
    /// <see cref="ToolResult"/> (#428) — the same shape <c>WorkspaceCommandRunner.RunAsync</c> and
    /// <c>IacSandboxRunner.RunAsync</c> already log on their own dispatch paths.
    /// </param>
    public DocumentIngestTool(IServiceScopeFactory scopeFactory, ILogger<DocumentIngestTool> logger)
    {
        ArgumentNullException.ThrowIfNull(scopeFactory);
        ArgumentNullException.ThrowIfNull(logger);
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Name => ToolName;

    /// <inheritdoc />
    public string Description =>
        "Ingests documents into the RAG index for later retrieval. Supports markdown and text " +
        "files. When per-tenant collection isolation (ScopedCollections) is enabled, the " +
        "'collection' parameter is rejected with a validation failure: the target collection " +
        "is always derived server-side from the caller's tenant.";

    /// <inheritdoc />
    public IReadOnlyList<string> SupportedOperations => Operations;

    /// <inheritdoc />
    public bool IsReadOnly => false;

    /// <inheritdoc />
    public BlastRadius RiskTier => BlastRadius.Medium;

    /// <inheritdoc />
    /// <remarks>
    /// The document being ingested is, by definition, content this agent did not author — the
    /// canonical untrusted-input shape, whether or not the document's own contents are later flagged
    /// by anything else. Not also declared <c>WritesFiles</c>: it mutates vector/BM25 index state, not
    /// the file system, and the narrow vocabulary does not have an "index write" bit of its own.
    /// </remarks>
    public ToolCompositionCapability Capabilities => ToolCompositionCapability.IngestsUntrustedInput;

    /// <inheritdoc />
    public bool IsConcurrencySafe => false;

    /// <inheritdoc />
    /// <remarks>
    /// Unlike <c>WorkspaceWriteFileTool</c>, the ingestion pipeline runs synchronously inside this
    /// call, not behind a separate approval step — <c>IngestDocumentCommandHandler</c> fetches the
    /// source URI (<see cref="ToolCapability.FileRead"/> for <c>file://</c>,
    /// <see cref="ToolCapability.NetworkAccess"/> for <c>https://</c>), calls the embedding service
    /// (<see cref="ToolCapability.LlmInvocation"/>), and commits to the vector/BM25 index
    /// (<see cref="ToolCapability.DatabaseWrite"/>) before returning.
    /// </remarks>
    public ToolCapability RequiredCapabilities =>
        ToolCapability.FileRead | ToolCapability.NetworkAccess
        | ToolCapability.LlmInvocation | ToolCapability.DatabaseWrite;

    /// <inheritdoc />
    public async Task<ToolResult> ExecuteAsync(
        string operation,
        IReadOnlyDictionary<string, object?> parameters,
        CancellationToken cancellationToken = default)
    {
        if (!string.Equals(operation, "ingest", StringComparison.OrdinalIgnoreCase))
            return ToolResult.Fail($"Unknown operation: {operation}. Supported: ingest");

        try
        {
            return await IngestAsync(parameters, cancellationToken);
        }
        catch (UriFormatException)
        {
            return ToolResult.Fail("Invalid URI format. Provide a valid file:// or https:// URI.");
        }
        catch (ArgumentException ex)
        {
            return ToolResult.Fail(ex.Message);
        }
    }

    private async Task<ToolResult> IngestAsync(
        IReadOnlyDictionary<string, object?> parameters,
        CancellationToken cancellationToken)
    {
        var uriString = GetRequiredString(parameters, "uri");
        var collection = GetOptionalString(parameters, "collection");

        var uri = new Uri(uriString);

        var command = new IngestDocumentCommand
        {
            DocumentUri = uri,
            CollectionName = collection
        };

        // Scoped separately from the caller's UriFormatException/ArgumentException handling above —
        // those guard parameter validation, not dispatch, and must keep their own messages.
        return await MediatorDispatchRunner.RunAsync(
            _scopeFactory,
            async (mediator, ct) =>
            {
                var result = await mediator.Send(command, ct);
                if (!result.Success)
                    return ToolResult.Fail($"Ingestion failed: {result.Error}");

                var response = new
                {
                    jobId = result.JobId,
                    chunksProduced = result.ChunksProduced,
                    tokensEmbedded = result.TokensEmbedded,
                    durationMs = result.Duration.TotalMilliseconds
                };
                return ToolResult.Ok(JsonSerializer.Serialize(response, JsonOptions));
            },
            _logger,
            ToolName,
            // GetLeftPart(UriPartial.Path) drops the query string — a document URI can legitimately be
            // a SAS-signed blob URL (?sv=...&sig=...), and this failure path is reached on exactly the
            // input this tool is expected to reject, so the credential must never land in an error log.
            failureContext: uri.GetLeftPart(UriPartial.Path),
            cancellationToken);
    }

    private static string GetRequiredString(IReadOnlyDictionary<string, object?> parameters, string key)
    {
        if (!parameters.TryGetValue(key, out var value) || value is not string s || string.IsNullOrWhiteSpace(s))
            throw new ArgumentException($"Required parameter '{key}' is missing or empty.");
        return s;
    }

    private static string? GetOptionalString(IReadOnlyDictionary<string, object?> parameters, string key) =>
        parameters.TryGetValue(key, out var value) && value is string s && !string.IsNullOrWhiteSpace(s)
            ? s
            : null;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };
}
