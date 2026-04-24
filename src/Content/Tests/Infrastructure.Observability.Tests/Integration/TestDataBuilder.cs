using Infrastructure.Observability.Persistence;
using Npgsql;

namespace Infrastructure.Observability.Tests.Integration;

public sealed class TestDataBuilder
{
    private readonly PostgresFixture _fixture;
    private readonly PostgresObservabilityStore _store;

    public TestDataBuilder(PostgresFixture fixture)
    {
        _fixture = fixture;
        _store = new PostgresObservabilityStore(fixture.ConnectionString, fixture.StoreLogger);
    }

    public async Task<SeededSession> CreateSessionAsync(
        string? conversationId = null,
        string agentName = "TestAgent",
        string model = "gpt-4o",
        string status = "completed",
        int turnCount = 3,
        int toolCallCount = 2,
        int subagentCount = 0,
        int inputTokens = 1000,
        int outputTokens = 500,
        int cacheRead = 200,
        int cacheWrite = 100,
        decimal costUsd = 0.15m,
        decimal cacheHitRate = 0.25m)
    {
        conversationId ??= _fixture.NewConversationId();
        var sessionId = await _store.StartSessionAsync(conversationId, agentName, model);

        await _store.UpdateSessionMetricsAsync(
            sessionId, turnCount, toolCallCount, subagentCount,
            inputTokens, outputTokens, cacheRead, cacheWrite,
            costUsd, cacheHitRate, model);

        if (status is "completed" or "error")
        {
            await _store.EndSessionAsync(
                sessionId, status,
                status == "error" ? "Test error" : null);
        }

        return new SeededSession(sessionId, conversationId, agentName, model);
    }

    public async Task<Guid> AddMessageAsync(
        Guid sessionId,
        int turnIndex = 0,
        string role = "user",
        string? source = "user_message",
        string? contentPreview = "Test message",
        string? model = "gpt-4o",
        int inputTokens = 100,
        int outputTokens = 50,
        int cacheRead = 20,
        int cacheWrite = 10,
        decimal costUsd = 0.01m,
        decimal cacheHitPct = 0.2m,
        string[]? toolNames = null)
    {
        return await _store.RecordMessageAsync(
            sessionId, turnIndex, role, source, contentPreview, model,
            inputTokens, outputTokens, cacheRead, cacheWrite,
            costUsd, cacheHitPct, toolNames);
    }

    public async Task AddToolAsync(
        Guid sessionId,
        string toolName = "get_weather",
        string toolSource = "keyed_di",
        int durationMs = 42,
        string status = "success",
        string? errorType = null,
        int? resultSize = 256,
        Guid? messageId = null)
    {
        await _store.RecordToolExecutionAsync(
            sessionId, messageId, toolName, toolSource,
            durationMs, status, errorType, resultSize);
    }

    public async Task AddSafetyAsync(
        Guid sessionId,
        string phase = "prompt",
        string outcome = "pass",
        string? category = null,
        int? severity = null,
        string? filterName = null)
    {
        await _store.RecordSafetyEventAsync(
            sessionId, phase, outcome, category, severity, filterName);
    }

    public async Task AddAuditAsync(
        string operation = "test_op",
        string source = "harness",
        Guid? sessionId = null,
        Dictionary<string, object>? metadata = null)
    {
        metadata ??= new Dictionary<string, object> { ["run_tag"] = _fixture.RunTag };
        if (!metadata.ContainsKey("run_tag"))
            metadata["run_tag"] = _fixture.RunTag;

        await _store.RecordAuditAsync(operation, source, sessionId, metadata);
    }

    public async Task BackdateSessionAsync(Guid sessionId, DateTime startedAt)
    {
        await _fixture.ExecuteAsync(
            "UPDATE sessions SET started_at = $1 WHERE id = $2",
            new NpgsqlParameter { Value = startedAt },
            new NpgsqlParameter { Value = sessionId });
    }

    public async Task RefreshDailyCostSummaryAsync()
    {
        await _fixture.ExecuteAsync("REFRESH MATERIALIZED VIEW daily_cost_summary");
    }

    public void Dispose() => _store.Dispose();
}

public record SeededSession(Guid Id, string ConversationId, string AgentName, string Model);
