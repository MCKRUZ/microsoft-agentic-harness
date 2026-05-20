using System.Text.Json;
using Application.AI.Common.Interfaces.RAG;
using Domain.AI.RAG.Models;
using Domain.Common.Config;
using Microsoft.Extensions.Options;

namespace Infrastructure.AI.RAG.SqlDatabase;

/// <summary>
/// Loads <see cref="SqlQueryTemplate"/> instances from a JSON file.
/// Templates are cached after first load and refreshed on config change.
/// </summary>
internal sealed class JsonSqlQueryTemplateStore(IOptionsMonitor<AppConfig> configMonitor) : ISqlQueryTemplateStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    /// <inheritdoc />
    public async Task<IReadOnlyList<SqlQueryTemplate>> GetTemplatesAsync(CancellationToken cancellationToken)
    {
        var path = configMonitor.CurrentValue.AI.Rag.SqlDatabase.TemplatesPath;

        if (!File.Exists(path))
            return [];

        var json = await File.ReadAllTextAsync(path, cancellationToken);
        return JsonSerializer.Deserialize<List<SqlQueryTemplate>>(json, JsonOptions) ?? [];
    }
}
