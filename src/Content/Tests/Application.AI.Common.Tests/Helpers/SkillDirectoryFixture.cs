namespace Application.AI.Common.Tests.Helpers;

/// <summary>
/// Creates disposable temporary skill directories containing real <c>SKILL.md</c> manifests, for tests
/// that need skills genuinely present on disk.
/// </summary>
/// <remarks>
/// <see cref="CreateSkill"/> writes a manifest the framework's file loader would accept — frontmatter
/// <c>name</c> ordinal-equal to the containing directory's name, description present. That fidelity is what
/// makes a test asserting such a skill is <em>not</em> reachable meaningful: a manifest the loader would
/// have rejected anyway proves nothing about whether the harness is confining disclosure correctly.
/// </remarks>
public sealed class SkillDirectoryFixture : IDisposable
{
    /// <summary>
    /// Creates a fixture rooted at a fresh temporary directory.
    /// </summary>
    /// <param name="label">Short prefix for the temp directory, to make stray folders identifiable.</param>
    public SkillDirectoryFixture(string label)
    {
        Root = Path.Combine(Path.GetTempPath(), $"{label}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Root);
    }

    /// <summary>The fixture's root directory. Use as a configured skill root.</summary>
    public string Root { get; }

    /// <summary>
    /// Creates a skill directory at <paramref name="relativePath"/> whose manifest the framework loader
    /// accepts, and returns its full path.
    /// </summary>
    /// <param name="relativePath">Path relative to <see cref="Root"/>; its last segment is the skill name.</param>
    /// <param name="body">Markdown body placed after the frontmatter.</param>
    /// <param name="description">Frontmatter description. Must be non-empty or the loader rejects the skill.</param>
    public string CreateSkill(string relativePath, string body = "body", string description = "A skill.")
    {
        var directory = Path.Combine(Root, relativePath);
        Directory.CreateDirectory(directory);

        WriteManifest(
            directory,
            $"name: {Path.GetFileName(directory)}\ndescription: {description}",
            body);

        return directory;
    }

    private static void WriteManifest(string directory, string frontmatter, string body) =>
        File.WriteAllText(
            Path.Combine(directory, "SKILL.md"),
            $"---\n{frontmatter}\n---\n\n{body}");

    /// <inheritdoc />
    public void Dispose()
    {
        if (Directory.Exists(Root))
            Directory.Delete(Root, recursive: true);
    }
}
