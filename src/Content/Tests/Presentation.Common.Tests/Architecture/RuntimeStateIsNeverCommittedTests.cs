using System.Diagnostics;
using Domain.Common.Config.AI;
using Domain.Common.Config.AI.Audit;
using Domain.Common.Config.AI.ContextManagement;
using Domain.Common.Config.AI.Governance;
using Domain.Common.Config.AI.Orchestration;
using FluentAssertions;
using Tests.Common;
using Xunit;

namespace Presentation.Common.Tests.Architecture;

/// <summary>
/// Pins that what a host writes at run time under its default paths can never be committed by mistake.
/// </summary>
/// <remarks>
/// <para>
/// The harness's default state paths are relative, so a host writes them into whatever folder it runs from: the repo
/// root, or a host's project folder under <c>dotnet run</c>. Nothing ignored them. Running the EvalRunner from its
/// project folder left <c>.agent-sessions/keys/audit-pseudonym.key</c> there as an untracked file: the key that turns
/// user ids in the audit trail into pseudonyms. Committed, it lets anyone with the repository reverse those pseudonyms.
/// Beside it sit the audit logs themselves, which hold approver names, reasons and agent output.
/// </para>
/// <para>
/// The paths are read from the config types' own defaults, so a renamed default is checked where it now points. The
/// verdict comes from <c>git check-ignore</c> itself rather than from parsing the ignore file, so it is the answer
/// <c>git add</c> would act on.
/// </para>
/// </remarks>
public sealed class RuntimeStateIsNeverCommittedTests
{
    /// <summary>
    /// One case per default state path per folder a host can run from: the setting's name, and a repo-relative file
    /// inside where that setting writes when a host starts in that folder.
    /// </summary>
    /// <remarks>
    /// Only the shipped defaults are checked. A consumer who points a setting at another folder name, or at an absolute
    /// path, chooses where that state lives and owns ignoring it.
    /// </remarks>
    public static TheoryData<string, string> DefaultStatePaths()
    {
        var paths = new (string Setting, string Path, bool IsFile)[]
        {
            ("AI:Audit:PseudonymKeyPath", new AuditConfig().PseudonymKeyPath, true),
            ("AI:Governance:AuditStoragePath", new GovernanceConfig().AuditStoragePath, false),
            ("AI:Governance:Escalation:AuditStoragePath", new EscalationConfig().AuditStoragePath, false),
            ("AI:Erasure:AuditStoragePath", new ErasureConfig().AuditStoragePath, false),
            ("AI:Egress:AuditStoragePath", new EgressConfig().AuditStoragePath, false),
            ("AI:Changes:AuditStoragePath", new ChangesConfig().AuditStoragePath, false),
            ("AI:Changes:EvidenceStoragePath", new ChangesConfig().EvidenceStoragePath, false),
            ("AI:ContextManagement:ToolResultStorage:StoragePath", new ToolResultStorageConfig().StoragePath, false),
            ("AI:Orchestration:Subagent:MailboxStoragePath", new SubagentConfig().MailboxStoragePath, false),
            ("AI:Orchestration:Subagent:DelegationStoragePath", new SubagentConfig().DelegationStoragePath, false),
        };

        var data = new TheoryData<string, string>();
        foreach (var folder in WhereAHostRuns())
        {
            foreach (var (setting, path, isFile) in paths)
                data.Add(setting, Probe(folder, path, isFile));
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(DefaultStatePaths))]
    public void A_file_under_a_default_state_path_is_ignored_by_git(string setting, string probe)
    {
        IsIgnored(probe).Should().BeTrue(
            $"'{probe}' is where {setting} writes when a host runs from that folder, and git would let it be committed");
    }

    // The repo root, plus every Presentation project folder: `dotnet run` uses a project's folder as the working
    // directory. Found by project file rather than appsettings.json, so a new host that ships no settings file is
    // still checked; a library folder among them costs one extra probe each.
    private static IEnumerable<string> WhereAHostRuns()
    {
        yield return string.Empty;

        var presentation = Path.Combine(RepoRoot.Path, "src", "Content", "Presentation");
        foreach (var project in Directory.EnumerateDirectories(presentation)
                     .Where(folder => Directory.EnumerateFiles(folder, "*.*proj").Any()))
        {
            yield return Path.GetRelativePath(RepoRoot.Path, project).Replace('\\', '/');
        }
    }

    // A file inside the configured path. A folder setting gets a file under it; a file setting is the file itself. Said
    // per setting, not guessed from the name: ".agent-sessions" has what Path.HasExtension calls an extension.
    private static string Probe(string folder, string configured, bool isFile)
    {
        var relative = configured.Replace('\\', '/').TrimEnd('/');
        if (!isFile)
            relative += "/probe.jsonl";

        return folder.Length == 0 ? relative : $"{folder}/{relative}";
    }

    private static bool IsIgnored(string path)
    {
        // --no-index: answer from the ignore rules alone, whether or not the file exists or is tracked.
        var start = new ProcessStartInfo("git", ["check-ignore", "--no-index", "-q", path])
        {
            WorkingDirectory = RepoRoot.Path,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        using var git = Process.Start(start) ?? throw new InvalidOperationException("git could not be started.");
        var error = git.StandardError.ReadToEnd();
        git.WaitForExit();

        // 0 = ignored, 1 = not ignored; anything else is git failing, which must not read as "not ignored".
        return git.ExitCode switch
        {
            0 => true,
            1 => false,
            _ => throw new InvalidOperationException($"git check-ignore failed ({git.ExitCode}): {error}"),
        };
    }
}
