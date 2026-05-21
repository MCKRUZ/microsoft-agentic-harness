namespace Domain.AI.Constants;

/// <summary>
/// Well-known skill source identifiers indicating where a skill was loaded from.
/// </summary>
public static class SkillSources
{
    public const string Bundled = "bundled";
    public const string FileSystem = "filesystem";
    public const string Mcp = "mcp";
    public const string Plugin = "plugin";
}
