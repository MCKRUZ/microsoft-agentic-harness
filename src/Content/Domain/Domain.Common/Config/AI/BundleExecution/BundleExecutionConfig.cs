namespace Domain.Common.Config.AI.BundleExecution;

/// <summary>
/// Root configuration for the bundle-execution subsystem — the host's ability to accept a
/// self-contained, externally-authored agent bundle (an <c>AGENT.md</c> with nested <c>SKILL.md</c>
/// files and plugin manifests, delivered as a zip archive), stage it to an isolated temp directory,
/// and run it as an ephemeral agent. Bound from <c>AppConfig:AI:BundleExecution</c>.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Off by default.</strong> A bundle is untrusted, externally-authored input, and the subsystem
/// that runs it is only useful once the full ingest → envelope → job → API sequence exists. A fresh
/// consumer therefore pays no cost and exposes no surface until they deliberately opt in.
/// </para>
/// <para>
/// The archive limits here are the first line of defence against a hostile archive: they bound how much
/// a bundle can cost the host before anything is parsed or built. They are enforced by the staging
/// service at ingest time, before untrusted content is trusted for execution. See
/// <c>IBundleStagingService</c>.
/// </para>
/// <code>
/// AppConfig.AI.BundleExecution
/// ├── Enabled                     — Master toggle (default false)
/// ├── TempRoot                    — Directory under which each bundle is staged in its own subfolder
/// ├── MaxArchiveBytes             — Reject archives larger than this on the wire (compressed size)
/// ├── MaxEntryCount               — Reject archives with more than this many entries
/// ├── MaxTotalUncompressedBytes   — Reject when the sum of entry sizes exceeds this (bomb guard)
/// └── MaxCompressionRatio         — Reject when uncompressed/compressed exceeds this (bomb guard)
/// </code>
/// </remarks>
public class BundleExecutionConfig
{
    /// <summary>
    /// Master toggle. When disabled (the default), no bundle staging, overlay, or run surface is active
    /// and the host behaves identically to one with no bundle-execution concept at all.
    /// </summary>
    /// <value>Default: false</value>
    public bool Enabled { get; set; }

    /// <summary>
    /// Directory under which each accepted bundle is staged into its own uniquely-named subdirectory.
    /// Must be a location outside every configured skill and agent discovery root (the staging service
    /// asserts this), so the global registries never independently discover a staged bundle's skills.
    /// When empty, the staging service falls back to a subdirectory of the system temp path.
    /// </summary>
    /// <value>Default: "" (system temp path)</value>
    public string TempRoot { get; set; } = string.Empty;

    /// <summary>
    /// Maximum accepted size, in bytes, of the archive as received (its compressed size on the wire).
    /// Archives larger than this are rejected before extraction. Must be positive.
    /// </summary>
    /// <value>Default: 10485760 (10 MiB)</value>
    public long MaxArchiveBytes { get; set; } = 10 * 1024 * 1024;

    /// <summary>
    /// Maximum number of entries an accepted archive may contain. Bounds the cost of iterating and
    /// extracting entries and caps a "many tiny files" denial-of-service. Must be positive.
    /// </summary>
    /// <value>Default: 2000</value>
    public int MaxEntryCount { get; set; } = 2000;

    /// <summary>
    /// Maximum total uncompressed size, in bytes, summed across all entries. This is the primary
    /// decompression-bomb guard: extraction is aborted the moment the running total would exceed it.
    /// Must be positive.
    /// </summary>
    /// <value>Default: 52428800 (50 MiB)</value>
    public long MaxTotalUncompressedBytes { get; set; } = 50 * 1024 * 1024;

    /// <summary>
    /// Maximum permitted ratio of total uncompressed size to compressed archive size. A high ratio is
    /// the signature of a decompression bomb (a tiny archive that explodes on disk). Evaluated once the
    /// compressed size is known and non-trivial. Must be positive.
    /// </summary>
    /// <value>Default: 100</value>
    public double MaxCompressionRatio { get; set; } = 100;

    /// <summary>
    /// The per-caller capability-grant table. A bundle's self-declared tools, MCP references, and autonomy
    /// are only <em>requests</em>; the envelope resolved from this table for the calling credential is the
    /// authoritative <em>grant</em>, and anything beyond it is denied. Defaults to a fail-closed table that
    /// grants nothing until an operator configures it.
    /// </summary>
    public CapabilityEnvelopesConfig Envelopes { get; set; } = new();

    /// <summary>
    /// How long a staged-bundle handle stays valid. The lifetime is sliding: it is refreshed each time the
    /// handle is looked up or a run against it starts, so an actively-used bundle stays alive while an
    /// abandoned one expires and its staging directory is deleted by the cleanup sweeper. Must be positive.
    /// </summary>
    /// <value>Default: 30 minutes</value>
    public TimeSpan HandleTtl { get; set; } = TimeSpan.FromMinutes(30);

    /// <summary>
    /// How long a run record stays pollable after it reaches a terminal state before the cleanup sweeper
    /// evicts it. Bundle runs are not persisted (the host is not their system of record); this bounds how
    /// long a caller has to read a completed run's result. Must be positive.
    /// </summary>
    /// <value>Default: 30 minutes</value>
    public TimeSpan RunRecordTtl { get; set; } = TimeSpan.FromMinutes(30);

    /// <summary>
    /// How often the background cleanup sweeper runs its pass over expired handles (deleting their staging
    /// directories) and expired run records. This is the belt-and-suspenders backstop that guarantees a
    /// staging directory is removed even if no explicit delete arrives. Must be positive.
    /// </summary>
    /// <value>Default: 60 seconds</value>
    public TimeSpan CleanupInterval { get; set; } = TimeSpan.FromSeconds(60);
}
