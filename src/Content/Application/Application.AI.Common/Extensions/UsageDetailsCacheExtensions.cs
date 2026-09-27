using Microsoft.Extensions.AI;

namespace Application.AI.Common.Extensions;

/// <summary>
/// Reads prompt-cache token counts from <see cref="UsageDetails.AdditionalCounts"/> in a way that
/// tolerates every known <see cref="IChatClient"/> adapter's key-naming convention, instead of a
/// single hardcoded spelling.
/// </summary>
/// <remarks>
/// Different adapters populate this dictionary under different conventions: some pass through the
/// provider API's own wire-format field name (snake_case), but Anthropic.SDK's <c>IChatClient</c>
/// bridge builds its keys from <c>nameof(Usage.CacheReadInputTokens)</c> — the literal C# property
/// name, PascalCase — not the wire name (issue #592). Comparing case- and separator-insensitively
/// means any adapter's rendering of the same logical field matches, not just the specific spellings
/// seen so far — closing the class of naming-convention mismatch rather than two named instances of
/// it. Shared by every consumer of <see cref="UsageDetails"/> cache counts so a fix here reaches all
/// of them, rather than each maintaining its own copy that can drift out of sync (as
/// <c>ObservabilityMiddleware</c> and <c>CacheStatsEnrichingChatClient</c> did before this existed).
/// </remarks>
public static class UsageDetailsCacheExtensions
{
    /// <summary>The number of prompt-cache tokens read on this call, or 0 if none were reported.</summary>
    public static long GetCacheReadTokens(this UsageDetails usage) =>
        GetNormalizedCount(usage, "cachereadinputtokens");

    /// <summary>The number of prompt-cache tokens written on this call, or 0 if none were reported.</summary>
    public static long GetCacheCreationTokens(this UsageDetails usage) =>
        GetNormalizedCount(usage, "cachecreationinputtokens");

    private static long GetNormalizedCount(UsageDetails usage, string normalizedKey)
    {
        if (usage.AdditionalCounts is null)
            return 0;

        foreach (var (key, value) in usage.AdditionalCounts)
        {
            if (Normalize(key) == normalizedKey)
                return value;
        }

        return 0;
    }

    private static string Normalize(string key) =>
        key.Replace("_", "").Replace("-", "").ToLowerInvariant();
}
