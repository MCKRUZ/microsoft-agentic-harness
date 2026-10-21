namespace Application.AI.Common.Models.Conversations;

/// <summary>A conversation nobody has written to since a cutoff, found by the retention sweep (#901).</summary>
/// <param name="Id">The conversation id.</param>
/// <param name="OwnerId">The owner, exactly as the identity provider issued it.</param>
/// <param name="LastActivityAt">When the conversation was last written to.</param>
public sealed record IdleConversation(string Id, string OwnerId, DateTimeOffset LastActivityAt);
