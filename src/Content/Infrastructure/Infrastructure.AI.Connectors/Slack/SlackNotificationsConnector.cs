using Application.AI.Common.Interfaces.Connectors;
using Domain.Common.Config;
using Domain.Common.Config.Connectors;
using Infrastructure.AI.Connectors.Core;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Text;
using System.Text.Json;

namespace Infrastructure.AI.Connectors.Slack;

/// <summary>
/// Connector client for Slack notifications.
/// Supports sending messages (via Bot Token or Webhook) and uploading files
/// to Slack channels.
/// </summary>
/// <remarks>
/// Two authentication modes:
/// <list type="bullet">
///   <item><description><strong>Bot Token</strong> — full API (messages, uploads, threads). Takes precedence when both are configured.</description></item>
///   <item><description><strong>Webhook</strong> — simple one-way notifications only</description></item>
/// </list>
/// </remarks>
public sealed class SlackNotificationsConnector : ConnectorClientBase
{
    #region Variables

    private SlackConfig Config => _appConfig.CurrentValue.Connectors.Slack;

    private readonly string[] _supportedOperations =
    [
        "send_message",
        "upload_file"
    ];

    #endregion

    #region Constructor

    /// <summary>
    /// Initializes a new instance of <see cref="SlackNotificationsConnector"/>.
    /// </summary>
    public SlackNotificationsConnector(
        ILogger<SlackNotificationsConnector> logger,
        IHttpClientFactory httpClientFactory,
        IOptionsMonitor<AppConfig> appConfig)
        : base(logger, httpClientFactory, appConfig)
    {
    }

    #endregion

    #region IConnectorClient Implementation

    /// <inheritdoc/>
    public override string ToolName => "slack_notifications";

    /// <inheritdoc/>
    public override bool IsAvailable => Config.IsConfigured;

    /// <inheritdoc/>
    public override IReadOnlyList<string> SupportedOperations => _supportedOperations;

    /// <inheritdoc/>
    public override Task<List<string>> ValidateParametersAsync(
        string operation,
        Dictionary<string, object> parameters)
    {
        var errors = new List<string>();

        switch (operation)
        {
            case "send_message":
                if (!parameters.ContainsKey("channel") && string.IsNullOrWhiteSpace(Config.DefaultChannel))
                    errors.Add("Channel is required when DefaultChannel is not configured");
                if (!parameters.ContainsKey("text") && !parameters.ContainsKey("blocks"))
                    errors.Add("Either 'text' or 'blocks' is required for the message content");
                break;
            case "upload_file":
                if (!parameters.ContainsKey("channel") && string.IsNullOrWhiteSpace(Config.DefaultChannel))
                    errors.Add("Channel is required when DefaultChannel is not configured");
                if (!parameters.ContainsKey("content") && !parameters.ContainsKey("fileUrl"))
                    errors.Add("Either 'content' (file data) or 'fileUrl' is required");
                if (!parameters.ContainsKey("filename"))
                    errors.Add("Filename is required");
                break;
        }

        return Task.FromResult(errors);
    }

    #endregion

    #region ExecuteOperationAsync

    /// <inheritdoc/>
    protected override async Task<ConnectorOperationResult> ExecuteOperationAsync(
        string operation,
        Dictionary<string, object> parameters,
        CancellationToken cancellationToken)
    {
        return operation switch
        {
            "send_message" => await SendMessageAsync(parameters, cancellationToken),
            "upload_file" => await UploadFileAsync(parameters, cancellationToken),
            _ => ConnectorOperationResult.Failure($"Operation '{operation}' is not implemented")
        };
    }

    #endregion

    #region Operations

    private async Task<ConnectorOperationResult> SendMessageAsync(
        Dictionary<string, object> parameters,
        CancellationToken cancellationToken)
    {
        var config = Config;
        var channel = GetOptionalParameter<string>(parameters, "channel") ?? config.DefaultChannel!;
        var text = GetOptionalParameter<string>(parameters, "text");
        var blocks = GetOptionalParameter<List<Dictionary<string, object>>>(parameters, "blocks");
        var threadTs = GetOptionalParameter<string>(parameters, "threadTs");

        try
        {
            // Bot Token takes precedence when both are configured
            if (!string.IsNullOrWhiteSpace(config.BotToken))
                return await SendViaBotTokenAsync(config, channel, text, blocks, threadTs, cancellationToken);

            if (!string.IsNullOrWhiteSpace(config.WebhookUrl))
                return await SendViaWebhookAsync(config, channel, text, cancellationToken);

            return ConnectorOperationResult.Failure("Either BotToken or WebhookUrl must be configured");
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "HTTP error sending Slack message");
            return ConnectorOperationResult.Failure($"Network error: {ex.Message}");
        }
        catch (Exception ex) when (ex is ArgumentException or JsonException)
        {
            _logger.LogError(ex, "Error sending Slack message");
            return ConnectorOperationResult.Failure($"Error: {ex.Message}");
        }
    }

    private async Task<ConnectorOperationResult> UploadFileAsync(
        Dictionary<string, object> parameters,
        CancellationToken cancellationToken)
    {
        var config = Config;
        var channel = GetOptionalParameter<string>(parameters, "channel") ?? config.DefaultChannel!;
        var fileContent = GetOptionalParameter<string>(parameters, "content");
        var filename = GetRequiredParameter<string>(parameters, "filename");
        var title = GetOptionalParameter<string>(parameters, "title");
        var initialComment = GetOptionalParameter<string>(parameters, "initialComment");

        try
        {
            if (string.IsNullOrWhiteSpace(config.BotToken))
                return ConnectorOperationResult.Failure("BotToken is required for file uploads (webhooks don't support uploads)");

            if (string.IsNullOrWhiteSpace(fileContent))
                return ConnectorOperationResult.Failure("File content must be provided");

            var httpClient = CreateHttpClient();
            ConfigureBotTokenClient(httpClient, config);

            var isBase64 = GetOptionalParameter<bool>(parameters, "isBase64", false);
            var fileBytes = isBase64
                ? Convert.FromBase64String(fileContent)
                : Encoding.UTF8.GetBytes(fileContent);

            using var multipartContent = new MultipartFormDataContent();
            multipartContent.Add(new StringContent(channel), "channels");
            multipartContent.Add(new ByteArrayContent(fileBytes), "file", filename);
            if (!string.IsNullOrWhiteSpace(title))
                multipartContent.Add(new StringContent(title), "title");
            if (!string.IsNullOrWhiteSpace(initialComment))
                multipartContent.Add(new StringContent(initialComment), "initial_comment");

            // Note: files.upload is deprecated by Slack (March 2024). Migrate to files.uploadV2 when needed.
            var response = await httpClient.PostAsync("https://slack.com/api/files.upload", multipartContent, cancellationToken);

            var httpError = await CheckHttpErrorAsync(response, "Upload file", cancellationToken);
            if (httpError != null) return httpError;

            var result = await response.Content.ReadAsStringAsync(cancellationToken);
            using var doc = JsonDocument.Parse(result);

            var slackError = CheckSlackApiError(doc);
            if (slackError != null) return slackError;

            var file = doc.RootElement.GetProperty("file");
            var fileId = file.GetProperty("id").GetString();
            var fileUrlResult = file.GetProperty("url_private").GetString();

            var markdown = $"## File Uploaded to Slack\n\n**Channel:** {channel}\n**Filename:** {filename}\n" +
                $"**File ID:** {fileId}\n**Size:** {fileBytes.Length} bytes\n**URL:** {fileUrlResult}\n";

            _logger.LogInformation("Uploaded file {Filename} to Slack channel {Channel}", filename, channel);
            return ConnectorOperationResult.Success(new { channel, file_id = fileId, filename, url = fileUrlResult }, markdown);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "HTTP error uploading file to Slack");
            return ConnectorOperationResult.Failure($"Network error: {ex.Message}");
        }
        catch (Exception ex) when (ex is ArgumentException or JsonException)
        {
            _logger.LogError(ex, "Error uploading file to Slack");
            return ConnectorOperationResult.Failure($"Error: {ex.Message}");
        }
    }

    #endregion

    #region Helper Methods

    private void ConfigureBotTokenClient(HttpClient httpClient, SlackConfig config)
    {
        ConfigureHttpClientBase(httpClient, config.TimeoutSeconds);
        AddAcceptHeader(httpClient);
        AddBearerAuth(httpClient, config.BotToken!);
    }

    private async Task<ConnectorOperationResult> SendViaBotTokenAsync(
        SlackConfig config, string channel, string? text,
        List<Dictionary<string, object>>? blocks, string? threadTs,
        CancellationToken cancellationToken)
    {
        var httpClient = CreateHttpClient();
        ConfigureBotTokenClient(httpClient, config);

        var payload = new Dictionary<string, object?>
        {
            ["channel"] = channel.StartsWith('#') || channel.StartsWith('@') ? channel : $"#{channel}",
            ["mrkdwn"] = true
        };
        if (!string.IsNullOrWhiteSpace(text)) payload["text"] = text;
        if (blocks != null && blocks.Count > 0) payload["blocks"] = blocks;
        if (!string.IsNullOrWhiteSpace(threadTs)) payload["thread_ts"] = threadTs;

        var response = await httpClient.PostAsync(
            "https://slack.com/api/chat.postMessage", CreateJsonContent(payload), cancellationToken);

        var httpError = await CheckHttpErrorAsync(response, "Send message", cancellationToken);
        if (httpError != null) return httpError;

        var result = await response.Content.ReadAsStringAsync(cancellationToken);
        using var doc = JsonDocument.Parse(result);

        var slackError = CheckSlackApiError(doc);
        if (slackError != null) return slackError;

        var timestamp = doc.RootElement.GetProperty("ts").GetString();
        var channelPosted = doc.RootElement.GetProperty("channel").GetString();

        var markdown = $"## Message Sent to Slack\n\n**Channel:** {channelPosted}\n**Timestamp:** {timestamp}\n";
        if (!string.IsNullOrWhiteSpace(text))
            markdown += $"**Message:** {(text.Length > 200 ? text[..200] + "..." : text)}\n";

        _logger.LogInformation("Sent Slack message to channel {Channel}", channel);
        return ConnectorOperationResult.Success(new { channel = channelPosted, timestamp }, markdown);
    }

    private async Task<ConnectorOperationResult> SendViaWebhookAsync(
        SlackConfig config, string channel, string? text, CancellationToken cancellationToken)
    {
        var httpClient = CreateHttpClient();
        httpClient.Timeout = TimeSpan.FromSeconds(config.TimeoutSeconds);

        var payload = new Dictionary<string, object?>();
        if (!string.IsNullOrWhiteSpace(channel)) payload["channel"] = channel;
        if (!string.IsNullOrWhiteSpace(text)) payload["text"] = text;

        var response = await httpClient.PostAsync(config.WebhookUrl!, CreateJsonContent(payload), cancellationToken);

        var httpError = await CheckHttpErrorAsync(response, "Webhook", cancellationToken);
        if (httpError != null) return httpError;

        var markdown = $"## Message Sent via Webhook\n\n**Channel:** {channel}\n";
        if (!string.IsNullOrWhiteSpace(text))
            markdown += $"**Message:** {(text.Length > 200 ? text[..200] + "..." : text)}\n";

        _logger.LogInformation("Sent Slack message via webhook to channel {Channel}", channel);
        return ConnectorOperationResult.Success(new { channel, method = "webhook" }, markdown);
    }

    private static ConnectorOperationResult? CheckSlackApiError(JsonDocument doc)
    {
        var ok = doc.RootElement.TryGetProperty("ok", out var okProp) && okProp.GetBoolean();
        if (ok) return null;

        var errorMessage = doc.RootElement.TryGetProperty("error", out var errorProp)
            ? errorProp.GetString() : "Unknown error";
        return ConnectorOperationResult.Failure($"Slack API error: {errorMessage}");
    }

    #endregion
}
