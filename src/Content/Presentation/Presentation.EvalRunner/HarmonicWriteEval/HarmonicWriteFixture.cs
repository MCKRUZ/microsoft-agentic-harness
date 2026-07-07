using System.Text.Json;
using System.Text.Json.Serialization;

namespace Presentation.EvalRunner.HarmonicWriteEval;

/// <summary>
/// A single fact in the harmonic write-eval fixture: what to remember, and the "gold" topic it truly
/// belongs to. The gold topic is the ground truth against which consolidation clustering is scored — it is
/// never shown to the abstractor or consolidator.
/// </summary>
public sealed record HarmonicWriteFact
{
    /// <summary>The memory key the fact is remembered under (mirrors <c>RememberAsync(key, ...)</c>).</summary>
    public required string Key { get; init; }

    /// <summary>The fact content to remember, phrased naturally (facts in the same topic are worded differently).</summary>
    public required string Content { get; init; }

    /// <summary>
    /// The ground-truth topic this fact belongs to. Facts sharing a gold topic <em>should</em> consolidate
    /// under one abstraction in Full mode; the eval measures how well they do.
    /// </summary>
    public required string GoldTopic { get; init; }
}

/// <summary>
/// The harmonic write-eval fixture: a set of facts that cluster into a known number of gold topics. Loaded
/// from a JSON file so the fixture is versioned alongside the other <c>eval-datasets/</c> suites.
/// </summary>
public sealed record HarmonicWriteFixture
{
    /// <summary>Human-readable description of what the fixture exercises.</summary>
    public string? Description { get; init; }

    /// <summary>The facts to remember, across all gold topics.</summary>
    public IReadOnlyList<HarmonicWriteFact> Facts { get; init; } = [];

    /// <summary>The number of distinct gold topics represented — the ideal distinct-abstraction count for Full mode.</summary>
    [JsonIgnore]
    public int GoldTopicCount => Facts.Select(f => f.GoldTopic).Distinct(StringComparer.OrdinalIgnoreCase).Count();

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    /// <summary>
    /// Loads and validates a fixture from a JSON file.
    /// </summary>
    /// <param name="path">Path to the fixture JSON.</param>
    /// <exception cref="FileNotFoundException">The file does not exist.</exception>
    /// <exception cref="InvalidOperationException">The file is empty, unparseable, or has no facts.</exception>
    public static async Task<HarmonicWriteFixture> LoadAsync(string path, CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException($"Harmonic write-eval fixture not found: {path}", path);

        await using var stream = File.OpenRead(path);
        var fixture = await JsonSerializer.DeserializeAsync<HarmonicWriteFixture>(stream, SerializerOptions, cancellationToken)
            ?? throw new InvalidOperationException($"Fixture '{path}' deserialized to null.");

        if (fixture.Facts.Count == 0)
            throw new InvalidOperationException($"Fixture '{path}' contains no facts.");

        var blank = fixture.Facts.FirstOrDefault(f =>
            string.IsNullOrWhiteSpace(f.Key) || string.IsNullOrWhiteSpace(f.Content) || string.IsNullOrWhiteSpace(f.GoldTopic));
        if (blank is not null)
            throw new InvalidOperationException($"Fixture '{path}' has a fact with a blank key, content, or goldTopic.");

        var dupeKey = fixture.Facts
            .GroupBy(f => f.Key, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(g => g.Count() > 1);
        if (dupeKey is not null)
            throw new InvalidOperationException(
                $"Fixture '{path}' reuses key '{dupeKey.Key}'. Each fact needs a distinct key (a reused key would overwrite in-place).");

        return fixture;
    }
}
