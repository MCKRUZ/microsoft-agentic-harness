using FluentAssertions;
using Infrastructure.AI.Audit;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Infrastructure.AI.Tests.Audit;

public sealed class HashChainedJsonlWriterTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _filePath;

    public HashChainedJsonlWriterTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"audit-chain-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _filePath = Path.Combine(_tempDir, "audit.jsonl");
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); }
        catch { /* best effort */ }
    }

    private HashChainedJsonlWriter NewWriter() =>
        new(_filePath, NullLogger.Instance);

    [Fact]
    public async Task Append_PersistsOneFramedLinePerRecord()
    {
        using var sut = NewWriter();

        (await sut.AppendAsync("{\"a\":1}", CancellationToken.None)).IsSuccess.Should().BeTrue();
        (await sut.AppendAsync("{\"a\":2}", CancellationToken.None)).IsSuccess.Should().BeTrue();

        var lines = await File.ReadAllLinesAsync(_filePath);
        lines.Should().HaveCount(2);
        // {json}\t{recordHash}\t{previousHash}\t{sequence}
        lines[0].Split('\t').Should().HaveCount(4);
        lines[0].Should().StartWith("{\"a\":1}\t");
        lines[0].Should().EndWith("\t0");
        lines[1].Should().EndWith("\t1");
    }

    [Fact]
    public async Task FirstRecord_LinksToGenesisHash()
    {
        using var sut = NewWriter();
        await sut.AppendAsync("{\"a\":1}", CancellationToken.None);

        var parts = (await File.ReadAllLinesAsync(_filePath))[0].Split('\t');
        parts[2].Should().Be(HashChainedJsonlWriter.GenesisHash);
    }

    [Fact]
    public async Task EachRecord_LinksToPreviousRecordHash()
    {
        using var sut = NewWriter();
        await sut.AppendAsync("{\"a\":1}", CancellationToken.None);
        await sut.AppendAsync("{\"a\":2}", CancellationToken.None);

        var lines = await File.ReadAllLinesAsync(_filePath);
        var firstRecordHash = lines[0].Split('\t')[1];
        var secondPreviousHash = lines[1].Split('\t')[2];
        secondPreviousHash.Should().Be(firstRecordHash);
    }

    [Fact]
    public async Task VerifyChain_OnUntamperedChain_ReturnsValid()
    {
        using var sut = NewWriter();
        for (var i = 0; i < 5; i++)
            await sut.AppendAsync($"{{\"a\":{i}}}", CancellationToken.None);

        var result = await sut.VerifyChainAsync(CancellationToken.None);

        result.IsValid.Should().BeTrue();
        result.VerifiedCount.Should().Be(5);
        result.FirstBrokenSequence.Should().BeNull();
    }

    [Fact]
    public async Task VerifyChain_OnEmptyOrMissingFile_ReturnsValidWithZeroCount()
    {
        using var sut = NewWriter();
        var result = await sut.VerifyChainAsync(CancellationToken.None);

        result.IsValid.Should().BeTrue();
        result.VerifiedCount.Should().Be(0);
    }

    [Fact]
    public async Task VerifyChain_WhenRecordContentAltered_BreaksAtThatExactSequence()
    {
        using var sut = NewWriter();
        for (var i = 0; i < 5; i++)
            await sut.AppendAsync($"{{\"a\":{i}}}", CancellationToken.None);

        // Tamper with record at sequence 2: change the payload but leave its stored hash.
        var lines = await File.ReadAllLinesAsync(_filePath);
        var parts = lines[2].Split('\t');
        lines[2] = string.Join('\t', "{\"a\":99}", parts[1], parts[2], parts[3]);
        await File.WriteAllLinesAsync(_filePath, lines);

        var result = await sut.VerifyChainAsync(CancellationToken.None);

        result.IsValid.Should().BeFalse();
        result.FirstBrokenSequence.Should().Be(2);
        result.VerifiedCount.Should().Be(2); // sequences 0 and 1 verified before the break
        result.FailureReason.Should().Contain("altered");
    }

    [Fact]
    public async Task VerifyChain_WhenRecordDeleted_DetectsSequenceGap()
    {
        using var sut = NewWriter();
        for (var i = 0; i < 5; i++)
            await sut.AppendAsync($"{{\"a\":{i}}}", CancellationToken.None);

        // Remove the record at sequence 2 entirely.
        var lines = (await File.ReadAllLinesAsync(_filePath)).ToList();
        lines.RemoveAt(2);
        await File.WriteAllLinesAsync(_filePath, lines);

        var result = await sut.VerifyChainAsync(CancellationToken.None);

        result.IsValid.Should().BeFalse();
        result.FirstBrokenSequence.Should().Be(2);
        result.FailureReason.Should().Contain("Sequence gap");
    }

    [Fact]
    public async Task VerifyChain_WhenLinkRehashedButPreviousBroken_DetectsMismatch()
    {
        using var sut = NewWriter();
        for (var i = 0; i < 4; i++)
            await sut.AppendAsync($"{{\"a\":{i}}}", CancellationToken.None);

        // Sophisticated tamper: change record 1's payload AND recompute its own hash so the
        // record-hash check passes — but its hash no longer matches what record 2 points back to.
        var lines = await File.ReadAllLinesAsync(_filePath);
        var p = lines[1].Split('\t');
        var altered = "{\"a\":777}";
        // Recompute a self-consistent hash the same way the writer does.
        var material = $"1\n{p[2]}\n{altered}";
        var newHash = Convert.ToHexStringLower(
            System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(material)));
        lines[1] = string.Join('\t', altered, newHash, p[2], "1");
        await File.WriteAllLinesAsync(_filePath, lines);

        var result = await sut.VerifyChainAsync(CancellationToken.None);

        result.IsValid.Should().BeFalse();
        // Record 1 verifies (self-consistent), but record 2's previous-hash no longer matches.
        result.FirstBrokenSequence.Should().Be(2);
        result.FailureReason.Should().Contain("Previous-hash mismatch");
    }

    [Fact]
    public async Task Append_AcrossWriterInstances_ContinuesTheSameChain()
    {
        using (var first = NewWriter())
        {
            await first.AppendAsync("{\"a\":0}", CancellationToken.None);
            await first.AppendAsync("{\"a\":1}", CancellationToken.None);
        }

        // A fresh instance (simulating process restart) must recover the head and continue.
        using var second = NewWriter();
        await second.AppendAsync("{\"a\":2}", CancellationToken.None);

        var lines = await File.ReadAllLinesAsync(_filePath);
        lines.Should().HaveCount(3);
        lines[2].Should().EndWith("\t2");

        var result = await second.VerifyChainAsync(CancellationToken.None);
        result.IsValid.Should().BeTrue();
        result.VerifiedCount.Should().Be(3);
    }

    [Fact]
    public async Task Append_OverLegacyUnchainedLines_StartsChainAndVerifies()
    {
        // Simulate a file written before the hash-chain primitive existed: raw JSON, no framing.
        await File.WriteAllLinesAsync(_filePath, new[] { "{\"legacy\":1}", "{\"legacy\":2}" });

        using var sut = NewWriter();
        await sut.AppendAsync("{\"a\":0}", CancellationToken.None);
        await sut.AppendAsync("{\"a\":1}", CancellationToken.None);

        var lines = await File.ReadAllLinesAsync(_filePath);
        lines.Should().HaveCount(4);
        // The chain genesis is the first record written after rollout.
        lines[2].Split('\t')[2].Should().Be(HashChainedJsonlWriter.GenesisHash);
        lines[2].Should().EndWith("\t0");

        var result = await sut.VerifyChainAsync(CancellationToken.None);
        result.IsValid.Should().BeTrue();
        result.VerifiedCount.Should().Be(2); // only the chained records count
    }

    [Fact]
    public async Task Append_PayloadWithRawTab_IsRejected()
    {
        using var sut = NewWriter();
        var result = await sut.AppendAsync("{\"a\":\"has\ttab\"}", CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Errors.Should().ContainSingle().Which.Should().Contain("tab");
        File.Exists(_filePath).Should().BeFalse();
    }

    [Theory]
    [InlineData("{\"a\":\"has\nnewline\"}")]
    [InlineData("{\"a\":\"has\rcarriage\"}")]
    public async Task Append_PayloadWithRawLineBreak_IsRejected(string payload)
    {
        using var sut = NewWriter();
        var result = await sut.AppendAsync(payload, CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        File.Exists(_filePath).Should().BeFalse();
    }

    [Fact]
    public async Task Append_AfterForgedTailLine_ContinuesFromLastValidHeadNotTheForgedLine()
    {
        using (var first = NewWriter())
        {
            await first.AppendAsync("{\"a\":0}", CancellationToken.None);
            await first.AppendAsync("{\"a\":1}", CancellationToken.None);
            await first.AppendAsync("{\"a\":2}", CancellationToken.None);
        }

        var lines = (await File.ReadAllLinesAsync(_filePath)).ToList();
        var lastValidHash = lines[2].Split('\t')[1]; // record hash of sequence 2

        // An attacker appends a structurally-valid but forged line out of band.
        var forged = string.Join('\t', "{\"x\":666}", new string('f', 64), new string('e', 64), "99");
        await File.AppendAllTextAsync(_filePath, forged + "\n");

        // A fresh instance recovers its head and must chain onto the last VALID record (seq 2),
        // never onto the forged tail.
        using var second = NewWriter();
        (await second.AppendAsync("{\"a\":3}", CancellationToken.None)).IsSuccess.Should().BeTrue();

        var newLine = (await File.ReadAllLinesAsync(_filePath)).Last();
        newLine.Should().EndWith("\t3"); // sequence = lastValidSequence (2) + 1
        newLine.Split('\t')[2].Should().Be(lastValidHash); // previousHash links to seq 2, not the forged line

        // The forged line is still surfaced as a break by verification.
        var verification = await second.VerifyChainAsync(CancellationToken.None);
        verification.IsValid.Should().BeFalse();
        verification.FirstBrokenSequence.Should().Be(3);
    }

    [Fact]
    public async Task Append_ConcurrentWrites_KeepSequencesMonotonicAndChainIntact()
    {
        using var sut = NewWriter();

        var tasks = Enumerable.Range(0, 50)
            .Select(i => sut.AppendAsync($"{{\"a\":{i}}}", CancellationToken.None))
            .ToArray();
        await Task.WhenAll(tasks);

        var result = await sut.VerifyChainAsync(CancellationToken.None);
        result.IsValid.Should().BeTrue();
        result.VerifiedCount.Should().Be(50);

        var sequences = (await File.ReadAllLinesAsync(_filePath))
            .Select(l => long.Parse(l.Split('\t')[3]))
            .OrderBy(x => x)
            .ToArray();
        sequences.Should().Equal(Enumerable.Range(0, 50).Select(i => (long)i));
    }
}
