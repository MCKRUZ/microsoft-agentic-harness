namespace Domain.AI.Hooks;

/// <summary>
/// Defines the execution mechanism for a hook.
/// </summary>
public enum HookType
{
    /// <summary>Executes a shell command. Deferred in POC -- logs warning.</summary>
    Command,

    /// <summary>Evaluates a prompt template and injects the result as additional context.</summary>
    Prompt,

    /// <summary>Resolves a middleware type from DI and invokes it.</summary>
    Middleware,

    /// <summary>Sends an HTTP POST to a webhook URL with the hook context as JSON.</summary>
    Http
}
