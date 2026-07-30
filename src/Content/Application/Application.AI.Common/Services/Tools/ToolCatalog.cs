using Application.AI.Common.Interfaces.Tools;
using Domain.AI.Bundles;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Application.AI.Common.Services.Tools;

/// <summary>
/// Default <see cref="IToolCatalog"/>: resolves each registered <see cref="ITool"/> key and projects it
/// into a <see cref="ToolDescriptor"/>, filtered per call by the caller's envelope.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Discovery is by enumerating registration keys, not by a registry tools opt into.</strong>
/// The keys are read from the host's own <c>IServiceCollection</c>, so a tool appears here by virtue of
/// being registered. The alternative — a registry each tool's DI file must remember to feed — fails
/// silently in exactly one direction: a tool that forgets to register itself is invisible rather than
/// broken, which is the "inert machinery" defect this codebase keeps rediscovering.
/// </para>
/// <para>
/// <strong>Why per-key resolution rather than <c>GetKeyedServices(AnyKey)</c>.</strong> Enumerating in
/// bulk constructs every tool in one pass, so a single tool whose dependencies the host does not provide
/// takes the whole catalog down with it. That is not hypothetical: <c>dashboard_control</c> and the
/// <c>render_*</c> tools require an <c>IClientToolBridge</c> that only AgentHub registers, so in any
/// other host they are registered but unconstructible. Resolving one key at a time contains that. A
/// tool the host cannot construct is not invocable, so omitting it from a catalog of what the caller
/// may invoke is the correct answer rather than a workaround — but it is logged as a warning, because
/// a host registering a tool it cannot build is a real misconfiguration that was previously invisible.
/// </para>
/// <para>
/// <strong>The registration key is the identity.</strong> Descriptors are keyed by the DI registration
/// key, not by <see cref="ITool.Name"/>, because the key is what <c>GetKeyedService</c> answers to and
/// therefore what an invocation must supply. The harness assumes the two are equal — see
/// <see cref="IToolCatalog"/> — so a divergence is logged as a warning rather than silently papered
/// over.
/// </para>
/// <para>
/// The projection is materialized once because registrations cannot change after the container is
/// built, and because constructing tools per request would be pure waste.
/// </para>
/// </remarks>
public sealed class ToolCatalog : IToolCatalog
{
    private readonly IReadOnlyList<ToolDescriptor> _descriptors;

    /// <summary>
    /// Initializes the catalog by resolving and projecting each registered tool key.
    /// </summary>
    /// <param name="serviceProvider">Provider used to resolve keyed <see cref="ITool"/> registrations.</param>
    /// <param name="registeredToolKeys">
    /// The keys under which tools are registered, read from the host's service collection. Duplicates
    /// are collapsed: keyed resolution answers with one instance per key regardless of how many
    /// registrations share it.
    /// </param>
    /// <param name="logger">Records tools that could not be constructed or whose name disagrees with their key.</param>
    public ToolCatalog(
        IServiceProvider serviceProvider,
        IEnumerable<string> registeredToolKeys,
        ILogger<ToolCatalog> logger)
    {
        ArgumentNullException.ThrowIfNull(serviceProvider);
        ArgumentNullException.ThrowIfNull(registeredToolKeys);
        ArgumentNullException.ThrowIfNull(logger);

        var descriptors = new List<ToolDescriptor>();

        foreach (var key in registeredToolKeys
            .Where(static key => !string.IsNullOrWhiteSpace(key))
            .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var descriptor = TryDescribe(serviceProvider, key, logger);
            if (descriptor is not null)
                descriptors.Add(descriptor);
        }

        _descriptors = [.. descriptors.OrderBy(static descriptor => descriptor.Name, StringComparer.Ordinal)];
    }

    /// <inheritdoc />
    public IReadOnlyList<ToolDescriptor> ListGranted(CapabilityEnvelope envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);

        return [.. _descriptors.Where(descriptor => envelope.GrantsTool(descriptor.Name))];
    }

    /// <inheritdoc />
    public ToolDescriptor? FindGranted(string toolName, CapabilityEnvelope envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);

        if (string.IsNullOrWhiteSpace(toolName) || !envelope.GrantsTool(toolName))
            return null;

        return _descriptors.FirstOrDefault(
            descriptor => string.Equals(descriptor.Name, toolName, StringComparison.OrdinalIgnoreCase));
    }

    private static ToolDescriptor? TryDescribe(IServiceProvider serviceProvider, string key, ILogger logger)
    {
        ITool? tool;

        try
        {
            tool = serviceProvider.GetKeyedService<ITool>(key);
        }
        catch (Exception ex)
        {
            // Catching broadly is deliberate: a tool factory can fail in any way its dependencies can,
            // and the catalog's contract is to describe what the caller may invoke. One unbuildable
            // tool must not deny every caller the whole listing.
            logger.LogWarning(
                ex,
                "Tool '{ToolKey}' is registered but could not be constructed in this host, so it is "
                + "omitted from the tool catalog. It is equally unavailable to agent runs here.",
                key);

            return null;
        }

        if (tool is null)
            return null;

        if (!string.Equals(tool.Name, key, StringComparison.OrdinalIgnoreCase))
        {
            logger.LogWarning(
                "Tool registered under key '{ToolKey}' reports its name as '{ToolName}'. The harness "
                + "resolves tools by name, so the catalog advertises the key. Align the two.",
                key,
                tool.Name);
        }

        return new ToolDescriptor(
            key,
            tool.Description ?? string.Empty,
            [.. tool.SupportedOperations ?? []],
            new ToolRiskProfile(tool.RiskTier, tool.IsReadOnly),
            tool.IsConcurrencySafe);
    }
}
