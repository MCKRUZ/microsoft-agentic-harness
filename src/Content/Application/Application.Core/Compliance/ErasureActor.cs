namespace Application.Core.Compliance;

/// <summary>
/// Who asked for an erasure: the person themselves, or a named operator acting on their request (#900).
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

    private ErasureActor(string executorId, bool isOperator)
    {
        ExecutorId = executorId;
        IsOperator = isOperator;
    }

    /// <summary>Gets the executor written to the audit record.</summary>
    public string ExecutorId { get; }

    /// <summary>Gets whether an operator, rather than the person themselves, asked for the erasure.</summary>
    public bool IsOperator { get; }

    /// <summary>The person erasing their own data.</summary>
    public static ErasureActor DataSubject { get; } = new(DataSubjectExecutor, isOperator: false);

    /// <summary>
    /// An operator request refused because the caller carried no usable identity; recorded so the attempt is still visible.
    /// </summary>
    public static ErasureActor UnidentifiedOperator { get; } = new("unidentified-operator", isOperator: true);

    /// <summary>An operator erasing someone else's data on their request.</summary>
    /// <param name="operatorId">The operator's id, from their authenticated identity.</param>
    /// <returns>The operator actor.</returns>
    public static ErasureActor Operator(string operatorId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operatorId);
        return new ErasureActor(operatorId, isOperator: true);
    }
}
