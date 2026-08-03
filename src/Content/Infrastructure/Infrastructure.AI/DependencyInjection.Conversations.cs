using Application.AI.Common.Interfaces.AI;
using Domain.Common.Config;
using Domain.Common.Config.AI.Conversations;
using Infrastructure.AI.Conversations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Infrastructure.AI;

public static partial class DependencyInjection
{
    /// <summary>
    /// Registers the durable conversation transcript store.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This lives in Infrastructure rather than in a host's own composition because the transcript
    /// store is shared: the interactive AgentHub host and the Execution API both read and write the
    /// same conversations. While the registration belonged to <c>Presentation.AgentHub</c> the
    /// harness had the capability exactly once, reachable only from its interactive entry point.
    /// </para>
    /// <para>
    /// <strong>Singleton is load-bearing.</strong> <see cref="FileSystemConversationStore"/> serialises
    /// all of its file I/O behind one <see cref="SemaphoreSlim"/>; a scoped or transient registration
    /// would hand out several stores with several semaphores and lose that serialisation.
    /// </para>
    /// </remarks>
    private static void RegisterConversationStore(IServiceCollection services)
    {
        // Project the shared AppConfig section onto the narrow options type the store depends on, so
        // the store's dependency surface is the two settings it reads rather than the whole config
        // tree. Same shape as the PlannerOptions projection below.
        services.AddOptions<ConversationsConfig>()
            .Configure<IOptionsMonitor<AppConfig>>((opts, app) =>
                opts.ConversationsPath = app.CurrentValue.AI.Conversations.ConversationsPath);

        services.AddSingleton<IConversationStore, FileSystemConversationStore>();
    }
}
