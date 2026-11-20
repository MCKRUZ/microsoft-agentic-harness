using Azure.Identity;

namespace Infrastructure.AI.Resilience;

// Reads Azure.Identity credential failures, which carry no HTTP status (#957).
public partial class DefaultProviderErrorClassifier
{
    /// <summary>Which kind of credential failure, if any, a link of the exception chain is.</summary>
    private enum CredentialFailure
    {
        None,
        Unavailable,
        Rejected
    }

    /// <summary>
    /// Reads whether a single exception is an Azure.Identity credential failure, and which kind.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="CredentialUnavailableException"/> means no credential could be tried at all: a managed identity
    /// endpoint (IMDS) that did not answer, or a developer credential that is not signed in. The endpoint case can
    /// recover, so it is transient. A misconfigured host pays a few retries for that, which is the cheap direction to
    /// be wrong in.
    /// </para>
    /// <para>
    /// <see cref="AuthenticationFailedException"/> means a credential was tried and refused. The chain's providers
    /// share the identity as they share an API key, so it stops the chain as a 401 does. A refusal caused by a
    /// transport fault, or by a transient status on an exception <see cref="TryGetHttpStatus"/> reads, never reaches
    /// this reading: <see cref="Classify"/> returns those as transient first. MSAL's own service error carries its
    /// status where this class does not yet look, so an Entra ID outage behind a client-secret credential still reads
    /// as a refusal (#1053).
    /// </para>
    /// <para>
    /// The subclass is tested first, since <see cref="CredentialUnavailableException"/> derives from
    /// <see cref="AuthenticationFailedException"/>.
    /// </para>
    /// <para>
    /// <see cref="Inspect"/> keeps the outermost credential link it finds. That link is the credential's own final
    /// verdict: a chained credential that found one credential unavailable and then had the next one refused throws the
    /// refusal, with the unavailable one inside it, and a refusal is what happened.
    /// </para>
    /// </remarks>
    private static CredentialFailure ReadCredentialFailure(Exception exception) => exception switch
    {
        CredentialUnavailableException => CredentialFailure.Unavailable,
        AuthenticationFailedException => CredentialFailure.Rejected,
        _ => CredentialFailure.None
    };
}
