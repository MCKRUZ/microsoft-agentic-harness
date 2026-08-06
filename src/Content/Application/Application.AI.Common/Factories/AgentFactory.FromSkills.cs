using Application.AI.Common.Interfaces;
using Application.AI.Common.Interfaces.Skills;
using Domain.AI.Skills;
using Microsoft.Agents.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Application.AI.Common.Factories;

// Skill-driven half of AgentFactory: the entry points that start from skill ids rather than from an
// already-built execution context. Resolving ids to definitions (agent-owned skills shadowing the
// global registry), validating that their declared prerequisites form a valid ordering, and the batch
// discovery overloads that build one agent per skill, category, or tag.
//
// These sit apart from the primary partial because they all end at the same place — handing a resolved
// AgentExecutionContext to CreateAgentAsync — and everything above that hand-off is about skills, not
// about agents or providers.
//
// Deliberately a plain comment, not an XML doc — see AgentFactory.ChatClient.cs.
public partial class AgentFactory
{
    /// <inheritdoc />
    public Task<AIAgent> CreateAgentFromSkillAsync(string skillId, CancellationToken cancellationToken = default)
        => CreateAgentFromSkillsAsync([skillId], new SkillAgentOptions(), cancellationToken);

    /// <inheritdoc />
    public Task<AIAgent> CreateAgentFromSkillAsync(string skillId, SkillAgentOptions options, CancellationToken cancellationToken = default)
        => CreateAgentFromSkillsAsync([skillId], options, cancellationToken);

    /// <inheritdoc />
    public async Task<AIAgent> CreateAgentFromSkillsAsync(
        IReadOnlyList<string> skillIds,
        SkillAgentOptions options,
        CancellationToken cancellationToken = default)
    {
        var built = await CreateAgentWithContextFromSkillsAsync(skillIds, options, cancellationToken);
        return built.Agent;
    }

    /// <inheritdoc />
    public async Task<AgentBuildResult> CreateAgentWithContextFromSkillsAsync(
        IReadOnlyList<string> skillIds,
        SkillAgentOptions options,
        CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Creating agent from {Count} skill(s): {SkillIds}",
            skillIds.Count, string.Join(", ", skillIds));

        // Resolve each skill id, preferring the owning agent's own nested skills (its
        // <agentDir>/skills/) over the global registry so an agent-owned skill can shadow — but never
        // pollute — the shared pool. The owned store is optional: hosts that do not discover agents
        // (for example the standalone MCP server) simply resolve everything from the global registry.
        var ownedSkills = _serviceProvider.GetService<IAgentOwnedSkillStore>();
        var skills = new List<SkillDefinition>();
        foreach (var id in skillIds)
        {
            var skill = ResolveSkill(id, options.OwningAgentId, ownedSkills)
                ?? throw new InvalidOperationException(
                    $"Skill '{id}' not found. Ensure it exists in the configured skill paths " +
                    "or the owning agent's skills/ directory.");
            skills.Add(skill);
        }

        ValidatePrerequisites(skills);

        var agentContext = await _agentContextFactory.MapToAgentContextAsync(skills, options);
        var agent = await CreateAgentAsync(agentContext, cancellationToken);

        _logger.LogInformation("Created agent {AgentName} from {Count} skill(s): {SkillIds}",
            agentContext.Name, skillIds.Count, string.Join(", ", skillIds));
        return new AgentBuildResult(agent, agentContext);
    }

    /// <summary>
    /// Resolves a skill id to its definition, checking the owning agent's own nested skills first (when
    /// an <paramref name="owningAgentId"/> and an <paramref name="ownedSkills"/> store are available)
    /// and falling back to the global registry. Returns null when neither source knows the id.
    /// </summary>
    private SkillDefinition? ResolveSkill(
        string id,
        string? owningAgentId,
        IAgentOwnedSkillStore? ownedSkills)
    {
        if (owningAgentId is not null && ownedSkills is not null)
        {
            var owned = ownedSkills.TryGet(owningAgentId, id);
            if (owned is not null)
                return owned;
        }

        return _skillRegistry.TryGet(id);
    }

    /// <inheritdoc />
    public async Task<IDictionary<string, AIAgent>> CreateAgentsFromSkillsAsync(
        IEnumerable<string> skillIds, SkillAgentOptions? options = null, CancellationToken cancellationToken = default)
    {
        var agents = new Dictionary<string, AIAgent>();
        options ??= new SkillAgentOptions();

        foreach (var skillId in skillIds)
        {
            try
            {
                var agent = await CreateAgentFromSkillAsync(skillId, options, cancellationToken);
                agents[skillId] = agent;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to create agent for skill: {SkillId}", skillId);
            }
        }

        return agents;
    }

    /// <inheritdoc />
    public async Task<IDictionary<string, AIAgent>> CreateAgentsByCategoryAsync(
        string category, SkillAgentOptions? options = null, CancellationToken cancellationToken = default)
    {
        var skills = _skillRegistry.GetByCategory(category);
        return await CreateAgentsFromSkillsAsync(skills.Select(s => s.Id), options, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<IDictionary<string, AIAgent>> CreateAgentsByTagsAsync(
        IEnumerable<string> tags, SkillAgentOptions? options = null, CancellationToken cancellationToken = default)
    {
        var skills = _skillRegistry.GetByTags(tags);
        return await CreateAgentsFromSkillsAsync(skills.Select(s => s.Id), options, cancellationToken);
    }

    /// <summary>
    /// Validates that all prerequisite references are valid and contain no cycles.
    /// Uses Kahn's algorithm for topological sort — if the sort doesn't include all skills,
    /// a cycle exists.
    /// </summary>
    private static void ValidatePrerequisites(IReadOnlyList<SkillDefinition> skills)
    {
        // Skip validation when no prerequisites exist
        if (!skills.Any(s => s.HasPrerequisites))
            return;

        var skillIds = new HashSet<string>(skills.Select(s => s.Id), StringComparer.OrdinalIgnoreCase);

        // Check all referenced prerequisites exist in the skill list
        foreach (var skill in skills)
        {
            foreach (var prereq in skill.Prerequisites)
            {
                if (!skillIds.Contains(prereq))
                    throw new InvalidOperationException(
                        $"Skill '{skill.Id}' declares prerequisite '{prereq}' which is not in the agent's skill list. " +
                        $"Available skills: [{string.Join(", ", skillIds)}]");
            }
        }

        // Topological sort to detect cycles (Kahn's algorithm)
        var inDegree = skills.ToDictionary(s => s.Id, _ => 0, StringComparer.OrdinalIgnoreCase);
        var adj = skills.ToDictionary(s => s.Id, _ => new List<string>(), StringComparer.OrdinalIgnoreCase);

        foreach (var skill in skills)
        {
            foreach (var prereq in skill.Prerequisites)
            {
                adj[prereq].Add(skill.Id);
                inDegree[skill.Id]++;
            }
        }

        var queue = new Queue<string>(inDegree.Where(kv => kv.Value == 0).Select(kv => kv.Key));
        var sorted = 0;

        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            sorted++;
            foreach (var dependent in adj[current])
            {
                inDegree[dependent]--;
                if (inDegree[dependent] == 0)
                    queue.Enqueue(dependent);
            }
        }

        if (sorted != skills.Count)
        {
            var cycleSkills = inDegree.Where(kv => kv.Value > 0).Select(kv => kv.Key);
            throw new InvalidOperationException(
                $"Prerequisite cycle detected among skills: [{string.Join(", ", cycleSkills)}]. " +
                "Remove or restructure prerequisites to eliminate the cycle.");
        }
    }
}
