namespace Application.AI.Common.Services;

/// <summary>What <see cref="AgentTurnAdmission.DispatchAsync{TResult}"/> reports once the dispatch has stopped (#964).</summary>
/// <typeparam name="TResult">What the dispatch returns.</typeparam>
/// <param name="Result">The dispatch's result, or the default when it threw.</param>
/// <param name="Failure">The exception the dispatch threw, or <see langword="null"/> when it returned.</param>
/// <param name="Admitted">
/// Whether the turn was admitted, so the store holds what the admission wrote. Decides what happened to the caller's
/// message, whatever <paramref name="Failure"/> is.
/// </param>
public sealed record AdmittedDispatch<TResult>(TResult? Result, Exception? Failure, bool Admitted);
