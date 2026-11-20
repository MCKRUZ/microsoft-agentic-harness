namespace Application.AI.Common.Services;

/// <summary>
/// Stores a turn's message only once the turn is admitted: every request check has passed and the turn handler is
/// about to run (#964).
/// </summary>
/// <remarks>
/// <para>
/// The interactive transports used to store the caller's message before dispatching the turn, then remove it again
/// when the turn was refused. The next turn reads its history back from the store, where nothing screens it, so every
/// exit that forgot the removal replayed unscreened text to the model: a refusal type nobody listed, a validation
/// rejection, the caller disconnecting during screening, screening outliving the turn deadline, a spent conversation
/// budget, an identity failure. Storing at admission closes all of them at once: a turn that never reached the handler
/// never stored anything, so there is nothing to undo and no list of exit paths to keep complete.
/// </para>
/// <para>
/// Same ambient shape as <see cref="AgentTurnStreamSink"/>: the transport dispatches through <see cref="DispatchAsync{TResult}"/>,
/// which sets <see cref="Current"/> around the dispatch, so the command stays a pure data record. <c>ExecuteAgentTurnCommandHandler</c> calls
/// <see cref="AdmitCurrentAsync"/> first thing, after the whole MediatR pipeline (validation, screening, hooks, token
/// budget) has let the turn through. It clears <see cref="Current"/> for its own flow before admitting, so a turn
/// dispatched from inside this one (a delegated or sub-agent turn) cannot store the caller's message a second time.
/// </para>
/// <para>
/// The transport decides what happened to the message from <see cref="AdmittedDispatch{TResult}.Admitted"/>, never from
/// the exception it caught. That is what keeps an output-side block raised after admission from removing a message the model already answered,
/// and a new rejection type from leaving one behind.
/// </para>
/// </remarks>
public sealed class AgentTurnAdmission
{
    private static readonly AsyncLocal<AgentTurnAdmission?> s_current = new();

    private readonly Func<CancellationToken, Task> _onAdmitted;
    private readonly Lock _gate = new();
    private Task? _admitting;
    private bool _closed;

    /// <summary>Creates an admission that runs <paramref name="onAdmitted"/> once, when the turn is admitted.</summary>
    /// <param name="onAdmitted">
    /// Stores the message, and applies whatever else waits on admission (an edit's or retry's truncation). It receives
    /// the turn's token.
    /// </param>
    public AgentTurnAdmission(Func<CancellationToken, Task> onAdmitted)
    {
        ArgumentNullException.ThrowIfNull(onAdmitted);
        _onAdmitted = onAdmitted;
    }

    /// <summary>
    /// The admission waiting on the current async flow, or <see langword="null"/> when the turn stores nothing on
    /// admission (bundle runs, tests, a nested turn). Set by the transport before dispatch and restored after.
    /// </summary>
    public static AgentTurnAdmission? Current
    {
        get => s_current.Value;
        set => s_current.Value = value;
    }

    /// <summary>
    /// Runs <paramref name="dispatch"/> with this admission as <see cref="Current"/>, then closes it and reports whether
    /// the turn was admitted. The one way a transport dispatches a turn that stores on admission.
    /// </summary>
    /// <typeparam name="TResult">What the dispatch returns.</typeparam>
    /// <param name="dispatch">Sends the turn, typically <c>IMediator.Send</c>.</param>
    /// <returns>
    /// The dispatch's result or the exception it threw, and whether the turn was admitted. The exception is returned, not
    /// thrown, because the admission must be closed before the transport decides anything (see <see cref="CloseAsync"/>).
    /// </returns>
    public async Task<AdmittedDispatch<TResult>> DispatchAsync<TResult>(Func<Task<TResult>> dispatch)
    {
        ArgumentNullException.ThrowIfNull(dispatch);

        TResult? result = default;
        Exception? failure = null;
        using (AmbientScope.Begin(s_current, this))
        {
            try
            {
                result = await dispatch();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        }

        return new AdmittedDispatch<TResult>(result, failure, await CloseAsync());
    }

    /// <summary>
    /// Admits the turn waiting on the current flow, if any: clears <see cref="Current"/> for this flow, then runs its
    /// action. Called by the turn handler before it does anything else.
    /// </summary>
    /// <param name="cancellationToken">The turn's token.</param>
    /// <returns>A task that completes once the admission action has.</returns>
    /// <remarks>
    /// Clearing first matters: <see cref="Current"/> flows into everything the handler awaits, including a nested
    /// dispatch of the same command, and that dispatch must not find the caller's message still waiting to be stored.
    /// A failure in the action propagates, so a turn whose message could not be stored does not run.
    /// </remarks>
    public static Task AdmitCurrentAsync(CancellationToken cancellationToken)
    {
        var admission = Current;
        if (admission is null)
            return Task.CompletedTask;

        Current = null;
        return admission.AdmitAsync(cancellationToken);
    }

    /// <summary>
    /// Stops this admission from being granted later, and reports whether it was granted. The transport calls it once
    /// the dispatch has returned or thrown, before it decides anything about the message.
    /// </summary>
    /// <returns><see langword="true"/> when the turn was admitted and the store holds what the action wrote.</returns>
    /// <remarks>
    /// <para>
    /// The dispatch can stop waiting while the pipeline keeps running: <c>TimeoutBehavior</c> abandons the inner
    /// pipeline when the deadline passes, and a disconnect does the same. Screening that finishes after that would
    /// still reach the handler and store the message, after the transport had told the client it was dropped. Once
    /// closed, a late admission is refused with <see cref="OperationCanceledException"/>, which also stops the
    /// abandoned turn before it calls the model.
    /// </para>
    /// <para>
    /// An admission already in progress is awaited rather than refused, so the answer reflects what the store holds.
    /// Its failure is not rethrown here: the dispatch already surfaced it, and a failed action stored nothing.
    /// </para>
    /// </remarks>
    public async Task<bool> CloseAsync()
    {
        Task? inFlight;
        lock (_gate)
        {
            _closed = true;
            inFlight = _admitting;
        }

        if (inFlight is null)
            return false;

        try
        {
            await inFlight;
        }
        catch (Exception)
        {
            // The dispatch that ran it already surfaced this; nothing was admitted.
        }

        return inFlight.IsCompletedSuccessfully;
    }

    private async Task AdmitAsync(CancellationToken cancellationToken)
    {
        var granted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate)
        {
            if (_closed)
                throw new OperationCanceledException("The turn was abandoned before it was admitted.");
            if (_admitting is not null)
                return;
            _admitting = granted.Task;
        }

        try
        {
            await _onAdmitted(cancellationToken);
            granted.SetResult();
        }
        catch (Exception ex)
        {
            granted.SetException(ex);
            throw;
        }
    }
}
