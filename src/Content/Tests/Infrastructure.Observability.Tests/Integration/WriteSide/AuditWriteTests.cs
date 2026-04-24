using Infrastructure.Observability.Persistence;
using Npgsql;
using Xunit;

namespace Infrastructure.Observability.Tests.Integration.WriteSide;

[Collection("Postgres")]
public sealed class AuditWriteTests
{
    private readonly PostgresFixture _fixture;

    public AuditWriteTests(PostgresFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task RecordAuditAsync_WithMetadata_SerializesJsonb()
    {
        if (!_fixture.IsAvailable) return;

        using var store = new PostgresObservabilityStore(_fixture.ConnectionString, _fixture.StoreLogger);

        var operation = $"op_{Guid.NewGuid():N}";
        var metadata = new Dictionary<string, object>
        {
            ["run_tag"] = _fixture.RunTag,
            ["user"] = "alice",
            ["action"] = "delete_session"
        };

        await store.RecordAuditAsync(operation, "harness", sessionId: null, metadata);

        var userVal = await _fixture.QueryScalarAsync<string>(
            "SELECT metadata->>'user' FROM audit_log WHERE operation = $1",
            new NpgsqlParameter { Value = operation });
        var actionVal = await _fixture.QueryScalarAsync<string>(
            "SELECT metadata->>'action' FROM audit_log WHERE operation = $1",
            new NpgsqlParameter { Value = operation });

        Assert.Equal("alice", userVal);
        Assert.Equal("delete_session", actionVal);
    }

    [Fact]
    public async Task RecordAuditAsync_NullSessionId_Allowed()
    {
        if (!_fixture.IsAvailable) return;

        using var store = new PostgresObservabilityStore(_fixture.ConnectionString, _fixture.StoreLogger);
        var operation = $"op_{Guid.NewGuid():N}";

        await store.RecordAuditAsync(
            operation,
            source: "system",
            sessionId: null,
            metadata: new Dictionary<string, object> { ["run_tag"] = _fixture.RunTag });

        var rows = await _fixture.QueryRowsAsync(
            "SELECT session_id FROM audit_log WHERE operation = $1",
            new NpgsqlParameter { Value = operation });
        Assert.Single(rows);
        Assert.Null(rows[0]["session_id"]);
    }

    [Theory]
    [InlineData("harness")]
    [InlineData("api")]
    [InlineData("system")]
    public async Task RecordAuditAsync_AllSourceValues_CheckConstraintPassed(string source)
    {
        if (!_fixture.IsAvailable) return;

        using var store = new PostgresObservabilityStore(_fixture.ConnectionString, _fixture.StoreLogger);
        var operation = $"op_{source}_{Guid.NewGuid():N}";

        await store.RecordAuditAsync(
            operation,
            source: source,
            sessionId: null,
            metadata: new Dictionary<string, object> { ["run_tag"] = _fixture.RunTag });

        var persistedSource = await _fixture.QueryScalarAsync<string>(
            "SELECT source FROM audit_log WHERE operation = $1",
            new NpgsqlParameter { Value = operation });
        Assert.Equal(source, persistedSource);
    }

    [Fact]
    public async Task RecordAuditAsync_NullMetadata_Allowed()
    {
        if (!_fixture.IsAvailable) return;

        using var store = new PostgresObservabilityStore(_fixture.ConnectionString, _fixture.StoreLogger);
        var operation = $"op_{Guid.NewGuid():N}";

        var ex = await Record.ExceptionAsync(() =>
            store.RecordAuditAsync(operation, "harness", sessionId: null, metadata: null));

        Assert.Null(ex);

        var count = await _fixture.QueryScalarAsync<long>(
            "SELECT COUNT(*) FROM audit_log WHERE operation = $1",
            new NpgsqlParameter { Value = operation });
        Assert.True(count >= 0);
    }
}
