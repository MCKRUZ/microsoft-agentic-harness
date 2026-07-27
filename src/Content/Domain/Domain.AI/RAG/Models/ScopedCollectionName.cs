using System.Security.Cryptography;
using System.Text;

namespace Domain.AI.RAG.Models;

/// <summary>
/// Derives the effective RAG collection name for a request when per-tenant collection
/// isolation (<c>AppConfig:AI:Rag:ScopedCollections</c>) is enabled. The derivation is
/// server-side only: caller-supplied collection names are rejected at validation when the
/// feature is on, so the collection a request reads or writes is always a pure function of
/// the caller's ambient tenant — never of request content.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Derivation scheme.</strong> The tenant id is normalized with trim + lowercase
/// (the same normalization idea as <c>KnowledgeMemoryService</c>'s private scope-key
/// sanitization, though the character policy here is broader because collection names
/// must satisfy store naming rules), then rendered as <c>tenant-{slug}-{hash}</c> where
/// <c>{slug}</c> is the normalized tenant with every non <c>[a-z0-9]</c> character
/// collapsed to a single dash (capped at <see cref="MaxSlugLength"/> characters, for
/// readability in store dashboards) and <c>{hash}</c> is the first
/// <see cref="HashLength"/> (16) hex characters — 64 bits — of the SHA-256 of the
/// normalized tenant. The hash suffix makes the name collision-safe: two distinct tenants
/// whose slugs sanitize to the same string still get distinct collections, and 64 bits
/// puts a deliberately crafted colliding tenant id (which would merge two tenants'
/// collections) out of offline-grinding reach.
/// </para>
/// <para>
/// The output alphabet (<c>[a-z0-9-]</c>, starts with a letter, ≤ 56 characters) is valid
/// for every store the harness ships: FAISS in-memory collection keys, SQLite FTS5
/// collection values, and Azure AI Search index naming rules.
/// </para>
/// <para>
/// <strong>No-identity semantics.</strong> A caller with no ambient tenant resolves to
/// <see langword="null"/> — the store's global/default collection. This is closed and
/// predictable: anonymous and in-process callers share one well-known collection and can
/// never read or write a tenant-derived one.
/// </para>
/// </remarks>
public static class ScopedCollectionName
{
    /// <summary>Prefix of every tenant-derived collection name.</summary>
    public const string Prefix = "tenant-";

    /// <summary>Maximum length of the human-readable slug segment.</summary>
    public const int MaxSlugLength = 32;

    /// <summary>
    /// Length of the SHA-256 hex suffix (16 hex characters = 64 bits). Sized so that
    /// deliberately crafting a tenant id whose derived name collides with another
    /// tenant's is computationally infeasible offline.
    /// </summary>
    public const int HashLength = 16;

    /// <summary>
    /// Resolves the effective collection name for an ingest or search request.
    /// </summary>
    /// <param name="scopedCollectionsEnabled">
    /// Whether <c>AppConfig:AI:Rag:ScopedCollections:Enabled</c> is on.
    /// </param>
    /// <param name="requestedCollectionName">
    /// The caller-supplied collection name from the request DTO. Honored only when the
    /// feature is off; when the feature is on it is ignored entirely (defense in depth —
    /// request validation has already rejected non-null values).
    /// </param>
    /// <param name="tenantId">The ambient tenant of the caller, if any.</param>
    /// <returns>
    /// The collection name to pass to the vector and BM25 stores: the caller's value when
    /// the feature is off; the tenant-derived name when the feature is on and a tenant is
    /// present; <see langword="null"/> (the global/default collection) when the feature is
    /// on and no tenant is present.
    /// </returns>
    public static string? Resolve(
        bool scopedCollectionsEnabled,
        string? requestedCollectionName,
        string? tenantId)
        => scopedCollectionsEnabled ? DeriveForTenant(tenantId) : requestedCollectionName;

    /// <summary>
    /// Derives the deterministic, collision-safe collection name for a tenant, or
    /// <see langword="null"/> when <paramref name="tenantId"/> is null or whitespace
    /// (the global/default collection).
    /// </summary>
    /// <param name="tenantId">The tenant identifier to derive from.</param>
    /// <returns>The derived collection name, e.g. <c>tenant-contoso-a1b2c3d4</c>.</returns>
    public static string? DeriveForTenant(string? tenantId)
    {
        if (string.IsNullOrWhiteSpace(tenantId))
        {
            return null;
        }

        var normalized = tenantId.Trim().ToLowerInvariant();
        var hash = Convert.ToHexStringLower(
            SHA256.HashData(Encoding.UTF8.GetBytes(normalized)))[..HashLength];
        var slug = Slugify(normalized);

        return $"{Prefix}{slug}-{hash}";
    }

    /// <summary>
    /// Collapses every non <c>[a-z0-9]</c> character run to a single dash, trims leading
    /// and trailing dashes, and caps the result at <see cref="MaxSlugLength"/> characters.
    /// An input with no usable characters yields the placeholder slug <c>"t"</c> — the
    /// hash suffix still disambiguates such tenants.
    /// </summary>
    private static string Slugify(string normalized)
    {
        var builder = new StringBuilder(Math.Min(normalized.Length, MaxSlugLength));
        var lastWasDash = false;

        foreach (var c in normalized)
        {
            if (builder.Length >= MaxSlugLength)
            {
                break;
            }

            if (c is >= 'a' and <= 'z' or >= '0' and <= '9')
            {
                builder.Append(c);
                lastWasDash = false;
            }
            else if (!lastWasDash && builder.Length > 0)
            {
                builder.Append('-');
                lastWasDash = true;
            }
        }

        var slug = builder.ToString().TrimEnd('-');
        return slug.Length == 0 ? "t" : slug;
    }
}
