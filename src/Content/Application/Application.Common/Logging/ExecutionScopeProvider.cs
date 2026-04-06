using Domain.Common.Logging;
using Microsoft.Extensions.Logging;

namespace Application.Common.Logging;

/// <summary>
/// An <see cref="IExternalScopeProvider"/> implementation that understands the execution
/// scope hierarchy (Executor &gt; Correlation &gt; Step &gt; Operation). All logging providers
/// that consume <c>IExternalScopeProvider</c> automatically receive execution context fields.
/// </summary>
/// <remarks>
/// This scope provider is registered once in DI and shared across all loggers.
/// When application code pushes an <see cref="ExecutionScope"/> via
/// <c>ILogger.BeginScope</c>, the scope data flows through to every formatter
/// and provider without those components needing to know about executor concepts.
/// <para>
/// The implementation delegates to <see cref="LoggerExternalScopeProvider"/>
/// for thread-safe scope management and adds convenience methods for
/// extracting the current <see cref="ExecutionScope"/> from the scope stack.
/// </para>
/// </remarks>
public sealed class ExecutionScopeProvider : IExternalScopeProvider
{
    private readonly LoggerExternalScopeProvider _inner = new();

    /// <inheritdoc />
    public void ForEachScope<TState>(Action<object?, TState> callback, TState state) =>
        _inner.ForEachScope(callback, state);

    /// <inheritdoc />
    public IDisposable Push(object? state) =>
        _inner.Push(state);

    /// <summary>
    /// Extracts the merged <see cref="ExecutionScope"/> from the current scope stack
    /// by walking all scopes and combining <c>ExecutionScope</c> entries.
    /// Inner scopes override outer scope properties.
    /// </summary>
    /// <param name="scopeProvider">The scope provider to inspect.</param>
    /// <returns>
    /// A merged <see cref="ExecutionScope"/> with all properties from the scope stack,
    /// or <c>null</c> if no <c>ExecutionScope</c> entries are present.
    /// </returns>
    public static ExecutionScope? GetCurrentScope(IExternalScopeProvider? scopeProvider)
    {
        if (scopeProvider is null)
            return null;

        var accumulator = t_accumulator ??= new ScopeAccumulator();
        accumulator.Reset();

        scopeProvider.ForEachScope(static (scope, state) =>
        {
            if (scope is ExecutionScope executionScope)
                state.MergeFrom(executionScope);
        }, accumulator);

        return accumulator.ToScope();
    }

    // Thread-affine: safe because ILogger.Log runs synchronously on the calling thread.
    // Do not call GetCurrentScope from async continuations on different threads.
    [ThreadStatic]
    private static ScopeAccumulator? t_accumulator;

    private sealed class ScopeAccumulator
    {
        public string? ExecutorId;
        public string? ParentExecutorId;
        public string? CorrelationId;
        public int? StepNumber;
        public string? OperationName;

        public void MergeFrom(ExecutionScope scope)
        {
            ExecutorId = scope.ExecutorId ?? ExecutorId;
            ParentExecutorId = scope.ParentExecutorId ?? ParentExecutorId;
            CorrelationId = scope.CorrelationId ?? CorrelationId;
            StepNumber = scope.StepNumber ?? StepNumber;
            OperationName = scope.OperationName ?? OperationName;
        }

        public ExecutionScope? ToScope() =>
            ExecutorId is null && ParentExecutorId is null && CorrelationId is null
                && StepNumber is null && OperationName is null
                ? null
                : new ExecutionScope(ExecutorId, ParentExecutorId, CorrelationId, StepNumber, OperationName);

        public void Reset()
        {
            ExecutorId = null;
            ParentExecutorId = null;
            CorrelationId = null;
            StepNumber = null;
            OperationName = null;
        }
    }
}
