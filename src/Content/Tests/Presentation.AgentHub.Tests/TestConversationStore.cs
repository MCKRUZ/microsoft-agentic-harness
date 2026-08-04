using Application.AI.Common.Interfaces.AI;
using Domain.Common.Config.AI.Conversations;
using Infrastructure.AI.Conversations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Presentation.AgentHub.Tests;

/// <summary>
/// Points a test host's conversation transcripts at an isolated directory, for the two web
/// application factories that override the composed store so tests never touch the configured path.
/// </summary>
/// <remarks>
/// <para>
/// Shared so the two factories cannot drift: they previously carried byte-identical construction, and
/// the store's constructor signature changed when it moved to Infrastructure. One call site means one
/// edit next time. Note that overriding the store here is also why the AgentHub suites cannot prove
/// the production registration exists — <c>ConversationStoreCompositionTests</c> covers that instead.
/// </para>
/// <para>
/// <strong>Store and turn lease are replaced together, and that is the whole point of this helper
/// registering both.</strong> They are two halves of one choice: the durable lease finds the
/// conversation by reading the same database the durable store writes to, so a host given the
/// file-backed store and left with the durable lease has a lease looking for conversations in a
/// database nothing writes to — every turn is refused. That is not hypothetical; it is what these
/// factories did for the length of one test run, and the four hub tests that failed said only
/// "Conversation not found."
/// </para>
/// </remarks>
internal static class TestConversationStore
{
    /// <summary>
    /// Registers a file-backed store and its matching in-process lease, both rooted at
    /// <paramref name="path"/>'s isolation.
    /// </summary>
    /// <param name="services">The test host's service collection.</param>
    /// <param name="path">An isolated temp directory owned by the calling factory.</param>
    internal static void UseIsolatedDirectory(IServiceCollection services, string path)
    {
        services.AddSingleton<IConversationStore>(new FileSystemConversationStore(
            Options.Create(new ConversationsConfig { ConversationsPath = path }),
            // Real time on purpose: these factories serve behavioural hub and controller tests that
            // assert on transcripts, not on timestamps, and a frozen clock would make every message
            // in a conversation share one instant.
            TimeProvider.System,
            NullLogger<FileSystemConversationStore>.Instance));

        services.AddSingleton<IConversationTurnLease>(new InProcessConversationTurnLease());
    }
}
