namespace Domain.Common.Config.AI.RAG;

/// <summary>
/// Loaded documents (#901): where the record of when each was last loaded is kept, and the optional age limit that removes
/// a document once it has not been loaded again for a set time. Bound from <c>AppConfig:AI:Rag:Documents</c>.
/// </summary>
/// <remarks>
/// <para>
/// A loaded document is the deploying organisation's own business content, so by default it is kept until someone removes
/// it (<c>POST /api/compliance/document-removals</c>). An organisation whose policy limits how long such content may be held
/// switches <see cref="Retention"/> on; the sweep then removes each document, with everything drawn from it, once it has
/// not been loaded for <see cref="RetentionPeriodConfig.RetainFor"/>. Loading a document again restarts its clock.
/// </para>
/// <para>
/// Documents loaded before the load record existed have no load time, so the age limit never reaches them; remove them
/// by id, or load them again to start their clock.
/// </para>
/// </remarks>
public sealed class DocumentsConfig
{
    /// <summary>Path to the SQLite file recording when each document was last loaded.</summary>
    /// <value>Default: "data/documents.db"</value>
    public string LoadLedgerDatabasePath { get; set; } = "data/documents.db";

    /// <summary>
    /// Declares that every process loading documents into the configured stores records its loads in this same file: one
    /// machine, the same absolute <see cref="LoadLedgerDatabasePath"/>. Required before the age limit runs over stores
    /// other processes can write (anything but the in-process FAISS index and in-memory graph); without it, a host whose
    /// record missed another host's recent load would delete that document for everyone.
    /// </summary>
    /// <value>Default: false</value>
    public bool LoadLedgerSharedByEveryWriter { get; set; }

    /// <summary>
    /// The optional age limit. Off by default; when switched on, a document not loaded for
    /// <see cref="RetentionPeriodConfig.RetainFor"/> (default three years) is removed.
    /// </summary>
    public RetentionPeriodConfig Retention { get; set; } = new() { Enabled = false, RetainFor = TimeSpan.FromDays(1095) };
}
