namespace Infrastructure.AI.Tests;

/// <summary>
/// A fresh, randomly-named temp directory, deleted on disposal. Shared here (code-review finding on
/// #660) because it was previously duplicated by hand in <c>SkillParserExtensionTests</c> and again in
/// a new test added by that PR.
/// </summary>
internal sealed class TempDirectory : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(
        System.IO.Path.GetTempPath(),
        System.IO.Path.GetRandomFileName());

    public TempDirectory() => Directory.CreateDirectory(Path);

    public void Dispose()
    {
        if (Directory.Exists(Path))
            Directory.Delete(Path, recursive: true);
    }
}
