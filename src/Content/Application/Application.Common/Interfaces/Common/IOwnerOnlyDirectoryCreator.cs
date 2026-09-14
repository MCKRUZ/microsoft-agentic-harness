using Microsoft.Extensions.Logging;

namespace Application.Common.Interfaces.Common;

/// <summary>
/// Creates a directory (and any missing parents) with owner-only access on POSIX, for storage roots
/// that hold sensitive content and were never meant to be readable by anything beyond the process's
/// own user.
/// </summary>
/// <remarks>
/// <para>
/// The real implementation (<c>Infrastructure.AI.Helpers.OwnerOnlyDirectoryHelper</c>) is
/// <see langword="internal"/> to <c>Infrastructure.AI</c>, so it cannot be called directly from
/// <c>Application.Core</c> (Clean Architecture forbids an Application-layer project depending on
/// Infrastructure at all — not merely a visibility problem a wider modifier would fix), from
/// <c>Application.Common</c> itself, or from <c>Infrastructure.AI.RAG</c> (a sibling Infrastructure
/// project with no reference back to <c>Infrastructure.AI</c>). This interface is the seam: declared
/// here per this repo's own placement convention ("Interfaces for external services → Application/
/// Interfaces/, implemented in Infrastructure"), implemented in <c>Infrastructure.AI</c>, and consumed
/// by DI-injecting this interface rather than calling the internal helper directly (#671, #672, #673).
/// </para>
/// <para>
/// <strong>No constructor-injected <see cref="ILogger{TCategoryName}"/> in any implementation.</strong>
/// A logger flows in per-call instead. Several first-party <see cref="ILoggerProvider"/>
/// implementations (<c>FileLoggerProvider</c>, <c>StructuredJsonLoggerProvider</c>) are themselves
/// constructed while <c>ILoggerFactory</c> is being built; a registered implementation of this
/// interface that took <c>ILogger&lt;T&gt;</c> at construction would re-enter that same factory and
/// deadlock/stack-overflow the host at startup — the exact shape of a prior incident recorded in this
/// repo's own history for a different wrapper class. Because this interface's only implementation has
/// no constructor dependencies at all, it can never participate in that cycle regardless of when or
/// from where it is resolved.
/// </para>
/// </remarks>
public interface IOwnerOnlyDirectoryCreator
{
    /// <summary>
    /// Creates <paramref name="directory"/> (and any missing parents) with owner-only read/write/execute
    /// access on POSIX, applied to every directory this call actually creates — not just the leaf. A
    /// no-op permission-wise for any segment that already exists. A no-op entirely on Windows, which is
    /// left to its inherited ACL.
    /// </summary>
    /// <param name="directory">The directory to create.</param>
    /// <param name="logger">
    /// Used to report why this call aborted, when it does. <see langword="null"/> is accepted for
    /// callers with no logger available.
    /// </param>
    /// <exception cref="IOException">
    /// A segment this call is responsible for could not be confirmed as an owner-only directory this
    /// process controls after creating it — see the implementation's own remarks for why this call
    /// stops immediately rather than continuing under an unverified parent.
    /// </exception>
    void Create(string directory, ILogger? logger = null);
}
