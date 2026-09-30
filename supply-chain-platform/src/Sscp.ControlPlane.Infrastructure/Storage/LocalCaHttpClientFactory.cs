// HTTP client for the S3 SDK that trusts exactly the platform's local root CA (MinIO serves
// a certificate issued by it). Host names are always verified; only the root changes.
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using Amazon.Runtime;

namespace Sscp.ControlPlane.Infrastructure.Storage;

public sealed class LocalCaHttpClientFactory(string? caCertificatePath) : HttpClientFactory
{
    public override HttpClient CreateHttpClient(IClientConfig clientConfig) => new(CreateHandler(caCertificatePath));

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
            {
                if (certificate is null || errors.HasFlag(SslPolicyErrors.RemoteCertificateNameMismatch))
                {
                    return false;
                }

                using var chain = new X509Chain();
                chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
                chain.ChainPolicy.CustomTrustStore.Add(root);
                chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
                return chain.Build(new X509Certificate2(certificate));
            },
        };
        return handler;
    }
}
