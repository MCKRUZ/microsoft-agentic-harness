using Application.AI.Common.Interfaces;
using Application.AI.Common.Interfaces.Skills;
using Microsoft.Extensions.DependencyInjection;

namespace Infrastructure.AI.Skills;

/// <summary>
/// Registers the skill discovery trio as one unit.
/// </summary>
public static class SkillDiscoveryServiceCollectionExtensions
{
    /// <summary>
    /// Registers the sandboxed skill file reader, the SKILL.md parser, and the skill registry.
    /// </summary>
    /// <remarks>
    /// <para>
    /// These three must be registered together or not at all: the registry needs the parser, and the
    /// parser cannot be constructed without the reader. A host that registers the trio by hand and
    /// omits the reader does not degrade — the container fails to build at startup.
    /// </para>
    /// <para>
    /// That is not hypothetical. The standalone MCP server composes its own skill services rather
    /// than calling the full AI registration, and adding the reader to only one of the two
    /// composition roots broke its startup outright (issue #247). One entry point removes the
    /// opportunity: a future host calls this and cannot get the set wrong.
    /// </para>
    /// <para>
    /// <b>On the reader specifically.</b> It is deliberately a <em>separate</em> sandbox from
    /// <c>IFileSystemService</c>, which is what the model reaches through the <c>file_system</c>
    /// tool and which can write. Adding the skill roots to that service — the obvious way to put
    /// skill loading behind a sandbox — would let the model rewrite its own <c>SKILL.md</c> files,
    /// <c>allowed-tools</c> list included. See <see cref="ISkillFileReader"/>.
    /// </para>
    /// </remarks>
    /// <param name="services">The service collection to register into.</param>
    /// <returns>The same collection, for chaining.</returns>
    public static IServiceCollection AddSkillDiscovery(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton<ISkillFileReader, SkillFileReader>();
        services.AddSingleton<SkillMetadataParser>();
        services.AddSingleton<ISkillMetadataRegistry, SkillMetadataRegistry>();

        return services;
    }
}
