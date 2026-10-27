using System.Text.RegularExpressions;
using Infrastructure.Observability.Persistence;
using Xunit;

namespace Infrastructure.Observability.Tests.Schema;

/// <summary>
/// Reads the values a named <c>CHECK (column IN (…))</c> constraint accepts out of the shipped migrations, so a
/// schema agreement test needs no database.
/// </summary>
/// <remarks>
/// <para>
/// The constraint is found by its own name, wherever a migration declares it: inline in a <c>CREATE TABLE</c> or in a
/// later <c>ADD CONSTRAINT</c>. Anchoring on the name rather than the table block matters because a migration declares
/// several <c>CHECK (… IN (…))</c> constraints, most of them over unrelated vocabularies.
/// </para>
/// <para>
/// <b>The last declaration wins.</b> A later migration widens a constraint by dropping and re-adding it, so the values
/// that count are the last ones declared. This rests on <see cref="ObservabilityMigrations.Load"/> returning ordinal
/// order, which it documents and <c>EmbeddedSqlMigrationSource</c> enforces.
/// </para>
/// </remarks>
internal static class MigrationCheckConstraint
{
    /// <summary>Matches a declaration of <paramref name="constraint"/> over <paramref name="column"/>, capturing its values.</summary>
    /// <param name="constraint">The constraint's name.</param>
    /// <param name="column">The column it checks.</param>
    public static Regex Declaration(string constraint, string column) => new(
        $@"CONSTRAINT\s+{Regex.Escape(constraint)}\s+CHECK\s*\(\s*{Regex.Escape(column)}\s+IN\s*\((?<values>[^)]*)\)\s*\)",
        RegexOptions.Singleline | RegexOptions.IgnoreCase);

    /// <summary>The values the last migration that declares <paramref name="constraint"/> accepts.</summary>
    /// <param name="constraint">The constraint's name.</param>
    /// <param name="column">The column it checks.</param>
    public static HashSet<string> AcceptedValues(string constraint, string column)
    {
        var pattern = Declaration(constraint, column);
        var declaration = ObservabilityMigrations.Load()
            .Select(script => pattern.Match(script.Sql))
            .LastOrDefault(match => match.Success);

        Assert.True(
            declaration is not null,
            $"No migration declares a CHECK constraint named '{constraint}' on '{column}'.");

        return Values(declaration!);
    }

    /// <summary>The quoted values a matched declaration lists.</summary>
    /// <param name="declaration">A match of <see cref="Declaration"/>.</param>
    public static HashSet<string> Values(Match declaration) =>
        declaration.Groups["values"].Value
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(value => value.Trim('\''))
            .ToHashSet(StringComparer.Ordinal);
}
