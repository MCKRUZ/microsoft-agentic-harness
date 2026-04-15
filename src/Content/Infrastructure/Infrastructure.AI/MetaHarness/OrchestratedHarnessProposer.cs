using System.Text.Json;
using Application.AI.Common.Exceptions;
using Application.AI.Common.Interfaces.MetaHarness;
using Application.Core.CQRS.Agents.RunOrchestratedTask;
using Domain.Common.Config.MetaHarness;
using Domain.Common.MetaHarness;
using MediatR;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Infrastructure.AI.MetaHarness;

/// <summary>
/// Proposer implementation that runs an orchestrated agent via <see cref="IMediator"/>
/// to analyze execution traces and return a structured harness change proposal.
/// </summary>
/// <remarks>
/// Dispatches a <see cref="RunOrchestratedTaskCommand"/> using the <c>harness-proposer</c>
/// skill, then extracts the JSON proposal block from the agent's final synthesis string.
/// Throws <see cref="HarnessProposalParsingException"/> on malformed output so the outer
/// loop can mark the candidate as failed and continue.
/// </remarks>
public sealed class OrchestratedHarnessProposer : IHarnessProposer
{
    private readonly IMediator _mediator;
    private readonly IOptionsMonitor<MetaHarnessConfig> _config;
    private readonly ILogger<OrchestratedHarnessProposer> _logger;

    /// <summary>
    /// Initializes a new instance of <see cref="OrchestratedHarnessProposer"/>.
    /// </summary>
    public OrchestratedHarnessProposer(
        IMediator mediator,
        IOptionsMonitor<MetaHarnessConfig> config,
        ILogger<OrchestratedHarnessProposer> logger)
    {
        _mediator = mediator;
        _config = config;
        _logger = logger;
    }

    /// <inheritdoc/>
    public async Task<HarnessProposal> ProposeAsync(
        HarnessProposerContext context,
        CancellationToken cancellationToken)
    {
        var command = new RunOrchestratedTaskCommand
        {
            OrchestratorName = "harness-proposer",
            TaskDescription = BuildTaskPrompt(context),
            AvailableAgents = BuildAgentList(_config.CurrentValue)
        };

        var result = await _mediator.Send(command, cancellationToken);
        var proposal = ParseProposal(result.FinalSynthesis);

        _logger.LogInformation(
            "Proposer iteration {Iteration}: {SkillCount} skill change(s), {ConfigCount} config change(s), system prompt changed: {HasPromptChange}",
            context.Iteration,
            proposal.ProposedSkillChanges.Count,
            proposal.ProposedConfigChanges.Count,
            proposal.ProposedSystemPromptChange is not null);

        return proposal;
    }

    private static string BuildTaskPrompt(HarnessProposerContext context)
    {
        var priorIds = context.PriorCandidateIds.Count > 0
            ? string.Join(", ", context.PriorCandidateIds.Select(id => id.ToString("N")[..8]))
            : "(none — this is the first iteration)";

        var learningsSection = string.IsNullOrWhiteSpace(context.PriorLearnings)
            ? string.Empty
            : $"""

               ## Prior Learnings (cross-iteration memory)
               The following was recorded from previous iterations. Use it to avoid re-attempting
               failed approaches and to build on what worked:

               {context.PriorLearnings}
               """;

        return $"""
            Optimization run directory: {context.OptimizationRunDirectoryPath}
            Current iteration: {context.Iteration}
            Current candidate ID: {context.CurrentCandidate.CandidateId:N}
            Prior candidate IDs (oldest first, short form): {priorIds}
            {learningsSection}

            Analyze the execution traces in the candidates/ subdirectory and propose targeted
            harness improvements. Respond with a single JSON object only (no markdown fences,
            no preamble text).

            Include a "learnings" key (string) with your observations about what worked, what
            failed, and patterns you noticed this iteration. This will be saved as cross-iteration
            memory for future iterations.
            """;
    }

    private static IReadOnlyList<string> BuildAgentList(MetaHarnessConfig cfg)
    {
        var agents = new List<string> { "file_system", "read_history" };

        if (cfg.EnableShellTool)
            agents.Add("restricted_search");

        return agents;
    }

    private HarnessProposal ParseProposal(string rawOutput)
    {
        var start = rawOutput.IndexOf('{');
        var end = rawOutput.LastIndexOf('}');

        if (start < 0 || end <= start)
            throw new HarnessProposalParsingException(rawOutput);

        var json = rawOutput[start..(end + 1)];

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            throw new HarnessProposalParsingException(rawOutput, inner: ex);
        }

        using (doc)
        {
            var root = doc.RootElement;

            var reasoning = root.TryGetProperty("reasoning", out var r) && r.ValueKind == JsonValueKind.String
                ? r.GetString() ?? ""
                : "";

            var skillChanges = ReadStringDict(root, "proposed_skill_changes");
            var configChanges = ReadStringDict(root, "proposed_config_changes");
            var promptChange = root.TryGetProperty("proposed_system_prompt_change", out var sp)
                               && sp.ValueKind == JsonValueKind.String
                ? sp.GetString()
                : null;

            var learnings = root.TryGetProperty("learnings", out var l) && l.ValueKind == JsonValueKind.String
                ? l.GetString()
                : null;

            return new HarnessProposal
            {
                Reasoning = reasoning,
                ProposedSkillChanges = skillChanges,
                ProposedConfigChanges = configChanges,
                ProposedSystemPromptChange = promptChange,
                Learnings = learnings,
            };
        }
    }

    private static IReadOnlyDictionary<string, string> ReadStringDict(JsonElement root, string key)
    {
        if (!root.TryGetProperty(key, out var prop) || prop.ValueKind != JsonValueKind.Object)
            return new Dictionary<string, string>();

        return prop.EnumerateObject()
            .Where(p => p.Value.ValueKind == JsonValueKind.String)
            .ToDictionary(p => p.Name, p => p.Value.GetString()!);
    }
}
