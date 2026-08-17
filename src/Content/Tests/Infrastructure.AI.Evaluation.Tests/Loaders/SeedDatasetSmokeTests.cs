using FluentAssertions;
using Infrastructure.AI.Evaluation.Loaders;
using Xunit;

namespace Infrastructure.AI.Evaluation.Tests.Loaders;

/// <summary>
/// Smoke tests that load each repo-root <c>eval-datasets/seed/*.yaml</c> seed file
/// to catch authoring errors (typos, schema drift, missing required fields) before
/// they hit CI.
/// </summary>
public sealed class SeedDatasetSmokeTests
{
    private readonly YamlEvalDatasetLoader _sut = new();

    /// <summary>Minimum number of seed datasets expected — guards against silent zero-runs.</summary>
    private const int ExpectedSeedFileCount = 8;

    public static IEnumerable<object[]> SeedFiles()
    {
        var seedDir = LocateSeedDir();
        if (seedDir is null) yield break;

        foreach (var f in Directory.EnumerateFiles(seedDir, "*.yaml"))
        {
            yield return new object[] { f };
        }
    }

    [Fact]
    public void Seed_dir_is_locatable_and_has_expected_file_count()
    {
        // Hard fail when the seed directory can't be located OR when the file count
        // drifts — without this, the Theory below silently runs zero invocations on
        // hosts where the path resolution breaks (shadow-copy test runners, etc.),
        // hiding schema drift in CI.
        var dir = LocateSeedDir();
        dir.Should().NotBeNull("seed dir 'eval-datasets/seed' must be reachable from test bin");

        var count = Directory.EnumerateFiles(dir!, "*.yaml").Count();
        count.Should().BeGreaterThanOrEqualTo(ExpectedSeedFileCount,
            $"plan locks at least {ExpectedSeedFileCount} seed datasets; deletions need an explicit plan update");
    }

    [Theory]
    [MemberData(nameof(SeedFiles))]
    public async Task Each_seed_dataset_loads_successfully(string path)
    {
        var ds = await _sut.LoadAsync(path, CancellationToken.None);

        ds.Cases.Should().NotBeEmpty($"{Path.GetFileName(path)} should declare at least one case");
        foreach (var c in ds.Cases)
        {
            c.Id.Should().NotBeNullOrWhiteSpace();
            c.Input.Should().NotBeNullOrWhiteSpace();
            c.MetricSpecs.Should().NotBeEmpty($"case '{c.Id}' must declare ≥1 metric");
        }
    }

    [Fact]
    public async Task Governance_sanitization_gov_san_09_carries_all_three_new_verdict_contract_parameters()
    {
        // Schema-drift guard: MetricSpec.Parameters is a free-form string dictionary, so a
        // typo'd key (e.g. "verdict_contact") parses without error and silently falls back
        // to legacy behaviour. This is the only thing that would catch that for the
        // lucky-correct-negative fixture proving #335/#334/#336 work together.
        var seedDir = LocateSeedDir();
        seedDir.Should().NotBeNull();
        var ds = await _sut.LoadAsync(Path.Combine(seedDir!, "governance-sanitization.yaml"), CancellationToken.None);

        var gov09 = ds.Cases.Should().ContainSingle(c => c.Id == "gov-san-09-lucky-refusal-without-governance")
            .Which;

        var spec = gov09.MetricSpecs.Should().ContainSingle(m => m.MetricKey == "llm_judge").Which;
        spec.Parameters.Should().Contain("verdict_contract", "strict");
        spec.Parameters.Should().Contain("include_expected_output", "false");
        spec.Parameters.Should().Contain("trajectory", "tools,governance");
    }

    private static string? LocateSeedDir()
    {
        // Walk up from the test bin directory looking for "eval-datasets/seed".
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "eval-datasets", "seed");
            if (Directory.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        return null;
    }
}
