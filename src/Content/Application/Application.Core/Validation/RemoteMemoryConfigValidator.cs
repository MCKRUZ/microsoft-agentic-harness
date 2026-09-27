using Domain.Common.Config.AI;
using FluentValidation;

namespace Application.Core.Validation;

/// <summary>
/// Validates <see cref="RemoteMemoryConfig"/>. All rules are conditional on
/// <see cref="RemoteMemoryConfig.Enabled"/> — a disabled (default) config imposes no constraints
/// so the template runs out of the box.
/// </summary>
/// <remarks>
/// Without this validator, an operator who sets <c>Enabled = true</c> with a blank
/// <see cref="RemoteMemoryConfig.BaseUrl"/> or <see cref="RemoteMemoryConfig.ApiKey"/> would only
/// discover the mistake when every <c>Remote*</c> call throws inside its own catch-all and logs a
/// warning — memory silently does nothing instead of failing at startup.
/// <see cref="RemoteMemoryConfig.BaseUrl"/> is additionally required to be <c>https://</c>: every
/// remote call sends the operator's
/// <see cref="RemoteMemoryConfig.ApiKey"/> and, via <c>/extract</c>, full conversation transcripts,
/// so a plain <c>http://</c> endpoint would send both in cleartext.
/// </remarks>
public sealed class RemoteMemoryConfigValidator : AbstractValidator<RemoteMemoryConfig>
{
    /// <summary>Initializes a new instance of the <see cref="RemoteMemoryConfigValidator"/> class.</summary>
    public RemoteMemoryConfigValidator()
    {
        When(x => x.Enabled, () =>
        {
            RuleFor(x => x.BaseUrl)
                .NotEmpty()
                .Must(BeAbsoluteHttpsUrl)
                .WithMessage(
                    "BaseUrl must be a non-empty absolute https:// URL when RemoteMemory is enabled — " +
                    "every call sends the API key and, via /extract, full conversation transcripts.");

            RuleFor(x => x.ApiKey)
                .NotEmpty()
                .WithMessage("ApiKey is required when RemoteMemory is enabled.");

            RuleFor(x => x.AvatarId)
                .NotEmpty()
                .WithMessage("AvatarId is required when RemoteMemory is enabled.");

            RuleFor(x => x.TimeoutSeconds)
                .GreaterThan(0)
                .WithMessage("TimeoutSeconds must be > 0 when RemoteMemory is enabled.");

            RuleFor(x => x.AcknowledgeSharedRecallBoundary)
                .Equal(true)
                .WithMessage(
                    "AcknowledgeSharedRecallBoundary must be true when RemoteMemory is enabled — " +
                    "the current remote contract does not filter recall by caller identity, so every " +
                    "caller sharing this AvatarId shares one recall pool. Set this only after " +
                    "provisioning a separate harness deployment (and AvatarId) per isolation " +
                    "boundary you actually need — see RemoteMemoryConfig.AvatarId's remarks.");
        });
    }

    private static bool BeAbsoluteHttpsUrl(string url) =>
        !string.IsNullOrWhiteSpace(url) &&
        Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
        uri.Scheme == Uri.UriSchemeHttps;
}
