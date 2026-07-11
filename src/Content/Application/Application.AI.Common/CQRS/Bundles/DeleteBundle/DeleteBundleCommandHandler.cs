using Application.AI.Common.Interfaces.Bundles;
using Domain.Common;
using Domain.Common.Config;
using MediatR;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Application.AI.Common.CQRS.Bundles.DeleteBundle;

/// <summary>
/// Handles <see cref="DeleteBundleCommand"/>: refuses when bundle execution is disabled, otherwise removes
/// the handle from the store (which deletes its staging directory once no run holds a lease). Idempotent —
/// a missing handle still succeeds.
/// </summary>
public sealed class DeleteBundleCommandHandler : IRequestHandler<DeleteBundleCommand, Result<bool>>
{
    private readonly IBundleHandleStore _handleStore;
    private readonly IOptionsMonitor<AppConfig> _config;
    private readonly ILogger<DeleteBundleCommandHandler> _logger;

    /// <summary>Initializes a new <see cref="DeleteBundleCommandHandler"/>.</summary>
    public DeleteBundleCommandHandler(
        IBundleHandleStore handleStore,
        IOptionsMonitor<AppConfig> config,
        ILogger<DeleteBundleCommandHandler> logger)
    {
        ArgumentNullException.ThrowIfNull(handleStore);
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(logger);

        _handleStore = handleStore;
        _config = config;
        _logger = logger;
    }

    /// <inheritdoc />
    public Task<Result<bool>> Handle(DeleteBundleCommand request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        if (!_config.CurrentValue.AI.BundleExecution.Enabled)
        {
            return Task.FromResult(Result<bool>.Forbidden(
                "Bundle execution is disabled. Set AppConfig.AI.BundleExecution.Enabled = true to enable it."));
        }

        // Only the owner may delete. A non-owner (or absent handle) is a non-disclosing no-op: from their
        // view the handle is not theirs to remove, so nothing is deleted and the idempotent "already absent"
        // outcome is returned rather than revealing that the handle exists for someone else.
        if (_handleStore.GetOwner(request.Handle) != request.OwnerId)
        {
            _logger.LogInformation(
                "Delete bundle handle {Handle}: not owned by caller (no-op).", request.Handle);
            return Task.FromResult(Result<bool>.Success(false));
        }

        var removed = _handleStore.Remove(request.Handle);
        _logger.LogInformation(
            "Delete bundle handle {Handle}: {Outcome}.", request.Handle, removed ? "removed" : "already absent");

        return Task.FromResult(Result<bool>.Success(removed));
    }
}
