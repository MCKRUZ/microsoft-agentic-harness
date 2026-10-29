using System.Collections.Concurrent;
using Application.Core.Compliance;
using Application.Core.CQRS.Compliance.RemoveDocument;
using Domain.Common;
using Domain.AI.KnowledgeGraph.Models;
using Domain.Common.Compliance;
using FluentAssertions;
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
    public async Task TheShippedAzureIndexWithNoEndpoint_IsNamedAsNotConfigured_InTheReceiptAndTheRecord()
    {
        // #1011, against the stores the host actually resolves: a decorator over either one that hid whether it was
        // configured would put "0 removed" back on the receipt, which a test of the bare store cannot see.
        await using var provider = BuildProvider(vectorStore: "azure_ai_search");
        await using var scope = provider.CreateAsyncScope();

        var result = await scope.ServiceProvider.GetRequiredService<IMediator>().Send(
            new RemoveDocumentCommand { DocumentId = "0123456789ABCDEF", OperatorId = Operator });

        result.IsSuccess.Should().BeTrue(string.Join("; ", result.Errors));
        result.Value!.Completeness.Should().Be(ErasureCompleteness.Full, "an index with no backend has held nothing");
        result.Value.Stores.Should().HaveCount(2).And.OnlyContain(s =>
            s.Status == StoreErasureStatus.NotConfigured && s.Store.StartsWith("document index (Azure AI Search", StringComparison.Ordinal));
        _logs.Messages.Should().ContainSingle(m => m.Contains("phase=outcome") && m.Contains("NotConfigured:0"),
            "the audit record names the store too, so it does not read as a search that found nothing");
    }

    private ServiceProvider BuildProvider(string vectorStore = "faiss") => CompositionRootTestHost.BuildProvider(
        new Dictionary<string, string?>
        {
            ["AppConfig:AI:Conversations:DatabasePath"] = Path.Combine(_workingDir, "conversations.db"),
            ["AppConfig:AI:Schedules:DatabasePath"] = Path.Combine(_workingDir, "schedules.db"),
            ["AppConfig:AI:Planner:DatabasePath"] = Path.Combine(_workingDir, "planner.db"),
            ["AppConfig:AI:Rag:VectorStore:Provider"] = vectorStore,
        },
        (services, _) => services.AddSingleton<ILoggerProvider>(_logs));

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
