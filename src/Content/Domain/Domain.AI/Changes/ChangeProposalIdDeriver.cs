using System.Security.Cryptography;
using System.Text;
using Domain.AI.Identity;
using Domain.Common.Helpers;

namespace Domain.AI.Changes;

/// <summary>
/// Computes the deterministic, idempotent id for a <see cref="ChangeProposal"/>
/// from <c>(target, diff, submittedBy, submittedAt-bucket)</c>. Re-submission of the
/// same logical change within the same time bucket returns the same id, so the
/// orchestrator can short-circuit duplicates instead of starting a parallel pipeline.
/// </summary>
/// <remarks>
/// <para>
/// Hash: SHA-256 over a versioned canonical string. The version prefix (<c>"v1|"</c>)
/// lets future canonicalization changes coexist with existing persisted ids — a
/// later <c>v2</c> formula would produce a different id space rather than silently
/// collide with v1 ids.
/// </para>
/// <para>
/// Encoding: 32-byte hash rendered as Base64URL without padding (43 chars). URL-safe,
/// stable across platforms, and shorter than hex.
/// </para>
/// <para>
/// Time bucket: 1 minute. Resubmitting the same proposal twice within the same
/// wall-clock minute produces the same id. A longer bucket would silently
/// deduplicate intentional resubmissions; a shorter one would defeat the
/// idempotency the orchestrator depends on.
/// </para>
/// </remarks>
public static class ChangeProposalIdDeriver
{
    /// <summary>The wall-clock window size for the time component of the id.</summary>
    public static readonly TimeSpan IdBucket = TimeSpan.FromMinutes(1);

    private const string Version = "v1";

    /// <summary>
    /// Derive the deterministic proposal id from its identifying inputs.
    /// </summary>
    /// <param name="target">The target the proposal modifies; uses <see cref="ChangeTarget.CanonicalKey"/>.</param>
    /// <param name="diff">The ordered list of edits to apply.</param>
    /// <param name="submittedBy">The submitting agent identity; uses <see cref="AgentIdentity.Id"/>.</param>
    /// <param name="submittedAt">The wall-clock submission time; floored to the bucket boundary.</param>
    /// <returns>A 43-character Base64URL-encoded SHA-256 hash of the canonicalized inputs.</returns>
    /// <exception cref="ArgumentNullException">Thrown when any argument is null.</exception>
    public static string Derive(
        ChangeTarget target,
        IReadOnlyList<ChangeEdit> diff,
        AgentIdentity submittedBy,
        DateTimeOffset submittedAt)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(diff);
        ArgumentNullException.ThrowIfNull(submittedBy);

        var canonical = Canonicalize(target, diff, submittedBy, submittedAt);
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
        return Base64UrlHelper.Encode(bytes);
    }

    /// <summary>
    /// Render the canonical string that feeds the hash. Exposed for tests and for
    /// diagnostics that need to debug why two seemingly-equal proposals produced
    /// different ids.
    /// </summary>
    /// <param name="target">The target the proposal modifies.</param>
    /// <param name="diff">The ordered list of edits to apply.</param>
    /// <param name="submittedBy">The submitting agent identity.</param>
    /// <param name="submittedAt">The wall-clock submission time.</param>
    /// <returns>The canonical string used as SHA-256 input.</returns>
    public static string Canonicalize(
        ChangeTarget target,
        IReadOnlyList<ChangeEdit> diff,
        AgentIdentity submittedBy,
        DateTimeOffset submittedAt)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(diff);
        ArgumentNullException.ThrowIfNull(submittedBy);

        var bucketUnix = FloorToBucket(submittedAt).ToUnixTimeSeconds();
        var sb = new StringBuilder();
        sb.Append(Version).Append('|');
        sb.Append(target.CanonicalKey()).Append('|');
        AppendDiff(sb, diff);
        sb.Append('|');
        sb.Append(submittedBy.Id).Append('|');
        sb.Append(bucketUnix);
        return sb.ToString();
    }

    /// <summary>
    /// Floor a wall-clock instant to the start of the current id bucket. Two times
    /// within the same bucket produce the same floor.
    /// </summary>
    /// <param name="when">The instant to floor.</param>
    /// <returns>The bucket-start instant in UTC.</returns>
    public static DateTimeOffset FloorToBucket(DateTimeOffset when)
    {
        var ticks = when.UtcTicks;
        var bucketTicks = IdBucket.Ticks;
        var floored = ticks - (ticks % bucketTicks);
        return new DateTimeOffset(floored, TimeSpan.Zero);
    }

    private static void AppendDiff(StringBuilder sb, IReadOnlyList<ChangeEdit> diff)
    {
        sb.Append('[');
        for (var i = 0; i < diff.Count; i++)
        {
            if (i > 0)
            {
                sb.Append(';');
            }

            var edit = diff[i];
            // Length-prefix Target and Content so concatenation never confuses
            // two edits whose Target+Content concatenations happen to coincide.
            sb.Append(edit.Op).Append(':');
            sb.Append(edit.Target.Length).Append(':').Append(edit.Target).Append(':');
            sb.Append(edit.Content.Length).Append(':').Append(edit.Content);
        }
        sb.Append(']');
    }
}
