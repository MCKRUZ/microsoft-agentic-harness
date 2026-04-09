using Application.AI.Common.Interfaces.Connectors;
using Domain.Common.Config;
using FluentAssertions;
using Infrastructure.AI.Connectors.Core;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Infrastructure.AI.Connectors.Tests.Core;

/// <summary>
/// Concrete test double that exposes ConnectorClientBase internals for testing.
/// </summary>
internal sealed class TestConnectorClient : ConnectorClientBase
{
    private readonly bool _isAvailable;
    private readonly string[] _supportedOps;
    private readonly Func<string, Dictionary<string, object>, CancellationToken, Task<ConnectorOperationResult>>? _handler;

    public TestConnectorClient(
        ILogger logger,
        IHttpClientFactory httpClientFactory,
        IOptionsMonitor<AppConfig> appConfig,
        bool isAvailable = true,
        string[]? supportedOps = null,
        Func<string, Dictionary<string, object>, CancellationToken, Task<ConnectorOperationResult>>? handler = null)
        : base(logger, httpClientFactory, appConfig)
    {
        _isAvailable = isAvailable;
        _supportedOps = supportedOps ?? ["test_op"];
        _handler = handler;
    }

    public override string ToolName => "test_connector";
    public override bool IsAvailable => _isAvailable;
    public override IReadOnlyList<string> SupportedOperations => _supportedOps;

    protected override Task<ConnectorOperationResult> ExecuteOperationAsync(
        string operation,
        Dictionary<string, object> parameters,
        CancellationToken cancellationToken)
    {
        if (_handler != null)
            return _handler(operation, parameters, cancellationToken);

        return Task.FromResult(ConnectorOperationResult.Success(new { operation }, "ok"));
    }

    // Expose protected methods for testing
    public T TestGetRequiredParameter<T>(Dictionary<string, object> parameters, string key)
        => GetRequiredParameter<T>(parameters, key);

    public T? TestGetOptionalParameter<T>(Dictionary<string, object> parameters, string key, T? defaultValue = default)
        => GetOptionalParameter(parameters, key, defaultValue);
}

public class ConnectorClientBaseTests
{
    private readonly Mock<IHttpClientFactory> _httpClientFactory = new();
    private readonly ILogger _logger = NullLogger.Instance;
    private readonly Mock<IOptionsMonitor<AppConfig>> _appConfigMonitor;

    public ConnectorClientBaseTests()
    {
        _appConfigMonitor = new Mock<IOptionsMonitor<AppConfig>>();
        _appConfigMonitor.Setup(m => m.CurrentValue).Returns(new AppConfig());
    }

    private TestConnectorClient CreateClient(
        bool isAvailable = true,
        string[]? supportedOps = null,
        Func<string, Dictionary<string, object>, CancellationToken, Task<ConnectorOperationResult>>? handler = null)
    {
        return new TestConnectorClient(
            _logger, _httpClientFactory.Object, _appConfigMonitor.Object,
            isAvailable, supportedOps, handler);
    }

    [Fact]
    public async Task ExecuteAsync_WhenUnavailable_ReturnsFailure()
    {
        var client = CreateClient(isAvailable: false);

        var result = await client.ExecuteAsync("test_op", new Dictionary<string, object>());

        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().Contain("not configured");
    }

    [Fact]
    public async Task ExecuteAsync_UnsupportedOperation_ReturnsFailure()
    {
        var client = CreateClient(supportedOps: ["list", "create"]);

        var result = await client.ExecuteAsync("delete", new Dictionary<string, object>());

        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().Contain("not supported");
        result.ErrorMessage.Should().Contain("list");
        result.ErrorMessage.Should().Contain("create");
    }

    [Fact]
    public async Task ExecuteAsync_ValidOperation_CallsHandlerAndReturnsSuccess()
    {
        var handlerCalled = false;
        var client = CreateClient(handler: (op, p, ct) =>
        {
            handlerCalled = true;
            return Task.FromResult(ConnectorOperationResult.Success(new { worked = true }));
        });

        var result = await client.ExecuteAsync("test_op", new Dictionary<string, object>());

        handlerCalled.Should().BeTrue();
        result.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task ExecuteAsync_HandlerThrows_ReturnsFailureWithMessage()
    {
        var client = CreateClient(handler: (_, _, _) =>
            throw new InvalidOperationException("Something broke"));

        var result = await client.ExecuteAsync("test_op", new Dictionary<string, object>());

        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().Contain("Something broke");
    }

    [Fact]
    public void GetRequiredParameter_Missing_ThrowsArgumentException()
    {
        var client = CreateClient();
        var parameters = new Dictionary<string, object>();

        var act = () => client.TestGetRequiredParameter<string>(parameters, "missing_key");

        act.Should().Throw<ArgumentException>()
            .WithMessage("*missing_key*");
    }

    [Fact]
    public void GetRequiredParameter_Present_ReturnsValue()
    {
        var client = CreateClient();
        var parameters = new Dictionary<string, object> { ["name"] = "test-value" };

        var result = client.TestGetRequiredParameter<string>(parameters, "name");

        result.Should().Be("test-value");
    }

    [Fact]
    public void GetOptionalParameter_Missing_ReturnsDefault()
    {
        var client = CreateClient();
        var parameters = new Dictionary<string, object>();

        var result = client.TestGetOptionalParameter<string>(parameters, "absent", "fallback");

        result.Should().Be("fallback");
    }

    [Fact]
    public void GetOptionalParameter_Present_ReturnsValue()
    {
        var client = CreateClient();
        var parameters = new Dictionary<string, object> { ["count"] = 42 };

        var result = client.TestGetOptionalParameter<int>(parameters, "count", 0);

        result.Should().Be(42);
    }
}
