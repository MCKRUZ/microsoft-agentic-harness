using Application.AI.Common.Interfaces.Tools;
using Microsoft.Extensions.Logging;

namespace Application.AI.Common.Services.Tools;

/// <summary>
/// Default <see cref="IToolRiskClassifier"/>: resolves the registered <see cref="ITool"/> for
/// a name via <see cref="FirstPartyToolLookup"/> and reads its declared risk. Returns
/// <see cref="ToolRiskProfile.Default"/> for names that do not resolve (external MCP tools,
/// unregistered names, or a registered tool whose constructor throws) so an unknown tool is never
/// treated as lower-risk than it is.
/// </summary>
public sealed class ToolRiskClassifier : IToolRiskClassifier
{
    private readonly FirstPartyToolLookup _firstPartyLookup;
    private readonly ILogger<ToolRiskClassifier> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="ToolRiskClassifier"/> class.
    /// </summary>
    /// <param name="firstPartyLookup">
    /// Shared bounded-key-set-gated lookup — see its remarks for why classifying via a raw keyed-DI
    /// probe (this type's original implementation) leaks process-lifetime memory once MCP/bundle
    /// tool calls, whose names are unbounded, reach this classifier on every governed invocation.
    /// </param>
    /// <param name="logger">
    /// Logs a construction failure via <see cref="FirstPartyToolLookup.TryResolveLogged"/> (#627: this
    /// used to call the unguarded resolve overload directly, propagating a keyed tool's constructor
    /// exception instead of catching it — the same host-boot failure mode #612 fixed for a
    /// permission-rule provider). Classification still degrades safely to
    /// <see cref="ToolRiskProfile.Default"/> either way; the log is what makes the anomaly visible.
    /// </param>
    public ToolRiskClassifier(FirstPartyToolLookup firstPartyLookup, ILogger<ToolRiskClassifier> logger)
    {
        ArgumentNullException.ThrowIfNull(firstPartyLookup);
        ArgumentNullException.ThrowIfNull(logger);
        _firstPartyLookup = firstPartyLookup;
        _logger = logger;
    }

    /// <inheritdoc />
    public ToolRiskProfile Classify(string toolName)
    {
        if (string.IsNullOrWhiteSpace(toolName))
            return ToolRiskProfile.Default;

        var tool = _firstPartyLookup.TryResolveLogged(
            toolName, _logger,
            $"to classify its risk — treating it as {ToolRiskProfile.Default} (the same conservative " +
            "answer used for an unrecognized tool)");

        return tool is null
            ? ToolRiskProfile.Default
            : new ToolRiskProfile(tool.RiskTier, tool.IsReadOnly);
    }
}
