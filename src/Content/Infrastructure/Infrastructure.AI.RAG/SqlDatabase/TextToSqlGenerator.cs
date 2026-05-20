using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;

namespace Infrastructure.AI.RAG.SqlDatabase;

/// <summary>
/// LLM fallback: generates a SELECT-only SQL query from natural language and a database schema.
/// Rejects any generated SQL containing mutation keywords as a defense-in-depth measure.
/// </summary>
internal sealed partial class TextToSqlGenerator(IChatClient chatClient)
{
    [GeneratedRegex(@"\b(INSERT|UPDATE|DELETE|DROP|ALTER|TRUNCATE|CREATE|EXEC|EXECUTE|GRANT|REVOKE)\b",
        RegexOptions.IgnoreCase)]
    private static partial Regex MutationPattern();

    /// <summary>
    /// Generates a SELECT SQL query from a natural language <paramref name="query"/> and
    /// <paramref name="databaseSchema"/>. Returns <see langword="null"/> if the LLM produces
    /// a mutation statement or an empty response.
    /// </summary>
    public async Task<string?> GenerateAsync(
        string naturalLanguageQuery, string databaseSchema, CancellationToken cancellationToken)
    {
        var systemPrompt = $"""
            You are a SQL query generator. Generate a single SELECT query based on the user's question.

            Rules:
            - ONLY generate SELECT statements. Never INSERT, UPDATE, DELETE, DROP, ALTER, TRUNCATE, or any DDL/DML.
            - Use the schema below to determine valid tables and columns.
            - Include LIMIT 100 unless the user specifies a different limit.
            - Do not use subqueries without LIMIT.
            - Respond with ONLY the SQL query, no explanation.

            Database schema:
            {databaseSchema}
            """;

        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, systemPrompt),
            new(ChatRole.User, naturalLanguageQuery)
        };

        var response = await chatClient.GetResponseAsync(messages, cancellationToken: cancellationToken);
        var sql = response.Text?.Trim();

        if (string.IsNullOrEmpty(sql))
            return null;

        if (MutationPattern().IsMatch(sql))
            return null;

        return sql;
    }
}
