namespace Infrastructure.Postgres.Migrations;

/// <summary>
/// Identifies one migration set within a database, so that independent subsystems sharing a
/// Postgres instance keep separate ledgers and do not serialize behind each other.
/// </summary>
/// <param name="LedgerTable">
/// Unqualified name of the table recording which scripts have been applied. It is interpolated into
/// DDL rather than parameterized — Postgres does not accept a parameter where an identifier belongs
/// — so the value must be a literal chosen by the harness, never anything derived from user input.
/// <see cref="Validate"/> enforces that.
/// </param>
/// <param name="AdvisoryLockKey">
/// The key passed to <c>pg_advisory_xact_lock</c> to serialize migration runs across processes.
/// Two migration sets must not share a key: they would block each other for no reason. Two hosts
/// running the <em>same</em> set must share one, or they can both decide a script is unapplied.
/// </param>
public sealed record PostgresMigrationOptions(string LedgerTable, long AdvisoryLockKey)
{
    /// <summary>
    /// Throws if <see cref="LedgerTable"/> is anything other than a plain lower-case identifier.
    /// </summary>
    /// <remarks>
    /// The name reaches Postgres by string interpolation, which is ordinarily how SQL injection
    /// arrives. It cannot be parameterized, so the defence has to be that the value is not
    /// attacker-reachable — and this check is what makes that claim checkable rather than assumed.
    /// </remarks>
    /// <exception cref="ArgumentException">The ledger table name is empty or not a bare identifier.</exception>
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(LedgerTable))
            throw new ArgumentException("A migration ledger table name is required.", nameof(LedgerTable));

        foreach (var c in LedgerTable)
        {
            if (c is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '_') continue;

            throw new ArgumentException(
                $"Ledger table '{LedgerTable}' must contain only lower-case letters, digits and " +
                "underscores. The name is interpolated into DDL and cannot be parameterized.",
                nameof(LedgerTable));
        }
    }
}
