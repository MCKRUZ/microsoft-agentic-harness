using System.Text.Json;
using Application.AI.Common.Interfaces.Compliance;
using Domain.Common.Compliance;

namespace Infrastructure.Observability.Persistence;

/// <summary>Personal-data export (#902): what the observability database recorded of the person's conversations.</summary>
/// <remarks>
/// <para>
/// <strong>The person's part of each row, by explicit column, never the whole row.</strong> This database also holds the
/// operator's own material, which today only the observer role may read: the registration bodies (system prompt, skill
/// instructions, tool and MCP descriptions) in <c>context_snapshot_loaded_bodies</c>, the per-turn inventory in
/// <c>context_snapshots.loaded_json</c>, system-context and hook-injected messages, raw error text, cost figures, filter
/// names, and audit metadata that can name other people (an approver). A copy made of whole rows would hand all of that to
/// every signed-in person, and every column a later migration adds would follow without anyone deciding it should. So the
/// copy carries what the person said and received, the tools run for them and what those returned (redacted when
/// stored), the shape and outcome of their sessions, and what was audited about them and when. A column a migration
/// adds reaches the copy only when it is added here.
/// </para>
/// <para>
/// <strong>Erasure still deletes everything.</strong> The context snapshots and their loaded bodies are erased with the
/// person's conversations although they are not in the copy: they describe the operator's configuration as it was for
/// that turn, not the person.
/// </para>
/// </remarks>
public sealed partial class PostgresObservabilityStore : IPersonalDataExporter
{
    /// <summary>Messages that are the operator's context rather than the conversation: never in a person's copy.</summary>
    private const string ConversationMessagesOnly =
        "t.role <> 'system' AND COALESCE(t.source, '') NOT IN ('system_context', 'hook_injection')";

    /// <summary>
    /// One read per table that records the person's conversations, through the same predicates erasure deletes by. The
    /// three tables erasure reaches only by cascade from <c>sessions</c> are read through their session here.
    /// </summary>
    private static readonly (string Table, string Sql)[] ExportQueries =
    [
        ("sessions",
            "SELECT json_build_object('id', t.id, 'conversationId', t.conversation_id, 'agentName', t.agent_name, "
            + "'model', t.model, 'startedAt', t.started_at, 'endedAt', t.ended_at, 'status', t.status, "
            + "'turnCount', t.turn_count, 'toolCallCount', t.tool_call_count, "
            + "'inputTokens', t.total_input_tokens, 'outputTokens', t.total_output_tokens)::text "
            + $"FROM sessions t WHERE {OfSubjectConversations} ORDER BY t.started_at, t.id"),
        ("session_messages",
            "SELECT json_build_object('sessionId', t.session_id, 'turnIndex', t.turn_index, 'role', t.role, "
            + "'source', t.source, 'content', COALESCE(t.content_full, t.content_preview), 'model', t.model, "
            + "'toolNames', t.tool_names, 'createdAt', t.created_at)::text "
            + $"FROM session_messages t WHERE {OfSubjectSessions} AND {ConversationMessagesOnly} "
            + "ORDER BY t.session_id, t.turn_index, t.created_at"),
        ("tool_executions",
            "SELECT json_build_object('sessionId', t.session_id, 'toolName', t.tool_name, 'status', t.status, "
            + "'durationMs', t.duration_ms, 'arguments', t.args, 'output', t.stdout, 'createdAt', t.created_at)::text "
            + $"FROM tool_executions t WHERE {OfSubjectSessions} ORDER BY t.session_id, t.created_at"),
        ("safety_events",
            "SELECT json_build_object('sessionId', t.session_id, 'phase', t.phase, 'outcome', t.outcome, "
            + "'category', t.category, 'severity', t.severity, 'createdAt', t.created_at)::text "
            + $"FROM safety_events t WHERE {OfSubjectSessions} ORDER BY t.session_id, t.created_at"),
        ("audit_log",
            "SELECT json_build_object('sessionId', t.session_id, 'operation', t.operation, 'source', t.source, "
            + "'createdAt', t.created_at)::text "
            + $"FROM audit_log t WHERE {OfSubjectSessions} ORDER BY t.created_at, t.id"),
    ];

    /// <inheritdoc />
    public string ExportStoreName => ErasureStoreName;

    /// <summary>
    /// The person's part of every row recorded for <see cref="ExportSubject.AllConversationIds"/>, which include the
    /// sessions of their planned tasks' model steps, read in one snapshot so the tables agree. Like erasure and unlike the
    /// write path, a failure is not swallowed; the coordinator reports it as a failed line.
    /// </summary>
    /// <param name="subject">The person the copy is for.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>One record per row: the table it came from and the columns that are the person's.</returns>
    public async Task<IReadOnlyList<JsonElement>> ExportSubjectAsync(ExportSubject subject, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(subject);
        if (subject.AllConversationIds.Count == 0)
            return [];

        var ids = subject.AllConversationIds.ToArray();
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(
            System.Data.IsolationLevel.RepeatableRead, cancellationToken);

        var records = new List<JsonElement>();
        foreach (var (table, sql) in ExportQueries)
        {
            await using var cmd = connection.CreateCommand();
            cmd.CommandText = sql;
            cmd.Transaction = transaction;
            cmd.Parameters.AddWithValue(ids);
            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                records.Add(ToRecord(table, reader.GetString(0)));
        }

        await transaction.CommitAsync(cancellationToken);
        return records;
    }

    private static JsonElement ToRecord(string table, string rowJson)
    {
        using var row = JsonDocument.Parse(rowJson);
        return JsonSerializer.SerializeToElement(new { table, row = row.RootElement });
    }
}
