using Application.AI.Common.Interfaces.SkillTraining;
using Application.AI.Common.Interfaces.Tools;
using Application.AI.Common.Services.SkillTraining;
using Application.Common.Interfaces.Common;
using Domain.Common.Config;
using Domain.Common.Config.AI.SkillTraining;
using Infrastructure.AI.SkillTraining;
using Infrastructure.AI.Tools;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Infrastructure.AI;

public static partial class DependencyInjection
{
    /// <summary>
    /// Registers the governed skill-training proposer (#843): the evidence root, the read-only
    /// <c>evidence_reader</c> tool confined to it, and <see cref="AgenticPatchProposer"/> as the loop's
    /// <see cref="IPatchProposer"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The evidence root is resolved once, here, and shared by the writer and the reader so a later
    /// configuration reload cannot leave them on different directories. The reader's file service is a
    /// second, separate instance whose only allowed base path is that root: it is deliberately not the
    /// general <c>IFileSystemService</c>, which is allowed to reach the workspace.
    /// </para>
    /// <para>
    /// The application layer registers a fail-fast <c>NotConfiguredPatchProposer</c> with
    /// <c>TryAddSingleton</c>. If this runs after it, the default is replaced; if this runs first, the
    /// application layer's <c>TryAdd</c> leaves it alone. A proposer a consumer registered themselves is
    /// never replaced. The rollout runner stays on its fail-fast default until a real one is registered.
    /// </para>
    /// </remarks>
    private static void RegisterSkillTrainingServices(IServiceCollection services, AppConfig appConfig)
    {
        var configured = appConfig.AI.SkillTraining.EvidenceRoot;
        var evidenceRoot = new SkillTrainingEvidenceRoot(Path.GetFullPath(
            string.IsNullOrWhiteSpace(configured) ? new SkillTrainingConfig().EvidenceRoot : configured));

        services.AddSingleton(evidenceRoot);

        services.AddKeyedSingleton<ITool>(EvidenceReaderTool.ToolName, (sp, _) =>
        {
            sp.GetRequiredService<IOwnerOnlyDirectoryCreator>().Create(evidenceRoot.Path);
            return new EvidenceReaderTool(
                new FileSystemService(sp.GetRequiredService<ILogger<FileSystemService>>(), [evidenceRoot.Path]));
        });

        ReplaceFailFastPatchProposer(services);
    }

    /// <summary>
    /// Makes <see cref="AgenticPatchProposer"/> the <see cref="IPatchProposer"/> unless a consumer has
    /// already registered their own. Scoped, not singleton: it dispatches through <c>IMediator</c>, which
    /// is scoped, and a singleton holding it would capture one request's scope for the life of the process.
    /// </summary>
    private static void ReplaceFailFastPatchProposer(IServiceCollection services)
    {
        var existing = services.Where(d => d.ServiceType == typeof(IPatchProposer)).ToList();
        if (existing.Any(d => d.ImplementationType != typeof(NotConfiguredPatchProposer)))
            return;

        foreach (var descriptor in existing)
            services.Remove(descriptor);

        services.AddScoped<IPatchProposer, AgenticPatchProposer>();
    }
}
