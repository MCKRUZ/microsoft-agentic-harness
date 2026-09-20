using Infrastructure.Observability.Persistence;
using Xunit;

namespace Infrastructure.Observability.Tests.Integration.ReadSide;

/// <summary>
/// Round-trip tests for the two compliance-report reads added to <see cref="PostgresObservabilityStore"/>
/// in #696: <c>GetAuditEntriesAsync</c> and <c>GetSafetyEventsAsync</c>. Unlike every other read on
/// this store, these two report failure via <c>Result&lt;T&gt;</c> rather than swallowing it into an
/// empty list — a compliance report must be able to tell "no matches" apart from "the read failed."
/// </summary>
[Collection("Postgres")]
public sealed class ComplianceReadsTests
{
    private readonly PostgresFixture _fixture;

    public ComplianceReadsTests(PostgresFixture fixture) => _fixture = fixture;

    [SkippableFact]
    public async Task GetAuditEntriesAsync_ReturnsEntriesWrittenInWindow()
    {
        _fixture.SkipIfUnavailable();
        using var store = new PostgresObservabilityStore(_fixture.ConnectionString, _fixture.StoreLogger);
        var operation = $"op_{Guid.NewGuid():N}";

        await store.RecordAuditAsync(operation, "harness", sessionId: null,
            metadata: new Dictionary<string, object> { ["run_tag"] = _fixture.RunTag });

        var result = await store.GetAuditEntriesAsync(
            since: DateTimeOffset.UtcNow.AddMinutes(-5), until: null, source: null, limit: 500, offset: 0);

        Assert.True(result.IsSuccess);
        Assert.Contains(result.Value!, e => e.Operation == operation && e.Source == "harness");
    }

    [SkippableFact]
    public async Task GetAuditEntriesAsync_FiltersBySource()
    {
        _fixture.SkipIfUnavailable();
        using var store = new PostgresObservabilityStore(_fixture.ConnectionString, _fixture.StoreLogger);
        var harnessOp = $"op_harness_{Guid.NewGuid():N}";
        var apiOp = $"op_api_{Guid.NewGuid():N}";

        await store.RecordAuditAsync(harnessOp, "harness", null,
            new Dictionary<string, object> { ["run_tag"] = _fixture.RunTag });
        await store.RecordAuditAsync(apiOp, "api", null,
            new Dictionary<string, object> { ["run_tag"] = _fixture.RunTag });

        var result = await store.GetAuditEntriesAsync(
            since: DateTimeOffset.UtcNow.AddMinutes(-5), until: null, source: "api", limit: 500, offset: 0);

        Assert.True(result.IsSuccess);
        Assert.Contains(result.Value!, e => e.Operation == apiOp);
        Assert.DoesNotContain(result.Value!, e => e.Operation == harnessOp);
    }

    [SkippableFact]
    public async Task GetSafetyEventsAsync_ReturnsEventsAcrossSessions()
    {
        _fixture.SkipIfUnavailable();
        using var store = new PostgresObservabilityStore(_fixture.ConnectionString, _fixture.StoreLogger);
        var sessionId = await store.StartSessionAsync($"conv_{Guid.NewGuid():N}", "TestAgent", null);
        var marker = $"marker_{Guid.NewGuid():N}";

        await store.RecordSafetyEventAsync(sessionId, "prompt", "block", category: marker, severity: 9, filterName: "test");

        var result = await store.GetSafetyEventsAsync(
            since: DateTimeOffset.UtcNow.AddMinutes(-5), until: null, outcome: null, limit: 500, offset: 0);

        Assert.True(result.IsSuccess);
        Assert.Contains(result.Value!, e => e.SessionId == sessionId && e.Category == marker);
    }

    [SkippableFact]
    public async Task GetSafetyEventsAsync_FiltersByOutcome()
    {
        _fixture.SkipIfUnavailable();
        using var store = new PostgresObservabilityStore(_fixture.ConnectionString, _fixture.StoreLogger);
        var sessionId = await store.StartSessionAsync($"conv_{Guid.NewGuid():N}", "TestAgent", null);
        var blockMarker = $"marker_block_{Guid.NewGuid():N}";
        var passMarker = $"marker_pass_{Guid.NewGuid():N}";

        await store.RecordSafetyEventAsync(sessionId, "prompt", "block", category: blockMarker, severity: null, filterName: null);
        await store.RecordSafetyEventAsync(sessionId, "prompt", "pass", category: passMarker, severity: null, filterName: null);

        var result = await store.GetSafetyEventsAsync(
            since: DateTimeOffset.UtcNow.AddMinutes(-5), until: null, outcome: "block", limit: 500, offset: 0);

        Assert.True(result.IsSuccess);
        Assert.Contains(result.Value!, e => e.Category == blockMarker);
        Assert.DoesNotContain(result.Value!, e => e.Category == passMarker);
    }
}
