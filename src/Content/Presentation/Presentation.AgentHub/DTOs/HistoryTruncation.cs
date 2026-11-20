namespace Presentation.AgentHub.DTOs;

/// <summary>
/// What a retry or an edit removed from a conversation, as the client is told the moment it commits (#328, #961).
/// </summary>
/// <remarks>
/// The client cuts its own message list at <see cref="FromMessageId"/> rather than at <see cref="KeepCount"/>. A
/// count only lands in the right place while the client's list matches the server's message for message, and the
/// client's list can be shorter (it keeps only the newest messages) or briefly out of step after a failed turn. An id
/// names the same message on both sides whatever the lists hold before it.
/// </remarks>
/// <param name="FromMessageId">
/// The first message the conversation no longer holds; every message after it went too. <see langword="null"/> when
/// the conversation did not hold the message the request named, so nothing was removed.
/// </param>
/// <param name="KeepCount">How many messages the conversation holds after the removal.</param>
public sealed record HistoryTruncation(Guid? FromMessageId, int KeepCount);
