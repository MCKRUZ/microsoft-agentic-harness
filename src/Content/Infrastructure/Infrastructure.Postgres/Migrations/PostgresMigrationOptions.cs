namespace Infrastructure.Postgres.Migrations;

/// <summary>
/// Identifies one migration set within a database, so that independent subsystems sharing a
/// Postgres instance keep separate ledgers and do not serialize behind each other.
/// </summary>
/// <param name="LedgerTable">
/// Unqualified name of the table recording which scripts have been applied. It is interpolated into
/// DDL rather than parameterized — Postgres does not accept a parameter where an identifier belongs
/// — so the value must be a literal chosen by the harness, never anything derived from user input.
/// The constructor enforces that it is a bare lower-case identifier.
/// <para>
/// Prefix it with the subsystem, as <c>obs_schema_migrations</c> and <c>kg_schema_migrations</c> do.
/// A bare <c>schema_migrations</c> is the name Rails, Flyway and Django all use verbatim, so a
/// consumer who points the harness at an existing application database would collide with a ledger
/// that is not ours — the runner would read someone else's table, find none of its ids, and apply
/// its whole baseline.
/// </para>
/// </param>
/// <param name="AdvisoryLockKey">
/// The key passed to <c>pg_advisory_xact_lock</c> to serialize migration runs across processes.
/// Two migration sets must not share a key: they would block each other for no reason. Two hosts
/// running the <em>same</em> set must share one, or they can both decide a script is unapplied.
/// </param>
public sealed record PostgresMigrationOptions(string LedgerTable, long AdvisoryLockKey)
{
    /// <summary>
    /// The ledger table name, guaranteed to be a bare lower-case identifier.
    /// </summary>
    /// <remarks>
    /// Checked here rather than in a separate <c>Validate()</c> the caller had to remember. That is
    /// how it was written first, and the giveaway was the test that had to exist to document the
    /// seam: a record that can hold an invalid value plus one constructor that happens to check it is
    /// strictly worse than a record that cannot hold one. The name reaches Postgres by string
    /// interpolation, which is ordinarily how SQL injection arrives; the defence is that the value
    /// cannot be attacker-reachable, and this is what makes that claim checkable rather than assumed.
    /// </remarks>
    /// <exception cref="ArgumentException">
    /// The name is empty or contains anything other than lower-case letters, digits and underscores.
    /// </exception>
    public string LedgerTable { get; } = ValidateLedgerTable(LedgerTable);

    private static string ValidateLedgerTable(string ledgerTable)
    {
        if (string.IsNullOrWhiteSpace(ledgerTable))
            throw new ArgumentException("A migration ledger table name is required.", nameof(ledgerTable));

        foreach (var c in ledgerTable)
        {
            if (c is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '_') continue;

            throw new ArgumentException(
                $"Ledger table '{ledgerTable}' must contain only lower-case letters, digits and " +
                "underscores. The name is interpolated into DDL and cannot be parameterized.",
                nameof(ledgerTable));
        }

        return ledgerTable;
    }
}
