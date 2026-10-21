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
}
