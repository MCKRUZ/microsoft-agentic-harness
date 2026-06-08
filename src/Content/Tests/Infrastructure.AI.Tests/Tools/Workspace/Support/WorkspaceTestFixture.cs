using Domain.AI.Workspace;
using Infrastructure.AI.Tools.Workspace;

namespace Infrastructure.AI.Tests.Tools.Workspace.Support;

/// <summary>
/// Fixture that prepares a temporary directory styled as a working copy and
/// exposes a configured <see cref="WorkspaceContext"/> + a freshly-scoped
/// <see cref="WorkspaceContextAccessor"/>. Each test gets a unique temp
/// directory so parallel runs don't collide; the directory is removed on
/// <see cref="Dispose"/>.
/// </summary>
internal sealed class WorkspaceTestFixture : IDisposable
{
    public WorkspaceTestFixture(
        string testCommand = "",
        string lintCommand = "")
    {
        WorkingCopy = Path.Combine(
            Path.GetTempPath(),
            "workspace-skill-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(WorkingCopy);

        Context = new WorkspaceContext(
            workingCopyPath: WorkingCopy,
            repoUrl: "https://github.com/org/repo",
            branch: "main",
            headSha: "abc123",
            testCommand: testCommand,
            lintCommand: lintCommand);

        Accessor = new WorkspaceContextAccessor();
        Scope = Accessor.BeginScope(Context);
    }

    public string WorkingCopy { get; }
    public WorkspaceContext Context { get; }
    public WorkspaceContextAccessor Accessor { get; }
    public IDisposable Scope { get; }

    /// <summary>
    /// Writes <paramref name="content"/> to a file whose name is supplied by the
    /// test. Only the bare file name (no directory segments) is accepted —
    /// <see cref="Path.GetFileName(string)"/> strips any path components before
    /// the file is combined with the working copy so the fixture can never be
    /// coerced outside its own temporary directory.
    /// </summary>
    public string WriteFile(string fileName, string content)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        var leaf = Path.GetFileName(fileName);
        if (string.IsNullOrWhiteSpace(leaf))
            throw new ArgumentException("File name must be a single leaf segment.", nameof(fileName));
        var full = Path.Combine(WorkingCopy, leaf);
        File.WriteAllText(full, content);
        return full;
    }

    /// <summary>
    /// Writes <paramref name="content"/> to a file inside a nested folder of the
    /// working copy. Both the folder name and the file name are sanitised to
    /// leaf segments via <see cref="Path.GetFileName(string)"/>.
    /// </summary>
    public string WriteFileInFolder(string folderName, string fileName, string content)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folderName);
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        var folderLeaf = Path.GetFileName(folderName);
        var fileLeaf = Path.GetFileName(fileName);
        if (string.IsNullOrWhiteSpace(folderLeaf) || string.IsNullOrWhiteSpace(fileLeaf))
            throw new ArgumentException("Folder and file names must be single leaf segments.");
        var folder = Path.Combine(WorkingCopy, folderLeaf);
        Directory.CreateDirectory(folder);
        var full = Path.Combine(folder, fileLeaf);
        File.WriteAllText(full, content);
        return full;
    }

    public void Dispose()
    {
        Scope.Dispose();
        try
        {
            if (Directory.Exists(WorkingCopy))
                Directory.Delete(WorkingCopy, recursive: true);
        }
        catch
        {
            // Best effort; some platforms hold file locks briefly.
        }
    }
}
