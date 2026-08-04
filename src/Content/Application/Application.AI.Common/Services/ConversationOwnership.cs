using Application.Common.Exceptions.ExceptionTypes;
using Microsoft.Extensions.Logging;

namespace Application.AI.Common.Services;

/// <summary>
/// The ownership rules every <see cref="Interfaces.AI.IConversationStore"/> implementation answers
/// to, in one place so no two of them can answer differently.
/// </summary>
/// <remarks>
/// <para>
/// Public, and in the Application layer rather than beside the shipped implementations, because the
/// interface now <em>promises</em> that ownership is enforced. A consumer of this template who writes
/// a third store — a server-backed one, for the horizontally scaled deployment the SQLite store
/// cannot serve — has to be able to honour that promise with the same code, not a re-reading of it.
/// </para>
/// <para>
/// The comparison lives here for the same reason the check moved out of the call sites: it was
/// hand-written in six places and drifted into three different failure shapes. Two copies inside the
/// store layer would be that defect again at a smaller scale, and a divergence there would be worse
/// — the contract tests run every backend against one set of expectations, so a reader is entitled
/// to assume one rule.
/// </para>
/// </remarks>
public static class ConversationOwnership
{
    /// <summary>Rejects a blank caller id.</summary>
    /// <param name="callerId">The identity supplied by the caller.</param>
    /// <exception cref="ArgumentException"><paramref name="callerId"/> is null, empty, or whitespace.</exception>
    /// <remarks>
    /// Fail closed. An absent identity is the input most likely to arrive by accident — an
    /// unauthenticated request, a claim that did not resolve, a test fixture that forgot one — and
    /// this codebase has three recorded incidents of that absence being read as "everyone". Refusing
    /// it at the boundary means it can never become a widened scope.
    /// </remarks>
    public static void RequireCallerId(string callerId)
    {
        if (string.IsNullOrWhiteSpace(callerId))
        {
            throw new ArgumentException(
                "A caller id is required: conversations are never read or written unscoped.",
                nameof(callerId));
        }
    }

    /// <summary>
    /// Refuses <paramref name="callerId"/> access to a conversation owned by
    /// <paramref name="ownerId"/>, and does nothing when the two match.
    /// </summary>
    /// <param name="logger">The store's logger, used for the audit line.</param>
    /// <param name="conversationId">The conversation being reached for.</param>
    /// <param name="callerId">The caller reaching for it.</param>
    /// <param name="ownerId">The user the conversation actually belongs to.</param>
    /// <exception cref="ConversationAccessDeniedException">The two identities differ.</exception>
    /// <remarks>
    /// Both ids are logged deliberately: a caller reaching for a conversation it does not own is the
    /// signature of an insecure-direct-object-reference probe, and the pair is what makes the audit
    /// trail useful. The message handed back stays bare — naming the owner would answer the question
    /// the caller was refused.
    /// </remarks>
    public static void RequireOwner(ILogger logger, string conversationId, string callerId, string ownerId)
    {
        if (ownerId == callerId)
            return;

        logger.LogWarning(
            "User {CallerId} attempted to access conversation {ConversationId} owned by {OwnerId}.",
            callerId, conversationId, ownerId);

        throw new ConversationAccessDeniedException();
    }
}
