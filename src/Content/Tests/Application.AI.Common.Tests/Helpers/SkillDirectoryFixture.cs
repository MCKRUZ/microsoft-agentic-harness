namespace Application.AI.Common.Tests.Helpers;

/// <summary>
/// Creates disposable temporary skill directories containing real <c>SKILL.md</c> manifests, for tests
/// that exercise the framework's file-backed skill loader.
/// </summary>
/// <remarks>
/// Shared deliberately rather than duplicated per test class. <see cref="CreateSkill"/> encodes the
/// loader's acceptance rule — frontmatter <c>name</c> must ordinal-equal the containing directory's name,
/// and a description is mandatory — and getting that wrong does not fail loudly: the loader silently
/// yields no skills, and every assertion built on it becomes a vacuous pass. Holding the rule in one place
/// means an SDK change to it is one edit, not a hunt for every fixture that happened to hard-code it.
/// </remarks>
public sealed class SkillDirectoryFixture : IDisposable
{
    private readonly string _root;

    /// <summary>
    /// Creates a fixture rooted at a fresh temporary directory.
    /// </summary>
    /// <param name="label">Short prefix for the temp directory, to make stray folders identifiable.</param>
    public SkillDirectoryFixture(string label)
    {
        _root = Path.Combine(Path.GetTempPath(), $"{label}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
    }

    /// <summary>The fixture's root directory. Use as a configured skill root.</summary>
    public string Root => _root;

    /// <summary>
    /// Creates a skill directory at <paramref name="relativePath"/> whose manifest the framework loader
    /// accepts, and returns its full path.
    /// </summary>
    /// <param name="relativePath">Path relative to <see cref="Root"/>; its last segment is the skill name.</param>
    /// <param name="body">Markdown body placed after the frontmatter.</param>
    /// <param name="description">Frontmatter description. Must be non-empty or the loader rejects the skill.</param>
    public string CreateSkill(string relativePath, string body = "body", string description = "A skill.")
    {
        var directory = Path.Combine(_root, relativePath);
        Directory.CreateDirectory(directory);

        WriteManifest(
            directory,
            $"name: {Path.GetFileName(directory)}\ndescription: {description}",
            body);

        return directory;
    }

    /// <summary>
    /// Creates a skill directory named <paramref name="directoryName"/> whose manifest carries
    /// <paramref name="frontmatter"/> verbatim, for the malformed-manifest cases.
    /// </summary>
    public string CreateSkillWithFrontmatter(string directoryName, string frontmatter, string body = "body")
    {
        var directory = Path.Combine(_root, directoryName);
        Directory.CreateDirectory(directory);

        WriteManifest(directory, frontmatter, body);

        return directory;
    }

    /// <summary>
    /// Creates a directory containing a <c>SKILL.md</c> with no frontmatter block at all.
    /// </summary>
    public string CreateSkillWithoutFrontmatter(string directoryName, string content = "# Demo\n\nno frontmatter")
    {
        var directory = Path.Combine(_root, directoryName);
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "SKILL.md"), content);

        return directory;
    }

    /// <summary>Creates a directory under <see cref="Root"/> with no <c>SKILL.md</c> in it.</summary>
    public string CreateEmptyDirectory(string relativePath)
    {
        var directory = Path.Combine(_root, relativePath);
        Directory.CreateDirectory(directory);

        return directory;
    }

    private static void WriteManifest(string directory, string frontmatter, string body) =>
        File.WriteAllText(
            Path.Combine(directory, "SKILL.md"),
            $"---\n{frontmatter}\n---\n\n{body}");

    /// <inheritdoc />
    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }
}
