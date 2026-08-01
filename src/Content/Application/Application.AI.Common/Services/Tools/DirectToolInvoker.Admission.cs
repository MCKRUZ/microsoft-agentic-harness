using Application.AI.Common.Interfaces.Planner;
using Application.AI.Common.Interfaces.Tools;
using Domain.Common.Config.AI.DirectToolInvocation;
using Microsoft.Extensions.Logging;

namespace Application.AI.Common.Services.Tools;

/// <summary>
/// Admission: everything decidable about an invocation before any scope, governor, or tool exists.
/// </summary>
/// <remarks>
/// Split out because it changes for different reasons than the arming path does — admission answers
/// "is this request well formed and is this tool reachable by this caller", while the other partial
/// answers "how is a reachable tool run safely". Keeping the checks ahead of scope creation is also
/// what makes a malformed or ungranted request cost a dictionary lookup rather than a DI scope and a
/// governance evaluation.
/// </remarks>
public sealed partial class DirectToolInvoker
{
    /// <summary>
    /// Prefix for the synthetic agent identity minted per caller. The colon is inside the charset
    /// <see cref="PlanRunRequest.IsWellFormedAgentId"/> permits, and no configured agent is named this
    /// way, so a direct invocation cannot pick up permission rules authored for a real agent.
    /// </summary>
    private const string SyntheticAgentPrefix = "direct-invoke:";

    /// <summary>
    /// Validates the request, resolves the tool against the caller's grant, and mints the identity the
    /// invocation will run under.
    /// </summary>
    private Admission Admit(DirectToolInvocationRequest request, DirectToolInvocationConfig config)
    {
        if (string.IsNullOrWhiteSpace(request.ToolName) || string.IsNullOrWhiteSpace(request.Operation))
            return Admission.Refuse(DirectToolInvocationStatus.Invalid, "A tool name and an operation are required.");

        if (request.Parameters.Count > config.MaxParameterCount)
        {
            return Admission.Refuse(
                DirectToolInvocationStatus.Invalid,
                $"An invocation may pass at most {config.MaxParameterCount} parameters.");
        }

        if (TimeoutRefusal(request, config) is { } timeoutRefusal)
            return timeoutRefusal;

        var agentId = SyntheticAgentPrefix + request.OwnerId;
        if (!PlanRunRequest.IsWellFormedAgentId(agentId))
        {
            // The identity is the permission subject and the audit subject. The rejected value is
            // deliberately not echoed or logged verbatim — it originates in a token claim.
            _logger.LogWarning(
                "Direct invocation rejected: caller identity is unusable as a permission subject (length {Length})",
                request.OwnerId?.Length ?? 0);
            return Admission.Refuse(
                DirectToolInvocationStatus.Invalid, "The caller's identity cannot be used as a permission subject.");
        }

        // FindGranted answers null for a tool that does not exist AND for one the envelope does not
        // grant — the two are indistinguishable on purpose. Adding "not offered on this surface" to
        // that set keeps the disclosure boundary at exactly one bit: reachable, or not.
        var descriptor = _catalog.FindGranted(request.ToolName, request.Envelope);
        if (descriptor is null || !descriptor.IsDirectlyInvocable)
            return Admission.Refuse(DirectToolInvocationStatus.NotFound, DirectToolInvocationErrors.NoSuchTool);

        if (OperationRefusal(request, descriptor) is { } operationRefusal)
            return operationRefusal;

        return Admission.Accept(descriptor.Name, agentId);
    }

    /// <summary>
    /// Refuses a requested deadline that is not usable, rather than clamping it.
    /// </summary>
    /// <remarks>
    /// Clamping would be friendlier-looking and worse: a caller silently given less time than they
    /// asked for experiences a timeout with nothing in the response that accounts for it.
    /// </remarks>
    private static Admission? TimeoutRefusal(
        DirectToolInvocationRequest request, DirectToolInvocationConfig config)
    {
        if (request.RequestedTimeout is not { } requested)
            return null;

        if (requested > TimeSpan.Zero && requested <= config.InvocationTimeout)
            return null;

        return Admission.Refuse(
            DirectToolInvocationStatus.Invalid,
            $"Requested timeout must be positive and no greater than {config.InvocationTimeout}.");
    }

    /// <summary>
    /// Refuses an operation the tool does not declare.
    /// </summary>
    /// <remarks>
    /// Naming the accepted operations is not a disclosure: the caller is already entitled to this tool
    /// and can read the same list from the catalog, so withholding it would only cost them a round
    /// trip. Matched case-insensitively, as tool and operation names are everywhere else in the
    /// harness — a stricter check here would refuse invocations every other layer accepts.
    /// </remarks>
    private static Admission? OperationRefusal(
        DirectToolInvocationRequest request, ToolDescriptor descriptor)
    {
        if (descriptor.SupportedOperations.Contains(request.Operation, StringComparer.OrdinalIgnoreCase))
            return null;

        return Admission.Refuse(
            DirectToolInvocationStatus.Invalid,
            descriptor.SupportedOperations.Count == 0
                ? $"Tool '{descriptor.Name}' declares no operations."
                : $"Tool '{descriptor.Name}' supports: {string.Join(", ", descriptor.SupportedOperations)}.");
    }

    /// <summary>
    /// The result of pre-execution admission: either a refusal to return as-is, or the resolved tool
    /// name and caller identity to run with.
    /// </summary>
    private readonly record struct Admission(
        DirectToolInvocationOutcome? Refusal, string? ToolName, string? AgentId)
    {
        /// <summary>An admission that refuses, carrying the outcome to return unchanged.</summary>
        public static Admission Refuse(DirectToolInvocationStatus status, string error) =>
            new(DirectToolInvocationOutcome.Refused(status, error), null, null);

        /// <summary>An admission that proceeds, carrying the resolved tool name and caller identity.</summary>
        public static Admission Accept(string toolName, string agentId) => new(null, toolName, agentId);
    }
}
