namespace Infrastructure.AI.Persistence;

/// <summary>
/// SQLite extended result codes this codebase matches on, in one place, so the two stores that
/// translate a constraint violation into a domain-meaningful refusal (a lost conversation-create
/// race, a claimed call-once ledger slot) cannot drift into checking different values for the
/// same underlying condition.
/// </summary>
/// <remarks>
/// Both codes are accepted wherever either is checked: which one SQLite reports for a clashing
/// key depends on how that key is declared (primary vs. unique), not on anything the caller
/// controls, so recognising only one variant would turn an ordinary, expected collision into an
/// unhandled write failure.
/// </remarks>
internal static class SqliteErrorCodes
{
    /// <summary>SQLite's <c>SQLITE_CONSTRAINT_PRIMARYKEY</c> extended result code.</summary>
    public const int ConstraintPrimaryKey = 1555;

    /// <summary>SQLite's <c>SQLITE_CONSTRAINT_UNIQUE</c> extended result code.</summary>
    public const int ConstraintUnique = 2067;
}
