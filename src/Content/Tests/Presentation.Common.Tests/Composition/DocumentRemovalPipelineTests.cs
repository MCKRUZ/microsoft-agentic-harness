using System.Collections.Concurrent;
using Application.AI.Common.Interfaces.RAG;
using Application.Core.Compliance;
using Application.Core.CQRS.Compliance.RemoveDocument;
using Domain.Common;
using Domain.AI.KnowledgeGraph.Models;
using Domain.AI.RAG.Models;
using FluentAssertions;
using Infrastructure.AI.RAG.Retrieval;
using MediatR;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Presentation.Common.Tests.Composition;

/// <summary>
/// Operator removal of a document through the real composition (#901): the command reaches its handler through the
/// standard pipeline, a malformed id is refused before anything is removed or written, and a well-formed one leaves the
/// attempt and outcome in the shipped audit trail.
/// </summary>
/// <remarks>
/// The handler trusts its validator to have refused a malformed id; the unit tests build the validator directly, so only
/// this test proves the pipeline actually runs it.
/// </remarks>
[Collection(SqliteConnectionPoolCollection.Name)]
public sealed class DocumentRemovalPipelineTests : IDisposable
{
    private const string Operator = "dpo-operator-9";
    private const string MalformedId = "not-a-document-id-but-free-text";

    private readonly string _workingDir =
        Path.Combine(Path.GetTempPath(), "document-removal-" + Guid.NewGuid().ToString("N"));
    private readonly CapturingLoggerProvider _logs = new();

    /// <inheritdoc />
    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_workingDir))
            Directory.Delete(_workingDir, recursive: true);
    }

    [Fact]
    public async Task MalformedId_IsRefusedByThePipelineBeforeAnythingIsWritten()
    {
        await using var provider = BuildProvider();
        await using var scope = provider.CreateAsyncScope();

        var result = await scope.ServiceProvider.GetRequiredService<IMediator>().Send(
            new RemoveDocumentCommand { DocumentId = MalformedId, OperatorId = Operator });

        result.FailureType.Should().Be(ResultFailureType.Validation);
        _logs.Messages.Should().NotContain(m => m.Contains(AuditedDocumentRemoval.AuditAction, StringComparison.Ordinal),
            "a refused id must not reach the removal or its attempt record");
        _logs.Messages.Should().NotContain(m => m.Contains(MalformedId, StringComparison.Ordinal));
    }

    [Fact]
    public async Task WellFormedId_IsRemovedAndAuditedByTheOperator()
    {
        await using var provider = BuildProvider();
        await using var scope = provider.CreateAsyncScope();

        var result = await scope.ServiceProvider.GetRequiredService<IMediator>().Send(
            new RemoveDocumentCommand { DocumentId = "0123456789ABCDEF", OperatorId = Operator });

        result.IsSuccess.Should().BeTrue(string.Join("; ", result.Errors));
        result.Value!.UnmatchedDocumentIds.Should().ContainSingle(id => id == "0123456789ABCDEF", "nothing was loaded under that id");
        var auditLines = _logs.Messages
            .Where(m => m.Contains(AuditedDocumentRemoval.AuditAction, StringComparison.Ordinal)).ToList();
        auditLines.Should().HaveCount(2).And.OnlyContain(m => m.Contains($"by {Operator}", StringComparison.Ordinal));
        auditLines.Should().ContainSingle(m => m.Contains("phase=outcome") && m.Contains("matched=false"));
    }

    [Fact]
    public async Task TheShippedDefaultIndex_IsAskedAndReadsFull_WhenTheDocumentIsRemoved()
    {
        // #1012: a host that never names a document index runs on the in-process stores, which are really asked. Before,
        // the default was an Azure AI Search index with no endpoint, listed as not configured and never asked.
        await using var provider = BuildProvider(vectorStore: null);
        await using var scope = provider.CreateAsyncScope();
        var vectors = scope.ServiceProvider.GetRequiredService<IVectorStore>();
        var bm25 = scope.ServiceProvider.GetRequiredService<IBm25Store>();
        vectors.Should().BeOfType<FaissVectorStore>();
        bm25.Should().BeOfType<SqliteFts5Store>();

        // Unique per run: the full-text store is one in-memory database for the whole process.
        var documentId = Guid.NewGuid().ToString("N")[..16].ToUpperInvariant();
        IReadOnlyList<DocumentChunk> chunks = [Chunk(documentId)];
        await vectors.IndexAsync(chunks);
        await bm25.IndexAsync(chunks);

        var result = await scope.ServiceProvider.GetRequiredService<IMediator>().Send(
            new RemoveDocumentCommand { DocumentId = documentId, OperatorId = Operator });

        result.IsSuccess.Should().BeTrue(string.Join("; ", result.Errors));
        result.Value!.Completeness.Should().Be(ErasureCompleteness.Full);
        result.Value.VectorEmbeddingsDeleted.Should().Be(1, "the vector store was asked and removed the chunk");
        result.Value.Bm25DocumentsDeleted.Should().Be(1, "the full-text store was asked and removed the chunk");
        result.Value.UnmatchedDocumentIds.Should().BeEmpty();
    }

    private static DocumentChunk Chunk(string documentId) => new()
    {
        Id = documentId + "-0",
        DocumentId = documentId,
        SectionPath = "Section",
        Content = "Quarterly figures for the removal test.",
        Tokens = 8,
        Embedding = [0.1f, 0.2f, 0.3f],
        Metadata = new ChunkMetadata { SourceUri = new Uri($"file:///docs/{documentId}.md"), CreatedAt = DateTimeOffset.UtcNow },
    };

    private ServiceProvider BuildProvider(string? vectorStore = "faiss")
    {
        var settings = new Dictionary<string, string?>
        {
            ["AppConfig:AI:Conversations:DatabasePath"] = Path.Combine(_workingDir, "conversations.db"),
            ["AppConfig:AI:Schedules:DatabasePath"] = Path.Combine(_workingDir, "schedules.db"),
            ["AppConfig:AI:Planner:DatabasePath"] = Path.Combine(_workingDir, "planner.db"),
        };
        if (vectorStore is not null)
            settings["AppConfig:AI:Rag:VectorStore:Provider"] = vectorStore;

        return CompositionRootTestHost.BuildProvider(settings, (services, _) => services.AddSingleton<ILoggerProvider>(_logs));
    }

    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        private readonly ConcurrentQueue<string> _messages = new();

        public IReadOnlyList<string> Messages => _messages.ToList();

        public ILogger CreateLogger(string categoryName) => new Logger(_messages);

        public void Dispose()
        {
        }

        private sealed class Logger(ConcurrentQueue<string> messages) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter) =>
                messages.Enqueue(formatter(state, exception) + (exception is null ? "" : " " + exception.Message));
        }
    }
}
