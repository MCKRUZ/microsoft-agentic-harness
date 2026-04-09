using System.Text;
using Application.AI.Common.Helpers;
using Application.AI.Common.Interfaces.Prompts;
using Domain.AI.Prompts;
using Microsoft.Extensions.Logging;

namespace Infrastructure.AI.Prompts;

/// <summary>
/// Composes the full system prompt from registered section providers with memoization.
/// Cacheable sections are resolved once and reused until explicitly invalidated.
/// Non-cacheable sections are recomputed on every call. Sections exceeding the token
/// budget are dropped from the end (lowest priority = highest number).
/// </summary>
public sealed class MemoizedPromptComposer : ISystemPromptComposer
{
    private readonly IReadOnlyList<IPromptSectionProvider> _providers;
    private readonly IPromptSectionCache _cache;
    private readonly ILogger<MemoizedPromptComposer> _logger;

    /// <summary>
    /// Initializes a new instance of <see cref="MemoizedPromptComposer"/>.
    /// </summary>
    /// <param name="providers">All registered section providers.</param>
    /// <param name="cache">The section cache for memoization.</param>
    /// <param name="logger">Logger instance.</param>
    public MemoizedPromptComposer(
        IEnumerable<IPromptSectionProvider> providers,
        IPromptSectionCache cache,
        ILogger<MemoizedPromptComposer> logger)
    {
        ArgumentNullException.ThrowIfNull(providers);
        ArgumentNullException.ThrowIfNull(cache);
        ArgumentNullException.ThrowIfNull(logger);

        _providers = providers.ToList();
        _cache = cache;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<string> ComposeAsync(
        string agentId,
        int tokenBudget,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(agentId);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(tokenBudget);

        var (cachedSections, providersToCompute) = ResolveCachedSections(agentId);

        var computedSections = await ComputeMissingSectionsAsync(
            agentId, providersToCompute, cancellationToken);

        CacheComputedSections(agentId, computedSections);

        var allSections = cachedSections
            .Concat(computedSections)
            .OrderBy(s => s.Priority)
            .ToList();

        var assembled = AssembleWithinBudget(agentId, allSections, tokenBudget);

        _logger.LogDebug(
            "Composed system prompt for agent {AgentId}: {SectionCount} sections, ~{TokenCount} tokens within {Budget} budget",
            agentId, assembled.Count, assembled.Sum(s => s.EstimatedTokens), tokenBudget);

        return JoinSections(assembled);
    }

    /// <inheritdoc />
    public void InvalidateSection(SystemPromptSectionType type)
    {
        _cache.Invalidate(type);
        _logger.LogDebug("Invalidated cached sections of type {SectionType}", type);
    }

    /// <inheritdoc />
    public void InvalidateAll()
    {
        _cache.InvalidateAll();
        _logger.LogDebug("Invalidated all cached prompt sections");
    }

    private (List<SystemPromptSection> Cached, List<IPromptSectionProvider> ToCompute) ResolveCachedSections(
        string agentId)
    {
        var cached = new List<SystemPromptSection>();
        var toCompute = new List<IPromptSectionProvider>();

        foreach (var provider in _providers)
        {
            if (_cache.TryGet(agentId, provider.SectionType, out var section) && section is not null)
            {
                cached.Add(section);
            }
            else
            {
                toCompute.Add(provider);
            }
        }

        return (cached, toCompute);
    }

    private static async Task<List<SystemPromptSection>> ComputeMissingSectionsAsync(
        string agentId,
        List<IPromptSectionProvider> providers,
        CancellationToken cancellationToken)
    {
        if (providers.Count == 0)
            return [];

        var tasks = providers.Select(p => p.GetSectionAsync(agentId, cancellationToken));
        var results = await Task.WhenAll(tasks);

        return results
            .Where(s => s is not null)
            .Select(s => EnsureTokenEstimate(s!))
            .ToList();
    }

    private void CacheComputedSections(string agentId, List<SystemPromptSection> sections)
    {
        foreach (var section in sections.Where(s => s.IsCacheable))
        {
            _cache.Set(agentId, section);
        }
    }

    private List<SystemPromptSection> AssembleWithinBudget(
        string agentId,
        List<SystemPromptSection> sortedSections,
        int tokenBudget)
    {
        var included = new List<SystemPromptSection>();
        var runningTokens = 0;

        foreach (var section in sortedSections)
        {
            var sectionTokens = section.EstimatedTokens;
            if (runningTokens + sectionTokens > tokenBudget)
            {
                _logger.LogInformation(
                    "Dropping section {SectionName} ({SectionType}) for agent {AgentId}: " +
                    "would exceed budget ({Running} + {Section} > {Budget})",
                    section.Name, section.Type, agentId,
                    runningTokens, sectionTokens, tokenBudget);
                continue;
            }

            included.Add(section);
            runningTokens += sectionTokens;
        }

        return included;
    }

    private static string JoinSections(List<SystemPromptSection> sections)
    {
        if (sections.Count == 0)
            return string.Empty;

        var builder = new StringBuilder();
        for (var i = 0; i < sections.Count; i++)
        {
            if (i > 0)
                builder.Append("\n\n");
            builder.Append(sections[i].Content);
        }

        return builder.ToString();
    }

    private static SystemPromptSection EnsureTokenEstimate(SystemPromptSection section)
    {
        if (section.EstimatedTokens > 0)
            return section;

        var estimated = TokenEstimationHelper.EstimateTokens(section.Content);
        return section with { EstimatedTokens = estimated };
    }
}
