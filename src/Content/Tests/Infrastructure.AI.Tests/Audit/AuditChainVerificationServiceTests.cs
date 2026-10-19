using Application.AI.Common.Interfaces.Audit;
using Domain.AI.Audit;
using Domain.Common.Config;
using Domain.Common.Config.AI;
using Domain.Common.Config.AI.Audit;
using FluentAssertions;
using Infrastructure.AI.Audit;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Moq;
using Xunit;

namespace Infrastructure.AI.Tests.Audit;

public sealed class AuditChainVerificationServiceTests : IDisposable
{
    private readonly string _receiptDir;
    private readonly FakeTimeProvider _timeProvider;

    public AuditChainVerificationServiceTests()
    {
        _receiptDir = Path.Combine(Path.GetTempPath(), $"audit-verify-{Guid.NewGuid():N}");
        _timeProvider = new FakeTimeProvider(new DateTimeOffset(2026, 6, 20, 3, 0, 0, TimeSpan.Zero));
    }

    public void Dispose()
    {
        try { Directory.Delete(_receiptDir, recursive: true); }
        catch { /* best effort */ }
    }

    private IOptionsMonitor<AppConfig> Config(string receiptPath) =>
        Mock.Of<IOptionsMonitor<AppConfig>>(o => o.CurrentValue == new AppConfig
        {
            AI = new AIConfig { Audit = new AuditConfig { ReceiptPath = receiptPath } }
        });

    private AuditChainVerificationService NewService(string receiptPath, params IVerifiableAuditChain[] chains) =>
        new(chains, Config(receiptPath), _timeProvider, NullLogger<AuditChainVerificationService>.Instance);

    [Fact]
    public async Task VerifyAllChains_VerifiesEveryChainOnce()
    {
        var a = new FakeChain("a", AuditChainVerificationResult.Valid(3));
        var b = new FakeChain("b", AuditChainVerificationResult.Valid(5));
        var sut = NewService(_receiptDir, a, b);

        await sut.VerifyAllChainsAsync(CancellationToken.None);

        a.Calls.Should().Be(1);
        b.Calls.Should().Be(1);
    }

    [Fact]
    public async Task VerifyAllChains_WritesOneReceiptLinePerChain()
    {
        var a = new FakeChain("a", AuditChainVerificationResult.Valid(3));
        var b = new FakeChain("b", AuditChainVerificationResult.Broken(2, 2, "Record-hash mismatch at sequence 2."));
        var sut = NewService(_receiptDir, a, b);

        await sut.VerifyAllChainsAsync(CancellationToken.None);

        var receiptFile = Path.Combine(_receiptDir, "2026-06-20.jsonl");
        var lines = await File.ReadAllLinesAsync(receiptFile);
        lines.Should().HaveCount(2);
        lines.Should().Contain(l => l.Contains("\"audit_name\":\"a\"") && l.Contains("\"is_valid\":true"));
        lines.Should().Contain(l => l.Contains("\"audit_name\":\"b\"") && l.Contains("\"is_valid\":false")
            && l.Contains("\"first_broken_sequence\":2"));
    }

    [Fact]
    public async Task VerifyAllChains_ReceiptDirectoryIsCreatedOwnerOnly()
    {
        // #660, following #640/#527's precedent: a receipt reports which audit chains verified
        // clean or broken, including a failure reason -- confidentiality-relevant on a shared host.
        var a = new FakeChain("a", AuditChainVerificationResult.Valid(1));
        var sut = NewService(_receiptDir, a);

        await sut.VerifyAllChainsAsync(CancellationToken.None);

        _receiptDir.ShouldBeOwnerOnlyDirectory();
    }

    [Fact]
    public async Task VerifyAllChains_WhenReceiptPathEmpty_DoesNotWriteButStillVerifies()
    {
        var a = new FakeChain("a", AuditChainVerificationResult.Valid(1));
        var sut = NewService(string.Empty, a);

        await sut.VerifyAllChainsAsync(CancellationToken.None);

        a.Calls.Should().Be(1);
        Directory.Exists(_receiptDir).Should().BeFalse();
    }

    [Fact]
    public async Task VerifyAllChains_WhenOneChainThrows_StillVerifiesTheOthers()
    {
        var throwing = new ThrowingChain("bad");
        var healthy = new FakeChain("good", AuditChainVerificationResult.Valid(2));
        var sut = NewService(_receiptDir, throwing, healthy);

        await sut.VerifyAllChainsAsync(CancellationToken.None);

        healthy.Calls.Should().Be(1); // the throwing chain did not abort the pass
    }

    // #416: each clean pass records the chain's head, and the next pass hands it back so the chain must
    // still reach it. A broken pass must not move it forward, or the alert would stop after one pass.

    [Fact]
    public async Task VerifyAllChains_CleanPass_HandsItsHeadToTheNextPassAsTheCheckpoint()
    {
        var chain = new FakeChain("a", AuditChainVerificationResult.Valid(3, headSequence: 2, headHash: "hash-2"));
        var sut = NewService(_receiptDir, chain);

        await sut.VerifyAllChainsAsync(CancellationToken.None);
        await sut.VerifyAllChainsAsync(CancellationToken.None);

        chain.CheckpointsSeen.Should().Equal(null, new AuditChainCheckpoint(2, "hash-2"));
    }

    [Fact]
    public async Task VerifyAllChains_BrokenPass_KeepsThePreviousCheckpoint()
    {
        var chain = new FakeChain("a", AuditChainVerificationResult.Valid(3, headSequence: 2, headHash: "hash-2"));
        var sut = NewService(_receiptDir, chain);
        await sut.VerifyAllChainsAsync(CancellationToken.None);

        chain.Result = AuditChainVerificationResult.Broken(1, 1, "truncated") with { HeadSequence = 0, HeadHash = "hash-0" };
        await sut.VerifyAllChainsAsync(CancellationToken.None);
        await sut.VerifyAllChainsAsync(CancellationToken.None);

        chain.CheckpointsSeen[^1].Should().Be(new AuditChainCheckpoint(2, "hash-2"),
            "a broken pass must not overwrite the point the chain has to reach");
    }

    [Fact]
    public async Task VerifyAllChains_AgainstARealChain_ReportsTheLogBeingDeletedAndRegrown()
    {
        // End to end with the real primitive: three records, a pass, then the file is deleted and a
        // new chain of the same length grows in its place. Internally consistent, so only the checkpoint
        // can tell.
        var logPath = Path.Combine(_receiptDir, "chain", "audit.jsonl");
        Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
        var writer = new HashChainedJsonlWriter(logPath, NullLogger.Instance);
        for (var i = 0; i < 3; i++)
            (await writer.AppendAsync($"{{\"n\":{i}}}", CancellationToken.None)).IsSuccess.Should().BeTrue();

        var chain = new WriterChain("real", writer);
        var sut = NewService(_receiptDir, chain);
        await sut.VerifyAllChainsAsync(CancellationToken.None);
        chain.LastResult!.IsValid.Should().BeTrue();

        writer.Dispose();
        File.Delete(logPath);
        var regrown = new HashChainedJsonlWriter(logPath, NullLogger.Instance);
        for (var i = 0; i < 3; i++)
            (await regrown.AppendAsync($"{{\"forged\":{i}}}", CancellationToken.None)).IsSuccess.Should().BeTrue();
        chain.Writer = regrown;

        await sut.VerifyAllChainsAsync(CancellationToken.None);

        chain.LastResult!.IsValid.Should().BeFalse();
        chain.LastResult.FailureReason.Should().Contain("differs from the one last verified");
        regrown.Dispose();
    }

    private sealed class FakeChain(string name, AuditChainVerificationResult result) : IVerifiableAuditChain
    {
        public string AuditName => name;
        public int Calls { get; private set; }
        public AuditChainVerificationResult Result { get; set; } = result;
        public List<AuditChainCheckpoint?> CheckpointsSeen { get; } = [];

        public Task<AuditChainVerificationResult> VerifyChainAsync(
            AuditChainCheckpoint? checkpoint, CancellationToken cancellationToken)
        {
            Calls++;
            CheckpointsSeen.Add(checkpoint);
            return Task.FromResult(Result);
        }
    }

    private sealed class WriterChain(string name, HashChainedJsonlWriter writer) : IVerifiableAuditChain
    {
        public string AuditName => name;
        public HashChainedJsonlWriter Writer { get; set; } = writer;
        public AuditChainVerificationResult? LastResult { get; private set; }

        public async Task<AuditChainVerificationResult> VerifyChainAsync(
            AuditChainCheckpoint? checkpoint, CancellationToken cancellationToken)
        {
            LastResult = await Writer.VerifyChainAsync(checkpoint, cancellationToken);
            return LastResult;
        }
    }

    private sealed class ThrowingChain(string name) : IVerifiableAuditChain
    {
        public string AuditName => name;

        public Task<AuditChainVerificationResult> VerifyChainAsync(
            AuditChainCheckpoint? checkpoint, CancellationToken cancellationToken) =>
            throw new IOException("disk gone");
    }
}
