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
