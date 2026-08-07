using System.Globalization;

namespace Infrastructure.Postgres.Migrations;

/// <summary>
/// One ordered schema change, ready to apply.
/// </summary>
/// <param name="Id">
/// The script's file name without its extension — <c>001_baseline_schema</c> — and the key recorded
/// in the applied-migrations ledger. Renaming a released script therefore makes it look unapplied and
/// it will run again, which is safe only because every script in this repo is written to be
/// idempotent; it is still a rename you should not make. Add a new script instead.
/// </param>
/// <param name="Sql">The script body, applied verbatim as a single command.</param>
public sealed record MigrationScript(string Id, string Sql)
{
    /// <summary>
    /// The numeric prefix of <see cref="Id"/>, used to sort the set.
    /// </summary>
    /// <remarks>
    /// Computed rather than supplied. It was briefly a third constructor parameter, which let a
    /// caller hand over an ordinal that disagreed with the id it sat next to — a state that cannot
    /// exist in reality, since both come from one file name, but that nothing validated. The test
    /// helpers were already doing it: one assigned the ordinal from array position while the id
    /// carried its own prefix, and they agreed only by the author's care.
    /// <para>
    /// Ordinals need not be contiguous — gaps are expected once a migration is abandoned before
    /// release — but they must be unique, because two scripts sharing one have no defined order
    /// between them. <see cref="EmbeddedSqlMigrationSource"/> enforces that.
    /// </para>
    /// </remarks>
    public int Ordinal { get; } = ParseOrdinal(Id);

    /// <summary>
    /// Reads the leading digits of a migration id.
    /// </summary>
    /// <param name="id">The migration id.</param>
    /// <returns>The numeric prefix.</returns>
    /// <exception cref="ArgumentException">
    /// The id does not start with a number, so its place in the apply order is undefined. Thrown
    /// rather than defaulted: a script that silently sorted to position zero would run before the
    /// baseline that creates the tables it alters.
    /// </exception>
    private static int ParseOrdinal(string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);

        var digits = 0;
        while (digits < id.Length && char.IsAsciiDigit(id[digits])) digits++;

        if (digits == 0)
        {
            throw new ArgumentException(
                $"Migration '{id}' does not start with a number. Apply order is determined by that " +
                "prefix, so it is required — name the file like '001_baseline_schema.sql'.",
                nameof(id));
        }

        return int.Parse(id[..digits], CultureInfo.InvariantCulture);
    }
}
