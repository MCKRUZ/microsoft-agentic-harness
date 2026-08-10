using System.Collections.Concurrent;
using Application.AI.Common.Interfaces.Tools;
using Domain.AI.Governance;
using Microsoft.Extensions.DependencyInjection;

namespace Application.AI.Common.Services.Tools;

/// <summary>
/// Default <see cref="IToolBehaviorRegistry"/>: holds the declarations external MCP servers advertised
/// at discovery, and reads a first-party tool's declaration straight off its keyed-DI registration.
/// </summary>
/// <remarks>
/// <para>
/// Registered as a singleton because the advertised half must outlive the discovery scope that
/// recorded it — the tool call it governs happens later, on a different scope. The dictionary is the
/// only mutable state and every write is a whole-entry replacement.
/// </para>
/// <para>
/// <strong>The registry is never emptied.</strong> A tool that vanishes from a server keeps its
/// recorded declaration, which looks like a leak and is deliberate: forgetting an entry can only ever
/// turn a known behaviour into <see cref="ToolBehavior.Unknown"/>, and the entries are a few dozen
/// short records. Growth is bounded by how many distinct tool names the configured servers have ever
/// advertised.
/// </para>
/// </remarks>
public sealed class ToolBehaviorRegistry : IToolBehaviorRegistry
{
    private readonly ConcurrentDictionary<string, ToolBehavior> _advertised =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly IServiceProvider _serviceProvider;

    /// <summary>
    /// Initializes a new instance of the <see cref="ToolBehaviorRegistry"/> class.
    /// </summary>
    /// <param name="serviceProvider">Provider used to resolve keyed <see cref="ITool"/> registrations.</param>
    public ToolBehaviorRegistry(IServiceProvider serviceProvider)
    {
        ArgumentNullException.ThrowIfNull(serviceProvider);
        _serviceProvider = serviceProvider;
    }

    /// <inheritdoc />
    public void RecordAdvertised(string toolName, ToolBehavior behavior)
    {
        ArgumentNullException.ThrowIfNull(behavior);

        if (string.IsNullOrWhiteSpace(toolName))
            return;

        // Two different rules, and conflating them was a defect: a server re-reporting its own tool is
        // always believed, while a DIFFERENT server may only ever tighten.
        //
        // Believing a re-report is what catches a rug-pull — a server that advertised a clean read-only
        // tool and later advertises it as a writer — and it is also the only way a change to that
        // server's TrustToolAnnotations setting can ever take effect: the earlier untrusted record is
        // non-exempt, so a rule that kept the stricter entry unconditionally would pin it forever and
        // the operator's config change would silently do nothing until the process restarted.
        //
        // Evaluated inside the update so two servers advertising the same name concurrently cannot
        // interleave a read and a write and lose the stricter declaration.
        _advertised.AddOrUpdate(
            toolName,
            behavior,
            (_, existing) => IsSameSource(existing, behavior) || existing.IsExemptFromApproval
                ? behavior
                : existing);
    }

    /// <inheritdoc />
    public ToolBehavior Resolve(string toolName)
    {
        if (string.IsNullOrWhiteSpace(toolName))
            return ToolBehavior.Unknown;

        var advertised = _advertised.GetValueOrDefault(toolName);
        var registered = ResolveFirstParty(toolName);

        if (advertised is null)
            return registered ?? ToolBehavior.Unknown;

        if (registered is null)
            return advertised;

        // Both describe this name. Return whichever does not exempt the tool: the harness resolves a
        // declared tool from MCP before falling back to keyed DI, so which implementation actually runs
        // depends on discovery succeeding, and a rule that changes with it would be worse than one that
        // always answers with the stricter of the two.
        return advertised.IsExemptFromApproval ? registered : advertised;
    }

    /// <summary>
    /// Whether two declarations came from the same MCP server, and may therefore replace one another
    /// freely.
    /// </summary>
    /// <remarks>
    /// A record with no server name cannot be attributed, so it is never treated as the same source as
    /// anything — including another unattributed record. That is the fail-closed reading: an
    /// unattributable declaration must not gain the right to overwrite an attributable one.
    /// </remarks>
    private static bool IsSameSource(ToolBehavior existing, ToolBehavior incoming) =>
        existing.ServerName is { Length: > 0 }
        && string.Equals(existing.ServerName, incoming.ServerName, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Reads the declaration off the tool's own registration, or returns <see langword="null"/> when
    /// the name is not registered in this process.
    /// </summary>
    /// <remarks>
    /// <strong>This deliberately does not go through <see cref="IToolRiskClassifier"/>,</strong> which
    /// performs the same keyed-DI lookup and already exposes an <c>IsReadOnly</c>. That type answers
    /// with a <em>fail-safe default</em> for a name it cannot resolve — a profile that reports
    /// "not read-only" for an unregistered tool exactly as it does for a registered one that writes.
    /// Collapsing the two would erase the distinction this registry exists to keep: a tool that said
    /// it writes and a tool nobody has ever heard of are the same answer for graded autonomy, and
    /// different facts for anything reporting on which tools still need annotating.
    /// </remarks>
    private ToolBehavior? ResolveFirstParty(string toolName)
    {
        var tool = _serviceProvider.GetKeyedService<ITool>(toolName);
        if (tool is null)
            return null;

        // ITool.IsReadOnly is a bool, not a bool? — a first-party tool always answers, because the
        // interface's default answers "no" on its behalf. Destructive is left unstated: the harness has
        // never modelled it locally, and inventing a value here would put a claim in the record that
        // nobody made.
        return new ToolBehavior(ToolBehaviorSource.FirstParty, ReadOnly: tool.IsReadOnly);
    }
}
