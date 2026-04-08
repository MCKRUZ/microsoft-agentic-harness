using System.Text;
using Application.AI.Common.Helpers;
using Application.AI.Common.Interfaces.Context;
using Application.AI.Common.Models.Context;
using Domain.AI.Skills;
using Microsoft.Extensions.Logging;

namespace Application.AI.Common.Services.Context;

/// <summary>
/// Assembles context from all three progressive disclosure tiers for a skill,
/// reading content from the skill's loaded resources and enforcing per-tier token budgets.
/// </summary>
/// <remarks>
/// <para><b>Tier defaults:</b></para>
/// <list type="bullet">
///   <item><description>Tier 1: 3,000 tokens (organizational/strategic context)</description></item>
///   <item><description>Tier 2: 8,000 tokens (domain/activity-specific context)</description></item>
///   <item><description>Tier 3: config only — no tokens loaded, paths exposed for on-demand access</description></item>
/// </list>
/// </remarks>
public sealed class TieredContextAssembler : ITieredContextAssembler
{
    private const int DefaultTier1MaxTokens = 3000;
    private const int DefaultTier2MaxTokens = 8000;

    private readonly ILogger<TieredContextAssembler> _logger;
    private readonly IContextBudgetTracker _budgetTracker;

    /// <summary>
    /// Initializes a new instance of the <see cref="TieredContextAssembler"/> class.
    /// </summary>
    /// <param name="logger">Logger for tier loading diagnostics.</param>
    /// <param name="budgetTracker">Budget tracker for recording per-tier allocations.</param>
    public TieredContextAssembler(
        ILogger<TieredContextAssembler> logger,
        IContextBudgetTracker budgetTracker)
    {
        _logger = logger;
        _budgetTracker = budgetTracker;
    }

    /// <inheritdoc />
    public Task<AssembledContext> AssembleContextAsync(
        SkillDefinition skill,
        string? basePath = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(skill);

        if (skill.ContextLoading is null || !skill.ContextLoading.HasConfiguration)
        {
            _logger.LogDebug("Skill {SkillId} has no context loading configuration", skill.Id);
            return Task.FromResult(BuildEmptyContext());
        }

        var agentName = skill.Name;
        var tier1 = LoadTier1(skill, agentName);
        var tier2 = LoadTier2(skill, agentName);
        var tier3 = BuildTier3Config(skill);
        var totalTokens = tier1.TotalTokens + tier2.TotalTokens;
        var formattedPrompt = FormatPromptSection(tier1, tier2, tier3);

        _logger.LogInformation(
            "Assembled context for skill {SkillId}: Tier1={Tier1Tokens}, Tier2={Tier2Tokens}, Total={TotalTokens}",
            skill.Id, tier1.TotalTokens, tier2.TotalTokens, totalTokens);

        var result = new AssembledContext(tier1, tier2, tier3, totalTokens, formattedPrompt);
        return Task.FromResult(result);
    }

    private Tier1LoadedContext LoadTier1(SkillDefinition skill, string agentName)
    {
        var tierConfig = skill.ContextLoading!.Tier1;
        var maxTokens = tierConfig?.MaxTokens ?? DefaultTier1MaxTokens;
        var files = new List<LoadedContextFile>();
        var totalTokens = 0;

        if (tierConfig?.Files is { Count: > 0 })
        {
            foreach (var filePath in tierConfig.Files)
            {
                var resource = FindResource(skill, filePath);
                if (resource is null || !resource.IsLoaded || string.IsNullOrEmpty(resource.Content))
                {
                    _logger.LogDebug("Tier 1 file {FilePath} not found or not loaded for skill {SkillId}",
                        filePath, skill.Id);
                    continue;
                }

                var originalTokens = TokenEstimationHelper.EstimateTokens(resource.Content);
                var remainingBudget = maxTokens - totalTokens;

                if (remainingBudget <= 0)
                {
                    _logger.LogWarning("Tier 1 budget exhausted for skill {SkillId}, skipping {FilePath}",
                        skill.Id, filePath);
                    break;
                }

                string content;
                bool isTruncated;
                int tokenCount;

                if (originalTokens <= remainingBudget)
                {
                    content = resource.Content;
                    isTruncated = false;
                    tokenCount = originalTokens;
                }
                else
                {
                    content = TokenEstimationHelper.TruncateToTokenBudget(resource.Content, remainingBudget);
                    isTruncated = true;
                    tokenCount = TokenEstimationHelper.EstimateTokens(content);
                }

                files.Add(new LoadedContextFile(
                    resource.FileName,
                    filePath,
                    content,
                    tokenCount,
                    isTruncated,
                    isTruncated ? originalTokens : null));

                totalTokens += tokenCount;
            }
        }

        _budgetTracker.RecordAllocation(agentName, "tier1_context", totalTokens);
        return new Tier1LoadedContext(files, totalTokens, maxTokens);
    }

    private Tier2LoadedContext LoadTier2(SkillDefinition skill, string agentName)
    {
        var tierConfig = skill.ContextLoading!.Tier2;
        var maxTokens = tierConfig?.MaxTokens ?? DefaultTier2MaxTokens;
        var files = new List<LoadedContextFile>();
        var truncatedFiles = new List<TruncatedArtifactInfo>();
        var totalTokens = 0;

        if (tierConfig?.Files is { Count: > 0 })
        {
            foreach (var filePath in tierConfig.Files)
            {
                var resource = FindResource(skill, filePath);
                if (resource is null || !resource.IsLoaded || string.IsNullOrEmpty(resource.Content))
                {
                    _logger.LogDebug("Tier 2 file {FilePath} not found or not loaded for skill {SkillId}",
                        filePath, skill.Id);
                    continue;
                }

                var originalTokens = TokenEstimationHelper.EstimateTokens(resource.Content);
                var remainingBudget = maxTokens - totalTokens;

                if (remainingBudget <= 0)
                {
                    truncatedFiles.Add(new TruncatedArtifactInfo(
                        resource.FileName, originalTokens, null, TruncationReason.Skipped));
                    _logger.LogDebug("Tier 2 budget exhausted, skipping {FilePath} ({Tokens} tokens)",
                        filePath, originalTokens);
                    continue;
                }

                if (originalTokens <= remainingBudget)
                {
                    files.Add(new LoadedContextFile(
                        resource.FileName, filePath, resource.Content, originalTokens));
                    totalTokens += originalTokens;
                }
                else
                {
                    var truncatedContent = TokenEstimationHelper.TruncateToTokenBudget(
                        resource.Content, remainingBudget);
                    var includedTokens = TokenEstimationHelper.EstimateTokens(truncatedContent);

                    files.Add(new LoadedContextFile(
                        resource.FileName, filePath, truncatedContent, includedTokens,
                        IsTruncated: true, OriginalTokenCount: originalTokens));

                    truncatedFiles.Add(new TruncatedArtifactInfo(
                        resource.FileName, originalTokens, includedTokens, TruncationReason.Truncated));

                    totalTokens += includedTokens;
                }
            }
        }

        _budgetTracker.RecordAllocation(agentName, "tier2_context", totalTokens);
        return new Tier2LoadedContext(files, totalTokens, maxTokens, truncatedFiles);
    }

    private static Tier3AccessConfig BuildTier3Config(SkillDefinition skill)
    {
        var tierConfig = skill.ContextLoading!.Tier3;

        var lookupPaths = tierConfig?.LookupPaths is { Count: > 0 }
            ? tierConfig.LookupPaths.ToList().AsReadOnly()
            : (IReadOnlyList<string>)[];

        var fallbackPrompt = tierConfig?.FallbackPrompt;

        return new Tier3AccessConfig(lookupPaths, fallbackPrompt);
    }

    private static SkillResource? FindResource(SkillDefinition skill, string filePath)
    {
        var fileName = Path.GetFileName(filePath);

        // Search Templates, then References, then Assets
        var match = skill.Templates.FirstOrDefault(r =>
            r.FileName.Equals(fileName, StringComparison.OrdinalIgnoreCase) ||
            r.RelativePath?.EndsWith(filePath, StringComparison.OrdinalIgnoreCase) == true);

        match ??= skill.References.FirstOrDefault(r =>
            r.FileName.Equals(fileName, StringComparison.OrdinalIgnoreCase) ||
            r.RelativePath?.EndsWith(filePath, StringComparison.OrdinalIgnoreCase) == true);

        match ??= skill.Assets.FirstOrDefault(r =>
            r.FileName.Equals(fileName, StringComparison.OrdinalIgnoreCase) ||
            r.RelativePath?.EndsWith(filePath, StringComparison.OrdinalIgnoreCase) == true);

        return match;
    }

    private static string FormatPromptSection(
        Tier1LoadedContext tier1,
        Tier2LoadedContext tier2,
        Tier3AccessConfig tier3)
    {
        var sb = new StringBuilder();

        if (tier1.Files.Count > 0)
        {
            sb.AppendLine("## Organizational Context");
            foreach (var file in tier1.Files)
            {
                sb.AppendLine($"### {file.Name}");
                sb.AppendLine(file.Content);
                sb.AppendLine();
            }
        }

        if (tier2.Files.Count > 0)
        {
            sb.AppendLine("## Domain Context");
            foreach (var file in tier2.Files)
            {
                sb.AppendLine($"### {file.Name}");
                sb.AppendLine(file.Content);
                sb.AppendLine();
            }
        }

        if (tier3.AllowedLookupPaths.Count > 0 || tier3.FallbackPrompt is not null)
        {
            sb.AppendLine("## On-Demand Resources");
            if (tier3.AllowedLookupPaths.Count > 0)
            {
                sb.AppendLine("Available lookup paths:");
                foreach (var path in tier3.AllowedLookupPaths)
                    sb.AppendLine($"- {path}");
            }
            if (tier3.FallbackPrompt is not null)
            {
                sb.AppendLine();
                sb.AppendLine(tier3.FallbackPrompt);
            }
        }

        return sb.ToString().TrimEnd();
    }

    private static AssembledContext BuildEmptyContext()
    {
        var emptyTier1 = new Tier1LoadedContext([], 0, DefaultTier1MaxTokens);
        var emptyTier2 = new Tier2LoadedContext([], 0, DefaultTier2MaxTokens, []);
        var emptyTier3 = new Tier3AccessConfig([], null);
        return new AssembledContext(emptyTier1, emptyTier2, emptyTier3, 0, string.Empty);
    }
}
