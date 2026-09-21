using Application.AI.Common.Interfaces.KnowledgeGraph;
using Domain.Common.Config;
using Infrastructure.AI.KnowledgeGraph.Remote;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Infrastructure.AI.KnowledgeGraph;

public static partial class DependencyInjection
{
    /// <summary>
    /// Registers the remote memory-hosting seams (avatar-hosting migration, M3a) when
    /// <c>AppConfig:AI:RemoteMemory:Enabled</c> is <see langword="true"/> — a named
    /// <see cref="System.Net.Http.IHttpClientFactory"/> client for every <c>Remote*</c>
    /// implementation to resolve, plus the implementations themselves, replacing the local
    /// defaults <see cref="AddKnowledgeGraphDependencies"/> registers above this call.
    /// </summary>
    /// <remarks>
    /// Registered with plain <c>Add*</c> calls, not <c>TryAdd</c>, and only after every local
    /// default has already been registered — the last registration for a service type is the one
    /// every non-keyed <c>GetService&lt;T&gt;</c> resolves, so this must run last to actually win.
    /// </remarks>
    private static void AddRemoteMemoryDependencies(IServiceCollection services, AppConfig appConfig)
    {
        var config = appConfig.AI.RemoteMemory;
        if (!config.Enabled)
            return;

        services.AddHttpClient(RemoteMemoryHttpClientNames.ClientName, (sp, client) =>
        {
            var current = sp.GetRequiredService<IOptionsMonitor<AppConfig>>().CurrentValue.AI.RemoteMemory;
            client.BaseAddress = new Uri($"{current.BaseUrl.TrimEnd('/')}/api/v1/harness-memory/{current.AvatarId}/");
            client.DefaultRequestHeaders.Add("X-Api-Key", current.ApiKey);
            client.Timeout = TimeSpan.FromSeconds(current.TimeoutSeconds);
        });

        services.AddTransient<IConversationFactExtractor, RemoteConversationFactExtractor>();
        services.AddScoped<IKnowledgeMemory, RemoteKnowledgeMemory>();
        services.AddSingleton<IMemoryAbstractor, RemoteMemoryAbstractor>();
        services.AddSingleton<IMemoryConsolidator, RemoteMemoryConsolidator>();

        services.AddHostedService<RemoteMemoryEnabledStartupWarning>();
    }
}
