using System.Text.Json;
using Domain.AI.RAG.Models;
using Domain.Common.Config;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;

namespace Infrastructure.AI.RAG.SqlDatabase;

/// <summary>
/// Uses an LLM to match a natural language query to the best <see cref="SqlQueryTemplate"/>
/// and extract parameter values. Returns null if confidence is below threshold.
/// </summary>
internal sealed class SqlQueryTemplateMatcher(IChatClient chatClient, IOptionsMonitor<AppConfig> configMonitor)
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    /// <summary>
    /// Result of a successful template match: the matched template and extracted parameter values.
    /// </summary>
    public readonly record struct TemplateMatch(SqlQueryTemplate Template, IReadOnlyDictionary<string, string> Parameters);

    /// <summary>
    /// Matches a natural language <paramref name="query"/> against the provided <paramref name="templates"/>
    /// using an LLM, then extracts parameter values. Returns null when no template exceeds the
    /// confidence threshold configured in <c>AppConfig.AI.Rag.SqlDatabase.TemplateMatchConfidenceThreshold</c>.
    /// </summary>
    public async Task<TemplateMatch?> MatchAsync(
        string query,
        IReadOnlyList<SqlQueryTemplate> templates,
        CancellationToken cancellationToken)
    {
        if (templates.Count == 0)
            return null;

        var threshold = configMonitor.CurrentValue.AI.Rag.SqlDatabase.TemplateMatchConfidenceThreshold;

        var templateDescriptions = templates
            .Select(t => $"- {t.Name}: {t.Description} (params: {string.Join(", ", t.Parameters)})")
            .Aggregate((a, b) => $"{a}\n{b}");

        var systemPrompt =
            "You are a SQL template matcher. Given a natural language query and available templates, " +
            "select the best matching template and extract parameter values.\n\n" +
            "Available templates:\n" + templateDescriptions + "\n\n" +
            """Respond with JSON only: {"templateName":"...","confidence":0.0-1.0,"parameters":{"param":"value"}}""" + "\n" +
            """If no template matches, return {"templateName":"none","confidence":0.0,"parameters":{}}""";

        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, systemPrompt),
            new(ChatRole.User, query)
        };

        var response = await chatClient.GetResponseAsync(messages, cancellationToken: cancellationToken);
        var responseText = response.Text ?? "";

        MatchResponse? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<MatchResponse>(responseText, JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }

        if (parsed is null || parsed.Confidence < threshold || parsed.TemplateName == "none")
            return null;

        var matchedTemplate = templates.FirstOrDefault(t =>
            t.Name.Equals(parsed.TemplateName, StringComparison.OrdinalIgnoreCase));

        if (matchedTemplate is null)
            return null;

        return new TemplateMatch(matchedTemplate, parsed.Parameters ?? new Dictionary<string, string>());
    }

    private sealed record MatchResponse(string TemplateName, double Confidence, Dictionary<string, string>? Parameters);
}
