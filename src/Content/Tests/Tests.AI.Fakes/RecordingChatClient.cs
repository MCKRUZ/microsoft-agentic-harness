using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;

namespace Tests.AI.Fakes;

/// <summary>
/// In-memory <see cref="IChatClient"/> bound to one agent role's <see cref="RoleScript"/>, that
/// records every call it receives into a shared <see cref="ChatInvocationLog"/>.
/// </summary>
/// <remarks>
/// Streams each response's content items individually with usage arriving as a trailing
/// <see cref="UsageContent"/> chunk — mirroring what a real provider does, so a test asserting a
/// per-frame streaming invariant sees a real multi-frame sequence rather than one collapsed frame.
/// Construct via <see cref="ScriptedChatClientFactory"/> rather than directly, so the invocation log
/// and role binding stay consistent with the factory's role resolution.
/// </remarks>
public sealed class RecordingChatClient(string? agentId, RoleScript script, ChatInvocationLog log) : IChatClient
{
    /// <inheritdoc />
    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var messageList = messages.ToList();
        log.Record(agentId, messageList.Count, options?.ResponseFormat is not null);
        return Task.FromResult(script.Next());
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var response = await GetResponseAsync(messages, options, cancellationToken);
        await foreach (var update in ChatResponseStreaming.ToUpdatesAsync(response).WithCancellation(cancellationToken))
            yield return update;
    }

    /// <inheritdoc />
    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    /// <inheritdoc />
    public void Dispose() { }
}
