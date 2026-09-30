// Trusts exactly one root CA for outgoing TLS connections (Keycloak, MinIO).
//
// Services validate certificates against the platform's local root CA instead of turning
// validation off. When no CA path is configured the operating system trust store is used.
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;

namespace Commerce.BuildingBlocks.Security;

public static class LocalCaTrust
{
    public static HttpMessageHandler CreateHandler(string? caCertificatePath)
    {
        var handler = new SocketsHttpHandler();
        if (string.IsNullOrWhiteSpace(caCertificatePath))
        {
            return handler;
        }

        var root = X509CertificateLoader.LoadCertificateFromFile(caCertificatePath);
        handler.SslOptions = new SslClientAuthenticationOptions
        {
            RemoteCertificateValidationCallback = (_, certificate, _, errors) =>
                certificate is not null && ValidateAgainst(root, new X509Certificate2(certificate), errors),
        };
        return handler;
    }

    public static bool ValidateAgainst(X509Certificate2 root, X509Certificate2 certificate, SslPolicyErrors errors)
    {
        // Host name mismatches are never tolerated; only the chain is re-evaluated.
        if (errors.HasFlag(SslPolicyErrors.RemoteCertificateNameMismatch) || errors.HasFlag(SslPolicyErrors.RemoteCertificateNotAvailable))
        {
            return false;
        }

        using var chain = new X509Chain();
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.Add(root);
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        return chain.Build(certificate);
    }
}
