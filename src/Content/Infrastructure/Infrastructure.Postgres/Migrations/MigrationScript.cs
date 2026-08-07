namespace Infrastructure.Postgres.Migrations;

/// <summary>
/// One ordered schema change, ready to apply.
/// </summary>
/// <param name="Ordinal">
/// The numeric prefix parsed from the script's file name, used only to sort the set. Ordinals need
/// not be contiguous — gaps are expected once a migration is abandoned before release — but they
/// must be unique, because two scripts sharing an ordinal have no defined order between them.
/// </param>
/// <param name="Id">
/// The script's file name without its extension, and the key recorded in the applied-migrations
/// ledger. Renaming a released script therefore makes it look unapplied and it will run again,
/// which is safe only because every script in this repo is written to be idempotent — but it is
/// still a rename you should not make. Add a new script instead.
/// </param>
/// <param name="Sql">The script body, applied verbatim as a single command.</param>
public sealed record MigrationScript(int Ordinal, string Id, string Sql);
