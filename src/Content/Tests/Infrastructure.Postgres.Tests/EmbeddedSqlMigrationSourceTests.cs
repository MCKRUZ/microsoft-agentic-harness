using System.Reflection;
using Infrastructure.Observability.Persistence;
using Infrastructure.Postgres.Migrations;
using Xunit;

namespace Infrastructure.Postgres.Tests;

/// <summary>
/// Covers how a migration set is discovered and ordered — the part of the runner that needs no
/// database and therefore cannot be skipped.
/// </summary>
/// <remarks>
/// Every case here is a failure the old mechanism could not detect. Scripts were fed to Postgres by
/// a shell glob over a directory, so an unreadable set was an empty apply, a badly named file was
/// silently sorted somewhere, and two files sharing a number were ordered by whatever the glob
/// returned. All three now throw.
/// </remarks>
public sealed class EmbeddedSqlMigrationSourceTests
{
    private static Assembly TestAssembly => typeof(EmbeddedSqlMigrationSourceTests).Assembly;

    [Fact]
    public void Load_ObservabilityAssembly_ReturnsTheShippedScriptsInAscendingOrdinalOrder()
    {
        var scripts = ObservabilityMigrations.Load();

        // Asserting the exact ids, not just "some scripts were found". A prefix that silently
        // stopped matching would otherwise surface as an empty set, and an empty set reads as
        // "the schema is already up to date" — the quietest possible way for this to break.
        Assert.Equal(
            new[]
            {
                "001_baseline_schema",
                "002_context_snapshots",
                "003_loaded_bodies",
                "004_message_and_tool_bodies",
                "005_sessions_status_cancelled",
            },
            scripts.Select(s => s.Id));

        Assert.Equal(new[] { 1, 2, 3, 4, 5 }, scripts.Select(s => s.Ordinal));
        Assert.All(scripts, s => Assert.False(string.IsNullOrWhiteSpace(s.Sql)));
    }

    [Fact]
    public void Load_ScriptsOutOfNameOrder_StillOrdersByOrdinalRatherThanByResourceName()
    {
        var scripts = ObservabilityMigrations.Load();

        // GetManifestResourceNames makes no ordering guarantee, so this asserts the sort actually
        // happens rather than that the runtime happened to hand them over in a helpful order.
        var ordinals = scripts.Select(s => s.Ordinal).ToArray();
        Assert.Equal(ordinals.OrderBy(o => o), ordinals);
    }

    [Fact]
    public void Load_NoScriptsUnderThePrefix_ThrowsRatherThanReturningAnEmptySet()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => EmbeddedSqlMigrationSource.Load(TestAssembly, "NoSuchPrefix"));

        Assert.Contains("No embedded migration scripts found", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Load_TwoScriptsShareAnOrdinal_ThrowsAndNamesBoth()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => EmbeddedSqlMigrationSource.Load(TestAssembly, "DuplicateOrdinal"));

        Assert.Contains("001_first", ex.Message, StringComparison.Ordinal);
        Assert.Contains("001_second", ex.Message, StringComparison.Ordinal);
        Assert.Contains("share ordinal", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Load_ScriptWithoutANumericPrefix_Throws()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => EmbeddedSqlMigrationSource.Load(TestAssembly, "Unnumbered"));

        Assert.Contains("does not start with a number", ex.Message, StringComparison.Ordinal);
    }
}
