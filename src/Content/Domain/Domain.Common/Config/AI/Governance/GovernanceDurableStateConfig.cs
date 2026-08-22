namespace Domain.Common.Config.AI.Governance;

/// <summary>
/// Configuration for the durable governance-state store: SQLite-backed persistence of
/// pending escalations and change proposals so they survive host restarts.
/// Bound from <c>AppConfig:AI:Governance:DurableState</c> in appsettings.json.
/// </summary>
/// <remarks>
/// <para>
/// Both toggles default to <c>false</c> — the template ships with the current in-memory
/// behavior unchanged. When a toggle is off, the corresponding subsystem never touches the
/// database and no file or directory is created.
/// </para>
/// <para>
/// <b>The toggles are read once, at first service resolution.</b> Durability is a topology
/// property, not a live tunable: flipping a toggle at runtime would split state between the
/// in-memory and durable stores (records created before the flip would be invisible to the
/// other side). Changing either value requires a host restart to take effect.
/// </para>
/// <para>
/// What durability does and does not restore: pending escalations and proposals are
/// rehydrated on startup as pending — listable, decidable, and cancellable. In-process
/// waiters (an agent turn blocked on <c>RequestEscalationAsync</c>) are
/// <see cref="System.Threading.Tasks.TaskCompletionSource"/> instances and cannot survive a
/// restart; the blocked turn is gone, but the escalation record remains actionable and its
/// eventual outcome is durably queryable.
/// </para>
/// <para>
/// <b>Prerequisite when <see cref="EscalationsEnabled"/> or <see cref="ChangeProposalsEnabled"/>
/// is true:</b> HMAC attestation key material must be configured (User Secrets / Key Vault,
/// never appsettings). Persisted escalation outcomes and change proposals are sealed with it,
/// each bound to its own record id, and a record whose seal does not verify is never served or
/// re-driven — otherwise anyone with write access to the database file could launder a forged
/// approval into the hash-chained audit log. <see cref="CallOnceEnforcementEnabled"/> does not
/// share this prerequisite — see its own remarks.
/// </para>
/// <para>
/// <b>Key retirement is destructive here.</b> A seal records the key version that produced it,
/// and verification needs that key. Removing a retired key from the keychain permanently
/// strands every row still sealed under it: those records fail verification and are quarantined
/// rather than served. Retire a key only after the records sealed with it have aged out of
/// <see cref="RetentionDays"/>, or re-seal them first.
/// </para>
/// </remarks>
public sealed class GovernanceDurableStateConfig
{
    /// <summary>
    /// When true, pending escalations (request, collected decisions, and resolution progress)
    /// are persisted to the governance-state database and rehydrated as pending on startup.
    /// Durable writes are fail-closed: a decision whose durable write fails is not reported
    /// as recorded. Default false — in-memory behavior is byte-for-byte unchanged.
    /// </summary>
    public bool EscalationsEnabled { get; set; }

    /// <summary>
    /// When true, <c>IChangeProposalStore</c> resolves to the SQLite-backed implementation
    /// instead of the in-memory one, so proposals survive host restarts. Default false —
    /// the in-memory store (and the startup validator's production guard against it)
    /// remains active.
    /// </summary>
    public bool ChangeProposalsEnabled { get; set; }

    /// <summary>
    /// When true, a tool declared <c>CallOncePerConversation</c> is enforced durably: the
    /// admission pipeline refuses a second call to it within the same conversation, keyed by
    /// conversation id, surviving across turns, across separate runs continuing the same
    /// conversation, and across hosts sharing this database. Default false — call-once tools
    /// are declared but not enforced until a template consumer opts in.
    /// </summary>
    /// <remarks>
    /// Does <b>not</b> require HMAC attestation key material, unlike
    /// <see cref="EscalationsEnabled"/> and <see cref="ChangeProposalsEnabled"/>. A ledger row
    /// here records a refusal-producing fact ("this tool already ran"), not an approval-producing
    /// one: forging or corrupting a row can only make the system MORE restrictive — it denies a
    /// call that should have been allowed — never less, so there is no equivalent of laundering
    /// a forged approval for an attacker to gain from.
    /// </remarks>
    public bool CallOnceEnforcementEnabled { get; set; }

    /// <summary>
    /// Path of the SQLite database file holding escalation state, change proposals, and the
    /// call-once tool ledger. Relative paths resolve from the application base directory. The
    /// path is normalized and must remain under that directory; the containing folder is
    /// created lazily, on first use.
    /// </summary>
    /// <remarks>
    /// Deliberately <b>not</b> under <c>.agent-sessions/</c>. That tree is the harness's own
    /// working area, reachable by the file-system tool; a database holding approval verdicts
    /// must not sit where an agent can write. <c>.agent-state/</c> is denied to the
    /// file-system tool (see <c>FileSystemService</c>'s protected-segment list).
    /// </remarks>
    public string DatabasePath { get; set; } = ".agent-state/governance-state.db";

    /// <summary>
    /// How often (in seconds) the background reconciler scans for escalations stuck in the
    /// "resolved but never audited" state and re-drives them. Values below one minute are
    /// clamped up; the scan is cheap but is not meant to be a hot loop. Default 5 minutes.
    /// </summary>
    public int ReconcileIntervalSeconds { get; set; } = 300;

    /// <summary>
    /// How long (in days) terminal records are retained before the prune pass deletes them.
    /// Governs the escalation and change-proposal tables only — the call-once tool ledger is
    /// never pruned by this setting; see <c>GovernanceStatePruner</c>'s remarks for why a ledger
    /// row cannot be safely aged out and is left to grow without bound regardless of this value.
    /// Zero or negative disables pruning of the two governed tables entirely (the operator takes
    /// over retention). Default 90 days.
    /// </summary>
    public int RetentionDays { get; set; } = 90;

    /// <summary>
    /// Hard cap on the number of non-terminal records read in a single rehydration or
    /// reconcile scan. Bounds both memory and startup time when a database has accumulated a
    /// pathological backlog. Default 10,000.
    /// </summary>
    public int MaxScanRecords { get; set; } = 10_000;

    /// <summary>
    /// Hard cap (in bytes) on any single serialized JSON payload column. A payload above this
    /// is rejected before <c>SaveChangesAsync</c> rather than being written and later failing
    /// to load. Default 1 MiB — escalations and proposals are human-scale documents.
    /// </summary>
    public int MaxPayloadBytes { get; set; } = 1024 * 1024;
}
