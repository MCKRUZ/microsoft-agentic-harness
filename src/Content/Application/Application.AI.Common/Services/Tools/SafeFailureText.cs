namespace Application.AI.Common.Services.Tools;

/// <summary>
/// Formats a caught exception into a failure message that names the exception's type without ever
/// echoing <see cref="Exception.Message"/>.
/// </summary>
/// <remarks>
/// A caught exception's message can carry a secret (a connection string, a SAS token, a credential
/// embedded in a malformed argument) that the caller never intended to surface. The type name alone
/// is enough for a human or an agent to recognize the failure shape without risking that leak — the
/// same convention this harness already used before this helper existed
/// (<c>MediatorDispatchRunner.cs</c>, <c>WorkspaceCommandRunner.cs</c>) and that a security review
/// found hand-copied, with no shared mechanism, into eight more call sites across gates and tools.
/// Centralizing it here means the next catch block added anywhere can't reintroduce the raw-message
/// leak by hand.
/// </remarks>
public static class SafeFailureText
{
    /// <summary>
    /// Returns <c>"{prefix}: {ex.GetType().Name}."</c> — never <paramref name="ex"/>'s own message.
    /// </summary>
    /// <param name="prefix">What was being attempted, e.g. <c>"Invalid ingest arguments"</c>.</param>
    /// <param name="ex">The caught exception. Only its type name is used.</param>
    public static string For(string prefix, Exception ex) => $"{prefix}: {ex.GetType().Name}.";
}
