namespace Application.AI.Common.StructuredOutput;

/// <summary>
/// The outcome of one structured-output invocation — never throws for an expected failure; see
/// <see cref="Outcome"/>.
/// </summary>
/// <typeparam name="T">The response shape requested.</typeparam>
public sealed record StructuredOutputResult<T>
{
    /// <summary>Why the call terminated.</summary>
    public required StructuredOutcome Outcome { get; init; }

    /// <summary><see langword="true"/> only when <see cref="Outcome"/> is <see cref="StructuredOutcome.Parsed"/>.</summary>
    public bool IsSuccess => Outcome == StructuredOutcome.Parsed;

    /// <summary>The deserialized value on success; <see langword="default"/> otherwise.</summary>
    public T? Value { get; init; }

    /// <summary>
    /// The raw text of the last attempt, whether or not it parsed — useful for diagnosing why a
    /// call failed without needing to re-run it.
    /// </summary>
    public string? RawOutput { get; init; }

    /// <summary>Human-readable failure reason. <see langword="null"/> on success.</summary>
    public string? ErrorMessage { get; init; }

    /// <summary>Builds a successful result.</summary>
    public static StructuredOutputResult<T> Success(T value, string? rawOutput) => new()
    {
        Outcome = StructuredOutcome.Parsed,
        Value = value,
        RawOutput = rawOutput,
    };

    /// <summary>Builds a failed result with the given outcome and reason.</summary>
    public static StructuredOutputResult<T> Fail(StructuredOutcome outcome, string errorMessage, string? rawOutput = null) => new()
    {
        Outcome = outcome,
        ErrorMessage = errorMessage,
        RawOutput = rawOutput,
    };
}
