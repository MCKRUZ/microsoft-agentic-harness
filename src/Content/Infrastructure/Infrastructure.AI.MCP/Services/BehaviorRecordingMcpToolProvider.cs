using Application.AI.Common.Interfaces;
using Application.AI.Common.Interfaces.Tools;
using Domain.AI.Governance;
using Domain.Common.Config.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Client;

namespace Infrastructure.AI.MCP.Services;

/// <summary>
/// Decorates an <see cref="IMcpToolProvider"/> so that whatever each external MCP server declares about
/// its tools' behaviour is captured into the <see cref="IToolBehaviorRegistry"/> as the tools are
/// discovered, together with whether the operator has said that server's word can be taken.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Discovery is the only moment the information exists.</strong> The MCP protocol carries tool
/// annotations on the definition a server advertises, and the harness converts that definition into an
/// <c>AITool</c> the model can call. By the time a tool call arrives, all that remains is a name — the
/// annotations are unreachable from it. Recording them here is not an optimisation; it is the
/// difference between governing on behaviour and governing on names.
/// </para>
/// <para>
/// <strong>Outside the security scanner, not inside it.</strong> This wraps the screened provider, so
/// only tools that survived the definition scan are recorded. A tool withheld for a poisoned
/// description never reaches the model and needs no behaviour on file; recording it would put an entry
/// in the registry for a name the model was never told about.
/// </para>
/// <para>
/// <strong>The by-name lookup records nothing, deliberately.</strong> That path does not report which
/// server answered, so the trust decision — which is per server — cannot be made. Recording an
/// unattributed declaration would mean guessing, and under the registry's stricter-wins rule a guess of
/// "untrusted" would silently revoke an exemption a trusted server had legitimately earned. A tool the
/// registry has never heard of resolves to <see cref="ToolBehavior.Unknown"/> and is gated, so
/// declining to guess fails closed.
/// </para>
/// </remarks>
public sealed class BehaviorRecordingMcpToolProvider : IMcpToolProvider
{
    private readonly IMcpToolProvider _inner;
    private readonly IToolBehaviorRegistry _registry;
    private readonly IOptionsMonitor<AIConfig> _config;
    private readonly ILogger<BehaviorRecordingMcpToolProvider> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="BehaviorRecordingMcpToolProvider"/> class.
    /// </summary>
    /// <param name="inner">The tool provider whose results are recorded.</param>
    /// <param name="registry">Receives each tool's declared behaviour.</param>
    /// <param name="config">Supplies per-server trust; read per call so a config reload takes effect.</param>
    /// <param name="logger">Logger for servers whose declarations are being taken at face value.</param>
    public BehaviorRecordingMcpToolProvider(
        IMcpToolProvider inner,
        IToolBehaviorRegistry registry,
        IOptionsMonitor<AIConfig> config,
        ILogger<BehaviorRecordingMcpToolProvider> logger)
    {
        _inner = inner;
        _registry = registry;
        _config = config;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<IList<AITool>> GetToolsAsync(string serverName, CancellationToken cancellationToken = default)
    {
        var tools = await _inner.GetToolsAsync(serverName, cancellationToken);
        Record(serverName, tools);
        return tools;
    }

    /// <inheritdoc />
    public async Task<Dictionary<string, IList<AITool>>> GetAllToolsAsync(CancellationToken cancellationToken = default)
    {
        var discovered = await _inner.GetAllToolsAsync(cancellationToken);

        foreach (var (serverName, tools) in discovered)
            Record(serverName, tools);

        return discovered;
    }

    /// <inheritdoc />
    public Task<AIFunction?> GetToolByNameAsync(string name, CancellationToken cancellationToken = default) =>
        _inner.GetToolByNameAsync(name, cancellationToken);

    /// <inheritdoc />
    public Task<bool> IsServerAvailableAsync(string serverName, CancellationToken cancellationToken = default) =>
        _inner.IsServerAvailableAsync(serverName, cancellationToken);

    /// <inheritdoc />
    /// <remarks>
    /// Does not dispose the decorated provider — the container registers both as singletons and owns
    /// its lifetime, matching the arrangement the scanning decorator has with the transport provider.
    /// </remarks>
    public void Dispose()
    {
    }

    /// <summary>
    /// Records every tool in the batch against the trust level configured for the server that offered it.
    /// </summary>
    private void Record(string serverName, IEnumerable<AITool> tools)
    {
        var trusted = IsTrusted(serverName);

        if (trusted)
        {
            _logger.LogDebug(
                "MCP server '{ServerName}' is marked as trusted for tool annotations — a tool it declares "
                + "read-only is exempt from the non-read-only approval posture.",
                serverName);
        }

        foreach (var tool in tools)
            _registry.RecordAdvertised(tool.Name, Describe(tool, serverName, trusted));
    }

    /// <summary>
    /// Whether the operator has marked this server's behaviour annotations as believable when they
    /// loosen. Unknown server names — a server removed from configuration between discovery calls —
    /// are not trusted.
    /// </summary>
    private bool IsTrusted(string serverName)
    {
        var servers = _config.CurrentValue.McpServers?.Servers;

        return servers is not null
               && servers.TryGetValue(serverName, out var definition)
               && definition.TrustToolAnnotations;
    }

    /// <summary>
    /// Translates the SDK's advertised annotations into the harness's behaviour record.
    /// </summary>
    /// <remarks>
    /// A tool that is not an <see cref="McpClientTool"/>, or one whose server sent no annotations at
    /// all, still gets a record — carrying its source and four unanswered questions. That is not the
    /// same as no record: it distinguishes "this came from an untrusted server and said nothing" from
    /// "the harness has never seen this name", and only the first can be reported to an operator as a
    /// server that should be asked to annotate its tools.
    /// </remarks>
    private static ToolBehavior Describe(AITool tool, string serverName, bool trusted)
    {
        var source = trusted ? ToolBehaviorSource.TrustedMcpServer : ToolBehaviorSource.UntrustedMcpServer;
        var annotations = (tool as McpClientTool)?.ProtocolTool.Annotations;

        return new ToolBehavior(
            source,
            ReadOnly: annotations?.ReadOnlyHint,
            Destructive: annotations?.DestructiveHint,
            Idempotent: annotations?.IdempotentHint,
            OpenWorld: annotations?.OpenWorldHint,
            ServerName: serverName);
    }
}
