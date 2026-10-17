using Application.AI.Common.Exceptions;
using Application.AI.Common.Interfaces.Skills;
using Infrastructure.AI.Tools;

namespace Infrastructure.AI.Skills;

/// <summary>
/// The read, validate, enumerate and refusal-translation logic shared by every <see cref="ISkillFileReader"/>
/// that confines skill content to a <see cref="SandboxedPathGuard"/>. A subclass supplies only the guard
/// and the wording that is specific to its sandbox.
/// </summary>
/// <remarks>
/// <para>
/// Two readers carried near-identical copies of this logic: <see cref="SkillFileReader"/>, which confines
/// reads to the configured skill content roots, and the meta-harness eval reader, which confines them to
/// one run's materialized candidate directory. Their history already showed the cost of that shape: a fix
/// landing on one copy and not the sibling. They now share this base (#685).
/// </para>
/// <para>
/// The things that genuinely differ are the hooks. <see cref="CurrentGuard"/> is where the live reader
/// recomputes its permitted roots on every call (plugin directories can be appended after startup) and the
/// eval reader returns the one immutable guard it built at construction. The wording of a refusal and of
/// a skipped subdirectory names the sandbox in play, so each reader keeps its own.
/// </para>
/// </remarks>
public abstract class SandboxedSkillFileReaderBase : ISkillFileReader
{
    // One limit for every skill sandbox, also matched to FileSystemService deliberately: a manifest or
    // reference file large enough to exhaust memory is a defect or an attack either way, and two
    // different limits on two sandboxes over the same disk would be a difference nobody could justify.
    private const long MaxFileSizeBytes = 10 * 1024 * 1024; // 10 MB

    /// <inheritdoc />
    public string ReadText(string path)
    {
        var fullPath = ValidateForRead(path);
        return File.ReadAllText(fullPath);
    }

    /// <inheritdoc />
    public async Task<string> ReadTextAsync(string path, CancellationToken cancellationToken = default)
    {
        var fullPath = ValidateForRead(path);
        return await File.ReadAllTextAsync(fullPath, System.Text.Encoding.UTF8, cancellationToken);
    }

    /// <inheritdoc />
    public bool FileExists(string path) => File.Exists(Resolve(path));

    /// <inheritdoc />
    public bool DirectoryExists(string path) => Directory.Exists(Resolve(path));

    /// <inheritdoc />
    public IReadOnlyList<string> EnumerateDirectories(string path)
    {
        var guard = CurrentGuard();
        var fullPath = Resolve(path);

        if (!Directory.Exists(fullPath))
            throw new DirectoryNotFoundException($"Directory not found: {path}");

        var results = new List<string>();
        foreach (var subdirectory in Directory.EnumerateDirectories(fullPath))
        {
            // Re-checked rather than trusted because it was enumerated from inside the sandbox: a
            // junction planted in a skill root enumerates with a legitimate-looking literal path,
            // and a caller that recursed into it would be walking wherever it points. Logged rather
            // than silently dropped, so "why didn't this sibling skill load" is diagnosable.
            if (guard.IsPathAllowed(subdirectory))
                results.Add(subdirectory);
            else
                LogSkippedSubdirectory(subdirectory);
        }

        return results;
    }

    /// <summary>The guard that confines reads right now.</summary>
    private protected abstract SandboxedPathGuard CurrentGuard();

    /// <summary>The refusal text for a path outside this reader's sandbox.</summary>
    /// <param name="path">The path that was refused, as the caller supplied it.</param>
    protected abstract string OutsideSandboxMessage(string path);

    /// <summary>Logs that an enumerated subdirectory was dropped for being outside the sandbox.</summary>
    /// <param name="subdirectory">The dropped subdirectory.</param>
    protected abstract void LogSkippedSubdirectory(string subdirectory);

    /// <summary>
    /// Validates a path against the current sandbox, reporting a refusal as
    /// <see cref="SkillPathRefusedException"/>.
    /// </summary>
    /// <remarks>
    /// The translation is the point. Callers must be able to tell "the sandbox said no" — which is
    /// fatal, because absorbing it into an empty result would boot an agent silently missing its
    /// skills — apart from an ordinary file-permission denial on a directory that is legitimately
    /// inside the sandbox, which is not. Both arrive as <see cref="UnauthorizedAccessException"/>,
    /// so only the one raised here can be distinguished by type.
    /// </remarks>
    private string Resolve(string path)
    {
        try
        {
            return CurrentGuard().ResolveAndValidate(path);
        }
        catch (UnauthorizedAccessException ex) when (ex is not SkillPathRefusedException)
        {
            throw new SkillPathRefusedException(OutsideSandboxMessage(path), ex);
        }
    }

    /// <summary>
    /// Validates a path for reading and enforces the size limit before any content is loaded.
    /// </summary>
    private string ValidateForRead(string path)
    {
        var fullPath = Resolve(path);

        var fileInfo = new FileInfo(fullPath);
        if (!fileInfo.Exists)
            throw new FileNotFoundException($"Skill file not found: {path}");

        if (fileInfo.Length > MaxFileSizeBytes)
            throw new IOException($"Skill file exceeds size limit ({MaxFileSizeBytes / 1024 / 1024} MB).");

        return fullPath;
    }
}
