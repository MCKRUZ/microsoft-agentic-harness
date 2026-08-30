namespace Application.AI.Common.Interfaces.ClaimVerification;

/// <summary>
/// Resolves one <see cref="Domain.AI.ClaimVerification.Claim.Location"/> scheme to its evidence
/// content — the ground truth a claim is checked against.
/// </summary>
/// <remarks>
/// <para>
/// Registered keyed by scheme (the substring of a location before its first <c>':'</c> — e.g.
/// <c>"file"</c>, <c>"config"</c>) via <c>AddKeyedSingleton&lt;ILocatedArtifactReader&gt;("file", ...)</c>.
/// <c>ClaimVerificationRunner</c> resolves the reader for a claim's scheme before ever calling
/// <see cref="TryReadAsync"/>; no reader registered for a scheme is a dispatch-level "unregistered
/// scheme" outcome the runner handles itself, not something an implementation needs to signal.
/// </para>
/// <para>
/// The critical semantic once a reader IS resolved: a <see langword="null"/> return means this
/// reader — already established as authoritative for the location's scheme — positively could not
/// resolve <em>this specific</em> location (file not found, config field not found). That is not
/// the same as "the claim is false," but it IS a real, reportable finding (the runner maps it to
/// <c>ClaimVerdict.LocationNotFound</c>, never to fail-safe silence) — the one place a naive reader
/// gets the fail-safe rule backwards. Only ever return content, or <see langword="null"/>; never
/// throw for "not found" — an implementation should let a genuine infrastructure failure (I/O error,
/// permission failure unrelated to sandboxing) propagate as an exception instead, which the runner
/// converts to <c>ClaimVerdict.VerifierError</c>.
/// </para>
/// </remarks>
public interface ILocatedArtifactReader
{
    /// <summary>
    /// Reads the evidence content at <paramref name="location"/>, or returns <see langword="null"/>
    /// if this reader cannot resolve that specific location — see this type's remarks for what that
    /// means to the caller.
    /// </summary>
    Task<string?> TryReadAsync(string location, CancellationToken cancellationToken);
}
