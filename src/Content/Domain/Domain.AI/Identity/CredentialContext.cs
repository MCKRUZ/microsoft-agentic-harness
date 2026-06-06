using System.Collections.Immutable;

namespace Domain.AI.Identity;

/// <summary>
/// Per-credential acquisition metadata — audience, issuer, and OAuth scopes — passed to
/// an <c>IAgentCredentialProvider</c> when it acquires a token for an
/// <see cref="AgentIdentity"/>. Decouples providers from environment-specific URLs so
/// the same provider impl can run in dev/staging/prod with only this context changing.
/// </summary>
/// <remarks>
/// <para>
/// The provider receives a <see cref="CredentialContext"/> + <see cref="AgentIdentity"/>;
/// no other state. That keeps providers stateless and lets the harness swap them per
/// deployment without rewriting consumers.
/// </para>
/// <para>
/// <see cref="Scopes"/> is an <see cref="IReadOnlyList{T}"/> so consumers cannot mutate
/// the underlying collection. The record's value-equality treats two scope lists as
/// equal when their elements match in order.
/// </para>
/// </remarks>
public sealed record CredentialContext
{
    /// <summary>
    /// The audience claim the acquired token must target. Required for OAuth client
    /// credentials and federated credential flows.
    /// </summary>
    public required string Audience { get; init; }

    /// <summary>
    /// The expected token issuer URL (e.g. <c>https://login.microsoftonline.com/{tenant}/v2.0</c>).
    /// Null when the provider determines issuer from the agent's tenant binding.
    /// </summary>
    public string? Issuer { get; init; }

    /// <summary>
    /// The OAuth scopes to request. Empty when the credential flow does not use scopes
    /// (e.g. legacy resource-based access tokens). Defaults to an empty array, never
    /// <see cref="ImmutableArray{T}.IsDefault">default</see>.
    /// </summary>
    /// <remarks>
    /// <see cref="ImmutableArray{T}"/> (not <see cref="IReadOnlyList{T}"/>) so the value
    /// is structurally unmodifiable — consumers cannot downcast and mutate the backing
    /// store. The mutation methods on the <c>IList&lt;T&gt;</c> facet throw
    /// <see cref="NotSupportedException"/>.
    /// </remarks>
    public ImmutableArray<string> Scopes { get; init; } = ImmutableArray<string>.Empty;

    /// <summary>
    /// Records get reference equality on reference-type members by default;
    /// <see cref="ImmutableArray{T}.Equals(ImmutableArray{T})"/> is reference equality
    /// on the backing array, not element equality. Override to sequence-equal the
    /// scope array so two contexts with the same scope strings compare equal.
    /// </summary>
    public bool Equals(CredentialContext? other) =>
        other is not null
        && Audience == other.Audience
        && Issuer == other.Issuer
        && Scopes.SequenceEqual(other.Scopes);

    /// <inheritdoc />
    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Audience);
        hash.Add(Issuer);
        foreach (var scope in Scopes)
            hash.Add(scope);
        return hash.ToHashCode();
    }
}
