using System.Text;
using Domain.AI.Skills;
using Microsoft.Agents.AI;
using Microsoft.Extensions.Logging;

namespace Application.AI.Common.Helpers;

/// <summary>
/// A skill that will be registered with the framework's skills provider, paired with the harness skill id
/// it was built from so the caller knows whose body it may omit from the static system prompt.
/// </summary>
/// <param name="SkillId">
/// The <see cref="SkillDefinition.Id"/> this was built from. Its instructions are served on demand through
/// <c>load_skill</c> and may therefore be left out of the prompt.
/// </param>
/// <param name="Skill">The framework skill handed to <c>AgentSkillsProviderBuilder.UseSkills</c>.</param>
public sealed record DisclosableSkill(string SkillId, AgentInlineSkill Skill);

/// <summary>
/// Builds the framework skills that back progressive (Tier 2/3) disclosure, one per skill actually
/// assigned to the agent.
/// </summary>
/// <remarks>
/// <para>
/// Registering skills explicitly — rather than pointing the framework at a directory and letting it
/// discover whatever it finds — is what confines <c>load_skill</c> to the agent's own skills. Directory
/// discovery advertises every skill installed on the host, including ones the agent was never granted.
/// </para>
/// <para>
/// <b>It also makes disclosure coverage exact rather than predicted.</b> The previous approach re-derived
/// the framework loader's rules to guess which skills it would load, and a guess that is wrong in the
/// permissive direction drops a skill's instructions from the prompt with nothing serving them on demand —
/// the agent then runs with no instructions and no error. Here the returned list <em>is</em> the registered
/// set, so the prompt and the provider cannot disagree. Every rejection below therefore fails in the safe
/// direction: the skill is simply absent from the result and its body stays in the prompt.
/// </para>
/// </remarks>
public static class DisclosableSkillFactory
{
    /// <summary>
    /// Creates a framework skill for each of <paramref name="skills"/> that can back on-demand disclosure.
    /// </summary>
    /// <param name="skills">The skills composing this agent.</param>
    /// <param name="logger">Receives a Debug record of each skill that could not be registered, and why.</param>
    /// <returns>
    /// The registrable skills, in input order. A skill is omitted — and so keeps its body in the static
    /// prompt — when it has no id, has no instructions to serve, is rejected by the framework's frontmatter
    /// validation, or would be shadowed by an earlier skill claiming the same name.
    /// </returns>
    public static IReadOnlyList<DisclosableSkill> Create(
        IReadOnlyList<SkillDefinition> skills,
        ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(skills);
        ArgumentNullException.ThrowIfNull(logger);

        var created = new List<DisclosableSkill>(skills.Count);
        var claimedNames = new HashSet<string>(StringComparer.Ordinal);

        foreach (var skill in skills)
        {
            // No id means the caller cannot match this back to a prompt section, and no instructions means
            // there is nothing for load_skill to serve — in both cases registration would buy nothing.
            if (string.IsNullOrWhiteSpace(skill.Id) || string.IsNullOrWhiteSpace(skill.Instructions))
                continue;

            AgentInlineSkill inline;
            try
            {
                inline = new AgentInlineSkill(skill.Name, skill.Description, skill.Instructions!);
            }
            catch (ArgumentException ex)
            {
                // AgentSkillFrontmatter validates the name (kebab-case, <= 64 chars) and description
                // (required, <= 1024 chars) and throws on anything it rejects. Letting the throw decide
                // keeps this in step with the framework's own rules instead of restating them here.
                logger.LogDebug(
                    ex,
                    "Skill {SkillId} cannot be disclosed on demand — the framework rejected its " +
                    "name/description, so its instructions stay in the static prompt",
                    skill.Id);
                continue;
            }

            // Ordinal, matching the provider's own load_skill lookup. A second skill claiming a name
            // already taken is unreachable through that lookup, so it must keep its body in the prompt.
            // (Deliberately a different comparison from the caller's skill-id set, which is
            // case-insensitive: this one has to mirror the framework, that one mirrors skill ids.)
            if (!claimedNames.Add(inline.Frontmatter.Name))
            {
                logger.LogDebug(
                    "Skill {SkillId} cannot be disclosed on demand — an earlier skill already claims the " +
                    "name {SkillName}, so its instructions stay in the static prompt",
                    skill.Id,
                    inline.Frontmatter.Name);
                continue;
            }

            AddResources(inline, skill);
            created.Add(new DisclosableSkill(skill.Id, inline));
        }

        return created;
    }

    /// <summary>
    /// Registers the skill's supporting files so <c>read_skill_resource</c> can serve them (Tier 3), and so
    /// the provider advertises them in the skill body's <c>available_resources</c> block.
    /// </summary>
    /// <remarks>
    /// Each file is registered as a deferred read rather than loaded here: a skill's references are exactly
    /// the bulk that progressive disclosure exists to keep out of the prompt, so reading them at agent
    /// construction would reintroduce the cost this design removes. Scripts are deliberately not
    /// registered — the harness runs skill scripts through its own sandboxed tool chain, not through the
    /// framework's runner.
    /// </remarks>
    private static void AddResources(AgentInlineSkill inline, SkillDefinition skill)
    {
        var claimed = new HashSet<string>(StringComparer.Ordinal);

        foreach (var resource in skill.References.Concat(skill.Templates).Concat(skill.Assets))
        {
            var name = resource.RelativePath;
            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(resource.FilePath))
                continue;

            // The provider resolves a resource by the first name match, so a duplicate would be dead
            // weight in the advertised list and could never be read.
            if (!claimed.Add(name))
                continue;

            // Hoisting the path is load-bearing, not stylistic. The lambda outlives this loop — it is held
            // by the skill, which is held by the provider, which is attached to an agent cached on a
            // sliding expiry. Closing over `resource` instead would pin every SkillResource (and the
            // Content each caches once read) for that whole lifetime; closing over one string does not.
            var path = resource.FilePath;
            inline.AddResource(name, () => ReadResourceAsync(path));
        }
    }

    /// <summary>
    /// Reads a resource file as UTF-8, matching how the framework's own file-backed resources are read.
    /// </summary>
    /// <remarks>
    /// Returns the task directly rather than awaiting it — nothing follows the read, so an async state
    /// machine would be pure overhead on every resource the model opens.
    /// </remarks>
    private static Task<string> ReadResourceAsync(string path) =>
        File.ReadAllTextAsync(path, Encoding.UTF8);
}
