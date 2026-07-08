using System.Text.Json;
using Application.AI.Common.Interfaces.Tools;
using Application.Core.CQRS.RAG.IngestDocument;
using Domain.AI.Changes;
using Domain.AI.Models;
using MediatR;
using Microsoft.Extensions.DependencyInjection;

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
///     new DocumentIngestTool(sp.GetRequiredService&lt;IServiceScopeFactory&gt;()));
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

    /// <summary>
    /// Initializes a new instance of the <see cref="DocumentIngestTool"/> class.
    /// </summary>
    /// <param name="scopeFactory">Scope factory used to resolve <see cref="IMediator"/> per ingestion dispatch.</param>
    public DocumentIngestTool(IServiceScopeFactory scopeFactory)
    {
        ArgumentNullException.ThrowIfNull(scopeFactory);
        _scopeFactory = scopeFactory;
    }

    /// <inheritdoc />
    public string Name => ToolName;

    /// <inheritdoc />
    public string Description =>
        "Ingests documents into the RAG index for later retrieval. Supports markdown and text files.";

    /// <inheritdoc />
    public IReadOnlyList<string> SupportedOperations => Operations;

    /// <inheritdoc />
    public bool IsReadOnly => false;

    /// <inheritdoc />
    public BlastRadius RiskTier => BlastRadius.Medium;

    /// <inheritdoc />
    public bool IsConcurrencySafe => false;

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

        // Dispatch inside a fresh scope: the MediatR pipeline resolves scoped services
        // (IAgentExecutionContext et al.), which a singleton must never pull from the root.
        await using var scope = _scopeFactory.CreateAsyncScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();

        var result = await mediator.Send(command, cancellationToken);

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
