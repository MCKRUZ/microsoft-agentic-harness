using Domain.AI.Audit;
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

    // --- Directory permissions (#640, following #527's precedent) ---

    [Fact]
    public async Task AppendAsync_SegmentDirectoryIsCreatedOwnerOnly()
    {
        // #640: this is the tamper-evident audit log covering governance/escalation/drift/change/
        // egress events -- the segment directory (not pre-created by this fixture, unlike _tempDir
        // above; nested one level deeper so the create actually exercises a new directory) must never
        // inherit whatever the process umask/ACL happens to grant.
        var nestedFilePath = Path.Combine(_tempDir, "nested", "audit.jsonl");
        using var sut = new HashChainedJsonlWriter(nestedFilePath, NullLogger.Instance);

        (await sut.AppendAsync("{\"a\":1}", CancellationToken.None)).IsSuccess.Should().BeTrue();

        Path.Combine(_tempDir, "nested").ShouldBeOwnerOnlyDirectory();
    }

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

    // --- Segmented (multi-file) chains ---

    private string _segment = "a.jsonl";

    private HashChainedJsonlWriter NewSegmentedWriter() =>
        new(
            _tempDir,
            () => Path.Combine(_tempDir, _segment),
            () => Directory.GetFiles(_tempDir, "*.jsonl").OrderBy(f => f, StringComparer.Ordinal).ToArray(),
            Path.Combine(_tempDir, ".chain.lock"),
            NullLogger.Instance);

    [Fact]
    public async Task Segmented_ChainSpansFiles_AndLinksAcrossSegments()
    {
        using var sut = NewSegmentedWriter();

        _segment = "a.jsonl";
        await sut.AppendAsync("{\"a\":0}", CancellationToken.None);
        _segment = "b.jsonl";
        await sut.AppendAsync("{\"a\":1}", CancellationToken.None);

        var result = await sut.VerifyChainAsync(CancellationToken.None);
        result.IsValid.Should().BeTrue();
        result.VerifiedCount.Should().Be(2);

        var aRecordHash = (await File.ReadAllLinesAsync(Path.Combine(_tempDir, "a.jsonl")))[0].Split('\t')[1];
        var bPreviousHash = (await File.ReadAllLinesAsync(Path.Combine(_tempDir, "b.jsonl")))[0].Split('\t')[2];
        bPreviousHash.Should().Be(aRecordHash); // segment B chains onto segment A's tail
    }

    [Fact]
    public async Task Segmented_DeletingAnEntireMiddleSegment_BreaksTheChain()
    {
        using var sut = NewSegmentedWriter();

        _segment = "a.jsonl";
        await sut.AppendAsync("{\"a\":0}", CancellationToken.None);
        _segment = "b.jsonl";
        await sut.AppendAsync("{\"a\":1}", CancellationToken.None);
        _segment = "c.jsonl";
        await sut.AppendAsync("{\"a\":2}", CancellationToken.None);

        File.Delete(Path.Combine(_tempDir, "b.jsonl")); // wipe the whole middle day

        var result = await sut.VerifyChainAsync(CancellationToken.None);
        result.IsValid.Should().BeFalse();
        result.FirstBrokenSequence.Should().Be(1); // sequence 1 (from b) is gone
        result.FailureReason.Should().Contain("Sequence gap");
    }

    [Fact]
    public async Task Segmented_TamperInEarlierSegment_DetectedDuringCrossFileWalk()
    {
        using var sut = NewSegmentedWriter();

        _segment = "a.jsonl";
        await sut.AppendAsync("{\"a\":0}", CancellationToken.None);
        await sut.AppendAsync("{\"a\":1}", CancellationToken.None);
        _segment = "b.jsonl";
        await sut.AppendAsync("{\"a\":2}", CancellationToken.None);

        var aLines = await File.ReadAllLinesAsync(Path.Combine(_tempDir, "a.jsonl"));
        var p = aLines[1].Split('\t');
        aLines[1] = string.Join('\t', "{\"a\":9}", p[1], p[2], p[3]); // edit seq 1 in segment A
        await File.WriteAllLinesAsync(Path.Combine(_tempDir, "a.jsonl"), aLines);

        var result = await sut.VerifyChainAsync(CancellationToken.None);
        result.IsValid.Should().BeFalse();
        result.FirstBrokenSequence.Should().Be(1);
    }
    // #416: a hash chain cannot see its own end being cut off; the checkpoint from the last pass can.

    private async Task<AuditChainCheckpoint> AppendAndCheckpointAsync(HashChainedJsonlWriter sut, int records)
    {
        for (var i = 0; i < records; i++)
            (await sut.AppendAsync($"{{\"n\":{i}}}", CancellationToken.None)).IsSuccess.Should().BeTrue();

        var result = await sut.VerifyChainAsync(CancellationToken.None);
        return new AuditChainCheckpoint(result.HeadSequence, result.HeadHash!);
    }

    [Fact]
    public async Task VerifyChainAsync_ChainGrewPastItsCheckpoint_IsValid()
    {
        using var sut = NewWriter();
        var checkpoint = await AppendAndCheckpointAsync(sut, 3);
        (await sut.AppendAsync("{\"n\":3}", CancellationToken.None)).IsSuccess.Should().BeTrue();

        var result = await sut.VerifyChainAsync(checkpoint, CancellationToken.None);

        result.IsValid.Should().BeTrue();
        result.HeadSequence.Should().Be(3);
    }

    [Fact]
    public async Task VerifyChainAsync_NewestRecordsCutOff_IsBroken()
    {
        using (var sut = NewWriter())
            await AppendAndCheckpointAsync(sut, 3);
        var checkpoint = await CheckpointOf(NewWriter());

        // Drop the last line: what remains is a perfectly consistent two-record chain.
        var lines = await File.ReadAllLinesAsync(_filePath);
        await File.WriteAllLinesAsync(_filePath, lines[..^1]);

        using var reopened = NewWriter();
        (await reopened.VerifyChainAsync(CancellationToken.None)).IsValid.Should().BeTrue(
            "control: without the checkpoint the truncated chain looks intact");

        var result = await reopened.VerifyChainAsync(checkpoint, CancellationToken.None);

        result.IsValid.Should().BeFalse();
        result.FirstBrokenSequence.Should().Be(2);
        result.FailureReason.Should().Contain("removed from the end");
    }

    [Fact]
    public async Task VerifyChainAsync_LogDeletedAndRegrownPastTheCheckpoint_IsBroken()
    {
        AuditChainCheckpoint checkpoint;
        using (var sut = NewWriter())
            checkpoint = await AppendAndCheckpointAsync(sut, 3);

        // Deleting the lock file too drops the recorded head, so the new chain starts again from genesis and
        // is internally consistent: only the checkpoint can tell.
        File.Delete(_filePath);
        File.Delete(_filePath + ".lock");
        using var regrown = NewWriter();
        for (var i = 0; i < 5; i++)
            (await regrown.AppendAsync($"{{\"forged\":{i}}}", CancellationToken.None)).IsSuccess.Should().BeTrue();

        var result = await regrown.VerifyChainAsync(checkpoint, CancellationToken.None);

        result.IsValid.Should().BeFalse();
        result.FirstBrokenSequence.Should().Be(checkpoint.Sequence);
        result.FailureReason.Should().Contain("differs from the one last verified");
    }

    private static async Task<AuditChainCheckpoint> CheckpointOf(HashChainedJsonlWriter writer)
    {
        using (writer)
        {
            var result = await writer.VerifyChainAsync(CancellationToken.None);
            return new AuditChainCheckpoint(result.HeadSequence, result.HeadHash!);
        }
    }
    // #416 finding 2: scaled-out copies of the host sharing one storage location. Each writer instance
    // below stands in for a separate process: it has its own in-process lock and its own idea of the head,
    // and only the lock file beside the chain is shared.

    [Fact]
    public async Task TwoWriters_TakingTurns_WriteOneValidChain()
    {
        using var first = NewWriter();
        using var second = NewWriter();

        for (var i = 0; i < 5; i++)
        {
            (await first.AppendAsync($"{{\"from\":\"first\",\"n\":{i}}}", CancellationToken.None)).IsSuccess.Should().BeTrue();
            (await second.AppendAsync($"{{\"from\":\"second\",\"n\":{i}}}", CancellationToken.None)).IsSuccess.Should().BeTrue();
        }

        var result = await first.VerifyChainAsync(CancellationToken.None);

        result.IsValid.Should().BeTrue(result.FailureReason);
        result.VerifiedCount.Should().Be(10);
    }

    [Fact]
    public async Task TwoWriters_AppendingAtTheSameTime_WriteOneValidChain()
    {
        using var first = NewWriter();
        using var second = NewWriter();

        async Task Burst(HashChainedJsonlWriter writer, string name)
        {
            for (var i = 0; i < 25; i++)
                (await writer.AppendAsync($"{{\"from\":\"{name}\",\"n\":{i}}}", CancellationToken.None)).IsSuccess.Should().BeTrue();
        }

        await Task.WhenAll(Task.Run(() => Burst(first, "first")), Task.Run(() => Burst(second, "second")));

        var result = await NewWriter().VerifyChainAsync(CancellationToken.None);
        result.IsValid.Should().BeTrue(result.FailureReason);
        result.VerifiedCount.Should().Be(50);
    }

    [Fact]
    public async Task Append_WhileAnotherProcessHoldsTheLock_WaitsForItThenWrites()
    {
        using var sut = NewWriter();
        Task<Domain.Common.Result> append;

        await using (new FileStream(_filePath + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
        {
            append = sut.AppendAsync("{\"a\":1}", CancellationToken.None);
            await Task.Delay(150);
            append.IsCompleted.Should().BeFalse("the append must wait while another writer holds the lock");
        }

        (await append).IsSuccess.Should().BeTrue();
        (await sut.VerifyChainAsync(CancellationToken.None)).VerifiedCount.Should().Be(1);
    }

    [Fact]
    public async Task Append_AfterAnotherWriterAppendedALineThatDoesNotChain_ContinuesFromTheLastValidRecord()
    {
        using var sut = NewWriter();
        (await sut.AppendAsync("{\"a\":1}", CancellationToken.None)).IsSuccess.Should().BeTrue();

        // A forged line, shaped like a record but not linked to the chain.
        var forged = $"{{\"forged\":true}}\t{new string('a', 64)}\t{new string('b', 64)}\t1\n";
        await File.AppendAllTextAsync(_filePath, forged);

        (await sut.AppendAsync("{\"a\":2}", CancellationToken.None)).IsSuccess.Should().BeTrue();

        var result = await sut.VerifyChainAsync(CancellationToken.None);
        result.IsValid.Should().BeFalse("the forged line must still be reported");
        result.FirstBrokenSequence.Should().Be(1);
    }

    // #416 security review of the cross-process lock: every writer must agree on the head, whatever the
    // files look like, and a chain that got shorter must stay visibly shorter.

    [Fact]
    public async Task TwoWriters_AfterACrashLeftATornLine_DoNotReuseASequence()
    {
        using var first = NewWriter();
        using var second = NewWriter();
        (await first.AppendAsync("{\"a\":0}", CancellationToken.None)).IsSuccess.Should().BeTrue();

        // A writer died half-way through a line: no newline, not a record.
        await File.AppendAllTextAsync(_filePath, "{\"torn\":");

        (await second.AppendAsync("{\"a\":1}", CancellationToken.None)).IsSuccess.Should().BeTrue();
        (await first.AppendAsync("{\"a\":2}", CancellationToken.None)).IsSuccess.Should().BeTrue();

        var records = (await File.ReadAllLinesAsync(_filePath)).Where(l => l.Split('\t').Length == 4).ToList();
        records.Select(l => l.Split('\t')[3]).Should().Equal("0", "1", "2");
        records[2].Split('\t')[2].Should().Be(records[1].Split('\t')[1], "the third record chains onto the second");
        (await File.ReadAllLinesAsync(_filePath)).Should().Contain("{\"torn\":", "the torn line stays on its own line");
    }

    [Fact]
    public async Task Append_AfterRecordsWereCutFromTheEnd_KeepsTheGapVisible()
    {
        using var sut = NewWriter();
        for (var i = 0; i < 3; i++)
            (await sut.AppendAsync($"{{\"a\":{i}}}", CancellationToken.None)).IsSuccess.Should().BeTrue();

        var firstLine = (await File.ReadAllLinesAsync(_filePath))[0];
        await File.WriteAllTextAsync(_filePath, firstLine + "\n");

        (await sut.AppendAsync("{\"a\":3}", CancellationToken.None)).IsSuccess.Should().BeTrue();

        var result = await sut.VerifyChainAsync(CancellationToken.None);
        result.IsValid.Should().BeFalse("an append must not paper over records cut from the end");
        result.FirstBrokenSequence.Should().Be(1);
        result.FailureReason.Should().Contain("Sequence gap");
    }

    [Fact]
    public async Task Append_OnRestart_ContinuesFromTheRecordedHeadWithoutRescanning()
    {
        using (var before = NewWriter())
        {
            for (var i = 0; i < 3; i++)
                (await before.AppendAsync($"{{\"a\":{i}}}", CancellationToken.None)).IsSuccess.Should().BeTrue();
        }

        // Tamper with the first record. A rescan would stop there and restart the chain at sequence 0;
        // the recorded head carries on from sequence 2. Same length, so the head's position still holds.
        var lines = await File.ReadAllLinesAsync(_filePath);
        var parts = lines[0].Split('\t');
        lines[0] = string.Join('\t', "{\"a\":9}", parts[1], parts[2], parts[3]);
        await File.WriteAllTextAsync(_filePath, string.Join('\n', lines) + "\n");

        using var after = NewWriter();
        (await after.AppendAsync("{\"a\":3}", CancellationToken.None)).IsSuccess.Should().BeTrue();

        (await File.ReadAllLinesAsync(_filePath))[^1].Should().EndWith("\t3");
    }

    [Fact]
    public async Task Append_WithAnUnreadableRecordedHead_RescansAndStillChains()
    {
        using (var before = NewWriter())
            (await before.AppendAsync("{\"a\":0}", CancellationToken.None)).IsSuccess.Should().BeTrue();

        await File.WriteAllTextAsync(_filePath + ".lock", "not json");

        using var after = NewWriter();
        (await after.AppendAsync("{\"a\":1}", CancellationToken.None)).IsSuccess.Should().BeTrue();

        var result = await after.VerifyChainAsync(CancellationToken.None);
        result.IsValid.Should().BeTrue(result.FailureReason);
        result.VerifiedCount.Should().Be(2);
    }

    [Fact]
    public async Task Segmented_WriterWhoseClockIsBehind_AppendsToTheLatestSegment()
    {
        using var ahead = NewSegmentedWriter();
        using var behind = NewSegmentedWriter();

        _segment = "b.jsonl";
        (await ahead.AppendAsync("{\"a\":0}", CancellationToken.None)).IsSuccess.Should().BeTrue();
        _segment = "a.jsonl"; // the other host still thinks it is yesterday
        (await behind.AppendAsync("{\"a\":1}", CancellationToken.None)).IsSuccess.Should().BeTrue();

        File.Exists(Path.Combine(_tempDir, "a.jsonl")).Should().BeFalse("an earlier segment would put sequence numbers out of order");
        var result = await ahead.VerifyChainAsync(CancellationToken.None);
        result.IsValid.Should().BeTrue(result.FailureReason);
        result.VerifiedCount.Should().Be(2);
    }

    [Fact]
    public async Task LockHeldPastTheTimeout_AppendFailsAndVerifyReportsBusyNotBroken()
    {
        using var sut = NewWriter();
        (await sut.AppendAsync("{\"a\":0}", CancellationToken.None)).IsSuccess.Should().BeTrue();
        using var other = NewWriter();

        Domain.Common.Result append;
        Func<Task> verify;
        await using (new FileStream(_filePath + ".lock", FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var appendTask = sut.AppendAsync("{\"a\":1}", CancellationToken.None);
            var verifyTask = other.VerifyChainAsync(CancellationToken.None);
            append = await appendTask;
            verify = () => verifyTask;
            await verify.Should().ThrowAsync<AuditChainBusyException>();
        }

        append.IsSuccess.Should().BeFalse();
        append.Errors.Should().ContainSingle().Which.Should().Contain("busy");
        (await sut.VerifyChainAsync(CancellationToken.None)).VerifiedCount.Should().Be(1);
    }
}
