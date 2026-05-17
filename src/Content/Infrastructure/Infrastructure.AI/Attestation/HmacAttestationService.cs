using System.Security.Cryptography;
using System.Text;
using Application.AI.Common.Interfaces.Attestation;
using Domain.AI.Attestation;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Infrastructure.AI.Attestation;

/// <summary>
/// HMAC-SHA256 attestation service that creates tamper-evident proofs of tool execution.
/// Keys are loaded via <see cref="IOptionsMonitor{T}"/> for hot-reload key rotation support.
/// </summary>
public sealed class HmacAttestationService : IAttestationService
{
    private readonly IOptionsMonitor<AttestationKeyOptions> _optionsMonitor;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<HmacAttestationService> _logger;

    public HmacAttestationService(
        IOptionsMonitor<AttestationKeyOptions> optionsMonitor,
        TimeProvider timeProvider,
        ILogger<HmacAttestationService> logger)
    {
        _optionsMonitor = optionsMonitor;
        _timeProvider = timeProvider;
        _logger = logger;

        ValidateOptions(optionsMonitor.CurrentValue);
    }

    /// <inheritdoc />
    public Task<ToolExecutionAttestation> SignAsync(string toolName, string input, string output, CancellationToken ct)
    {
        var options = _optionsMonitor.CurrentValue;
        var currentKey = GetKey(options, options.CurrentKeyVersion);
        try
        {
            var timestamp = _timeProvider.GetUtcNow();
            var inputHash = ComputeSha256Hex(input);
            var outputHash = ComputeSha256Hex(output);
            var payload = $"{toolName}|{inputHash}|{outputHash}|{timestamp:O}";
            var signature = ComputeHmac(currentKey, payload);

            var attestation = new ToolExecutionAttestation
            {
                ToolName = toolName,
                InputHash = inputHash,
                OutputHash = outputHash,
                Timestamp = timestamp,
                Signature = signature,
                KeyVersion = options.CurrentKeyVersion,
                IsFailureAttestation = false,
                FailureReason = null
            };

            return Task.FromResult(attestation);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(currentKey);
        }
    }

    /// <inheritdoc />
    public Task<ToolExecutionAttestation> SignFailureAsync(string toolName, string input, string failureReason, CancellationToken ct)
    {
        var options = _optionsMonitor.CurrentValue;
        var currentKey = GetKey(options, options.CurrentKeyVersion);
        try
        {
            var timestamp = _timeProvider.GetUtcNow();
            var inputHash = ComputeSha256Hex(input);
            var failureHash = ComputeSha256Hex(failureReason);
            var payload = $"{toolName}|{inputHash}|null|{failureHash}|{timestamp:O}";
            var signature = ComputeHmac(currentKey, payload);

            var attestation = new ToolExecutionAttestation
            {
                ToolName = toolName,
                InputHash = inputHash,
                OutputHash = null,
                Timestamp = timestamp,
                Signature = signature,
                KeyVersion = options.CurrentKeyVersion,
                IsFailureAttestation = true,
                FailureReason = failureReason
            };

            return Task.FromResult(attestation);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(currentKey);
        }
    }

    /// <inheritdoc />
    public Task<bool> VerifyAsync(ToolExecutionAttestation attestation, CancellationToken ct)
    {
        var options = _optionsMonitor.CurrentValue;
        var keyEntry = options.HmacKeys.FirstOrDefault(k => k.Version == attestation.KeyVersion);

        if (keyEntry is null)
        {
            _logger.LogWarning("Attestation key version {KeyVersion} not found in keychain", attestation.KeyVersion);
            return Task.FromResult(false);
        }

        byte[] keyBytes;
        byte[] actualSignature;
        try
        {
            keyBytes = Convert.FromBase64String(keyEntry.Key);
            actualSignature = Convert.FromBase64String(attestation.Signature);
        }
        catch (FormatException)
        {
            _logger.LogWarning("Malformed Base64 in attestation or key version {KeyVersion}", attestation.KeyVersion);
            return Task.FromResult(false);
        }

        try
        {
            var payload = BuildVerificationPayload(attestation);
            var payloadBytes = Encoding.UTF8.GetBytes(payload);
            var expectedSignature = HMACSHA256.HashData(keyBytes, payloadBytes);

            var isValid = CryptographicOperations.FixedTimeEquals(expectedSignature, actualSignature);
            return Task.FromResult(isValid);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(keyBytes);
        }
    }

    private static string BuildVerificationPayload(ToolExecutionAttestation attestation)
    {
        if (attestation.IsFailureAttestation)
        {
            var failureHash = attestation.FailureReason is not null
                ? ComputeSha256Hex(attestation.FailureReason)
                : ComputeSha256Hex(string.Empty);
            return $"{attestation.ToolName}|{attestation.InputHash}|null|{failureHash}|{attestation.Timestamp:O}";
        }

        return $"{attestation.ToolName}|{attestation.InputHash}|{attestation.OutputHash}|{attestation.Timestamp:O}";
    }

    private void ValidateOptions(AttestationKeyOptions options)
    {
        if (options.HmacKeys is null || options.HmacKeys.Count == 0)
            throw new ArgumentException("At least one HMAC key must be configured.", nameof(options));

        if (!options.HmacKeys.Any(k => k.Version == options.CurrentKeyVersion))
            throw new ArgumentException(
                $"CurrentKeyVersion '{options.CurrentKeyVersion}' does not match any entry in HmacKeys.",
                nameof(options));

        foreach (var key in options.HmacKeys)
        {
            var decoded = Convert.FromBase64String(key.Key);
            if (decoded.Length < 32)
                _logger.LogWarning("HMAC key version {Version} is shorter than 32 bytes ({Length} bytes)", key.Version, decoded.Length);
            CryptographicOperations.ZeroMemory(decoded);
        }
    }

    private static byte[] GetKey(AttestationKeyOptions options, string version)
    {
        var entry = options.HmacKeys.First(k => k.Version == version);
        return Convert.FromBase64String(entry.Key);
    }

    private static string ComputeSha256Hex(string input)
    {
        var bytes = Encoding.UTF8.GetBytes(input);
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexStringLower(hash);
    }

    private static string ComputeHmac(byte[] key, string payload)
    {
        var payloadBytes = Encoding.UTF8.GetBytes(payload);
        var hmac = HMACSHA256.HashData(key, payloadBytes);
        return Convert.ToBase64String(hmac);
    }
}
