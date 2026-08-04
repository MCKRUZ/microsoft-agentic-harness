using Application.AI.Common.Interfaces.AI;
using Domain.Common.Config.AI.Conversations;
using Infrastructure.AI.Conversations;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Presentation.AgentHub.Tests;

/// <summary>
/// Builds a file-backed <see cref="IConversationStore"/> over an isolated directory, for the two web
/// application factories that override the composed store so tests never touch the configured path.
/// </summary>
/// <remarks>
/// Shared so the two factories cannot drift: they previously carried byte-identical construction, and
/// the store's constructor signature changed when it moved to Infrastructure. One call site means one
/// edit next time. Note that overriding the store here is also why the AgentHub suites cannot prove
/// the production registration exists — <c>ConversationStoreCompositionTests</c> covers that instead.
/// </remarks>
internal static class TestConversationStore
{
    /// <summary>Creates a store rooted at <paramref name="path"/>, creating the directory if absent.</summary>
    /// <param name="path">An isolated temp directory owned by the calling factory.</param>
    internal static IConversationStore ForDirectory(string path) =>
        new FileSystemConversationStore(
            Options.Create(new ConversationsConfig { ConversationsPath = path }),
            // Real time on purpose: these factories serve behavioural hub and controller tests that
            // assert on transcripts, not on timestamps, and a frozen clock would make every message
            // in a conversation share one instant.
            TimeProvider.System,
            NullLogger<FileSystemConversationStore>.Instance);
}
