namespace Presentation.AuditArchiver;

/// <summary>The audit archiver's exit codes. A scheduler alerts on anything but <see cref="Done"/>.</summary>
internal static class ExitCodes
{
    /// <summary>Every log done.</summary>
    public const int Done = 0;

    /// <summary>A log stopped at something to investigate, or the command timed out: page someone.</summary>
    public const int Stopped = 1;

    /// <summary>The command line or configuration is not valid.</summary>
    public const int Usage = 2;

    /// <summary>The archive is not ready: not mounted, or init never run.</summary>
    public const int NotReady = 3;
}
