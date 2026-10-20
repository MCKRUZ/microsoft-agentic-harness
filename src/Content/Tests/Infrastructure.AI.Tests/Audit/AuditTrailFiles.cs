using Infrastructure.AI.Audit;

namespace Infrastructure.AI.Tests.Audit;

/// <summary>
/// Reads an audit trail that is stored one file per day (#416) the way a test used to read its single file.
/// </summary>
internal static class AuditTrailFiles
{
    /// <summary>Every line of the trail that used to live in <paramref name="legacyFile"/>, across all its files, in order.</summary>
    public static async Task<string[]> ReadAllLinesAsync(string legacyFile)
    {
        var lines = new List<string>();
        foreach (var file in Layout(legacyFile).Ordered())
            lines.AddRange(await File.ReadAllLinesAsync(file));
        return [.. lines];
    }

    /// <inheritdoc cref="ReadAllLinesAsync"/>
    public static string[] ReadAllLines(string legacyFile) =>
        [.. Layout(legacyFile).Ordered().SelectMany(File.ReadAllLines)];

    /// <summary>The one day-file a short test wrote the trail to.</summary>
    public static string OnlyDayFile(string legacyFile) =>
        Layout(legacyFile).Ordered().Single(f => DatedAuditSegments.DayOf(f) is not null);

    private static DatedAuditSegments Layout(string legacyFile) =>
        DatedAuditSegments.BesideLegacyFile(legacyFile, TimeProvider.System);
}
