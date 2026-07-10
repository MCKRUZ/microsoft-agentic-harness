using Domain.AI.Bundles;
using Domain.Common;

namespace Application.AI.Common.Interfaces.Bundles;

/// <summary>
/// Turns a received agent-bundle archive into a <see cref="StagedBundle"/> on disk, applying the host's
/// hostile-input guards before any of the archive's content is trusted for parsing or execution.
/// </summary>
/// <remarks>
/// <para>
/// A bundle is untrusted, externally-authored input. Staging is the boundary at which it is made safe:
/// the archive is size- and shape-checked (compressed size, entry count, total uncompressed size,
/// compression ratio), each entry's destination is verified to stay within the staging directory
/// (zip-slip guard), the extracted tree is checked for symlinks that escape the staging root, and the
/// staging root is verified to be disjoint from the configured skill and agent discovery roots so the
/// global registries can never independently discover the bundle's skills. Only after all of that does
/// the service reuse the host's ordinary <c>AGENT.md</c> / <c>SKILL.md</c> / <c>plugin.json</c> parsers
/// to produce the definitions carried on the returned <see cref="StagedBundle"/>.
/// </para>
/// <para>
/// Any guard failure returns a failed <see cref="Result{T}"/> with a safe, caller-facing reason and
/// leaves no partial extraction behind. The caller owns the lifetime of a successfully staged bundle's
/// directory on disk (the run-handle store deletes it on expiry).
/// </para>
/// </remarks>
public interface IBundleStagingService
{
    /// <summary>
    /// Validates and extracts <paramref name="archive"/> into an isolated staging directory, parsing its
    /// manifests into a <see cref="StagedBundle"/>.
    /// </summary>
    /// <param name="archive">
    /// The received archive stream (a zip). Read once; the caller retains ownership and disposal of the
    /// stream. The compressed length is enforced against the configured maximum while reading, so a
    /// non-seekable or oversized stream is rejected without buffering the whole of it.
    /// </param>
    /// <param name="cancellationToken">Token to cancel reading and extraction.</param>
    /// <returns>
    /// A successful result carrying the <see cref="StagedBundle"/>, or a failure describing the first
    /// guard the archive tripped (oversized, too many entries, decompression bomb, path traversal,
    /// escaping symlink, missing <c>AGENT.md</c>, or a staging root that overlaps a discovery root).
    /// </returns>
    Task<Result<StagedBundle>> StageAsync(Stream archive, CancellationToken cancellationToken = default);
}
