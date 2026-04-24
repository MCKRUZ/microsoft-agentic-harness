using Infrastructure.Observability.Persistence;
using Npgsql;
using Xunit;

namespace Infrastructure.Observability.Tests.Integration.WriteSide;

[Collection("Postgres")]
public sealed class SafetyEventWriteTests
{
    private readonly PostgresFixture _fixture;

    public SafetyEventWriteTests(PostgresFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task RecordSafetyEventAsync_PromptBlock_Persists()
    {
        if (!_fixture.IsAvailable) return;

        using var store = new PostgresObservabilityStore(_fixture.ConnectionString, _fixture.StoreLogger);
        var builder = new TestDataBuilder(_fixture);
        var session = await builder.CreateSessionAsync(status: "active");

        await store.RecordSafetyEventAsync(
            session.Id,
            phase: "prompt",
            outcome: "block",
            category: "hate",
            severity: 6,
            filterName: "azure_content_safety");

        var rows = await _fixture.QueryRowsAsync(
            "SELECT phase, outcome, category, severity, filter_name FROM safety_events WHERE session_id = $1",
            new NpgsqlParameter { Value = session.Id });

        Assert.Single(rows);
        var row = rows[0];
        Assert.Equal("prompt", row["phase"]);
        Assert.Equal("block", row["outcome"]);
        Assert.Equal("hate", row["category"]);
        Assert.Equal(6, Convert.ToInt32(row["severity"]));
        Assert.Equal("azure_content_safety", row["filter_name"]);
    }

    [Fact]
    public async Task RecordSafetyEventAsync_ResponseRedact_Persists()
    {
        if (!_fixture.IsAvailable) return;

        using var store = new PostgresObservabilityStore(_fixture.ConnectionString, _fixture.StoreLogger);
        var builder = new TestDataBuilder(_fixture);
        var session = await builder.CreateSessionAsync(status: "active");

        await store.RecordSafetyEventAsync(
            session.Id,
            phase: "response",
            outcome: "redact",
            category: "pii",
            severity: 3,
            filterName: "pii_filter");

        var rows = await _fixture.QueryRowsAsync(
            "SELECT phase, outcome FROM safety_events WHERE session_id = $1",
            new NpgsqlParameter { Value = session.Id });
        Assert.Single(rows);
        Assert.Equal("response", rows[0]["phase"]);
        Assert.Equal("redact", rows[0]["outcome"]);
    }

    [Fact]
    public async Task RecordSafetyEventAsync_PassOutcome_Persists()
    {
        if (!_fixture.IsAvailable) return;

        using var store = new PostgresObservabilityStore(_fixture.ConnectionString, _fixture.StoreLogger);
        var builder = new TestDataBuilder(_fixture);
        var session = await builder.CreateSessionAsync(status: "active");

        await store.RecordSafetyEventAsync(
            session.Id,
            phase: "prompt",
            outcome: "pass",
            category: null,
            severity: null,
            filterName: "azure_content_safety");

        var rows = await _fixture.QueryRowsAsync(
            "SELECT outcome FROM safety_events WHERE session_id = $1",
            new NpgsqlParameter { Value = session.Id });
        Assert.Single(rows);
        Assert.Equal("pass", rows[0]["outcome"]);
    }

    [Fact]
    public async Task RecordSafetyEventAsync_NullOptionalFields_Allowed()
    {
        if (!_fixture.IsAvailable) return;

        using var store = new PostgresObservabilityStore(_fixture.ConnectionString, _fixture.StoreLogger);
        var builder = new TestDataBuilder(_fixture);
        var session = await builder.CreateSessionAsync(status: "active");

        await store.RecordSafetyEventAsync(
            session.Id,
            phase: "prompt",
            outcome: "pass",
            category: null,
            severity: null,
            filterName: null);

        var rows = await _fixture.QueryRowsAsync(
            "SELECT category, severity, filter_name FROM safety_events WHERE session_id = $1",
            new NpgsqlParameter { Value = session.Id });
        Assert.Single(rows);
        Assert.Null(rows[0]["category"]);
        Assert.Null(rows[0]["severity"]);
        Assert.Null(rows[0]["filter_name"]);
    }
}
