namespace Domain.AI.Workspace;

/// <summary>
/// The sandbox-injected working-copy context for a workspace-skill turn.
/// Captures everything a workspace tool needs to operate on the right slice of
/// the host filesystem and to build a well-formed <c>ChangeProposal</c> when a
/// mutation is requested.
/// </summary>
/// <remarks>
/// <para>
/// The context flows ambiently (via <c>IWorkspaceContextAccessor</c>) so individual
/// tool implementations never accept the working-copy path as a parameter. The
/// sandbox harness establishes the scope before the agent turn begins and tears
/// it down afterwards; tool calls inside that scope see a consistent view.
/// </para>
/// <para>
/// <see cref="WorkingCopyPath"/> is the absolute path to the working copy on the
/// host. Tool implementations must treat any path the agent supplies as
/// <em>relative</em> to it and reject paths that escape (canonicalisation +
/// prefix check) — the sandbox is closed-by-default.
/// </para>
/// <para>
/// <see cref="RepoUrl"/> and <see cref="Branch"/> identify the git target that
/// proposed writes attach to. <see cref="HeadSha"/> is optional but recommended:
/// when supplied, the merge applier can refuse stale proposals where the head
/// has advanced. <see cref="TestCommand"/> and <see cref="LintCommand"/> are the
/// commands the workspace tools shell out to the sandbox to verify proposed
/// changes before approval.
/// </para>
/// </remarks>
public sealed record WorkspaceContext
{
    /// <summary>
    /// Construct a workspace context.
    /// </summary>
    /// <param name="workingCopyPath">Absolute path to the working copy on the host. Must be non-empty.</param>
    /// <param name="repoUrl">Git remote URL the working copy mirrors. Used as the <c>GitRepoTarget.RepoUrl</c> for proposed writes.</param>
    /// <param name="branch">Branch the working copy is checked out on. Used as the <c>GitRepoTarget.Branch</c> for proposed writes.</param>
    /// <param name="headSha">Optional head SHA the working copy is pinned to. Null when the proposal does not pin a head.</param>
    /// <param name="testCommand">Command line the <c>run_tests</c> tool executes inside the sandbox. Empty means tests are unavailable for this workspace.</param>
    /// <param name="lintCommand">Command line the <c>run_lint</c> tool executes inside the sandbox. Empty means lint is unavailable for this workspace.</param>
    /// <exception cref="System.ArgumentException">When any required string is null or whitespace.</exception>
    public WorkspaceContext(
        string workingCopyPath,
        string repoUrl,
        string branch,
        string? headSha = null,
        string testCommand = "",
        string lintCommand = "")
    {
        System.ArgumentException.ThrowIfNullOrWhiteSpace(workingCopyPath);
        System.ArgumentException.ThrowIfNullOrWhiteSpace(repoUrl);
        System.ArgumentException.ThrowIfNullOrWhiteSpace(branch);

        WorkingCopyPath = workingCopyPath;
        RepoUrl = repoUrl;
        Branch = branch;
        HeadSha = headSha;
        TestCommand = testCommand ?? string.Empty;
        LintCommand = lintCommand ?? string.Empty;
    }

    /// <summary>Absolute path to the working copy on the host filesystem.</summary>
    public string WorkingCopyPath { get; }

    /// <summary>Git remote URL the working copy mirrors.</summary>
    public string RepoUrl { get; }

    /// <summary>Branch the working copy is checked out on.</summary>
    public string Branch { get; }

    /// <summary>Optional head SHA the working copy is pinned to.</summary>
    public string? HeadSha { get; }

    /// <summary>Command line for <c>run_tests</c>; empty means unavailable.</summary>
    public string TestCommand { get; }

    /// <summary>Command line for <c>run_lint</c>; empty means unavailable.</summary>
    public string LintCommand { get; }

    /// <summary>True when a test command is configured.</summary>
    public bool HasTestCommand => !string.IsNullOrWhiteSpace(TestCommand);

    /// <summary>True when a lint command is configured.</summary>
    public bool HasLintCommand => !string.IsNullOrWhiteSpace(LintCommand);
}
