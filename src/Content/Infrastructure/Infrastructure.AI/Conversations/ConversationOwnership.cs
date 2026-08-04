using Application.Common.Exceptions.ExceptionTypes;
using Microsoft.Extensions.Logging;

namespace Infrastructure.AI.Conversations;

/// <summary>
/// The two ownership decisions every <c>IConversationStore</c> implementation has to make, in one
/// place so the backends cannot answer them differently.
/// </summary>
/// <remarks>
/// Shared rather than duplicated because the value of moving ownership into the store was that there
/// stopped being several copies of it. Two copies inside the store layer would be the same defect at
/// a smaller scale — and a divergence there would be worse, because the contract tests run both
/// backends against one set of expectations and a reader would reasonably assume one rule.
/// </remarks>
internal static class ConversationOwnership
{
    /// <summary>Rejects a blank caller id.</summary>
    /// <param name="callerId">The identity supplied by the caller.</param>
    /// <exception cref="ArgumentException"><paramref name="callerId"/> is null, empty, or whitespace.</exception>
    /// <remarks>
    /// Fail closed. An absent identity is the input most likely to arrive by accident — an
    /// unauthenticated request, a claim that did not resolve, a test fixture that forgot one — and
    /// this codebase has three recorded incidents of that absence being read as "everyone" further
    /// down. Refusing it at the boundary means it can never become a widened scope.
    /// </remarks>
    internal static void RequireCallerId(string callerId)
    {
        if (string.IsNullOrWhiteSpace(callerId))
        {
            throw new ArgumentException(
                "A caller id is required: conversations are never read or written unscoped.",
                nameof(callerId));
        }
    }

    /// <summary>
    /// Builds the refusal for a conversation that exists but belongs to someone else, logging the
    /// attempt first.
    /// </summary>
    /// <param name="logger">The store's logger.</param>
    /// <param name="conversationId">The conversation that was reached for.</param>
    /// <param name="callerId">The caller that reached for it.</param>
    /// <param name="ownerId">The user the conversation actually belongs to.</param>
    /// <returns>The exception to throw, so call sites read as <c>throw Denied(...)</c>.</returns>
    /// <remarks>
    /// Both ids are logged deliberately: a caller reaching for a conversation it does not own is the
    /// signature of an insecure-direct-object-reference probe, and the pair is what makes the audit
    /// trail useful. The message handed back to the caller stays bare — telling them whose it is
    /// would answer the question they were not allowed to ask.
    /// </remarks>
    internal static ConversationAccessDeniedException Denied(
        ILogger logger,
        string conversationId,
        string callerId,
        string ownerId)
    {
        logger.LogWarning(
            "User {CallerId} attempted to access conversation {ConversationId} owned by {OwnerId}.",
            callerId, conversationId, ownerId);

        return new ConversationAccessDeniedException();
    }
}
