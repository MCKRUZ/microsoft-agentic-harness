using Application.AI.Common.Interfaces.Tools;
using Microsoft.Extensions.DependencyInjection;

namespace Application.AI.Common.Services.Tools;

/// <summary>
/// Default <see cref="IToolRiskClassifier"/>: resolves the registered <see cref="ITool"/> for
/// a name from keyed DI and reads its declared risk. Returns
/// <see cref="ToolRiskProfile.Default"/> for names that do not resolve (external MCP tools,
/// unregistered names) so an unknown tool is never treated as lower-risk than it is.
/// </summary>
public sealed class ToolRiskClassifier : IToolRiskClassifier
{
    private readonly IServiceProvider _serviceProvider;

    /// <summary>
    /// Initializes a new instance of the <see cref="ToolRiskClassifier"/> class.
    /// </summary>
    /// <param name="serviceProvider">Provider used to resolve keyed <see cref="ITool"/> registrations.</param>
    public ToolRiskClassifier(IServiceProvider serviceProvider)
    {
        ArgumentNullException.ThrowIfNull(serviceProvider);
        _serviceProvider = serviceProvider;
    }

    /// <inheritdoc />
    public ToolRiskProfile Classify(string toolName)
    {
        if (string.IsNullOrWhiteSpace(toolName))
            return ToolRiskProfile.Default;

        var tool = _serviceProvider.GetKeyedService<ITool>(toolName);

        return tool is null
            ? ToolRiskProfile.Default
            : new ToolRiskProfile(tool.RiskTier, tool.IsReadOnly);
    }
}
