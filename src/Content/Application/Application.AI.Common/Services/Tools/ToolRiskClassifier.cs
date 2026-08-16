using Application.AI.Common.Interfaces.Tools;

namespace Application.AI.Common.Services.Tools;

/// <summary>
/// Default <see cref="IToolRiskClassifier"/>: resolves the registered <see cref="ITool"/> for
/// a name via <see cref="FirstPartyToolLookup"/> and reads its declared risk. Returns
/// <see cref="ToolRiskProfile.Default"/> for names that do not resolve (external MCP tools,
/// unregistered names) so an unknown tool is never treated as lower-risk than it is.
/// </summary>
public sealed class ToolRiskClassifier : IToolRiskClassifier
{
    private readonly FirstPartyToolLookup _firstPartyLookup;

    /// <summary>
    /// Initializes a new instance of the <see cref="ToolRiskClassifier"/> class.
    /// </summary>
    /// <param name="firstPartyLookup">
    /// Shared bounded-key-set-gated lookup — see its remarks for why classifying via a raw keyed-DI
    /// probe (this type's original implementation) leaks process-lifetime memory once MCP/bundle
    /// tool calls, whose names are unbounded, reach this classifier on every governed invocation.
    /// </param>
    public ToolRiskClassifier(FirstPartyToolLookup firstPartyLookup)
    {
        ArgumentNullException.ThrowIfNull(firstPartyLookup);
        _firstPartyLookup = firstPartyLookup;
    }

    /// <inheritdoc />
    public ToolRiskProfile Classify(string toolName)
    {
        if (string.IsNullOrWhiteSpace(toolName))
            return ToolRiskProfile.Default;

        var tool = _firstPartyLookup.Resolve(toolName);

        return tool is null
            ? ToolRiskProfile.Default
            : new ToolRiskProfile(tool.RiskTier, tool.IsReadOnly);
    }
}
