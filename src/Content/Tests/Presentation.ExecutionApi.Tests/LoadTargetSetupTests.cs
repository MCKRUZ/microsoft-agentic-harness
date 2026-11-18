using System.IO.Compression;
using System.Text.Json;
using FluentAssertions;
using Domain.AI.Planner;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Presentation.ExecutionApi.LoadTarget;
using Xunit;

namespace Presentation.ExecutionApi.Tests;

/// <summary>
/// What the load target hands the host and k6 must match what each of them expects: a token the host's
/// validation accepts, settings that let every scenario's work actually run, and fixture files in the
/// shapes the k6 scripts parse. A mismatch here shows up only as a load run where every request fails.
/// </summary>
public sealed class LoadTargetSetupTests : IDisposable
{
    private readonly LocalTokenIssuer _issuer = new();
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "load-target-tests-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Mint_GivesATokenThatValidatesAgainstTheIssuersOwnKey()
    {
        var token = _issuer.Mint("user-1", "tenant-1", TimeSpan.FromMinutes(5), ["Harness.Tools.Invoke"]);

        var result = await new JsonWebTokenHandler().ValidateTokenAsync(token, await Validation(_issuer));

        result.IsValid.Should().BeTrue(result.Exception?.Message);
        result.Claims["oid"].Should().Be("user-1");
        result.Claims["tid"].Should().Be("tenant-1");
        result.Claims["roles"].Should().Be("Harness.Tools.Invoke");
    }

    [Fact]
    public async Task Mint_IsRejectedByAHostThatTrustsAnotherIssuer()
    {
        using var other = new LocalTokenIssuer();
        var token = _issuer.Mint("user-1", "tenant-1", TimeSpan.FromMinutes(5), []);

        var result = await new JsonWebTokenHandler().ValidateTokenAsync(token, await Validation(other));

        result.IsValid.Should().BeFalse("the host must still verify signature, issuer and audience");
    }

    [Fact]
    public void Issuer_AndAudience_FollowTheFormatTheHostDerivesFromItsConfiguration()
    {
        _issuer.Issuer.Should().Be($"https://login.microsoftonline.com/{_issuer.TenantId}/v2.0");
        _issuer.Audience.Should().Be($"api://{_issuer.ClientId}");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Settings_LetAWorkflowLlmStepRun_WithOrWithoutToolInvocation(bool toolInvocation)
    {
        var settings = SettingsFor(toolInvocation);

        settings["AppConfig__AI__BundleExecution__Envelopes__Default__AllowedTools__0"].Should().Be(PlanCapabilities.LlmCall);
        settings["AppConfig__AI__BundleExecution__Envelopes__Default__AutonomyCeiling"].Should().Be("Autonomous",
            "below Autonomous a granted capability still waits for an approver the load target does not have");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Settings_GrantTheDemoToolOnlyWhenToolInvocationIsOn(bool toolInvocation)
    {
        var settings = SettingsFor(toolInvocation);

        settings.ContainsKey("AppConfig__AI__BundleExecution__Envelopes__Default__AllowedTools__1").Should().Be(toolInvocation);
        settings["AppConfig__AI__DirectToolInvocation__Enabled"].Should().Be(toolInvocation ? "true" : "false");
    }

    [Fact]
    public void Settings_KeepTelemetryExportOff_UnlessAskedFor()
    {
        SettingsFor(toolInvocation: false)["AppConfig__Observability__Exporters__Otlp__Enabled"].Should().Be("false",
            "a developer's user-secrets may point the exporter at a shared collector a load run would flood");
    }

    [Fact]
    public void Settings_KeepAnUnopenedStreamReservationLongerThanAPollStorm()
    {
        var ttl = TimeSpan.Parse(SettingsFor(toolInvocation: false)["AppConfig__AI__BundleExecution__StreamReservationTtl"],
            System.Globalization.CultureInfo.InvariantCulture);

        ttl.Should().BeGreaterThanOrEqualTo(TimeSpan.FromMinutes(15),
            "poll-storm polls reservations it never opens for up to about six minutes after setup");
    }

    [Fact]
    public void Write_MintsOneTokenPerCaller_SpreadRoundRobinAcrossTenants()
    {
        LoadTargetFixtures.Write(Options("--callers", "5", "--tenants", "2"), _issuer, new Uri("http://127.0.0.1:5080/"));

        using var tokens = JsonDocument.Parse(File.ReadAllText(Path.Combine(_folder, "tokens.json")));
        var callers = tokens.RootElement.EnumerateArray().ToList();
        callers.Should().HaveCount(5);
        callers.Select(c => c.GetProperty("user").GetString()).Should().OnlyHaveUniqueItems();
        callers.Select(c => c.GetProperty("tenant").GetString()).Should().Equal(
            "load-tenant-000", "load-tenant-001", "load-tenant-000", "load-tenant-001", "load-tenant-000");
        callers.Should().OnlyContain(c => !string.IsNullOrEmpty(c.GetProperty("token").GetString()));
    }

    [Fact]
    public void Write_BundleArchive_HoldsTheAgentAndTheSkillItDeclares()
    {
        LoadTargetFixtures.Write(Options(), _issuer, new Uri("http://127.0.0.1:5080/"));

        using var zip = ZipFile.OpenRead(Path.Combine(_folder, "bundle.zip"));
        zip.Entries.Select(e => e.FullName).Should().BeEquivalentTo(["AGENT.md", "skills/answerer/SKILL.md"]);
    }

    [Fact]
    public void Write_TargetDescription_IsInTheShapeTheK6ScriptsRead()
    {
        LoadTargetFixtures.Write(Options("--model", "realistic"), _issuer, new Uri("http://127.0.0.1:5080/"));

        using var target = JsonDocument.Parse(File.ReadAllText(Path.Combine(_folder, "target.json")));
        var root = target.RootElement;
        root.GetProperty("baseUrl").GetString().Should().Be("http://127.0.0.1:5080", "k6 appends paths that start with '/'");
        root.GetProperty("workflowAgent").GetString().Should().Be(LoadTargetEnvironment.WorkflowAgent);
        // lib/config.js modelAnswerMs() splits timeToFirstToken on ':' as hours:minutes:seconds.
        var model = root.GetProperty("model");
        model.GetProperty("timeToFirstToken").GetString().Should().Be("00:00:00.6000000");
        model.GetProperty("tokensPerSecond").GetDouble().Should().BePositive();
        model.GetProperty("responseTokens").GetInt32().Should().BePositive();
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _issuer.Dispose();
        if (Directory.Exists(_folder))
            Directory.Delete(_folder, recursive: true);
    }

    // The keys come from the issuer's configuration manager, the same way the host's JWT handler gets them.
    private static async Task<TokenValidationParameters> Validation(LocalTokenIssuer trusted)
    {
        var configuration = await trusted.CreateConfigurationManager().GetConfigurationAsync(CancellationToken.None);
        return new TokenValidationParameters
        {
            ValidIssuer = configuration.Issuer,
            ValidAudience = trusted.Audience,
            IssuerSigningKeys = configuration.SigningKeys,
            ClockSkew = TimeSpan.Zero,
        };
    }

    private Dictionary<string, string> SettingsFor(bool toolInvocation) =>
        LoadTargetEnvironment.Settings(
                toolInvocation ? Options("--enable-tool-invocation") : Options(), _issuer, "skills")
            .ToDictionary(s => s.Key, s => s.Value);

    private LoadTargetOptions Options(params string[] extra)
    {
        var (options, error) = LoadTargetOptions.Parse(["--out", _folder, .. extra]);
        error.Should().BeNull();
        return options!;
    }
}
