using Application.Core.Validation;
using Domain.Common.Config.Observability;
using FluentAssertions;
using Xunit;

namespace Application.Core.Tests.Validation;

/// <summary>
/// Tests for <see cref="Agent365ExporterConfigValidator"/>. Every rule is conditional on
/// <see cref="Agent365ExporterConfig.Enabled"/>, so a disabled section is always valid. When it is
/// enabled the rules exist to convert otherwise <em>silent</em> Agent 365 failures into a refused
/// boot: a malformed agent or tenant id does not raise an error at the service, it produces an agent
/// that never appears (or appears unidentified) in the tenant's dashboards. Pattern: a valid
/// baseline, mutate one field per test.
/// </summary>
public class Agent365ExporterConfigValidatorTests
{
    private const string AgentAppId = "11111111-1111-1111-1111-111111111111";
    private const string TenantId = "22222222-2222-2222-2222-222222222222";
    private const string BlueprintId = "33333333-3333-3333-3333-333333333333";
    private const string OtherAppId = "44444444-4444-4444-4444-444444444444";

    private readonly Agent365ExporterConfigValidator _validator = new();

    private static Agent365ExporterConfig Valid() => new()
    {
        Enabled = true,
        AgentAppId = AgentAppId,
        TenantId = TenantId,
    };

    [Fact]
    public void DefaultValues_AreOffAndDoNotPersistTelemetryToDisk()
    {
        var config = new Agent365ExporterConfig();

        config.Enabled.Should().BeFalse();
        config.Agents.Should().BeEmpty();

        // Both of these deliberately differ from the SDK's own defaults, so they are asserted rather
        // than assumed. Offline storage defaults ON in the SDK and would write undelivered spans —
        // which can carry prompts and tool arguments — to a per-user temp directory; and the SDK
        // defaults to the delegated endpoint, which is the wrong route for autonomous work.
        config.EnableOfflineStorage.Should().BeFalse();
        config.UseS2SEndpoint.Should().BeTrue();
    }

    [Fact]
    public async Task Validate_DisabledWithGarbageValues_IsValid()
    {
        // Disabled short-circuits every rule, so an omitted or off section always boots.
        var config = new Agent365ExporterConfig
        {
            Enabled = false,
            AgentAppId = "not-a-guid",
            TenantId = "also-not-a-guid",
            EnableOfflineStorage = true,
            OfflineStorageDirectory = null,
            Agents = { ["broken"] = new Agent365AgentIdentityConfig { AppId = "nope" } },
        };

        var result = await _validator.ValidateAsync(config);

        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public async Task Validate_EnabledWithGuidIds_IsValid()
    {
        var result = await _validator.ValidateAsync(Valid());

        result.IsValid.Should().BeTrue();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-a-guid")]
    [InlineData("11111111-1111-1111-1111")]
    public async Task Validate_EnabledWithNonGuidAgentAppId_IsInvalid(string? appId)
    {
        var config = Valid();
        config.AgentAppId = appId;

        var result = await _validator.ValidateAsync(config);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == nameof(Agent365ExporterConfig.AgentAppId));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-guid")]
    public async Task Validate_EnabledWithNonGuidTenantId_IsInvalid(string? tenantId)
    {
        var config = Valid();
        config.TenantId = tenantId;

        var result = await _validator.ValidateAsync(config);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == nameof(Agent365ExporterConfig.TenantId));
    }

    [Fact]
    public async Task Validate_EnabledWithBracedGuid_IsValid()
    {
        // Guid.TryParse accepts the braced and hyphenated forms. The validator should not be stricter
        // about punctuation than the service it is talking to.
        var config = Valid();
        config.AgentAppId = $"{{{AgentAppId}}}";

        var result = await _validator.ValidateAsync(config);

        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public async Task Validate_EnabledWithUnsetBlueprintId_IsValid()
    {
        // Optional: an absent blueprint only costs Agent 365 the ability to group agents of a kind.
        var config = Valid();
        config.BlueprintId = null;

        var result = await _validator.ValidateAsync(config);

        result.IsValid.Should().BeTrue();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Validate_EnabledWithBlankBlueprintId_IsValid(string blueprintId)
    {
        // This repository is a template consumers clone, so a leftover empty placeholder
        // ("BlueprintId": "") has to mean "not provided" rather than "malformed" — refusing a boot over
        // it would be a trap. Only a non-blank value is a claim about a real blueprint.
        var config = Valid();
        config.BlueprintId = blueprintId;

        var result = await _validator.ValidateAsync(config);

        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public async Task Validate_PerAgentOverrideWithBlankBlueprintId_IsValid()
    {
        var config = Valid();
        config.Agents["researcher"] = new Agent365AgentIdentityConfig
        {
            AppId = OtherAppId,
            BlueprintId = "",
        };

        var result = await _validator.ValidateAsync(config);

        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public async Task Validate_EnabledWithMalformedBlueprintId_IsInvalid()
    {
        // Set-but-invalid is a typo, and must not be silently treated as absent.
        var config = Valid();
        config.BlueprintId = "not-a-guid";

        var result = await _validator.ValidateAsync(config);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == nameof(Agent365ExporterConfig.BlueprintId));
    }

    [Fact]
    public async Task Validate_OfflineStorageEnabledWithoutADirectory_IsInvalid()
    {
        // The harness refuses to inherit the SDK's temp-directory default for content that can
        // include prompts and tool arguments — the deployment must name the location.
        var config = Valid();
        config.EnableOfflineStorage = true;
        config.OfflineStorageDirectory = null;

        var result = await _validator.ValidateAsync(config);

        result.IsValid.Should().BeFalse();
        result.Errors.Should()
            .Contain(e => e.PropertyName == nameof(Agent365ExporterConfig.OfflineStorageDirectory));
    }

    [Fact]
    public async Task Validate_OfflineStorageEnabledWithADirectory_IsValid()
    {
        var config = Valid();
        config.EnableOfflineStorage = true;
        config.OfflineStorageDirectory = Path.Combine(Path.GetTempPath(), "a365-test");

        var result = await _validator.ValidateAsync(config);

        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public async Task Validate_OfflineStorageDisabled_IgnoresAnUnsetDirectory()
    {
        // The directory is meaningless when store-and-forward is off, so it must not be demanded.
        var config = Valid();
        config.EnableOfflineStorage = false;
        config.OfflineStorageDirectory = null;

        var result = await _validator.ValidateAsync(config);

        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public async Task Validate_PerAgentOverrideWithGuidAppId_IsValid()
    {
        var config = Valid();
        config.Agents["researcher"] = new Agent365AgentIdentityConfig { AppId = OtherAppId };

        var result = await _validator.ValidateAsync(config);

        result.IsValid.Should().BeTrue();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-guid")]
    public async Task Validate_PerAgentOverrideWithNonGuidAppId_IsInvalid(string? appId)
    {
        // An override that matches an agent but carries a bad appId would mis-attribute that agent,
        // which is worse than having no override at all.
        var config = Valid();
        config.Agents["researcher"] = new Agent365AgentIdentityConfig { AppId = appId };

        var result = await _validator.ValidateAsync(config);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.ErrorMessage.Contains("researcher"));
    }

    [Fact]
    public async Task Validate_PerAgentOverrideWithBlankKey_IsInvalid()
    {
        // Keys are matched against the running agent's name, so a blank key can never match.
        var config = Valid();
        config.Agents["  "] = new Agent365AgentIdentityConfig { AppId = OtherAppId };

        var result = await _validator.ValidateAsync(config);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.ErrorMessage.Contains("blank agent name"));
    }

    [Fact]
    public async Task Validate_PerAgentOverrideWithMalformedBlueprintId_IsInvalid()
    {
        var config = Valid();
        config.Agents["researcher"] = new Agent365AgentIdentityConfig
        {
            AppId = OtherAppId,
            BlueprintId = "not-a-guid",
        };

        var result = await _validator.ValidateAsync(config);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.ErrorMessage.Contains("BlueprintId"));
    }

    [Fact]
    public async Task Validate_PerAgentOverrideWithoutABlueprintId_IsValid()
    {
        var config = Valid();
        config.Agents["researcher"] = new Agent365AgentIdentityConfig
        {
            AppId = OtherAppId,
            BlueprintId = null,
        };

        var result = await _validator.ValidateAsync(config);

        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public async Task Validate_SeveralOverrides_ReportsEveryBadEntryNotJustTheFirst()
    {
        // A host with two mistyped overrides should learn about both in one boot, rather than
        // fixing one and rediscovering the other on the next restart.
        var config = Valid();
        config.Agents["researcher"] = new Agent365AgentIdentityConfig { AppId = "bad-one" };
        config.Agents["summariser"] = new Agent365AgentIdentityConfig { AppId = "bad-two" };

        var result = await _validator.ValidateAsync(config);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.ErrorMessage.Contains("researcher"));
        result.Errors.Should().Contain(e => e.ErrorMessage.Contains("summariser"));
    }
}
