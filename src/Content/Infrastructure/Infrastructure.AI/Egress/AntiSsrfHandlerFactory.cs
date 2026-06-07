using Domain.Common.Config;
using Microsoft.Extensions.Options;
using Microsoft.Security.AntiSSRF;

namespace Infrastructure.AI.Egress;

/// <summary>
/// Wraps <c>Microsoft.Security.AntiSSRF</c> v1.0.0 in the harness's terms.
/// Builds an <see cref="AntiSSRFPolicy"/> tuned for the harness threat model
/// (RFC 1918 + link-local + loopback + IMDS + IPv6 ULA all denied; plain-text
/// HTTP off by default) and calls <see cref="AntiSSRFPolicy.GetHandler"/> to
/// produce the terminal <see cref="AntiSSRFHandler"/>.
/// </summary>
/// <remarks>
/// <para>
/// Per the §6.3 finding in <c>documentation/security/ssrf-defense.md</c>, the
/// <see cref="AntiSSRFPolicy"/>'s <c>_editLock</c> is a one-way immutability
/// latch: once <see cref="AntiSSRFPolicy.GetHandler"/> is called the policy is
/// frozen for the lifetime of the handler. The factory therefore creates the
/// handler exactly once and the registered named <see cref="HttpClient"/>
/// reuses it across all requests. Concurrent requests share an immutable
/// policy with zero lock contention on the read path.
/// </para>
/// <para>
/// Per §6.4, <c>IPAddressRanges.recommendedV1</c> is the curated superset that
/// includes <c>privateUse</c>, <c>loopback</c>, <c>linkLocal</c>, <c>imds</c>,
/// <c>multicast</c>, IPv6 unique-local (<c>fc00::/7</c>), plus reserved and
/// benchmarking ranges. Denying that single list covers the entire threat
/// model.
/// </para>
/// </remarks>
public sealed class AntiSsrfHandlerFactory
{
    private readonly IOptionsMonitor<AppConfig> _config;
    private readonly object _gate = new();
    private AntiSSRFHandler? _handler;

    /// <summary>Initializes a new <see cref="AntiSsrfHandlerFactory"/>.</summary>
    public AntiSsrfHandlerFactory(IOptionsMonitor<AppConfig> config)
    {
        ArgumentNullException.ThrowIfNull(config);
        _config = config;
    }

    /// <summary>
    /// Creates (or returns the cached) <see cref="AntiSSRFHandler"/>. The handler
    /// is the terminal <see cref="HttpMessageHandler"/> in the named
    /// <c>HttpClient</c> chain; the outer
    /// <see cref="EgressPolicyDelegatingHandler"/> wraps it.
    /// </summary>
    public AntiSSRFHandler GetOrCreate()
    {
        if (_handler is not null)
        {
            return _handler;
        }

        lock (_gate)
        {
            if (_handler is not null)
            {
                return _handler;
            }

            var egress = _config.CurrentValue.AI.Egress;

            // Start from a policy with no defaults — we curate the deny list
            // explicitly so the contract is auditable.
            var policy = new AntiSSRFPolicy(PolicyConfigOptions.None)
            {
                AllowPlainTextHttp = egress.AllowPlainTextHttp
            };

            // recommendedV1 is the curated superset: privateUse + loopback +
            // linkLocal + imds + multicast + uniqueLocal (IPv6 ULA fc00::/7) +
            // reserved + benchmarking. See ssrf-defense.md §6.4.
            policy.AddDeniedAddresses(IPAddressRanges.recommendedV1);

            // GetHandler() flips the policy's _editLock — the policy is now
            // immutable for the handler's lifetime. See ssrf-defense.md §6.3.
            _handler = policy.GetHandler();
            return _handler;
        }
    }
}
