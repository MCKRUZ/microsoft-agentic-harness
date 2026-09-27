namespace Application.Core.CQRS.Skills.RefreshSkillRegistry;

/// <summary>Shared validation constants for skill registry operator commands.</summary>
public static class SkillRegistryValidationRules
{
    /// <summary>Upper bound on <see cref="RefreshSkillRegistryCommand.CallerId"/>'s length.</summary>
    public const int MaxCallerIdLength = 256;
}
