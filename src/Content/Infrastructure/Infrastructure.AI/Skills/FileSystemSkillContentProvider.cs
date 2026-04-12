using Application.AI.Common.Interfaces.Skills;

namespace Infrastructure.AI.Skills;

/// <summary>
/// Reads skill content from the local filesystem.
/// Returns null when the file does not exist.
/// </summary>
public sealed class FileSystemSkillContentProvider : ISkillContentProvider
{
    /// <inheritdoc />
    /// <remarks>
    /// Trust boundary: <paramref name="skillPath"/> is expected to originate from
    /// <see cref="Domain.AI.Skills.SkillDefinition.FilePath"/> as set by <c>SkillMetadataParser</c>
    /// during filesystem discovery. Callers are trusted internal components.
    /// </remarks>
    public async Task<string?> GetSkillContentAsync(string skillPath, CancellationToken cancellationToken = default)
    {
        try
        {
            return await File.ReadAllTextAsync(skillPath, cancellationToken);
        }
        catch (FileNotFoundException)
        {
            return null;
        }
    }
}
