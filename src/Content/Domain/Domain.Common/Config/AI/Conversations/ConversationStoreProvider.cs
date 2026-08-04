namespace Domain.Common.Config.AI.Conversations;

/// <summary>
/// Selects the backing store for durable conversation transcripts.
/// </summary>
public enum ConversationStoreProvider
{
    /// <summary>
    /// SQLite via EF Core. The default, and the only option safe for more than one host process:
    /// each message is its own row, so appending is an <c>INSERT</c> and SQLite's own file locking
    /// serialises writers across processes on the same machine.
    /// </summary>
    /// <remarks>
    /// That guarantee stops at the machine boundary. A deployment that scales the Execution API
    /// across several machines needs a server-backed implementation behind
    /// <c>IConversationStore</c>; SQLite's locking does not travel over a network share.
    /// </remarks>
    Sqlite = 0,

    /// <summary>
    /// One JSON file per conversation. Single-process development use only — its write serialisation
    /// is an in-process lock, so two hosts sharing a path can move a torn record into place.
    /// </summary>
    FileSystem = 1,
}
