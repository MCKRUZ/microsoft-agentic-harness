using Application.AI.Common.Interfaces.Audit;
using Domain.AI.Audit;
using Domain.Common.Config;
using Domain.Common.Config.AI;
using Domain.Common.Config.AI.Audit;
using FluentAssertions;
using Infrastructure.AI.Audit;
using Microsoft.Extensions.Logging;
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
        NewService(new AuditChainCheckpointStore(Config(receiptPath), NullLogger<AuditChainCheckpointStore>.Instance), receiptPath, chains);

    private AuditChainVerificationService NewService(
        IAuditChainCheckpoints checkpoints, string receiptPath, params IVerifiableAuditChain[] chains) =>
        new(chains, checkpoints, Config(receiptPath), _timeProvider, NullLogger<AuditChainVerificationService>.Instance);

    [Fact]
    public async Task VerifyAllChains_VerifiesEveryChainOnce()
    {
        var a = new FakeChain("a", AuditChainVerificationResult.Valid(0, 3));
        var b = new FakeChain("b", AuditChainVerificationResult.Valid(0, 5));
        var sut = NewService(_receiptDir, a, b);

        await sut.VerifyAllChainsAsync(CancellationToken.None);

        a.Calls.Should().Be(1);
        b.Calls.Should().Be(1);
    }

    [Fact]
    public async Task VerifyAllChains_WritesOneReceiptLinePerChain()
    {
        var a = new FakeChain("a", AuditChainVerificationResult.Valid(0, 3));
        var b = new FakeChain("b", AuditChainVerificationResult.Broken(0, 2, 2, "Record-hash mismatch at sequence 2."));
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
        var a = new FakeChain("a", AuditChainVerificationResult.Valid(0, 1));
        var sut = NewService(_receiptDir, a);

        await sut.VerifyAllChainsAsync(CancellationToken.None);

        _receiptDir.ShouldBeOwnerOnlyDirectory();
    }

    [Fact]
    public async Task VerifyAllChains_WhenReceiptPathEmpty_DoesNotWriteButStillVerifies()
    {
        var a = new FakeChain("a", AuditChainVerificationResult.Valid(0, 1));
        var sut = NewService(string.Empty, a);

        await sut.VerifyAllChainsAsync(CancellationToken.None);

        a.Calls.Should().Be(1);
        Directory.Exists(_receiptDir).Should().BeFalse();
    }

    [Fact]
    public async Task VerifyAllChains_WhenOneChainThrows_StillVerifiesTheOthers()
    {
        var throwing = new ThrowingChain("bad");
        var healthy = new FakeChain("good", AuditChainVerificationResult.Valid(0, 2));
        var sut = NewService(_receiptDir, throwing, healthy);

        await sut.VerifyAllChainsAsync(CancellationToken.None);

        healthy.Calls.Should().Be(1); // the throwing chain did not abort the pass
    }

    // #416: each clean pass records the chain's head, and the next pass hands it back so the chain must
    // still reach it. A broken pass must not move it forward, or the alert would stop after one pass.

    [Fact]
    public async Task VerifyAllChains_CleanPass_HandsItsHeadToTheNextPassAsTheCheckpoint()
    {
        var chain = new FakeChain("a", AuditChainVerificationResult.Valid(0, 3, headSequence: 2, headHash: "hash-2"));
        var sut = NewService(_receiptDir, chain);

        await sut.VerifyAllChainsAsync(CancellationToken.None);
        await sut.VerifyAllChainsAsync(CancellationToken.None);

        chain.CheckpointsSeen.Should().Equal(null, new AuditChainCheckpoint(2, "hash-2"));
    }

    [Fact]
    public async Task VerifyAllChains_BrokenPass_KeepsThePreviousCheckpoint()
    {
        var chain = new FakeChain("a", AuditChainVerificationResult.Valid(0, 3, headSequence: 2, headHash: "hash-2"));
        var sut = NewService(_receiptDir, chain);
        await sut.VerifyAllChainsAsync(CancellationToken.None);

        chain.Result = AuditChainVerificationResult.Broken(0, 1, 1, "truncated") with { HeadSequence = 0, HeadHash = "hash-0" };
        await sut.VerifyAllChainsAsync(CancellationToken.None);
        await sut.VerifyAllChainsAsync(CancellationToken.None);

        chain.CheckpointsSeen[^1].Should().Be(new AuditChainCheckpoint(2, "hash-2"),
            "a broken pass must not overwrite the point the chain has to reach");
    }

    [Fact]
    public async Task VerifyAllChains_AgainstARealChain_ReportsTheLogBeingDeletedAndRegrown()
    {
        // End to end with the real primitive: three records, a pass, then the file and its lock file (which
        // records the head) are deleted and a new chain of the same length grows in their place. Internally
        // consistent, so only the checkpoint can tell.
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
        File.Delete(logPath + ".lock");
        var regrown = new HashChainedJsonlWriter(logPath, NullLogger.Instance);
        for (var i = 0; i < 3; i++)
            (await regrown.AppendAsync($"{{\"forged\":{i}}}", CancellationToken.None)).IsSuccess.Should().BeTrue();
        chain.Writer = regrown;

        await sut.VerifyAllChainsAsync(CancellationToken.None);

        chain.LastResult!.IsValid.Should().BeFalse();
        chain.LastResult.FailureReason.Should().Contain("differs from the one last verified");
        regrown.Dispose();
    }

    // #416 review: a checkpoint belongs to a chain at one location, so a moved or reset log gets a fresh
    // baseline rather than a permanent false alarm; and deleting the file while the host runs must not
    // let the next pass re-baseline onto a regrown chain.

    [Fact]
    public async Task VerifyAllChains_LogMovedToANewLocation_StartsAFreshBaselineInsteadOfAlarming()
    {
        var chain = new FakeChain("a", AuditChainVerificationResult.Valid(0, 3, headSequence: 2, headHash: "hash-2"));
        var sut = NewService(_receiptDir, chain);
        await sut.VerifyAllChainsAsync(CancellationToken.None);

        chain.Location = Path.Combine(_receiptDir, "moved", "a.jsonl");
        await sut.VerifyAllChainsAsync(CancellationToken.None);

        chain.CheckpointsSeen.Should().HaveCount(2).And.OnlyContain(c => c == null);
    }

    [Fact]
    public async Task VerifyAllChains_CheckpointFileDeletedWhileRunning_StillUsesTheOneHeldInMemory()
    {
        var chain = new FakeChain("a", AuditChainVerificationResult.Valid(0, 3, headSequence: 2, headHash: "hash-2"));
        var store = new AuditChainCheckpointStore(Config(_receiptDir), NullLogger<AuditChainCheckpointStore>.Instance);
        var sut = NewService(store, _receiptDir, chain);
        await sut.VerifyAllChainsAsync(CancellationToken.None);

        File.Delete(store.LocationOf(chain)!);
        await sut.VerifyAllChainsAsync(CancellationToken.None);

        chain.CheckpointsSeen[^1].Should().Be(new AuditChainCheckpoint(2, "hash-2"),
            "deleting the file must not let this process re-baseline silently");
    }

    private sealed class FakeChain(string name, AuditChainVerificationResult result) : IVerifiableAuditChain
    {
        public string AuditName => name;
        public string Location { get; set; } = Path.Combine(Path.GetTempPath(), name + ".jsonl");
        public string AuditLocation => Location;
        public int Calls { get; private set; }
        public AuditChainVerificationResult Result { get; set; } = result;
        public List<AuditChainCheckpoint?> CheckpointsSeen { get; } = [];
        public Exception? Refusal { get; set; }

        public Task<AuditChainVerificationResult> VerifyChainAsync(
            AuditChainCheckpoint? checkpoint, CancellationToken cancellationToken)
        {
            Calls++;
            CheckpointsSeen.Add(checkpoint);
            return Refusal is null ? Task.FromResult(Result) : Task.FromException<AuditChainVerificationResult>(Refusal);
        }
    }

    private sealed class WriterChain(string name, HashChainedJsonlWriter writer) : IVerifiableAuditChain
    {
        public string AuditName => name;
        public string AuditLocation => "real-chain-location";
        public HashChainedJsonlWriter Writer { get; set; } = writer;
        public AuditChainVerificationResult? LastResult { get; private set; }

        public async Task<AuditChainVerificationResult> VerifyChainAsync(
            AuditChainCheckpoint? checkpoint, CancellationToken cancellationToken)
        {
            LastResult = await Writer.VerifyChainAsync(checkpoint, cancellationToken);
            return LastResult;
        }
    }

    [Fact]
    public async Task VerifyAllChains_ARefusedFolder_IsReportedCritical_WithoutAdvisingARebaseline()
    {
        // #416: re-baselining (deleting the checkpoint) would not clear a refused folder and would throw away
        // truncation detection, so the alert must not suggest it, and must not read as a tamper break at 0.
        var chain = new FakeChain("a", AuditChainVerificationResult.Valid(0, 3, headSequence: 2, headHash: "hash-2"));
        var logger = new RecordingLogger();
        var sut = new AuditChainVerificationService([chain],
            new AuditChainCheckpointStore(Config(_receiptDir), NullLogger<AuditChainCheckpointStore>.Instance),
            Config(_receiptDir), _timeProvider, logger);
        await sut.VerifyAllChainsAsync(CancellationToken.None); // leaves a checkpoint behind
        logger.Entries.Clear();

        chain.Refusal = new AuditFolderRefusedException("folder holds 2026-10-21.JSONL");
        await sut.VerifyAllChainsAsync(CancellationToken.None);

        logger.Entries.Should().ContainSingle(e => e.Level == LogLevel.Critical && e.Message.Contains("NOT VERIFIED")
            && e.Message.Contains("folder holds 2026-10-21.JSONL"));
        logger.Entries.Should().NotContain(e => e.Message.Contains("re-baseline") || e.Message.Contains("INTEGRITY BROKEN"));
    }

    private sealed class RecordingLogger : ILogger<AuditChainVerificationService>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => Entries.Add((logLevel, formatter(state, exception)));
    }

    private sealed class ThrowingChain(string name) : IVerifiableAuditChain
    {
        public string AuditName => name;
        public string AuditLocation => name;

        public Task<AuditChainVerificationResult> VerifyChainAsync(
            AuditChainCheckpoint? checkpoint, CancellationToken cancellationToken) =>
            throw new IOException("disk gone");
    }
}
