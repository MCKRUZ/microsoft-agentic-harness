namespace Domain.AI.Tools;

/// <summary>
/// Classifies a tool's concurrency safety for batched execution.
/// Read-only tools run in parallel; write tools run serially.
/// </summary>
/// <remarks>
/// Used by <c>IToolConcurrencyClassifier</c> to partition tool calls within a batch.
/// The execution strategy uses this classification to determine whether a tool call
/// can safely run concurrently with other calls or must be serialized.
/// <para>
/// <strong>Fail-closed design:</strong> When a tool does not declare its concurrency
/// characteristics, it is classified as <see cref="Unknown"/>, which is treated
/// identically to <see cref="WriteSerial"/> — preventing accidental parallel writes.
/// </para>
/// </remarks>
public enum ToolConcurrencyClassification
{
    /// <summary>Tool only reads state — safe to run in parallel with other read-only tools.</summary>
    ReadOnly,

    /// <summary>Tool modifies state — must run serially to prevent race conditions.</summary>
    WriteSerial,

    /// <summary>Tool safety is unknown — treated as <see cref="WriteSerial"/> (fail-closed).</summary>
    Unknown
}
