namespace Presentation.AgentHub.AgUi;

/// <summary>
/// Values of <see cref="RunErrorEvent.Code"/>: machine-readable reasons a client can act on, beside the human-readable
/// message it shows.
/// </summary>
/// <remarks>
/// A wire contract: the WebUI's AG-UI client matches these strings, so a value here is renamed only together with it.
/// </remarks>
public static class AgUiErrorCodes
{
    /// <summary>
    /// The run was rejected before the model saw the caller's message, and the store dropped that message. The client
    /// shows the message as soon as it is sent, so it removes the one it sent with this run (#1036). A rejection that
    /// leaves the message stored sends no code, and the client keeps showing it.
    /// </summary>
    public const string MessageRemoved = "message_removed";
}
