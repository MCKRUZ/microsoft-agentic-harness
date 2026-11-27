using Application.Common.Exceptions;

namespace Application.AI.Common.Exceptions;

/// <summary>
/// Thrown by an <see cref="Evaluation.Interfaces.IAgentInvoker"/> when the harness could not run a case's agent
/// turn at all: the agent or its skills are not configured, the AI provider is not configured, the screening service
/// is unavailable, or the turn hit an internal error.
/// </summary>
/// <remarks>
/// <para>
/// An eval case has two different ways to come back without an answer, and they must not share a result. The agent
/// may run and be refused (content safety or prompt-injection screening blocked the input): that is an outcome a
/// metric can score, so the invoker returns a failed <see cref="Evaluation.Models.AgentInvocationResult"/>. Or the
/// harness may never get the agent to run: that is not an answer to score, it is a broken case, so the invoker throws
/// this instead.
/// </para>
/// <para>
/// Callers already treat a thrown invocation as a broken case: the eval runner records <see cref="Exception.Message"/>
/// as the case's error and counts it as errored, and the skill-training rollout runner logs it and scores the rollout
/// as failed. Before this type existed, a turn the harness could not run came back as an empty answer, was scored as
/// an ordinary wrong answer, and its reason appeared in no report (#1070).
/// </para>
/// </remarks>
public sealed class AgentInvocationFaultException : ApplicationExceptionBase
{
    /// <summary>Initializes a fault with a generic message.</summary>
    public AgentInvocationFaultException()
        : base("The harness could not run the agent turn.")
    {
    }

    /// <summary>Initializes a fault with the reason the turn could not run.</summary>
    /// <param name="message">Why the turn could not run. Shown in eval reports, so it must not carry secrets.</param>
    public AgentInvocationFaultException(string message)
        : base(message)
    {
    }

    /// <summary>Initializes a fault with the reason and the exception that caused it.</summary>
    /// <param name="message">Why the turn could not run. Shown in eval reports, so it must not carry secrets.</param>
    /// <param name="innerException">The exception the pipeline threw.</param>
    public AgentInvocationFaultException(string message, Exception? innerException)
        : base(message, innerException)
    {
    }
}
