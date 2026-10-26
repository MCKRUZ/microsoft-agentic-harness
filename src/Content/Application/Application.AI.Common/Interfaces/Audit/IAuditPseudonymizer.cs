namespace Application.AI.Common.Interfaces.Audit;

/// <summary>
/// Makes the keyed stand-ins the hash-chained audit trails record instead of personal data (#903): a reference for a person
/// (an approver, a reviewer, the caller behind a direct invocation) and a fingerprint for a value such as a tool argument
/// or a URL.
/// </summary>
/// <remarks>
/// <para>
/// The trails cannot be edited or erased without breaking their chain, so whatever they hold is held for good. A reference
/// keeps the accountability they exist for: whoever holds the key can compute a named person's reference and find every
/// approval they gave, or confirm that a given value was the one sent. Nobody without the key can reverse a reference or
/// confirm a guess, which a plain hash of a name or a short argument would allow.
/// </para>
/// <para>
/// Every result carries the id of the key it was made under (<c>ref:{keyId}:{hex}</c>, <c>fp:{keyId}:{hex}</c>), so a
/// reference made under a rotated key reads as one, rather than as a reference that matches nobody.
/// </para>
/// <para>
/// One key for the life of the process, from <c>AppConfig:AI:Audit:PseudonymKey</c> or the key file beside the session
/// data. It is not the telemetry key: that one may be random per process, and a write-once trail needs references that
/// still mean the same person after a restart.
/// </para>
/// </remarks>
public interface IAuditPseudonymizer
{
    /// <summary>Gets the short id of the key in use, as it appears in every result.</summary>
    string KeyId { get; }

    /// <summary>
    /// The reference recorded in place of a person's identifier. Case does not matter: approver names are compared
    /// ignoring case (<c>ApproverNames.Comparer</c>), so <c>Alice@Corp.com</c> and <c>alice@corp.com</c> are one person and
    /// get one reference.
    /// </summary>
    /// <param name="identifier">The person's name or id (approver name, user id), in any case.</param>
    /// <returns><c>ref:{keyId}:{hex}</c>.</returns>
    string Person(string identifier);

    /// <summary>
    /// The fingerprint recorded in place of a value. Each purpose hashes under a key of its own, so a fingerprint cannot
    /// be produced through a person reference or another purpose's fingerprint.
    /// </summary>
    /// <param name="purpose">What the value is (<see cref="Purposes"/>).</param>
    /// <param name="value">The value to fingerprint.</param>
    /// <returns><c>fp:{keyId}:{hex}</c>.</returns>
    string Fingerprint(string purpose, string value);

    /// <summary>The kinds of value fingerprints are made for.</summary>
    public static class Purposes
    {
        /// <summary>One argument value of a tool call awaiting approval.</summary>
        public const string ToolArgument = "tool-argument";

        /// <summary>The full target address of an outbound request.</summary>
        public const string EgressTarget = "egress-target";
    }
}
