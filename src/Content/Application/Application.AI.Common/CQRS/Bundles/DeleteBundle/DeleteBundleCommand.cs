using Domain.Common;
using MediatR;

namespace Application.AI.Common.CQRS.Bundles.DeleteBundle;

/// <summary>
/// Explicitly removes a staged bundle and deletes its staging directory, ahead of the TTL that would
/// eventually reclaim it. The application-level entry point for <c>DELETE /api/bundles/{handle}</c>.
/// </summary>
/// <remarks>
/// Idempotent: removing a handle that is already gone (never existed, already deleted, or already expired)
/// is a success, not an error — the post-condition "the handle no longer exists" holds either way. The
/// result's payload reports whether a handle was actually present to remove, for callers that care.
/// </remarks>
public sealed record DeleteBundleCommand : IRequest<Result<bool>>
{
    /// <summary>The handle of the staged bundle to remove.</summary>
    public required string Handle { get; init; }

    /// <summary>
    /// Stable identifier of the caller requesting deletion. Only the owner the handle was registered under
    /// can delete it; a non-owner request is a no-op (the handle is not theirs to remove). Resolved at the
    /// transport boundary from the authenticated principal.
    /// </summary>
    public required string OwnerId { get; init; }
}
