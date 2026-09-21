namespace Infrastructure.AI.KnowledgeGraph.Remote;

/// <summary>
/// The <see cref="IHttpClientFactory"/> client name every <c>Remote*</c> memory-seam
/// implementation resolves. Registered once in
/// <c>DependencyInjection.AddRemoteMemoryDependencies</c> with the remote service's
/// base address (already scoped to the configured avatar id) and <c>X-Api-Key</c> header, so every
/// consumer here uses plain relative paths.
/// </summary>
public static class RemoteMemoryHttpClientNames
{
    /// <summary>The named <see cref="IHttpClientFactory"/> client for remote memory-seam calls.</summary>
    public const string ClientName = "remote-memory";
}
