namespace Domain.AI.KnowledgeGraph.Scoping;

/// <summary>
/// Canonicalizes knowledge-scope identity strings — owner IDs and tenant IDs — to a
/// single normalized form so that authorization gates and storage-backend filters
/// compare them identically.
/// </summary>
/// <remarks>
/// <para>
/// The knowledge graph is scoped by owner and tenant identity (see
/// <see cref="Models.KnowledgeScopeDescriptor"/>). Historically the authorization gate
/// compared these identifiers case-insensitively while the storage backends filtered
/// case-sensitively, so the gate could authorize a right-to-erasure the store then
/// failed to match — silently leaving the subject's data in place.
/// </para>
/// <para>
/// This helper defines the single canonical form (trimmed, invariant-lowercase) that
/// MUST be applied on every write of an owner/tenant identity AND before every
/// comparison, so authorization and persistence agree. Identity providers issue these
/// values as GUID object-ids or e-mail-style names, both of which are case-insensitive
/// by nature, so lowercasing is a safe canonicalization that never merges two distinct
/// principals.
/// </para>
/// <para>
/// Whitespace-only and empty inputs canonicalize to <c>null</c>, matching the graph's
/// convention that a <c>null</c> owner/tenant denotes a global (unscoped) record.
/// </para>
/// </remarks>
public static class ScopeIdentity
{
    /// <summary>
    /// Returns the canonical form of a scope identity (owner ID or tenant ID): the
    /// trimmed, invariant-lowercase value, or <c>null</c> when the input is null, empty,
    /// or whitespace.
    /// </summary>
    /// <param name="id">The raw owner or tenant identifier.</param>
    /// <returns>The canonical identifier, or <c>null</c> for an absent identity.</returns>
    public static string? Canonicalize(string? id)
        => string.IsNullOrWhiteSpace(id) ? null : id.Trim().ToLowerInvariant();

    /// <summary>
    /// Determines whether two scope identities refer to the same principal after
    /// canonicalization. Two absent (null/empty) identities are considered the same
    /// (both denote the global scope).
    /// </summary>
    /// <param name="left">The first identifier.</param>
    /// <param name="right">The second identifier.</param>
    /// <returns><c>true</c> when the canonical forms are equal; otherwise <c>false</c>.</returns>
    public static bool AreSame(string? left, string? right)
        => string.Equals(Canonicalize(left), Canonicalize(right), StringComparison.Ordinal);
}
