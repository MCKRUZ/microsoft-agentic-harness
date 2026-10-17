using Application.AI.Common.Exceptions;
using Infrastructure.AI.MetaHarness;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Infrastructure.AI.Tests.MetaHarness;

/// <summary>
/// The eval run's reader confines every read to one materialized candidate directory. It shares its read,
/// validate and refusal logic with <c>SkillFileReader</c> through <c>SandboxedSkillFileReaderBase</c> (#685);
/// this pins that behaviour for the eval side, which previously had only indirect coverage through the
/// eval service tests.
/// </summary>
public sealed class MaterializedSkillDirectoryFileReaderTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"materialized-reader-{Guid.NewGuid():N}");
    private readonly string _outside = Path.Combine(Path.GetTempPath(), $"materialized-outside-{Guid.NewGuid():N}");
    private readonly MaterializedSkillDirectoryFileReader _sut;

    public MaterializedSkillDirectoryFileReaderTests()
    {
        Directory.CreateDirectory(_root);
        Directory.CreateDirectory(_outside);
        _sut = new MaterializedSkillDirectoryFileReader(_root, NullLogger.Instance);
    }

    public void Dispose()
    {
        foreach (var dir in new[] { _root, _outside })
        {
            try { Directory.Delete(dir, recursive: true); }
            catch (IOException) { /* best-effort temp cleanup */ }
        }
    }

    [Fact]
    public async Task ReadText_FileInsideTheRoot_IsReadSyncAndAsync()
    {
        var file = Path.Combine(_root, "SKILL.md");
        File.WriteAllText(file, "hello");

        Assert.Equal("hello", _sut.ReadText(file));
        Assert.Equal("hello", await _sut.ReadTextAsync(file));
        Assert.True(_sut.FileExists(file));
        Assert.True(_sut.DirectoryExists(_root));
    }

    [Fact]
    public void ReadText_PathOutsideTheRoot_IsRefusedAsASandboxRefusalNamingTheEvalDirectory()
    {
        var elsewhere = Path.Combine(_outside, "secret.md");
        File.WriteAllText(elsewhere, "secret");

        var ex = Assert.Throws<SkillPathRefusedException>(() => _sut.ReadText(elsewhere));

        Assert.Contains("materialized candidate skill directory", ex.Message);
    }

    [Fact]
    public void ReadText_FileLargerThanTheSizeLimit_IsRefusedBeforeItIsLoaded()
    {
        var big = Path.Combine(_root, "big.md");
        using (var stream = File.Create(big))
            stream.SetLength(10L * 1024 * 1024 + 1);

        var ex = Assert.Throws<IOException>(() => _sut.ReadText(big));

        Assert.Contains("size limit", ex.Message);
    }

    [Fact]
    public void ReadText_MissingFileInsideTheRoot_ThrowsFileNotFound()
    {
        Assert.Throws<FileNotFoundException>(() => _sut.ReadText(Path.Combine(_root, "absent.md")));
    }

    [Fact]
    public void EnumerateDirectories_ReturnsTheSubdirectoriesInsideTheRoot()
    {
        var a = Directory.CreateDirectory(Path.Combine(_root, "a")).FullName;
        var b = Directory.CreateDirectory(Path.Combine(_root, "b")).FullName;

        var found = _sut.EnumerateDirectories(_root);

        Assert.Equal(new[] { a, b }.Order(), found.Order());
    }

    [Fact]
    public void EnumerateDirectories_AbsentDirectory_ThrowsDirectoryNotFound()
    {
        Assert.Throws<DirectoryNotFoundException>(() => _sut.EnumerateDirectories(Path.Combine(_root, "nope")));
    }
}
