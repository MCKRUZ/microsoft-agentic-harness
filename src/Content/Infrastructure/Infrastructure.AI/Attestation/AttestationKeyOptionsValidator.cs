using Microsoft.Extensions.Options;

namespace Infrastructure.AI.Attestation;

/// <summary>
/// Validates <see cref="AttestationKeyOptions"/> on every configuration reload,
/// catching misconfiguration before it reaches the attestation service at runtime.
/// </summary>
public sealed class AttestationKeyOptionsValidator : IValidateOptions<AttestationKeyOptions>
{
    public ValidateOptionsResult Validate(string? name, AttestationKeyOptions options)
    {
        if (options.HmacKeys is null || options.HmacKeys.Count == 0)
            return ValidateOptionsResult.Fail("At least one HMAC key must be configured.");

        if (string.IsNullOrWhiteSpace(options.CurrentKeyVersion))
            return ValidateOptionsResult.Fail("CurrentKeyVersion must be specified.");

        if (!options.HmacKeys.Any(k => k.Version == options.CurrentKeyVersion))
            return ValidateOptionsResult.Fail(
                $"CurrentKeyVersion '{options.CurrentKeyVersion}' does not match any entry in HmacKeys.");

        foreach (var key in options.HmacKeys)
        {
            if (string.IsNullOrWhiteSpace(key.Version))
                return ValidateOptionsResult.Fail("All HMAC key entries must have a non-empty Version.");

            if (string.IsNullOrWhiteSpace(key.Key))
                return ValidateOptionsResult.Fail($"HMAC key version '{key.Version}' has an empty Key value.");

            try
            {
                var decoded = Convert.FromBase64String(key.Key);
                if (decoded.Length < 32)
                    return ValidateOptionsResult.Fail(
                        $"HMAC key version '{key.Version}' is {decoded.Length} bytes; minimum 32 bytes required.");
            }
            catch (FormatException)
            {
                return ValidateOptionsResult.Fail(
                    $"HMAC key version '{key.Version}' is not valid Base64.");
            }
        }

        return ValidateOptionsResult.Success;
    }
}
