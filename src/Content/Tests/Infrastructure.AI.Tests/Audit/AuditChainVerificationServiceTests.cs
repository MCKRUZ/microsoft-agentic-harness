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

    private sealed class FakeChain(string name, AuditChainVerificationResult result) : IVerifiableAuditChain
    {
        public string AuditName => name;
        public int Calls { get; private set; }

        public Task<AuditChainVerificationResult> VerifyChainAsync(CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(result);
        }
    }

    private sealed class ThrowingChain(string name) : IVerifiableAuditChain
    {
        public string AuditName => name;

        public Task<AuditChainVerificationResult> VerifyChainAsync(CancellationToken cancellationToken) =>
            throw new IOException("disk gone");
    }
}
