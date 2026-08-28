using Application.AI.Common.Interfaces;
using Application.AI.Common.Interfaces.Agent;
using Application.AI.Common.Interfaces.Context;
using Application.AI.Common.Interfaces.Tools;
using Domain.AI.Changes;
using Domain.AI.Models;
using Domain.AI.Sandbox;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Infrastructure.AI.Tools;

/// <summary>
/// Retrieves the full text of a tool result that was truncated and spilled to
/// <see cref="IToolResultStore"/> (#521) — the model's way to ask for the rest of a result it was
/// only shown a cut-down version of.
/// </summary>
/// <remarks>
/// <para>
/// <strong>The retrieval scope comes from the CALLING request's own <see cref="IAgentExecutionContext"/>,
/// resolved per invocation, never from a caller-supplied parameter.</strong> A model-facing
/// <c>resultId</c> is the only argument this tool accepts; widening that to also accept a scope id
/// would let the model simply state whose data it wants to read, defeating the isolation boundary
/// <see cref="IToolResultStore"/> enforces.
/// </para>
/// <para>
/// <strong>Registered <c>Singleton</c>, like every other keyed tool — NOT <c>Scoped</c>.</strong> An
/// earlier version of this type constructor-injected <see cref="IAgentExecutionContext"/> directly and
/// was registered <c>AddKeyedScoped</c> specifically to avoid a captive-dependency leak. That reasoning
/// was correct but the mechanism was wrong for this codebase: every production caller resolves a keyed
/// <see cref="ITool"/> by name from a SINGLETON holding the ROOT service provider
/// (<c>ToolChainBuilder.ResolveToolByName</c>, <c>FirstPartyToolLookup.Resolve</c>) — there is no
/// per-request scope at resolution time to be scoped INTO. Every host enables
/// <c>ServiceProviderOptions.ValidateScopes</c>, so a scoped registration doesn't leak state across
/// callers the way a naive singleton constructor-injecting a scoped service would — it fails LOUDLY,
/// with <see cref="InvalidOperationException"/>, on every turn of every skill that lists this tool
/// (caught by this repo's own <c>correctness</c> gate before this shipped). The fix is the same pattern
/// already used throughout this codebase for a long-lived singleton that needs a per-request, scoped
/// service (see <see cref="IAmbientRequestScope"/>'s own remarks, and its dozen-plus other consumers):
/// resolve <see cref="IAgentExecutionContext"/> from <see cref="IAmbientRequestScope.Current"/> AT
/// EXECUTION TIME, inside <see cref="ExecuteAsync"/>, never at construction. <see cref="IToolResultStore"/>
/// itself stays constructor-injected directly — it is registered singleton, so singleton-into-singleton
/// is safe and needs no ambient indirection.
/// </para>
/// <para>
/// <strong>Deliberately not <see cref="ITool.IsDirectlyInvocable"/>.</strong> Direct HTTP invocation
/// mints a fresh <see cref="IAgentExecutionContext.ToolResultScopeId"/> for every single call (see
/// that property's remarks) — a result spilled during one direct invocation can never be fetched by a
/// later, unrelated one, because their scopes never match. That is not a bug on this tool's part; it
/// is the correct consequence of direct-invoke having no session for "later" to mean anything within.
/// Removing this tool from that surface avoids offering a call that can only ever answer "not found".
/// Direct invocation also does not run inside the MediatR pipeline that establishes the ambient request
/// scope, so <see cref="IAmbientRequestScope.Current"/> would be null there regardless.
/// </para>
/// <para>
/// A fetched result is routed through the normal <c>ToolResult.Ok</c> return, so it flows back through
/// the same admission pipeline as any other tool result — the same size threshold applies, and a
/// result still too large to fit spills again under a fresh id, exactly like any other tool's output.
/// </para>
/// </remarks>
public sealed class ToolResultFetchTool : ITool
{
    public const string ToolName = "tool_result_fetch";

    private static readonly IReadOnlyList<string> Operations = ["fetch"];

    private readonly IToolResultStore _resultStore;
    private readonly IAmbientRequestScope _ambientScope;
    private readonly ILogger<ToolResultFetchTool> _logger;

    public ToolResultFetchTool(
        IToolResultStore resultStore,
        IAmbientRequestScope ambientScope,
        ILogger<ToolResultFetchTool> logger)
    {
        ArgumentNullException.ThrowIfNull(resultStore);
        ArgumentNullException.ThrowIfNull(ambientScope);
        ArgumentNullException.ThrowIfNull(logger);
        _resultStore = resultStore;
        _ambientScope = ambientScope;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Name => ToolName;

    /// <inheritdoc />
    public string Description =>
        "Retrieves the full text of a tool result that was truncated. Pass the id from the " +
        $"\"{string.Format(Application.AI.Common.Services.Governance.ToolCallAdmissionPipeline.SpilledResultMarkerFormat, "...").Trim()}\" " +
        "marker as the 'resultId' parameter.";

    /// <inheritdoc />
    public IReadOnlyList<string> SupportedOperations => Operations;

    /// <inheritdoc />
    public bool IsReadOnly => true;

    /// <inheritdoc />
    public bool IsConcurrencySafe => true;

    /// <inheritdoc />
    public bool IsDirectlyInvocable => false;

    /// <inheritdoc />
    public BlastRadius RiskTier => BlastRadius.Trivial;

    /// <summary>
    /// The shipped <see cref="IToolResultStore"/> implementation is <c>FileSystemToolResultStore</c> —
    /// a spilled result is read back from disk, so <see cref="ToolCapability.FileRead"/> is the honest
    /// declaration rather than <see cref="ToolCapability.None"/> (Infrastructure.AI.Governance.Tests'
    /// <c>AllToolsCapabilityCoverageTests</c> fails a new tool that leaves this undeclared).
    /// </summary>
    public ToolCapability RequiredCapabilities => ToolCapability.FileRead;

    /// <inheritdoc />
    public async Task<ToolResult> ExecuteAsync(
        string operation,
        IReadOnlyDictionary<string, object?> parameters,
        CancellationToken cancellationToken = default)
    {
        if (!parameters.TryGetValue("resultId", out var raw)
            || raw is not string resultId
            || string.IsNullOrWhiteSpace(resultId))
        {
            return ToolResult.Fail("A non-empty 'resultId' parameter is required.");
        }

        // Resolved here, not at construction — this instance is a process-lifetime singleton and the
        // execution context is scoped to the calling request. See this type's own remarks for why.
        var executionContext = _ambientScope.Current?.GetService<IAgentExecutionContext>();
        if (executionContext is null)
        {
            _logger.LogWarning(
                "tool_result_fetch invoked with no ambient request scope established; cannot resolve " +
                "the calling request's own retrieval scope.");
            return ToolResult.Fail("Unable to retrieve stored results outside an active agent turn.");
        }

        try
        {
            var content = await _resultStore
                .RetrieveFullContentAsync(resultId, executionContext.ToolResultScopeId, cancellationToken)
                .ConfigureAwait(false);
            return ToolResult.Ok(content);
        }
        catch (KeyNotFoundException)
        {
            // Deliberately generic — see IToolResultStore.RetrieveFullContentAsync's own remarks for
            // why "wrong scope" and "never existed" must be indistinguishable from outside the store.
            return ToolResult.Fail($"No stored result found for id '{resultId}'.");
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException
            or NotSupportedException or System.Security.SecurityException)
        {
            // A security review of #521 found these could otherwise escape ExecuteAsync as raw
            // exceptions — AIToolConverter invokes tools with no surrounding try/catch. The write side
            // (SpillAndBuildMarkerAsync) already degrades store failures instead of throwing; the read
            // side must give the same guarantee rather than letting a disk error fault the whole turn.
            _logger.LogWarning(ex, "Failed to retrieve tool result {ResultId}", resultId);
            return ToolResult.Fail($"No stored result found for id '{resultId}'.");
        }
    }
}
