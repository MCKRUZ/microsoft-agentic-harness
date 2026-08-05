using Application.AI.Common.Interfaces.Skills;

namespace Application.AI.Common.Tests;

/// <summary>
/// An <see cref="ISkillFileReader"/> with <b>no sandbox at all</b>, for tests whose subject is agent
/// or skill composition rather than confinement.
/// </summary>
/// <remarks>
/// <b>This double proves nothing about the sandbox.</b> Confinement is covered by
/// <c>SkillFileReaderTests</c> in <c>Infrastructure.AI.Tests</c> against the real implementation. A
/// test that means to assert a path is refused must not use this class — it permits everything, so
/// such a test would pass while asserting nothing.
/// </remarks>
internal sealed class UnsandboxedSkillFileReader : ISkillFileReader
{
    /// <inheritdoc />
    public string ReadText(string path) => File.ReadAllText(path);

    /// <inheritdoc />
    public Task<string> ReadTextAsync(string path, CancellationToken cancellationToken = default) =>
        File.ReadAllTextAsync(path, System.Text.Encoding.UTF8, cancellationToken);

    /// <inheritdoc />
    public bool FileExists(string path) => File.Exists(path);

    /// <inheritdoc />
    public bool DirectoryExists(string path) => Directory.Exists(path);

    /// <inheritdoc />
    public IReadOnlyList<string> EnumerateDirectories(string path) =>
        Directory.Exists(path)
            ? [.. Directory.EnumerateDirectories(path)]
            : throw new DirectoryNotFoundException($"Directory not found: {path}");
}
