using Application.AI.Common.CQRS.Changes.ApproveChangeProposal;
using Application.AI.Common.CQRS.Changes.RejectChangeProposal;
using Application.AI.Common.Interfaces.Changes;
using Domain.AI.Changes;
using Domain.Common.Config;
using FluentAssertions;
using Infrastructure.AI.Changes;
using Infrastructure.AI.Tests.Changes.Support;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Infrastructure.AI.Tests.Changes;

/// <summary>
/// End-to-end proof that a human decision made over the HTTP surface reaches the durable,
/// hash-chained <c>changes.jsonl</c> — with the reviewer identity the token supplied.
/// </summary>
/// <remarks>
/// <para>
/// The handler unit tests assert against a recording stub; this suite wires the REAL
/// <see cref="JsonlChangeAuditWriter"/> to a temp directory and reads the file back, because the
/// property that matters is "an auditor can reconstruct who approved this after the host
/// restarted", and only the on-disk bytes demonstrate that.
/// </para>
/// <para>
/// The threat this closes: the proposal store's production default
/// (<see cref="InMemoryChangeProposalStore"/>) is a per-process dictionary, and the orchestrator
/// early-returns on terminal proposals — so before the decision handlers appended here, an
/// approval could be merged and a rejection made terminal with no surviving record of the
/// deciding human anywhere.
/// </para>
/// </remarks>
public sealed class HumanDecisionAuditTrailTests : IDisposable
{
    private readonly string _tempDir =
        Path.Combine(Path.GetTempPath(), $"change-audit-{Guid.NewGuid():N}");

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); }
        catch { /* best effort */ }
    }

    private string AuditFilePath => Path.Combine(_tempDir, "changes.jsonl");

    private IOptionsMonitor<AppConfig> Config()
    {
        var config = new AppConfig();
        config.AI.Changes.Enabled = true;
        config.AI.Changes.DefaultMode = "Live";
        config.AI.Changes.AuditStoragePath = _tempDir;
        return new StaticMonitor(config);
    }

    private static ChangeProposal AwaitingApproval() =>
        TestProposals.NewProposal() with { Status = ChangeProposalStatus.AwaitingApproval };

    [Fact]
    public async Task Approve_WritesReviewerIdentityToDurableChangesJsonl()
    {
        var config = Config();
        var store = new InMemoryChangeProposalStore();
        var pending = AwaitingApproval();
        await store.SaveAsync(pending, CancellationToken.None);

        using var audit = new JsonlChangeAuditWriter(config, NullLogger<JsonlChangeAuditWriter>.Instance);
        var sut = new ApproveChangeProposalCommandHandler(
            store,
            new NoOpDispatchQueue(),
            audit,
            config,
            NullLogger<ApproveChangeProposalCommandHandler>.Instance,
            TimeProvider.System);

        var result = await sut.Handle(
            new ApproveChangeProposalCommand
            {
                ProposalId = pending.Id,
                ReviewerId = "alice@contoso.com",
                Reason = "diff reviewed"
            },
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();

        var lines = await File.ReadAllLinesAsync(AuditFilePath);
        lines.Should().ContainSingle("one decision produces exactly one audit line");
        var line = lines[0];
        line.Should().Contain("\"reviewer_id\":\"alice@contoso.com\"",
            "the durable record must name the human the token identified — this is the whole " +
            "point of stamping ReviewerId from claims instead of the request body");
        line.Should().Contain($"\"proposal_id\":\"{pending.Id}\"");
        line.Should().Contain("\"gate_key\":\"approval\"");
        line.Should().Contain("\"decision\":\"Pass\"");
        line.Should().Contain("\"reason\":\"diff reviewed\"");
    }

    [Fact]
    public async Task Reject_WritesReviewerIdentityToDurableChangesJsonl()
    {
        // Rejection drives the proposal terminal and the orchestrator skips terminal proposals,
        // so this handler is the only writer that will ever record this decision.
        var config = Config();
        var store = new InMemoryChangeProposalStore();
        var pending = AwaitingApproval();
        await store.SaveAsync(pending, CancellationToken.None);

        using var audit = new JsonlChangeAuditWriter(config, NullLogger<JsonlChangeAuditWriter>.Instance);
        var sut = new RejectChangeProposalCommandHandler(
            store,
            audit,
            config,
            NullLogger<RejectChangeProposalCommandHandler>.Instance,
            TimeProvider.System);

        var result = await sut.Handle(
            new RejectChangeProposalCommand
            {
                ProposalId = pending.Id,
                ReviewerId = "carol@contoso.com",
                Reason = "no rollback plan"
            },
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();

        var lines = await File.ReadAllLinesAsync(AuditFilePath);
        lines.Should().ContainSingle();
        lines[0].Should().Contain("\"reviewer_id\":\"carol@contoso.com\"");
        lines[0].Should().Contain("\"decision\":\"Fail\"");
    }

    [Fact]
    public async Task Approve_AuditChainVerifiesAfterDecision()
    {
        // The decision line must be a well-formed link in the tamper-evident chain, not just
        // appended text — otherwise later verification would flag the audit as corrupt.
        var config = Config();
        var store = new InMemoryChangeProposalStore();
        var pending = AwaitingApproval();
        await store.SaveAsync(pending, CancellationToken.None);

        using var audit = new JsonlChangeAuditWriter(config, NullLogger<JsonlChangeAuditWriter>.Instance);
        var sut = new ApproveChangeProposalCommandHandler(
            store,
            new NoOpDispatchQueue(),
            audit,
            config,
            NullLogger<ApproveChangeProposalCommandHandler>.Instance,
            TimeProvider.System);

        await sut.Handle(
            new ApproveChangeProposalCommand { ProposalId = pending.Id, ReviewerId = "alice@contoso.com" },
            CancellationToken.None);

        var verification = await audit.VerifyChainAsync(CancellationToken.None);

        verification.IsValid.Should().BeTrue(
            "a human decision must link into the hash chain like any orchestrator-written record");
    }

    private sealed class NoOpDispatchQueue : IChangeProposalDispatchQueue
    {
        public ValueTask EnqueueAsync(string proposalId, CancellationToken cancellationToken) =>
            ValueTask.CompletedTask;

        public async IAsyncEnumerable<string> DequeueAllAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.CompletedTask;
            yield break;
        }
    }

    private sealed class StaticMonitor(AppConfig value) : IOptionsMonitor<AppConfig>
    {
        public AppConfig CurrentValue { get; } = value;
        public AppConfig Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<AppConfig, string?> listener) => null;
    }
}
