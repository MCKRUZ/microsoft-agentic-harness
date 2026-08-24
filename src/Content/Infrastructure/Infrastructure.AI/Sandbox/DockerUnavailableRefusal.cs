namespace Infrastructure.AI.Sandbox;

/// <summary>
/// The refusal text <see cref="DockerSandboxExecutor"/> and <see cref="DockerSandboxSessionFactory"/>
/// both report — as the attestation reason and the caller-visible error message alike — when Docker
/// is unavailable. Shared so the two sibling classes' identical #434 fix can't drift apart.
/// </summary>
internal static class DockerUnavailableRefusal
{
    public const string Message =
        "Container isolation required but Docker is unavailable. Cannot downgrade to process isolation.";
}
