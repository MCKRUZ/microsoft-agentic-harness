using System.Globalization;
using System.Security.Cryptography;
using System.Text;

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
    /// A fingerprint of <see cref="Sql"/>, recorded alongside the id so an edit to an already-applied
    /// script is caught instead of silently splitting the installed base in two.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Without this, editing a released migration is a no-op on every database that already ran it and
    /// a full apply on every database created afterwards. The two populations then diverge for good,
    /// nothing reports it, and it is found by hand-diffing a live schema — structurally the same
    /// failure this whole subsystem was built to end. Flyway and Liquibase both keep a checksum for
    /// exactly this reason, and the answer is not an approval prompt at authoring time: the mistake
    /// does its damage at apply time, so that is where it has to be caught.
    /// </para>
    /// <para>
    /// Line endings are normalized before hashing, and that is not cosmetic. These scripts are
    /// embedded resources, so the bytes are whatever git checked out — CRLF on a Windows developer's
    /// machine, LF in Linux CI. Hashing raw bytes would make every database look tampered with the
    /// moment it met the other platform, which is a false alarm loud enough that the first fix anyone
    /// reaches for is to switch the check off.
    /// </para>
    /// </remarks>
    public string Checksum { get; } = ComputeChecksum(Sql);

    /// <summary>
    /// Hashes a script body, insensitive to the line endings the file happened to be checked out with.
    /// </summary>
    /// <param name="sql">The script body.</param>
    /// <returns>The lowercase hex SHA-256 of the normalized text.</returns>
    private static string ComputeChecksum(string sql)
    {
        ArgumentNullException.ThrowIfNull(sql);

        var normalized = sql.Replace("\r\n", "\n", StringComparison.Ordinal)
                            .Replace("\r", "\n", StringComparison.Ordinal);

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)));
    }

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
