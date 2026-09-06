using Application.AI.Common.Interfaces;
using Application.AI.Common.Interfaces.Skills;
using Application.AI.Common.Skills;
using Domain.AI.Skills;
using FluentValidation;
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
    /// <para>
    /// <b>On the egress validator (#531).</b> <see cref="SkillMetadataParser"/> also needs
    /// <c>IValidator&lt;EgressManifest&gt;</c>, registered here as a singleton rather than left to
    /// <c>AddValidatorsFromAssembly</c>'s default scoped lifetime — <see cref="EgressManifestValidator"/>
    /// is stateless (a pure FluentValidation rule tree with no mutable state), so a singleton is safe,
    /// and it must be one: <see cref="SkillMetadataParser"/> is itself a singleton, and a singleton
    /// cannot consume a scoped service (<c>ValidateOnBuild</c> catches this — a captive dependency —
    /// the moment any host builds its container). Registering it here, not only via the assembly scan,
    /// closes the same gap #247 already fixed once for the reader: a caller of just this method (the
    /// standalone MCP server, per this method's own remarks above) never runs
    /// <c>AddApplicationAIDependencies</c>'s <c>AddValidatorsFromAssembly</c> at all, so without this
    /// line <see cref="SkillMetadataParser"/> would fail to construct there outright, not merely with
    /// the wrong lifetime.
    /// </para>
    /// <para>
    /// <b>Composition order still matters for a caller that also runs the assembly scan.</b> .NET DI
    /// resolves the LAST registration of a service type for direct injection, so this singleton wins
    /// only because <c>AddGlobalProjectDependencies</c> calls <c>AddApplicationAIDependencies</c>
    /// (line ~532, the scoped scan) before <c>AddInfrastructureAIDependencies</c> → this method
    /// (line ~543). A future reordering of those two calls would silently restore the captive-dependency
    /// failure this fix closes — <c>ValidateOnBuild</c> would catch it, but only at container-build
    /// time, not at compile time. Keep this registration downstream of any consumer's own
    /// <c>AddValidatorsFromAssembly</c> call over an assembly that scans <see cref="EgressManifestValidator"/>.
    /// </para>
    /// </remarks>
    /// <param name="services">The service collection to register into.</param>
    /// <returns>The same collection, for chaining.</returns>
    public static IServiceCollection AddSkillDiscovery(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton<ISkillFileReader, SkillFileReader>();
        services.AddSingleton<IValidator<EgressManifest>, EgressManifestValidator>();
        services.AddSingleton<SkillMetadataParser>();
        services.AddSingleton<ISkillMetadataRegistry, SkillMetadataRegistry>();

        return services;
    }
}
