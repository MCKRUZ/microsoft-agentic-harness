using System.Text.Json;
using Application.AI.Common.Interfaces.Audit;
using Application.AI.Common.Interfaces.KnowledgeGraph;
using Application.Core.Compliance;
using Application.Core.CQRS.Compliance.EraseSubjectData;
using Application.Core.CQRS.Compliance.GenerateComplianceReport;
using Application.Core.CQRS.Compliance.ExportSubjectData;
using Domain.Common.Compliance;
using FluentAssertions;
using Infrastructure.AI.Audit;
using MediatR;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Presentation.Common.Tests.Composition;

/// <summary>
/// Through the real composition, every erasure, export and document removal leaves its record in the hash-chained
/// compliance audit trail (#969), not only in a log line that is deleted with the logs after 30 days.
/// </summary>
/// <remarks>
/// The trail is checked the way an auditor would: by reading its day-files and by the chain verification the host's
/// scheduled verifier runs. A record that reached only the log sink, or a trail the host never registered, fails here.
/// </remarks>
[Collection(SqliteConnectionPoolCollection.Name)]
public sealed class ComplianceAuditTrailEndToEndTests : IDisposable
{
    private const string Subject = "Trail-Subject-Oid-4B1D";
    private const string Operator = "dpo-operator-7";

    private readonly string _workingDir =
        Path.Combine(Path.GetTempPath(), "compliance-trail-" + Guid.NewGuid().ToString("N"));

    /// <inheritdoc />
    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_workingDir))
            Directory.Delete(_workingDir, recursive: true);
    }

    private string TrailFolder => Path.Combine(_workingDir, "compliance-audit", "compliance");

    [Fact]
    public async Task OperatorErasure_LeavesItsAttemptAndOutcomeInTheVerifiedComplianceTrail()
    {
        await using var provider = BuildProvider();

        var receipt = await SendAsync(provider, new EraseSubjectDataCommand { SubjectId = Subject, OperatorId = Operator });

        var records = TrailRecords();
        records.Should().HaveCount(2);
        records.Should().OnlyContain(r => r.GetProperty("action").GetString() == AuditedErasure.AuditAction);
        records.Select(r => r.GetProperty("metadata").GetProperty("phase").GetString()).Should().Equal("attempt", "outcome");
        records[1].GetProperty("metadata").GetProperty("receipt_request_id").GetString().Should().Be(receipt.RequestId,
            "the record must tie to the receipt the person was handed");
        records.Should().OnlyContain(r => r.GetProperty("executor_id").GetString()!.StartsWith("ref:", StringComparison.Ordinal),
            "a named operator is kept as a reference in a trail that is never deleted");
        TrailText().Should().NotContain(Subject).And.NotContain(Operator);
        await AssertTheTrailVerifiesAsync(provider, expectedRecords: 2);
    }

    [Fact]
    public async Task OperatorErasure_ListsTheComplianceTrailOnTheReceiptAsKeptByObligation()
    {
        await using var provider = BuildProvider();

        var receipt = await SendAsync(provider, new EraseSubjectDataCommand { SubjectId = Subject, OperatorId = Operator });

        receipt.Stores.Should().ContainSingle(s => s.Store == "compliance audit trail")
            .Which.Status.Should().Be(StoreErasureStatus.RetainedByObligation,
                "erasure keeps the trail on purpose, and the person is told so");
    }

    [Fact]
    public async Task OperatorExport_LeavesItsAttemptAndOutcomeInTheVerifiedComplianceTrail()
    {
        await using var provider = BuildProvider();

        var export = await SendAsync(provider, new ExportSubjectDataCommand { SubjectId = Subject, OperatorId = Operator });

        var records = TrailRecords();
        records.Should().HaveCount(2).And.OnlyContain(r => r.GetProperty("action").GetString() == AuditedExport.AuditAction);
        records[1].GetProperty("metadata").GetProperty("export_request_id").GetString().Should().Be(export.RequestId);
        TrailText().Should().NotContain(Subject).And.NotContain(Operator);
        await AssertTheTrailVerifiesAsync(provider, expectedRecords: 2);
    }

    [Fact]
    public async Task DocumentRemoval_LeavesItsAttemptAndOutcomeInTheComplianceTrail()
    {
        await using var provider = BuildProvider();
        await using (var scope = provider.CreateAsyncScope())
        {
            var result = await scope.ServiceProvider.GetRequiredService<AuditedDocumentRemoval>()
                .RemoveAsync("0123456789ABCDEF", ErasureActor.Retention, CancellationToken.None);
            result.IsSuccess.Should().BeTrue(string.Join("; ", result.Errors));
        }

        var records = TrailRecords();
        records.Should().HaveCount(2).And.OnlyContain(r =>
            r.GetProperty("action").GetString() == AuditedDocumentRemoval.AuditAction
            && r.GetProperty("executor_id").GetString() == ErasureActor.RetentionExecutor);
        await AssertTheTrailVerifiesAsync(provider, expectedRecords: 2);
    }

    [Fact]
    public async Task ComplianceReport_AfterAnErasure_SummarisesTheTrailsRecordsWithoutTheirIds()
    {
        await using var provider = BuildProvider();
        var receipt = await SendAsync(provider, new EraseSubjectDataCommand { SubjectId = Subject, OperatorId = Operator });

        var report = await SendAsync(provider, new GenerateComplianceReportQuery
        {
            CallerId = Operator,
            Start = DateTimeOffset.UtcNow.AddHours(-1),
            End = DateTimeOffset.UtcNow.AddHours(1),
        });

        report.ComplianceRequests.Counts.Should().Contain(c => c.Action == AuditedErasure.AuditAction && c.Count == 1);
        report.ComplianceRequests.Receipts.Should().ContainSingle(r => r.ReceiptId == receipt.RequestId,
            "an operator reading the report must see which requests were handled (#1000)");
        var shown = JsonSerializer.Serialize(report.ComplianceRequests);
        shown.Should().NotContain(Subject).And.NotContain(Operator).And.NotContain("ref:");
    }

    [Fact]
    public async Task ErasureRecord_EditedAfterwards_IsCaughtByTheHostsChainVerification()
    {
        await using var provider = BuildProvider();
        await SendAsync(provider, new EraseSubjectDataCommand { SubjectId = Subject, OperatorId = Operator });

        var file = Directory.GetFiles(TrailFolder, "*.jsonl").Single();
        File.WriteAllText(file, File.ReadAllText(file).Replace("\"Attempted\"", "\"Success\"", StringComparison.Ordinal));

        var verified = await ComplianceChain(provider).VerifyChainAsync(checkpoint: null, CancellationToken.None);
        verified.IsValid.Should().BeFalse("proof of an erasure that can be rewritten unnoticed proves nothing");
    }

    private ServiceProvider BuildProvider() => CompositionRootTestHost.BuildProvider(new Dictionary<string, string?>
    {
        ["AppConfig:AI:Conversations:DatabasePath"] = Path.Combine(_workingDir, "conversations.db"),
        ["AppConfig:AI:Schedules:DatabasePath"] = Path.Combine(_workingDir, "schedules.db"),
        ["AppConfig:AI:Planner:DatabasePath"] = Path.Combine(_workingDir, "planner.db"),
        ["AppConfig:AI:PromptUsage:ConnectionString"] = $"Data Source={Path.Combine(_workingDir, "prompt-usage.db")}",
        ["AppConfig:AI:ContextManagement:ToolResultStorage:StoragePath"] = Path.Combine(_workingDir, "spills"),
        ["AppConfig:AI:Rag:VectorStore:Provider"] = "faiss",
        ["AppConfig:AI:Erasure:AuditStoragePath"] = Path.Combine(_workingDir, "compliance-audit"),
        ["AppConfig:AI:Audit:PseudonymKeyPath"] = Path.Combine(_workingDir, "keys", "audit-pseudonym.key"),
    });

    private static async Task<T> SendAsync<T>(ServiceProvider provider, IRequest<Domain.Common.Result<T>> command)
    {
        await using var scope = provider.CreateAsyncScope();
        var sp = scope.ServiceProvider;
        using var operatorScope = sp.GetRequiredService<IKnowledgeScopeWriter>().SetScope(userId: Operator, tenantId: "contoso");
        var result = await sp.GetRequiredService<IMediator>().Send(command);
        result.IsSuccess.Should().BeTrue(string.Join("; ", result.Errors));
        return result.Value!;
    }

    private static IVerifiableAuditChain ComplianceChain(ServiceProvider provider) =>
        provider.GetServices<IVerifiableAuditChain>().Should().ContainSingle(c => c.AuditName == "compliance",
            "the host's scheduled verification must walk the compliance trail").Subject;

    private static async Task AssertTheTrailVerifiesAsync(ServiceProvider provider, long expectedRecords)
    {
        var verified = await ComplianceChain(provider).VerifyChainAsync(checkpoint: null, CancellationToken.None);
        verified.IsValid.Should().BeTrue(verified.FailureReason);
        verified.VerifiedCount.Should().Be(expectedRecords);
    }

    private string TrailText() => string.Concat(Directory.GetFiles(TrailFolder, "*.jsonl").Select(File.ReadAllText));

    private List<JsonElement> TrailRecords() => Directory.GetFiles(TrailFolder, "*.jsonl")
        .Order(StringComparer.Ordinal)
        .SelectMany(File.ReadAllLines)
        .Where(line => line.Length > 0)
        .Select(line => JsonDocument.Parse(HashChainedJsonlWriter.ExtractPayload(line)).RootElement)
        .ToList();
}
