using System.Security.Cryptography;
using System.Text;

namespace Domain.AI.Escalation;

/// <summary>
/// A fingerprint of the whole call an approver is asked about, and the reserved argument entries that
/// carry it to them.
/// </summary>
/// <remarks>
/// <para>
/// What an approver sees of a call is a rendering: values are scrubbed, cut to a length, and capped in
/// number, because the arguments land in a notification, a durable record and an audit line. Execution
/// uses the real call, so nothing can be swapped, but a human can approve arguments that differ beyond
/// what they were shown. The digest closes that gap in the only way a human can use: it is computed over
/// the full, untruncated, unscrubbed call, so it covers exactly the parts the rendering hides, and it
/// travels with the rendering into every surface (the notification, the approvals API, the audit line)
/// so what was approved can be matched to what ran.
/// </para>
/// <para>
/// <b>Carried inside the argument map.</b> Every approver surface already shows
/// <see cref="EscalationRequest.Arguments"/>, so reserved entries reach all of them with no change to a
/// wire contract. A model-supplied argument that happens to use a reserved name is shown under
/// <see cref="EscapedPrefix"/> instead, so a call cannot forge or hide the digest.
/// </para>
/// <para>
/// <b>Limit.</b> It is a plain SHA-256, not a keyed one: it does not reveal a value that is already shown,
/// but for a hidden value from a small space (a short secret in a 33rd argument) it would let someone who
/// can see it confirm a guess. The live surfaces (the notification, the approvals API) show it as computed. The
/// hash-chained audit trail records it, like every argument value, as a keyed fingerprint of the digest (#903), so
/// matching an approval to what ran from the trail needs the audit pseudonym key.
/// </para>
/// </remarks>
public static class EscalationCallDigest
{
    /// <summary>Argument-map key under which the digest is shown.</summary>
    public const string DigestKey = "(call digest)";

    /// <summary>
    /// Argument-map key under which a note is shown when the rendering is not the whole call. Present only
    /// when something was cut or omitted.
    /// </summary>
    public const string TruncatedKey = "(truncated)";

    /// <summary>Prefix a model-supplied argument gets when its name collides with a reserved key.</summary>
    public const string EscapedPrefix = "argument ";

    /// <summary>Whether <paramref name="key"/> is one of the reserved entries.</summary>
    public static bool IsReserved(string key) =>
        string.Equals(key, DigestKey, StringComparison.Ordinal)
        || string.Equals(key, TruncatedKey, StringComparison.Ordinal);

    /// <summary>
    /// The key to show a model-supplied argument under: its own name, unless that is reserved.
    /// </summary>
    /// <param name="key">The argument's name.</param>
    public static string DisplayKey(string key) => IsReserved(key) ? EscapedPrefix + key : key;

    /// <summary>
    /// Hashes the call: the tool name and every argument, in ordinal key order, as given.
    /// </summary>
    /// <param name="toolName">The tool (or step) the call is for.</param>
    /// <param name="arguments">
    /// Every argument of the call with its full, untruncated text. Order does not matter; it is sorted
    /// here, so two renderings of the same call always agree.
    /// </param>
    /// <returns><c>sha256:</c> followed by 64 lower-case hex characters.</returns>
    public static string Compute(string toolName, IEnumerable<KeyValuePair<string, string>> arguments)
    {
        ArgumentNullException.ThrowIfNull(toolName);
        ArgumentNullException.ThrowIfNull(arguments);

        // Length-prefixed fields: "a=b" + "c=d" can never collide with "a=b=c" + "d" style splits.
        var canonical = new StringBuilder();
        Append(canonical, toolName);
        foreach (var (key, value) in arguments.OrderBy(a => a.Key, StringComparer.Ordinal))
        {
            Append(canonical, key);
            Append(canonical, value);
        }

        return "sha256:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString())));
    }

    private static void Append(StringBuilder builder, string field) =>
        builder.Append(field.Length).Append(':').Append(field).Append('\n');
}
