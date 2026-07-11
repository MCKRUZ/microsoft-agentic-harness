using Domain.Common;
using MediatR;

namespace Application.AI.Common.CQRS.Bundles.RegisterBundle;

/// <summary>
/// Registers a received agent-bundle archive: validates and extracts it under the host's hostile-input
/// guards, then holds the staged bundle behind a short-lived TTL handle the caller uses to run or delete it.
/// The application-level entry point for <c>POST /api/bundles</c>.
/// </summary>
/// <remarks>
/// The command carries the archive as a <see cref="Stream"/> rather than a buffered byte array so an
/// oversized archive is rejected while reading, without first materialising all of it in memory. The stream
/// is read once by the staging service; the caller retains ownership and disposal of it. Registration does
/// not run the bundle — it only stages it and mints a handle.
/// </remarks>
public sealed record RegisterBundleCommand : IRequest<Result<RegisterBundleResult>>
{
    /// <summary>
    /// The received archive stream (a zip). Read once during staging; the caller retains ownership and
    /// disposal. Its compressed length is enforced against the configured maximum while reading.
    /// </summary>
    public required Stream Archive { get; init; }

    /// <summary>
    /// Stable identifier of the caller registering the bundle. The resulting handle is bound to this owner,
    /// so only the same caller can later run, read, or delete it — a leaked handle cannot be used across
    /// callers. Resolved at the transport boundary from the authenticated principal.
    /// </summary>
    public required string OwnerId { get; init; }
}
