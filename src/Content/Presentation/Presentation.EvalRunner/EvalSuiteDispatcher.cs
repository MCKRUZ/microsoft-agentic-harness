using Application.Core.CQRS.Evaluation.RunEvalSuite;
using Domain.AI.Evaluation;
using Domain.Common;
using MediatR;
using Microsoft.Extensions.DependencyInjection;

namespace Presentation.EvalRunner;

/// <summary>
/// Sends a <see cref="RunEvalSuiteCommand"/> through the MediatR pipeline.
/// </summary>
/// <remarks>
/// The dispatch happens inside a service scope. The host builds its container with scope validation on, and the
/// pipeline's validation behaviour depends on the scoped <c>IValidator&lt;T&gt;</c> services, so resolving the
/// mediator from the root provider is refused before any handler runs (#834).
/// </remarks>
internal static class EvalSuiteDispatcher
{
    /// <summary>Dispatches <paramref name="command"/> and returns the handler's result.</summary>
    /// <param name="provider">The host's root service provider.</param>
    /// <param name="command">The evaluation run to execute.</param>
    /// <param name="cancellationToken">Cancels the run.</param>
    public static async Task<Result<EvalRunReport>> DispatchAsync(
        IServiceProvider provider, RunEvalSuiteCommand command, CancellationToken cancellationToken)
    {
        await using var scope = provider.CreateAsyncScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
        return await mediator.Send(command, cancellationToken).ConfigureAwait(false);
    }
}
