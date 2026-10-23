namespace Application.Core.Compliance;

/// <summary>
/// Who asked for an erasure or removal: the person themselves, a named operator acting on their request (#900), or the
/// document age limit (#901).
/// </summary>
/// <remarks>
/// Recorded in the erasure audit trail. A data subject is recorded only as <see cref="DataSubjectExecutor"/>, because
/// their own id is exactly what the erasure removes; an operator is recorded by id, because operators are accountable
/// staff and their identity is not the data being erased.
/// </remarks>
public sealed record ErasureActor
{
    /// <summary>The executor recorded when the person erased their own data.</summary>
    public const string DataSubjectExecutor = "data-subject";

    /// <summary>The executor recorded when the document age limit removed a document.</summary>
    public const string RetentionExecutor = "retention";

    private ErasureActor(string executorId, string initiator)
    {
        ExecutorId = executorId;
        Initiator = initiator;
    }

    /// <summary>Gets the executor written to the audit record.</summary>
    public string ExecutorId { get; }

    /// <summary>
    /// Gets what started the request, as the audit record's <c>initiator</c>: <c>data_subject</c>, <c>operator</c> or
    /// <c>retention</c>.
    /// </summary>
    public string Initiator { get; }

    /// <summary>Gets whether an operator, rather than the person themselves or a retention rule, asked for the erasure.</summary>
    public bool IsOperator => Initiator == "operator";

    /// <summary>The person erasing their own data.</summary>
    public static ErasureActor DataSubject { get; } = new(DataSubjectExecutor, "data_subject");

    /// <summary>
    /// An operator request refused because the caller carried no usable identity; recorded so the attempt is still visible.
    /// </summary>
    public static ErasureActor UnidentifiedOperator { get; } = new("unidentified-operator", "operator");

    /// <summary>The document age limit removing a document no one has loaded for the configured period.</summary>
    public static ErasureActor Retention { get; } = new(RetentionExecutor, "retention");

    /// <summary>An operator erasing someone else's data on their request.</summary>
    /// <param name="operatorId">The operator's id, from their authenticated identity.</param>
    /// <returns>The operator actor.</returns>
    public static ErasureActor Operator(string operatorId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operatorId);
        return new ErasureActor(operatorId, "operator");
    }
}
