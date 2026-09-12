using Application.AI.Common.Interfaces.Skills;

namespace Tests.Common;

/// <summary>
/// An <see cref="ISkillFileReader"/> with <b>no sandbox at all</b>, for tests whose subject is
/// parsing, discovery, or agent/skill composition rather than confinement.
/// </summary>
/// <remarks>
/// <para>
/// Shared across <c>Infrastructure.AI.Tests</c>, <c>Application.AI.Common.Tests</c>, and
/// <c>Infrastructure.AI.Governance.Tests</c> — previously duplicated byte-for-byte in the first two
/// (issue #645). Parsing/discovery tests write fixtures to a temporary directory, which no
/// correctly-configured skill sandbox would permit — so injecting the real <c>SkillFileReader</c>
/// would make every one of them fail for a reason unrelated to what it asserts.
/// </para>
/// <para>
/// <b>This double proves nothing about the sandbox.</b> Confinement is covered by
/// <c>SkillFileReaderTests</c> against the real implementation. A test that means to assert a path
/// is refused must not use this class — it permits everything, so such a test would pass while
/// asserting nothing.
/// </para>
/// </remarks>
public sealed class UnsandboxedSkillFileReader : ISkillFileReader
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
