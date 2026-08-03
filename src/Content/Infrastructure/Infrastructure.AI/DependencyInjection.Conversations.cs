using Application.AI.Common.Interfaces.AI;
using Domain.Common.Config;
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
    /// would hand out several stores with several semaphores and lose that serialisation. Registering
    /// by type keeps construction lazy, so a host that never resolves the store never touches the disk.
    /// </para>
    /// </remarks>
    private static void RegisterConversationStore(IServiceCollection services, AppConfig appConfig)
    {
        // Hand the config section straight through rather than copying its properties across. The
        // source and target are the same type, so a property-by-property projection would only add a
        // place to forget: a setting added to ConversationsConfig later would silently keep its
        // default with every test still green. Same idiom as the ModelRouting/KnowledgeBridge
        // registrations in DependencyInjection.cs.
        services.AddSingleton(Options.Create(appConfig.AI.Conversations));

        services.AddSingleton<IConversationStore, FileSystemConversationStore>();
    }
}
