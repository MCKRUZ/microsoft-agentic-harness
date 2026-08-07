using System.Globalization;
using System.Reflection;

namespace Infrastructure.Postgres.Migrations;

/// <summary>
/// Loads an ordered migration set from <c>.sql</c> files embedded in an assembly.
/// </summary>
/// <remarks>
/// <para>
/// Migrations ship inside the assembly rather than beside it because a deployed harness has no
/// repository on disk. A consumer's published output contains DLLs; it does not contain
/// <c>Dashboards/init-db</c>, which is precisely why the previous mechanism could only ever reach a
/// database being created for the first time.
/// </para>
/// <para>
/// Resource names are pinned by an explicit <c>LogicalName</c> in each consuming project file, so
/// they are exactly <c>Migrations.&lt;file name&gt;</c>. That is deliberate: MSBuild's default
/// resource naming derives from the root namespace and folder path and rewrites characters it
/// considers invalid, which makes the name something you would have to verify rather than know.
/// </para>
/// </remarks>
public static class EmbeddedSqlMigrationSource
{
    /// <summary>
    /// Reads every embedded <c>.sql</c> resource under <paramref name="resourcePrefix"/> and returns
    /// them ordered by the numeric prefix of their file names.
    /// </summary>
    /// <param name="assembly">The assembly carrying the migration resources.</param>
    /// <param name="resourcePrefix">
    /// The logical-name prefix the scripts were embedded under, without a trailing dot — normally
    /// <c>Migrations</c>.
    /// </param>
    /// <returns>The migration set, in apply order.</returns>
    /// <exception cref="InvalidOperationException">
    /// No scripts were found, a file name lacks a numeric prefix, or two scripts share an ordinal.
    /// Each of these is thrown rather than tolerated because the failure they would otherwise cause
    /// is invisible: an empty set means "schema is up to date", and a duplicate ordinal means the
    /// apply order is whatever the sort happened to pick. The live folder this replaced had two
    /// files numbered <c>03-</c>, ordered only by shell glob luck.
    /// </exception>
    public static IReadOnlyList<MigrationScript> Load(Assembly assembly, string resourcePrefix = "Migrations")
    {
        ArgumentNullException.ThrowIfNull(assembly);
        ArgumentException.ThrowIfNullOrWhiteSpace(resourcePrefix);

        var prefix = resourcePrefix + ".";
        var names = assembly.GetManifestResourceNames()
            .Where(n => n.StartsWith(prefix, StringComparison.Ordinal)
                        && n.EndsWith(".sql", StringComparison.OrdinalIgnoreCase))
            .ToArray();

        if (names.Length == 0)
        {
            throw new InvalidOperationException(
                $"No embedded migration scripts found under '{prefix}' in assembly " +
                $"'{assembly.GetName().Name}'. Check that the project file embeds Migrations\\*.sql " +
                "with an explicit LogicalName.");
        }

        var scripts = new List<MigrationScript>(names.Length);
        var seen = new Dictionary<int, string>(names.Length);

        foreach (var name in names)
        {
            var id = name[prefix.Length..^".sql".Length];
            var ordinal = ParseOrdinal(id, assembly);

            if (seen.TryGetValue(ordinal, out var clash))
            {
                throw new InvalidOperationException(
                    $"Migrations '{clash}' and '{id}' share ordinal {ordinal} in assembly " +
                    $"'{assembly.GetName().Name}'. Two scripts with the same ordinal have no " +
                    "defined order between them; renumber one of them.");
            }

            seen[ordinal] = id;
            scripts.Add(new MigrationScript(ordinal, id, ReadResource(assembly, name)));
        }

        return scripts.OrderBy(s => s.Ordinal).ToArray();
    }

    private static int ParseOrdinal(string id, Assembly assembly)
    {
        var digits = 0;
        while (digits < id.Length && char.IsAsciiDigit(id[digits])) digits++;

        if (digits == 0)
        {
            throw new InvalidOperationException(
                $"Migration '{id}' in assembly '{assembly.GetName().Name}' does not start with a " +
                "number. Apply order is determined by that prefix, so it is required — name the " +
                "file like '001_baseline_schema.sql'.");
        }

        return int.Parse(id[..digits], CultureInfo.InvariantCulture);
    }

    private static string ReadResource(Assembly assembly, string name)
    {
        using var stream = assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException(
                $"Embedded resource '{name}' was listed by the assembly but could not be opened.");

        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
