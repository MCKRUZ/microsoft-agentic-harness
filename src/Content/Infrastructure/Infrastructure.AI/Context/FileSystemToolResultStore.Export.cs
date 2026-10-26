using System.Text.Json;
using Application.AI.Common.Interfaces.Compliance;
using Domain.Common.Compliance;

namespace Infrastructure.AI.Context;

/// <summary>Personal-data export (#902): the tool output spilled for the person's conversations and planned tasks.</summary>
public sealed partial class FileSystemToolResultStore : IPersonalDataExporter
{
    private static readonly JsonSerializerOptions ExportJson = new(JsonSerializerDefaults.Web);

    /// <inheritdoc />
    public string ExportStoreName => ErasureStoreName;

    /// <summary>
    /// Every spill file under the person's conversations, their planned tasks' model steps and their planned-task runs:
    /// the directories erasure deletes, found the same way. The text is returned as stored, which is after secret
    /// redaction; a spill is written once, so nothing else about it is kept.
    /// </summary>
    /// <param name="subject">The person the copy is for.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>One record per spill file: the scope it was written under, its result id and its text.</returns>
    public async Task<IReadOnlyList<JsonElement>> ExportSubjectAsync(ExportSubject subject, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(subject);

        var records = new List<JsonElement>();
        foreach (var (scopeId, _, directory) in SubjectSpillDirectories(subject.AllConversationIds, subject.PlanRuns))
        {
            foreach (var file in Directory.EnumerateFiles(directory).Order(StringComparer.Ordinal))
            {
                var spill = new ExportedSpill(
                    scopeId,
                    Path.GetFileNameWithoutExtension(file),
                    await File.ReadAllTextAsync(file, cancellationToken));
                records.Add(JsonSerializer.SerializeToElement(spill, ExportJson));
            }
        }

        return records;
    }

    /// <summary>One spilled tool result as it appears in a person's copy.</summary>
    /// <param name="Scope">The conversation, model step or planned-task run it was written under.</param>
    /// <param name="ResultId">The id the agent was given to page through it.</param>
    /// <param name="Content">The text as stored, after secret redaction.</param>
    private sealed record ExportedSpill(string Scope, string ResultId, string Content);
}
