namespace Domain.AI.Skills;

/// <summary>
/// Cache statistics for the skill loader service.
/// </summary>
public record SkillCacheStatistics(
	int LoadedSkillCount,
	long CacheHits,
	long CacheMisses,
	DateTime? LastClearTime);
