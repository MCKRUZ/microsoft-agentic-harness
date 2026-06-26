using Application.AI.Common.Evaluation.Interfaces;
using Domain.AI.Evaluation;
using YamlDotNet.Core;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Infrastructure.AI.Evaluation.Loaders;

/// <summary>
/// Loads evaluation datasets from YAML files using <see cref="YamlDotNet"/>.
/// </summary>
/// <remarks>
/// <para>
/// Uses snake_case naming convention (matches the on-disk schema documented in
/// <c>planning/phase5-eval-framework/PLAN.md</c>) and <em>does not</em> allow arbitrary type
/// construction, mitigating deserialization attacks via crafted YAML tags.
/// </para>
/// <para>
/// On parse failure, raises <see cref="InvalidDataException"/> rather than letting raw
/// <c>YamlException</c> escape — gives callers a stable contract for error handling.
/// </para>
/// </remarks>
public sealed class YamlEvalDatasetLoader : IEvalDatasetLoader
{
    private static readonly IDeserializer Deserializer = new DeserializerBuilder()
        .WithNamingConvention(UnderscoredNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        // Depth cap (defense-in-depth): the dataset schema is shallow
        // (dataset -> cases -> metrics), so 64 is far above any legitimate document.
        // Bounds stack growth from maliciously deep YAML, complementing the
        // no-arbitrary-type-construction stance above. Requires YamlDotNet >= 17.
        .WithMaximumRecursion(64)
        .Build();

    /// <inheritdoc />
    public IReadOnlyList<string> Extensions { get; } = new[] { "yaml", "yml" };

    /// <inheritdoc />
    public async Task<EvalDataset> LoadAsync(string path, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        if (!File.Exists(path))
            throw new FileNotFoundException($"Dataset file not found: {path}", path);

        var yaml = await File.ReadAllTextAsync(path, cancellationToken);

        DatasetYamlShape shape;
        try
        {
            shape = Deserializer.Deserialize<DatasetYamlShape>(yaml)
                ?? throw new InvalidDataException($"Dataset file is empty: {path}");
        }
        catch (YamlException ex)
        {
            throw new InvalidDataException($"Failed to parse YAML dataset {path}: {ex.Message}", ex);
        }

        var cases = (shape.Cases ?? []).Select(MapCase).ToList();

        return new EvalDataset
        {
            Name = shape.Name ?? Path.GetFileNameWithoutExtension(path),
            Version = shape.Version ?? "1.0.0",
            Description = shape.Description,
            SourcePath = path,
            Cases = cases
        };
    }

    private static EvalCase MapCase(CaseYamlShape c)
    {
        if (string.IsNullOrWhiteSpace(c.Id))
            throw new InvalidDataException("Case is missing required 'id' field.");
        if (string.IsNullOrWhiteSpace(c.Input))
            throw new InvalidDataException($"Case '{c.Id}' is missing required 'input' field.");

        return new EvalCase
        {
            Id = c.Id,
            Input = c.Input,
            ExpectedOutput = c.ExpectedOutput,
            RetrievedContext = c.RetrievedContext,
            Tags = c.Tags ?? [],
            InvocationOverrides = c.InvocationOverrides ?? new Dictionary<string, string>(),
            MetricSpecs = (c.Metrics ?? []).Select(MapMetricSpec).ToList()
        };
    }

    private static MetricSpec MapMetricSpec(MetricSpecYamlShape m)
    {
        if (string.IsNullOrWhiteSpace(m.Key))
            throw new InvalidDataException("Metric spec is missing required 'key' field.");

        return new MetricSpec
        {
            MetricKey = m.Key,
            Threshold = m.Threshold ?? 1.0,
            Parameters = m.Parameters ?? new Dictionary<string, string>()
        };
    }

    // YAML-shaped DTOs — separated from the domain records so the wire format can evolve
    // independently of the in-memory model.
#pragma warning disable CA1812 // Instantiated via reflection by YamlDotNet
    private sealed class DatasetYamlShape
    {
        public string? Name { get; set; }
        public string? Version { get; set; }
        public string? Description { get; set; }
        public List<CaseYamlShape>? Cases { get; set; }
    }

    private sealed class CaseYamlShape
    {
        public string? Id { get; set; }
        public string? Input { get; set; }
        public string? ExpectedOutput { get; set; }
        public string? RetrievedContext { get; set; }
        public List<string>? Tags { get; set; }
        public Dictionary<string, string>? InvocationOverrides { get; set; }
        public List<MetricSpecYamlShape>? Metrics { get; set; }
    }

    private sealed class MetricSpecYamlShape
    {
        public string? Key { get; set; }
        public double? Threshold { get; set; }
        public Dictionary<string, string>? Parameters { get; set; }
    }
#pragma warning restore CA1812
}
