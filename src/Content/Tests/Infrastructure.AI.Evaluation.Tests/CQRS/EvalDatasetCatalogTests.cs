using Application.Core.CQRS.Evaluation.RunEvalSuite;
using Domain.Common.Config;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Infrastructure.AI.Evaluation.Tests.CQRS;

/// <summary>
/// Tests for <see cref="EvalDatasetCatalog"/> — the mapping from a dataset <em>name</em> to the file it
/// stands for.
/// </summary>
/// <remarks>
/// <para>
/// This class is what makes "no filesystem paths on the wire" structural rather than aspirational. The
/// cases below are the ways a caller would try to make a name behave like a path, plus the disclosure
/// rules: an unconfined host publishes nothing, and a name that does not resolve is indistinguishable
/// from one that is malformed.
/// </para>
/// <para>
/// Exercised against real directories rather than an abstracted filesystem, because the property under
/// test is that resolution happens by <em>enumerating what is actually there</em>. A fake filesystem
/// would answer whatever the test told it to and would pass just as happily against an implementation
/// that concatenated the name onto a root.
/// </para>
/// </remarks>
public sealed class EvalDatasetCatalogTests : IDisposable
{
    private readonly string _base = Directory.CreateTempSubdirectory("eval-catalog-").FullName;
    private readonly string _root;
    private readonly string _second;

    /// <summary>
    /// Both roots are nested inside one base directory, so a traversal test has somewhere real to
    /// traverse <em>to</em>. A test whose traversal target does not exist proves nothing: an
    /// implementation that concatenated the name onto the root would also find nothing there, and
    /// would pass.
    /// </summary>
    public EvalDatasetCatalogTests()
    {
        _root = Directory.CreateDirectory(Path.Combine(_base, "root")).FullName;
        _second = Directory.CreateDirectory(Path.Combine(_base, "second")).FullName;
    }

    public void Dispose() => TryDelete(_base);

    [Fact]
    public void Lists_the_files_sitting_in_a_configured_root()
    {
        WriteDataset(_root, "alpha.yaml");
        WriteDataset(_root, "beta.yaml");

        Catalog(_root).ListNames().Should().BeEquivalentTo(["alpha", "beta"]);
    }

    [Fact]
    public void A_name_resolves_to_the_file_it_stands_for()
    {
        var path = WriteDataset(_root, "alpha.yaml");

        Catalog(_root).Resolve(["alpha"]).Paths.Should().ContainSingle().Which.Should().Be(path);
    }

    [Fact]
    public void Names_are_reported_in_a_stable_order()
    {
        // A listing whose order depends on the filesystem makes a caller's diff of two responses noise.
        WriteDataset(_root, "zeta.yaml");
        WriteDataset(_root, "alpha.yaml");

        Catalog(_root).ListNames().Should().ContainInOrder("alpha", "zeta");
    }

    [Fact]
    public void An_unconfined_host_publishes_nothing()
    {
        // Listing is a disclosure. With no roots configured there is no bounded thing to disclose, and
        // enumerating whatever directory the process happens to run from would be a filesystem read.
        Catalog().ListNames().Should().BeEmpty();
    }

    [Fact]
    public void An_unconfined_host_resolves_nothing()
    {
        // The stronger half of the same rule: an empty listing would be cosmetic if a caller could
        // still name a dataset and have it resolve.
        WriteDataset(_root, "alpha.yaml");

        Catalog().Resolve(["alpha"]).IsComplete.Should().BeFalse();
    }

    [Theory]
    [InlineData("../outside")]
    [InlineData("..\\outside")]
    [InlineData("../second/beta")]
    [InlineData("..\\second\\beta")]
    public void A_name_cannot_traverse_out_of_its_root_to_a_file_that_really_is_there(string name)
    {
        // The traversal targets exist. That is the whole point of the case: an implementation that
        // built a path by concatenating the name onto the root would reach both of these, so a test
        // aimed at somewhere empty would pass against exactly the implementation it is meant to
        // forbid.
        WriteDataset(_base, "outside.yaml");
        WriteDataset(_second, "beta.yaml");
        WriteDataset(_root, "alpha.yaml");

        Catalog(_root).Resolve([name]).IsComplete.Should().BeFalse();
    }

    [Theory]
    [InlineData("sub/alpha")]
    [InlineData("sub\\alpha")]
    [InlineData("C:alpha")]
    [InlineData("..")]
    [InlineData(".")]
    public void A_name_that_looks_like_a_path_resolves_to_nothing(string name)
    {
        WriteDataset(_root, "alpha.yaml");
        WriteDataset(Directory.CreateDirectory(Path.Combine(_root, "sub")).FullName, "alpha.yaml");

        Catalog(_root).Resolve([name]).IsComplete.Should().BeFalse();
    }

    [Fact]
    public void Resolved_paths_come_back_in_the_order_the_names_were_given()
    {
        // The paths are handed straight to the eval command, whose report is ordered by dataset. A
        // resolution that reordered them would silently mislabel which suite produced which result.
        var alpha = WriteDataset(_root, "alpha.yaml");
        var zeta = WriteDataset(_root, "zeta.yaml");

        Catalog(_root).Resolve(["zeta", "alpha"]).Paths.Should().ContainInOrder(zeta, alpha);
    }

    [Fact]
    public void One_unknown_name_makes_the_whole_set_unresolved()
    {
        // All of them or none: a suite quietly evaluated without one of its datasets reports a pass
        // rate for something that never ran. The caller is told which name so it can act on it.
        WriteDataset(_root, "alpha.yaml");

        var resolution = Catalog(_root).Resolve(["alpha", "nonexistent"]);

        resolution.IsComplete.Should().BeFalse();
        resolution.MissingName.Should().Be("nonexistent");
        resolution.Paths.Should().BeEmpty("a partial answer invites a caller to run part of a suite");
    }

    [Fact]
    public void A_file_in_a_subdirectory_is_not_published()
    {
        // Top level only, deliberately: a name that carried structure would be a path with extra steps,
        // needing to be split, rejoined and validated — the concatenation problem again.
        var nested = Directory.CreateDirectory(Path.Combine(_root, "sub"));
        WriteDataset(nested.FullName, "buried.yaml");

        var catalog = Catalog(_root);

        catalog.ListNames().Should().BeEmpty();
        catalog.Resolve(["buried"]).IsComplete.Should().BeFalse();
    }

    [Fact]
    public void A_name_nobody_published_resolves_to_nothing()
    {
        WriteDataset(_root, "alpha.yaml");

        Catalog(_root).Resolve(["nonexistent"]).IsComplete.Should().BeFalse();
    }

    [Fact]
    public void An_empty_name_resolves_to_nothing()
    {
        WriteDataset(_root, "alpha.yaml");

        Catalog(_root).Resolve(["   "]).IsComplete.Should().BeFalse();
    }

    [Fact]
    public void Datasets_across_several_roots_are_all_published()
    {
        WriteDataset(_root, "alpha.yaml");
        WriteDataset(_second, "beta.yaml");

        Catalog(_root, _second).ListNames().Should().BeEquivalentTo(["alpha", "beta"]);
    }

    [Fact]
    public void A_name_defined_in_two_roots_resolves_to_the_first()
    {
        // First-root-wins is arbitrary but has to be deterministic: a name that resolved differently
        // from one request to the next would run a different suite each time it was named.
        var first = WriteDataset(_root, "alpha.yaml");
        WriteDataset(_second, "alpha.yaml");

        Catalog(_root, _second).Resolve(["alpha"]).Paths.Should().ContainSingle().Which.Should().Be(first);
    }

    [Fact]
    public void A_name_defined_in_two_roots_is_published_once()
    {
        WriteDataset(_root, "alpha.yaml");
        WriteDataset(_second, "alpha.yaml");

        Catalog(_root, _second).ListNames().Should().ContainSingle().Which.Should().Be("alpha");
    }

    [Fact]
    public void A_root_that_does_not_exist_contributes_nothing_rather_than_failing()
    {
        // An operator's typo must not take the whole catalog down with it — the datasets in the roots
        // that do exist are still perfectly runnable.
        WriteDataset(_root, "alpha.yaml");

        Catalog(Path.Combine(_root, "no-such-directory"), _root)
            .ListNames().Should().BeEquivalentTo(["alpha"]);
    }

    [Fact]
    public void A_dataset_added_after_startup_is_published_without_a_restart()
    {
        // Not cached: a stale catalog would admit a run against a dataset that has since been removed,
        // which then fails at load time having already been queued.
        var catalog = Catalog(_root);
        catalog.ListNames().Should().BeEmpty();

        WriteDataset(_root, "alpha.yaml");

        catalog.ListNames().Should().BeEquivalentTo(["alpha"]);
    }

    [Fact]
    public void A_dataset_the_guard_refuses_is_never_published()
    {
        // The catalog must not be able to publish a name whose file the handler would then refuse to
        // load — the two would disagree, and the caller would see an accepted run fail on dispatch.
        WriteDataset(_root, "alpha.yaml");

        var guard = new Mock<IEvalDatasetPathGuard>();
        guard.Setup(g => g.Resolve(It.IsAny<string>()))
            .Returns(EvalDatasetPathDecision.Refuse("Dataset is not available."));

        var catalog = new EvalDatasetCatalog(
            Monitor(_root), guard.Object, NullLogger<EvalDatasetCatalog>.Instance);

        catalog.ListNames().Should().BeEmpty();
        catalog.Resolve(["alpha"]).IsComplete.Should().BeFalse();
    }

    private EvalDatasetCatalog Catalog(params string[] roots) => new(
        Monitor(roots),
        new EvalDatasetPathGuard(Monitor(roots), new EvalConfinementLatch(StartedConfined: roots.Length > 0)),
        NullLogger<EvalDatasetCatalog>.Instance);

    private static IOptionsMonitor<AppConfig> Monitor(params string[] roots)
    {
        var config = new AppConfig();
        config.AI.Evaluation.DatasetRoots = [.. roots];

        var monitor = new Mock<IOptionsMonitor<AppConfig>>();
        monitor.SetupGet(m => m.CurrentValue).Returns(config);
        return monitor.Object;
    }

    private static string WriteDataset(string directory, string fileName)
    {
        var path = Path.Combine(directory, fileName);
        File.WriteAllText(path, "cases: []");
        return path;
    }

    private static void TryDelete(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
        catch (IOException)
        {
            // A leftover temp directory is not worth failing a test run over.
        }
    }
}
