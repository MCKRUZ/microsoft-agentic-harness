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
    public Task<ToolExecutionAttestation> SignWithEgressAsync(
        string toolName,
        string input,
        string output,
        string egressDigest,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(egressDigest);

        var options = _optionsMonitor.CurrentValue;
        var currentKey = GetKey(options, options.CurrentKeyVersion);
        try
        {
            var timestamp = _timeProvider.GetUtcNow();
            var inputHash = ComputeSha256Hex(input);
            var outputHash = ComputeSha256Hex(output);
            // Payload schema extended (egress trailing field). Algorithm and
            // key derivation unchanged — HMAC-SHA256 over UTF-8 of the payload
            // string. PR-3a's existing payload shape is preserved by the
            // SignAsync overload; new callers opt in to the extended shape
            // explicitly.
            var payload = $"{toolName}|{inputHash}|{outputHash}|{timestamp:O}|egress:{egressDigest}";
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
                FailureReason = null,
                EgressDigest = egressDigest
            };

            return Task.FromResult(attestation);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(currentKey);
        }
    }

    /// <inheritdoc />
    public Task<ToolExecutionAttestation> SignFailureWithEgressAsync(
        string toolName,
        string input,
        string failureReason,
        string egressDigest,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(egressDigest);

        var options = _optionsMonitor.CurrentValue;
        var currentKey = GetKey(options, options.CurrentKeyVersion);
        try
        {
            var timestamp = _timeProvider.GetUtcNow();
            var inputHash = ComputeSha256Hex(input);
            var failureHash = ComputeSha256Hex(failureReason);
            var payload = $"{toolName}|{inputHash}|null|{failureHash}|{timestamp:O}|egress:{egressDigest}";
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
                FailureReason = failureReason,
                EgressDigest = egressDigest
            };

            return Task.FromResult(attestation);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(currentKey);
        }
    }

    /// <inheritdoc />
    public Task<ToolExecutionAttestation> SignFailureWithOutputAsync(
        string toolName,
        string input,
        string failureReason,
        string output,
        string? egressDigest,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(output);

        var options = _optionsMonitor.CurrentValue;
        var currentKey = GetKey(options, options.CurrentKeyVersion);
        try
        {
            var timestamp = _timeProvider.GetUtcNow();
            var inputHash = ComputeSha256Hex(input);
            var outputHash = ComputeSha256Hex(output);
            var failureHash = ComputeSha256Hex(failureReason);
            // Failure payload with the produced output's content hash occupying the output
            // slot (instead of the literal "null" used by output-less failures). The
            // discriminator during verification is OutputHash being non-null on a failure
            // attestation, so legacy failure attestations remain verifiable.
            var basePayload = $"{toolName}|{inputHash}|{outputHash}|{failureHash}|{timestamp:O}";
            var payload = egressDigest is null ? basePayload : $"{basePayload}|egress:{egressDigest}";
            var signature = ComputeHmac(currentKey, payload);

            var attestation = new ToolExecutionAttestation
            {
                ToolName = toolName,
                InputHash = inputHash,
                OutputHash = outputHash,
                Timestamp = timestamp,
                Signature = signature,
                KeyVersion = options.CurrentKeyVersion,
                IsFailureAttestation = true,
                FailureReason = failureReason,
                EgressDigest = egressDigest
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

    /// <inheritdoc />
    public async Task<bool> VerifyBoundAsync(ToolExecutionAttestation attestation, string actualOutput, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(actualOutput);

        if (attestation.OutputHash is null)
        {
            _logger.LogWarning(
                "Output-bound verification rejected for tool {ToolName}: attestation recorded no output hash",
                attestation.ToolName);
            return false;
        }

        var actualHashBytes = Encoding.UTF8.GetBytes(ComputeSha256Hex(actualOutput));
        var attestedHashBytes = Encoding.UTF8.GetBytes(attestation.OutputHash);

        if (actualHashBytes.Length != attestedHashBytes.Length
            || !CryptographicOperations.FixedTimeEquals(actualHashBytes, attestedHashBytes))
        {
            _logger.LogWarning(
                "Output-bound verification failed for tool {ToolName}: actual output diverges from the attested output hash",
                attestation.ToolName);
            return false;
        }

        return await VerifyAsync(attestation, ct);
    }

    private static string BuildVerificationPayload(ToolExecutionAttestation attestation)
    {
        // Payload shapes coexist: the PR-3a baseline (no egress digest), the PR-3c
        // extended shape (egress digest trailing), and the failure-with-output shape
        // (produced output hash occupying the output slot). Discriminators are field
        // presence on the record — EgressDigest for the egress suffix, OutputHash on a
        // failure attestation for the output slot — so earlier attestations remain
        // verifiable after each extension.
        if (attestation.IsFailureAttestation)
        {
            var failureHash = attestation.FailureReason is not null
                ? ComputeSha256Hex(attestation.FailureReason)
                : ComputeSha256Hex(string.Empty);
            var outputSlot = attestation.OutputHash ?? "null";
            var baselineFailure = $"{attestation.ToolName}|{attestation.InputHash}|{outputSlot}|{failureHash}|{attestation.Timestamp:O}";
            return attestation.EgressDigest is null
                ? baselineFailure
                : $"{baselineFailure}|egress:{attestation.EgressDigest}";
        }

        var baselineSuccess = $"{attestation.ToolName}|{attestation.InputHash}|{attestation.OutputHash}|{attestation.Timestamp:O}";
        return attestation.EgressDigest is null
            ? baselineSuccess
            : $"{baselineSuccess}|egress:{attestation.EgressDigest}";
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
