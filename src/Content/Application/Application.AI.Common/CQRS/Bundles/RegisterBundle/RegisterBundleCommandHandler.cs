using Application.AI.Common.Interfaces.Bundles;
using Domain.Common;
using Domain.Common.Config;
using MediatR;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Application.AI.Common.CQRS.Bundles.RegisterBundle;

/// <summary>
/// Handles <see cref="RegisterBundleCommand"/>: refuses when bundle execution is disabled, stages the
/// archive through <see cref="IBundleStagingService"/> (applying every hostile-input guard), registers the
/// resulting staged bundle in the <see cref="IBundleHandleStore"/>, and returns the handle with its expiry.
/// </summary>
/// <remarks>
/// The handler never runs the bundle — it only makes it available to run. A staging failure is surfaced
/// verbatim (the staging service already returns safe, caller-facing reasons); the handler adds no detail
/// that could leak an internal path. The returned <see cref="RegisterBundleResult.ExpiresAt"/> is computed
/// from the same TTL the store applies, so the caller gets an accurate earliest-expiry.
/// </remarks>
public sealed class RegisterBundleCommandHandler
    : IRequestHandler<RegisterBundleCommand, Result<RegisterBundleResult>>
{
    private readonly IBundleStagingService _stagingService;
    private readonly IBundleHandleStore _handleStore;
    private readonly IOptionsMonitor<AppConfig> _config;
    private readonly TimeProvider _time;
    private readonly ILogger<RegisterBundleCommandHandler> _logger;

    /// <summary>Initializes a new <see cref="RegisterBundleCommandHandler"/>.</summary>
    public RegisterBundleCommandHandler(
        IBundleStagingService stagingService,
        IBundleHandleStore handleStore,
        IOptionsMonitor<AppConfig> config,
        TimeProvider time,
        ILogger<RegisterBundleCommandHandler> logger)
    {
        ArgumentNullException.ThrowIfNull(stagingService);
        ArgumentNullException.ThrowIfNull(handleStore);
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(logger);

        _stagingService = stagingService;
        _handleStore = handleStore;
        _config = config;
        _time = time;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<Result<RegisterBundleResult>> Handle(
        RegisterBundleCommand request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        var bundleConfig = _config.CurrentValue.AI.BundleExecution;
        if (!bundleConfig.Enabled)
        {
            return Result<RegisterBundleResult>.Forbidden(
                "Bundle execution is disabled. Set AppConfig.AI.BundleExecution.Enabled = true to enable it.");
        }

        var staged = await _stagingService.StageAsync(request.Archive, cancellationToken).ConfigureAwait(false);
        if (!staged.IsSuccess || staged.Value is null)
        {
            // The staging service's reasons are already caller-safe (no internal paths); pass them through.
            return Result<RegisterBundleResult>.Fail([.. staged.Errors]);
        }

        var handle = _handleStore.Register(staged.Value);
        var expiresAt = _time.GetUtcNow() + bundleConfig.HandleTtl;

        _logger.LogInformation(
            "Registered bundle handle {Handle} (agent {AgentId}, {SkillCount} owned skill(s)); expires at {ExpiresAt:o}.",
            handle, staged.Value.Agent.Id, staged.Value.OwnedSkills.Count, expiresAt);

        return Result<RegisterBundleResult>.Success(new RegisterBundleResult
        {
            Handle = handle,
            ExpiresAt = expiresAt
        });
    }
}
