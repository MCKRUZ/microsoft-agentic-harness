using Infrastructure.AI.Tests.Audit;
using Application.AI.Common.Interfaces.Egress;
using Domain.AI.Egress;
using FluentAssertions;
using Infrastructure.AI.Egress;
using Infrastructure.AI.Tests.Egress.Support;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Infrastructure.AI.Tests.Egress;

public sealed class JsonlEgressAuditWriterTests : IDisposable
{
    private readonly string _tempDir;
    private readonly JsonlEgressAuditWriter _sut;
    private readonly string _expectedFile;

    public JsonlEgressAuditWriterTests()
    {
        var (monitor, dir) = TestConfig.NewMonitor();
        _tempDir = dir;
        _sut = new JsonlEgressAuditWriter(monitor, TimeProvider.System, NullLogger<JsonlEgressAuditWriter>.Instance, AuditPseudonyms.Create());
        _expectedFile = Path.Combine(dir, "audit", "egress.jsonl");
    }

    public void Dispose()
    {
        _sut.Dispose();
        try { Directory.Delete(_tempDir, recursive: true); }
        catch { /* best effort */ }
    }

    [Fact]
    public async Task Append_TheChainCannotBeWritten_StillRefusesTheRequestButHoldsTheRecordForReplay()
    {
        var dayFolder = Path.Combine(_tempDir, "audit", "egress");
        Directory.CreateDirectory(Path.GetDirectoryName(dayFolder)!);
        File.WriteAllText(dayFolder, "not a folder");

        var append = () => _sut.AppendAsync(AllowDecision(), TestIdentity.Default, CancellationToken.None);

        await append.Should().ThrowAsync<IOException>("an outbound request is never let through unaudited (#1001 changes nothing here)");
        File.ReadAllText(Directory.GetFiles(_sut.PendingLocation, "*.pending").Single()).Should().Contain(
            "\"not_carried_out\":true", "the decision is kept, marked as one whose request never went out");

        File.Delete(dayFolder);
        (await _sut.ReplayPendingAsync(CancellationToken.None)).Written.Should().Be(1);
        (await _sut.VerifyChainAsync(checkpoint: null, CancellationToken.None)).VerifiedCount.Should().Be(1);
        var replayed = (await _sut.GetRecordsAsync(new EgressAuditQuery(), CancellationToken.None)).Value!.Single();
        replayed.Allowed.Should().BeTrue("the decision itself is unchanged");
        replayed.NotCarriedOut.Should().BeTrue("an auditor must not read a refused request as one that went out");
    }

    [Fact]
    public async Task Append_AppendedWhenMade_CarriesNoNotCarriedOutMarker()
    {
        await _sut.AppendAsync(AllowDecision(), TestIdentity.Default, CancellationToken.None);

        (await AuditTrailFiles.ReadAllLinesAsync(_expectedFile)).Single().Should().NotContain("not_carried_out");
    }

    [Fact]
    public async Task Append_WritesOneLinePerDecision()
    {
        var d1 = AllowDecision();
        var d2 = DenyDecision();

        await _sut.AppendAsync(d1, TestIdentity.Default, CancellationToken.None);
        await _sut.AppendAsync(d2, TestIdentity.Default, CancellationToken.None);

        var lines = await AuditTrailFiles.ReadAllLinesAsync(_expectedFile);
        lines.Should().HaveCount(2);
    }

    [Fact]
    public async Task Append_IncludesAllExpectedFields()
    {
        var decision = AllowDecision();

        await _sut.AppendAsync(decision, TestIdentity.Default, CancellationToken.None);

        var line = (await AuditTrailFiles.ReadAllLinesAsync(_expectedFile))[0];
        line.Should().Contain("\"allowed\":true");
        line.Should().Contain("\"host\":\"api.github.com\"");
        line.Should().Contain("\"scheme\":\"https\"");
        line.Should().Contain("\"port\":443");
        line.Should().Contain("\"agent\":\"agent-egress\"");
        line.Should().Contain("\"tenant\":\"tenant-egress\"");
        line.Should().Contain("\"matched_allowlist_entry\":\"api.github.com\"");
    }

    [Fact]
    public async Task Append_RecordsWhereTheRequestWentButNotThePathQueryOrUserInfo()
    {
        // The trail is kept for good, and a path or query names people often enough ("/users/octocat",
        // "?email=..."). It keeps scheme, host and port, which the host filter and every reader need, and a keyed
        // fingerprint of the full address the key holder can match against a known one (#903).
        var target = new Uri("https://svc:s3cret@api.github.com/users/octocat?email=dana%40contoso.com");
        var decision = AllowDecision() with { Target = target };
        var caller = TestIdentity.Default with { Id = "direct-invoke:user-oid-42" };

        await _sut.AppendAsync(decision, caller, CancellationToken.None);

        var line = (await AuditTrailFiles.ReadAllLinesAsync(_expectedFile))[0];
        line.Should().NotContain("octocat").And.NotContain("dana").And.NotContain("s3cret").And.NotContain("user-oid-42");
        var pseudonyms = AuditPseudonyms.Create();
        line.Should().Contain(
            $"\"target\":\"https://api.github.com/#{pseudonyms.Fingerprint("egress-target", "https://api.github.com/users/octocat?email=dana%40contoso.com")}\"",
            "the fingerprint covers what went on the wire, never the user info, so it cannot confirm a guessed password");
        line.Should().Contain("\"host\":\"api.github.com\"");
        line.Should().Contain($"\"agent\":\"direct-invoke:{pseudonyms.Person("user-oid-42")}\"");
    }

    [Fact]
    public async Task Append_RelativeTarget_IsRecordedByFingerprintWithoutThrowing()
    {
        // No producer passes one today, but an audit write must never fail the request it records.
        var decision = AllowDecision() with { Target = new Uri("/users/octocat", UriKind.Relative) };

        await _sut.AppendAsync(decision, TestIdentity.Default, CancellationToken.None);

        var line = (await AuditTrailFiles.ReadAllLinesAsync(_expectedFile))[0];
        line.Should().NotContain("octocat").And.Contain("\"target\":\"#fp:");
    }

    [Fact]
    public async Task Append_DenyDecision_StillRecorded()
    {
        var decision = DenyDecision();

        await _sut.AppendAsync(decision, TestIdentity.Default, CancellationToken.None);

        var line = (await AuditTrailFiles.ReadAllLinesAsync(_expectedFile))[0];
        line.Should().Contain("\"allowed\":false");
        line.Should().Contain("\"reason\":\"No allowlist entry matched (host, scheme, port).\"");
    }

    [Fact]
    public async Task Append_ConcurrentWrites_AllSerialised()
    {
        var tasks = Enumerable.Range(0, 20)
            .Select(_ => _sut.AppendAsync(AllowDecision(), TestIdentity.Default, CancellationToken.None))
            .ToArray();

        await Task.WhenAll(tasks);

        var lines = await AuditTrailFiles.ReadAllLinesAsync(_expectedFile);
        lines.Should().HaveCount(20);
        lines.Should().OnlyContain(l => l.Contains("\"allowed\":true"));
    }

    [Fact]
    public async Task GetRecords_RoundTripsAllowAndDenyDecisions()
    {
        await _sut.AppendAsync(AllowDecision(), TestIdentity.Default, CancellationToken.None);
        await _sut.AppendAsync(DenyDecision(), TestIdentity.Default, CancellationToken.None);

        var result = await _sut.GetRecordsAsync(new EgressAuditQuery(), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().HaveCount(2);
        result.Value.Should().Contain(r => r.Allowed && r.Host == "api.github.com");
        result.Value.Should().Contain(r => !r.Allowed && r.Host == "evil.example.com");
    }

    [Fact]
    public async Task GetRecords_FiltersByAllowed()
    {
        await _sut.AppendAsync(AllowDecision(), TestIdentity.Default, CancellationToken.None);
        await _sut.AppendAsync(DenyDecision(), TestIdentity.Default, CancellationToken.None);

        var result = await _sut.GetRecordsAsync(new EgressAuditQuery { Allowed = false }, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().ContainSingle(r => !r.Allowed);
    }

    [Fact]
    public async Task GetRecords_FiltersByHost()
    {
        await _sut.AppendAsync(AllowDecision(), TestIdentity.Default, CancellationToken.None);
        await _sut.AppendAsync(DenyDecision(), TestIdentity.Default, CancellationToken.None);

        var result = await _sut.GetRecordsAsync(
            new EgressAuditQuery { Host = "evil.example.com" }, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().ContainSingle(r => r.Host == "evil.example.com");
    }

    [Fact]
    public async Task GetRecords_FiltersByDateRange()
    {
        await _sut.AppendAsync(AllowDecision(), TestIdentity.Default, CancellationToken.None);

        var result = await _sut.GetRecordsAsync(
            new EgressAuditQuery { Start = DateTimeOffset.UtcNow.AddMinutes(1) }, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().BeEmpty();
    }

    [Fact]
    public async Task GetRecords_EmptyDirectory_ReturnsEmpty()
    {
        var result = await _sut.GetRecordsAsync(new EgressAuditQuery(), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().BeEmpty();
    }

    [Fact]
    public async Task GetRecords_CorruptLine_IsSkippedNotFailed()
    {
        await _sut.AppendAsync(AllowDecision(), TestIdentity.Default, CancellationToken.None);
        await File.AppendAllTextAsync(AuditTrailFiles.OnlyDayFile(_expectedFile), "not-json-and-not-chained\n");
        await _sut.AppendAsync(DenyDecision(), TestIdentity.Default, CancellationToken.None);

        var result = await _sut.GetRecordsAsync(new EgressAuditQuery(), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().HaveCount(2);
    }

    [Fact]
    public async Task GetRecords_ReturnsChronologicalOrder()
    {
        await _sut.AppendAsync(AllowDecision(), TestIdentity.Default, CancellationToken.None);
        await _sut.AppendAsync(DenyDecision(), TestIdentity.Default, CancellationToken.None);

        var result = await _sut.GetRecordsAsync(new EgressAuditQuery(), CancellationToken.None);

        result.Value.Should().BeInAscendingOrder(r => r.Timestamp);
    }

    private static EgressDecision AllowDecision() => new()
    {
        Allowed = true,
        Reason = "Matched allowlist entry.",
        MatchedAllowlistEntry = "api.github.com",
        FinalIpAddress = "140.82.114.6",
        Target = new Uri("https://api.github.com/users/octocat"),
        DecidedAt = DateTimeOffset.UtcNow
    };

    private static EgressDecision DenyDecision() => new()
    {
        Allowed = false,
        Reason = "No allowlist entry matched (host, scheme, port).",
        Target = new Uri("https://evil.example.com/"),
        DecidedAt = DateTimeOffset.UtcNow
    };
}
