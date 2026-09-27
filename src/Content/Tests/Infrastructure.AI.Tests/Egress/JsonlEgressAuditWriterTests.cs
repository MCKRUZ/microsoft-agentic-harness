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
        _sut = new JsonlEgressAuditWriter(monitor, NullLogger<JsonlEgressAuditWriter>.Instance);
        _expectedFile = Path.Combine(dir, "audit", "egress.jsonl");
    }

    public void Dispose()
    {
        _sut.Dispose();
        try { Directory.Delete(_tempDir, recursive: true); }
        catch { /* best effort */ }
    }

    [Fact]
    public async Task Append_WritesOneLinePerDecision()
    {
        var d1 = AllowDecision();
        var d2 = DenyDecision();

        await _sut.AppendAsync(d1, TestIdentity.Default, CancellationToken.None);
        await _sut.AppendAsync(d2, TestIdentity.Default, CancellationToken.None);

        var lines = await File.ReadAllLinesAsync(_expectedFile);
        lines.Should().HaveCount(2);
    }

    [Fact]
    public async Task Append_IncludesAllExpectedFields()
    {
        var decision = AllowDecision();

        await _sut.AppendAsync(decision, TestIdentity.Default, CancellationToken.None);

        var line = (await File.ReadAllLinesAsync(_expectedFile))[0];
        line.Should().Contain("\"allowed\":true");
        line.Should().Contain("\"host\":\"api.github.com\"");
        line.Should().Contain("\"scheme\":\"https\"");
        line.Should().Contain("\"port\":443");
        line.Should().Contain("\"agent\":\"agent-egress\"");
        line.Should().Contain("\"tenant\":\"tenant-egress\"");
        line.Should().Contain("\"matched_allowlist_entry\":\"api.github.com\"");
    }

    [Fact]
    public async Task Append_DenyDecision_StillRecorded()
    {
        var decision = DenyDecision();

        await _sut.AppendAsync(decision, TestIdentity.Default, CancellationToken.None);

        var line = (await File.ReadAllLinesAsync(_expectedFile))[0];
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

        var lines = await File.ReadAllLinesAsync(_expectedFile);
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
        await File.AppendAllTextAsync(_expectedFile, "not-json-and-not-chained\n");
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
