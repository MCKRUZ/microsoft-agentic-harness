using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Infrastructure.AI.KnowledgeGraph.Remote;

/// <summary>
/// Logs, once at host startup, the read-side trust and cross-caller isolation limitations that
/// apply whenever <c>AppConfig:AI:RemoteMemory:Enabled</c> is <see langword="true"/> — see the
/// remarks on <see cref="Domain.Common.Config.AI.RemoteMemoryConfig"/> for the full explanation.
/// Registered only when remote memory is enabled, so a disabled (default) deployment logs nothing.
/// </summary>
public sealed class RemoteMemoryEnabledStartupWarning : IHostedService
{
    private readonly ILogger<RemoteMemoryEnabledStartupWarning> _logger;

    /// <summary>Initializes a new instance of the <see cref="RemoteMemoryEnabledStartupWarning"/> class.</summary>
    /// <param name="logger">Logger the warning is written to.</param>
    public RemoteMemoryEnabledStartupWarning(ILogger<RemoteMemoryEnabledStartupWarning> logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogWarning(
            "AppConfig:AI:RemoteMemory:Enabled is true. Known limitations: (1) content already " +
            "present in the remote memory store is not re-verified by this harness's write-gate on " +
            "recall — only writes this harness itself makes are scanned before leaving this " +
            "process; (2) the remote contract does not yet filter recall by caller identity, so " +
            "different callers of this deployment can see each other's remote-stored facts through " +
            "a content search. Treat one AvatarId as a single shared trust boundary until the " +
            "remote contract enforces per-caller filtering and echoes a trust signal on recall.");

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
