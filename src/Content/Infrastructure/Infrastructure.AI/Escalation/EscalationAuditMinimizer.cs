using Application.AI.Common.Interfaces.Audit;
using Application.AI.Common.Interfaces.Telemetry;
using Application.AI.Common.Services.Tools;
using Domain.AI.Escalation;
using Domain.AI.Governance;
using Domain.AI.Telemetry.Redaction;

namespace Infrastructure.AI.Escalation;

/// <summary>
/// Makes the copy of an escalation request, decision or outcome that the hash-chained audit trail records (#903): people as
/// keyed references, tool arguments as keyed fingerprints, and free text with identifiers redacted.
/// </summary>
/// <remarks>
/// <para>
/// The trail cannot be edited or erased without breaking its chain, so it records the least that still answers its
/// questions: which tool, which argument names, who approved (as a reference the key holder can recompute for a named
/// person), the verdict and the reasoning. The escalation state store keeps the full values for as long as the escalation
/// needs them, under its own retention, and is the copy the running system reads.
/// </para>
/// <para>
/// <b>Free text is scrubbed of the values the record pseudonymizes elsewhere.</b> A request's description is written by
/// its producer and routinely repeats them: "Agent 'direct-invoke:{user id}' is attempting…", or a delegation's task text
/// that is also its <c>taskDescription</c> argument. A raw value left there beside its reference would hand any reader the
/// link between the two without the key. Each such value is swapped for a marker, the text redacted, then each marker
/// replaced by its reference or fingerprint; the order keeps redaction away from the references, whose hex the digit
/// rules could otherwise take for a phone number.
/// </para>
/// <para>
/// Works on a <c>with</c> copy of the domain record and changes values only: every property, and so every field the
/// readers require, is still present. The domain types and their converters are untouched, because the state store
/// serializes them too and seals what it writes.
/// </para>
/// </remarks>
internal sealed class EscalationAuditMinimizer
{
    /// <summary>
    /// The shortest argument value looked for in free text. Shorter ones ("true", "1", "en") occur in unrelated words and
    /// would mangle the text without hiding anything identifying.
    /// </summary>
    internal const int MinimumEchoedValueLength = 8;

    private const char MarkerStart = '';
    private const char MarkerEnd = '';

    private readonly IAuditPseudonymizer _pseudonymizer;
    private readonly IContentRedactionFilter _redaction;

    /// <summary>Initializes a new instance of the <see cref="EscalationAuditMinimizer"/> class.</summary>
    public EscalationAuditMinimizer(IAuditPseudonymizer pseudonymizer, IContentRedactionFilter redaction)
    {
        _pseudonymizer = pseudonymizer;
        _redaction = redaction;
    }

    /// <summary>The audit copy of a request.</summary>
    public EscalationRequest Minimize(EscalationRequest request)
    {
        var arguments = request.Arguments.ToDictionary(
            pair => pair.Key,
            pair => _pseudonymizer.Fingerprint(IAuditPseudonymizer.Purposes.ToolArgument, pair.Value),
            StringComparer.Ordinal);
        var standIns = StandIns(request, arguments);

        return request with
        {
            AgentId = AgentId(request.AgentId),
            Arguments = arguments,
            Description = Scrub(request.Description, standIns)!,
            Approvers = People(request.Approvers),
            OriginatingDecision = Minimize(request.OriginatingDecision, standIns),
            PriorFailureReason = Scrub(request.PriorFailureReason, standIns),
            PriorRevisionInstructions = Scrub(request.PriorRevisionInstructions, standIns),
        };
    }

    /// <summary>The audit copy of one approver's decision.</summary>
    public ApproverDecision Minimize(ApproverDecision decision) => decision with
    {
        ApproverName = _pseudonymizer.Person(decision.ApproverName),
        Reason = Scrub(decision.Reason, []),
        Instructions = Scrub(decision.Instructions, []),
    };

    /// <summary>The audit copy of an outcome.</summary>
    public EscalationOutcome Minimize(EscalationOutcome outcome) => outcome with
    {
        Decisions = [.. outcome.Decisions.Select(Minimize)],
        Approvers = People(outcome.Approvers),
        CancelledBy = outcome.CancelledBy is { } cancelledBy ? _pseudonymizer.Person(cancelledBy) : null,
    };

    private GovernanceDecision? Minimize(GovernanceDecision? decision, IReadOnlyList<(string Raw, string StandIn)> standIns) =>
        decision is null
            ? null
            : decision with
            {
                Reason = Scrub(decision.Reason, standIns)!,
                Approvers = decision.Approvers is null ? null : People(decision.Approvers),
            };

    // The raw values this request pseudonymizes elsewhere, longest first so a value inside another is replaced last.
    private List<(string Raw, string StandIn)> StandIns(EscalationRequest request, Dictionary<string, string> fingerprints)
    {
        var standIns = new List<(string Raw, string StandIn)>();
        if (DirectInvocationAgentIds.TryGetOwner(request.AgentId, out var owner))
        {
            standIns.Add((request.AgentId, AgentId(request.AgentId)));
            standIns.Add((owner, _pseudonymizer.Person(owner)));
        }

        standIns.AddRange(request.Arguments
            .Where(pair => pair.Value.Length >= MinimumEchoedValueLength)
            .Select(pair => (pair.Value, fingerprints[pair.Key])));
        return [.. standIns.DistinctBy(s => s.Raw).OrderByDescending(s => s.Raw.Length)];
    }

    // A direct invocation runs as "direct-invoke:{user id}"; a configured agent's name is not personal data.
    private string AgentId(string agentId) =>
        DirectInvocationAgentIds.TryGetOwner(agentId, out var owner)
            ? DirectInvocationAgentIds.For(_pseudonymizer.Person(owner))
            : agentId;

    private IReadOnlyList<string> People(IReadOnlyList<string> people) => [.. people.Select(_pseudonymizer.Person)];

    private string? Scrub(string? text, IReadOnlyList<(string Raw, string StandIn)> standIns)
    {
        if (text is null)
            return null;

        var marked = text;
        for (var i = 0; i < standIns.Count; i++)
            marked = marked.Replace(standIns[i].Raw, Marker(i), StringComparison.Ordinal);

        var redacted = _redaction.Redact(marked, RedactionCategories.All);
        for (var i = 0; i < standIns.Count; i++)
            redacted = redacted.Replace(Marker(i), standIns[i].StandIn, StringComparison.Ordinal);
        return redacted;
    }

    // Letters inside private-use characters: no redaction rule matches a letter run, and nothing real contains them.
    private static string Marker(int index) => $"{MarkerStart}{ToLetters(index)}{MarkerEnd}";

    private static string ToLetters(int index) =>
        index < 26 ? ((char)('a' + index)).ToString() : ToLetters((index / 26) - 1) + (char)('a' + (index % 26));
}
