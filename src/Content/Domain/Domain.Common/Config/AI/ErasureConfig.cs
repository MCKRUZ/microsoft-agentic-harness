namespace Domain.Common.Config.AI;

/// <summary>
/// Configuration for right-to-erasure (#900). Bound to <c>AppConfig:AI:Erasure</c>.
/// </summary>
public sealed class ErasureConfig
{
    /// <summary>
    /// The secret key, base64-encoded and at least 32 bytes, that turns an erased person's id into the subject reference
    /// recorded in the erasure audit trail. Empty (the default) records no subject reference at all.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The audit trail must prove an erasure happened without keeping the identity that was erased. The reference is
    /// a keyed hash (HMAC-SHA256) of the person's id: anyone holding the key can recompute it from an id and find that
    /// person's erasure records, and no one without it can reverse the reference. A plain hash would not do, because ids
    /// are guessable and a list of candidates reverses it.
    /// </para>
    /// <para>
    /// Keep the key stable: a new key makes earlier references unfindable. Like every other secret in this harness,
    /// supply it from user-secrets, environment configuration or a key vault, never a committed settings file. Without
    /// it, erasure still runs and is still audited, but an audit record cannot be tied back to a person later; the
    /// receipt handed to the requester is then the only link.
    /// </para>
    /// </remarks>
    public string SubjectReferenceKey { get; set; } = string.Empty;

    /// <summary>
    /// The tenants (identity-provider tenant ids, the <c>tid</c> claim) whose operators may erase another person's data.
    /// Empty (the default) switches operator-initiated erasure off; self-service erasure is unaffected.
    /// </summary>
    /// <remarks>
    /// The erasure role alone is not enough. In an application that accepts sign-ins from several organisations, each
    /// organisation's administrator assigns the application's roles inside their own tenant, so a customer could grant
    /// themselves the erasure role and erase a person belonging to someone else. Erasure runs across every tenant, so the
    /// operators allowed to run it must come from tenants the deployment trusts, usually only its own.
    /// </remarks>
    public List<string> OperatorTenantIds { get; set; } = [];

    /// <summary>
    /// Where the erasure journal's SQLite database lives, relative to the host's base directory unless absolute (#907).
    /// The journal keeps the ids an erasure request read until every store that reads them has succeeded, so a request
    /// that fails part-way can be retried.
    /// </summary>
    public string JournalDatabasePath { get; set; } = "data/erasure-journal.db";

    /// <summary>
    /// How long an unfinished journal entry is kept before it is removed (#907). It must be at least as long as the
    /// longest retention among the stores the entries lead to (observability, tool-result spills, prompt usage, budgets,
    /// the call-once ledger): once the entry is gone nothing reaches what those stores still hold, but by then they have
    /// aged it out themselves. Withheld conversation ids, which are never erased through, are kept this long too, so later
    /// receipts keep saying so. The default is a year.
    /// </summary>
    public TimeSpan JournalRetention { get; set; } = TimeSpan.FromDays(365);

    /// <summary>
    /// The folder of the compliance audit trail (#969): the hash-chained record of every erasure, data export and document
    /// removal, kept as proof that each was handled. Relative to the working directory unless absolute. The day-files go in
    /// a <c>compliance</c> folder inside it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Kept for good, like the other audit trails, and archived with them when <c>AppConfig:AI:Audit:ArchivePath</c> is
    /// set. Application logs are deleted after <c>Logging:Retention:RetainFor</c> (30 days by default), far too soon to
    /// show, years later, that a person's request was carried out.
    /// </para>
    /// <para>
    /// The records hold who asked (an operator as a keyed reference, a person erasing their own data as
    /// <c>data-subject</c>), the subject reference when <see cref="SubjectReferenceKey"/> is set, the receipt id and
    /// per-store counts. Never the person's id and never any of their data.
    /// </para>
    /// </remarks>
    public string AuditStoragePath { get; set; } = ".agent-sessions/compliance";
}
