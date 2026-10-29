namespace Application.Core.Compliance;

/// <summary>
/// Who asked for an erasure or removal: the person themselves, a named operator acting on their request (#900), or the
/// document age limit (#901).
/// </summary>
/// <remarks>
/// A data subject is recorded only as <see cref="DataSubjectExecutor"/>, because their own id is exactly what the erasure
/// removes. A named operator is accountable staff: the compliance audit trail, which is kept for good, records them as a
/// keyed reference (#903), and the log line by id. Whether an actor is a named operator is decided by how it was made
/// (<see cref="IsNamedOperator"/>), never by its id: an operator whose id happens to read <c>unidentified-operator</c> is
/// still a named person.
/// </remarks>
public sealed record ErasureActor
{
    /// <summary>The executor recorded when the person erased their own data.</summary>
    public const string DataSubjectExecutor = "data-subject";

    /// <summary>The executor recorded when the document age limit removed a document.</summary>
    public const string RetentionExecutor = "retention";

    // No default for isNamedOperator: every factory states it, so a new one cannot record a person's id in plain text by
    // forgetting to.
    private ErasureActor(string executorId, string initiator, bool isNamedOperator)
    {
        ExecutorId = executorId;
        Initiator = initiator;
        IsNamedOperator = isNamedOperator;
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

    /// <summary>
    /// Gets whether <see cref="ExecutorId"/> is a person's own id: true only for <see cref="Operator"/>, false for the fixed
    /// names, <see cref="UnidentifiedOperator"/> included.
    /// </summary>
    public bool IsNamedOperator { get; }

    /// <summary>The person erasing their own data.</summary>
    public static ErasureActor DataSubject { get; } = new(DataSubjectExecutor, "data_subject", isNamedOperator: false);

    /// <summary>
    /// An operator request refused because the caller carried no usable identity; recorded so the attempt is still visible.
    /// </summary>
    public static ErasureActor UnidentifiedOperator { get; } = new("unidentified-operator", "operator", isNamedOperator: false);

    /// <summary>The document age limit removing a document no one has loaded for the configured period.</summary>
    public static ErasureActor Retention { get; } = new(RetentionExecutor, "retention", isNamedOperator: false);

    /// <summary>An operator erasing someone else's data on their request.</summary>
    /// <param name="operatorId">The operator's id, from their authenticated identity.</param>
    /// <returns>The operator actor.</returns>
    public static ErasureActor Operator(string operatorId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operatorId);
        return new ErasureActor(operatorId, "operator", isNamedOperator: true);
    }
}
