using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Domain.Common.Config;
using Domain.Common.Config.Observability;
using FluentAssertions;
using Infrastructure.Observability.Processors;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Infrastructure.Observability.Tests.Processors;

public sealed class PiiFilteringProcessorTests : IDisposable
{
    private readonly ActivitySource _source = new("test.pii-filtering");
    private readonly ActivityListener _listener;

    public PiiFilteringProcessorTests()
    {
        _listener = new ActivityListener
        {
            ShouldListenTo = _ => true,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) =>
                ActivitySamplingResult.AllDataAndRecorded
        };
        ActivitySource.AddActivityListener(_listener);
    }

    public void Dispose()
    {
        _listener.Dispose();
        _source.Dispose();
    }

    private static PiiFilteringProcessor CreateProcessor(PiiFilteringConfig? config = null)
    {
        var appConfig = new AppConfig();
        if (config is not null)
            appConfig.Observability.PiiFiltering = config;

        var options = Options.Create(appConfig);
        return new PiiFilteringProcessor(
            NullLogger<PiiFilteringProcessor>.Instance,
            options);
    }

    [Fact]
    public void OnEnd_AuthorizationHeader_DeletedFromSpan()
    {
        var processor = CreateProcessor();
        using var activity = _source.StartActivity("test-op")!;
        activity.SetTag("http.request.header.authorization", "Bearer secret-token-123");

        processor.OnEnd(activity);

        activity.GetTagItem("http.request.header.authorization").Should().BeNull();
    }

    [Fact]
    public void OnEnd_CookieHeader_DeletedFromSpan()
    {
        var processor = CreateProcessor();
        using var activity = _source.StartActivity("test-op")!;
        activity.SetTag("http.request.header.cookie", "session=abc123; user=admin");

        processor.OnEnd(activity);

        activity.GetTagItem("http.request.header.cookie").Should().BeNull();
    }

    [Fact]
    public void OnEnd_UserEmail_HashedWithSha256()
    {
        var processor = CreateProcessor();
        using var activity = _source.StartActivity("test-op")!;
        var email = "user@example.com";
        activity.SetTag("user.email", email);

        processor.OnEnd(activity);

        var result = activity.GetTagItem("user.email") as string;
        result.Should().NotBeNull();
        result.Should().NotBe(email);

        var expectedHash = Convert.ToHexStringLower(
            SHA256.HashData(Encoding.UTF8.GetBytes(email)));
        result.Should().Be(expectedHash);
    }

    [Fact]
    public void OnEnd_EndUserId_HashedWithSha256()
    {
        var processor = CreateProcessor();
        using var activity = _source.StartActivity("test-op")!;
        var userId = "user-42";
        activity.SetTag("enduser.id", userId);

        processor.OnEnd(activity);

        var result = activity.GetTagItem("enduser.id") as string;
        result.Should().NotBeNull();
        result.Should().NotBe(userId);

        var expectedHash = Convert.ToHexStringLower(
            SHA256.HashData(Encoding.UTF8.GetBytes(userId)));
        result.Should().Be(expectedHash);
    }

    [Fact]
    public void OnEnd_NonPiiAttribute_RemainsUntouched()
    {
        var processor = CreateProcessor();
        using var activity = _source.StartActivity("test-op")!;
        activity.SetTag("http.method", "GET");
        activity.SetTag("http.status_code", 200);

        processor.OnEnd(activity);

        activity.GetTagItem("http.method").Should().Be("GET");
        activity.GetTagItem("http.status_code").Should().Be(200);
    }

    [Fact]
    public void OnEnd_Disabled_NoAttributesModified()
    {
        var config = new PiiFilteringConfig { Enabled = false };
        var processor = CreateProcessor(config);
        using var activity = _source.StartActivity("test-op")!;
        var secretValue = "Bearer secret-token";
        activity.SetTag("http.request.header.authorization", secretValue);
        activity.SetTag("user.email", "user@example.com");

        processor.OnEnd(activity);

        activity.GetTagItem("http.request.header.authorization").Should().Be(secretValue);
        activity.GetTagItem("user.email").Should().Be("user@example.com");
    }

    [Fact]
    public void OnEnd_CaseInsensitiveMatching_DeletesAttribute()
    {
        var config = new PiiFilteringConfig
        {
            Enabled = true,
            DeleteAttributes = ["HTTP.Request.Header.Authorization"]
        };
        var processor = CreateProcessor(config);
        using var activity = _source.StartActivity("test-op")!;
        activity.SetTag("http.request.header.authorization", "Bearer token");

        processor.OnEnd(activity);

        activity.GetTagItem("http.request.header.authorization").Should().BeNull();
    }

    [Fact]
    public void OnEnd_CaseInsensitiveMatching_HashesAttribute()
    {
        var config = new PiiFilteringConfig
        {
            Enabled = true,
            HashAttributes = ["USER.EMAIL"]
        };
        var processor = CreateProcessor(config);
        using var activity = _source.StartActivity("test-op")!;
        activity.SetTag("user.email", "test@test.com");

        processor.OnEnd(activity);

        var result = activity.GetTagItem("user.email") as string;
        result.Should().NotBe("test@test.com");

        var expectedHash = Convert.ToHexStringLower(
            SHA256.HashData(Encoding.UTF8.GetBytes("test@test.com")));
        result.Should().Be(expectedHash);
    }
}
