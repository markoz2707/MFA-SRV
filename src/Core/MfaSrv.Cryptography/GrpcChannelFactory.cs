using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using Grpc.Net.Client;

namespace MfaSrv.Cryptography;

/// <summary>
/// Creates gRPC channels with mTLS client certificate authentication.
/// Used by agents to establish authenticated connections to the Central Server.
/// </summary>
public static class GrpcChannelFactory
{
    private static readonly TimeSpan DefaultConnectionTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan DefaultKeepAlivePingDelay = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan DefaultKeepAlivePingTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Creates a gRPC channel configured with mTLS client certificate authentication.
    /// The client certificate is presented during the TLS handshake for mutual authentication.
    /// </summary>
    /// <param name="serverUrl">The gRPC server URL (must be https://).</param>
    /// <param name="clientCert">The client certificate with private key for mTLS.</param>
    /// <param name="caCert">Optional CA certificate for custom server validation.
    /// If provided, the server's certificate is validated against this CA instead of the system trust store.</param>
    /// <returns>A configured <see cref="GrpcChannel"/> ready for mTLS communication.</returns>
    public static GrpcChannel CreateMtlsChannel(
        string serverUrl,
        X509Certificate2 clientCert,
        X509Certificate2? caCert = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serverUrl);
        ArgumentNullException.ThrowIfNull(clientCert);

        if (!clientCert.HasPrivateKey)
            throw new ArgumentException("Client certificate must contain a private key for mTLS.", nameof(clientCert));

        var handler = new SocketsHttpHandler
        {
            ConnectTimeout = DefaultConnectionTimeout,
            KeepAlivePingDelay = DefaultKeepAlivePingDelay,
            KeepAlivePingTimeout = DefaultKeepAlivePingTimeout,
            KeepAlivePingPolicy = HttpKeepAlivePingPolicy.WithActiveRequests,
            EnableMultipleHttp2Connections = true,
            SslOptions = new SslClientAuthenticationOptions
            {
                ClientCertificates = new X509CertificateCollection { clientCert },
                RemoteCertificateValidationCallback = caCert != null
                    ? CreateCaValidationCallback(caCert)
                    : null // Use default system validation
            }
        };

        var channelOptions = new GrpcChannelOptions
        {
            HttpHandler = handler,
            DisposeHttpClient = true
        };

        return GrpcChannel.ForAddress(serverUrl, channelOptions);
    }

    /// <summary>
    /// Creates a gRPC channel without TLS for development and testing only.
    /// WARNING: This channel sends traffic in plaintext. Never use in production.
    /// </summary>
    public static GrpcChannel CreateInsecureChannel(string serverUrl)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serverUrl);

        var handler = new SocketsHttpHandler
        {
            ConnectTimeout = DefaultConnectionTimeout,
            EnableMultipleHttp2Connections = true
        };

        var channelOptions = new GrpcChannelOptions
        {
            HttpHandler = handler,
            DisposeHttpClient = true
        };

        return GrpcChannel.ForAddress(serverUrl, channelOptions);
    }

    /// <summary>
    /// Creates a remote certificate validation callback that validates
    /// the server's certificate against a specific CA certificate.
    /// This is used when agents connect to the Central Server using a private CA
    /// that is not in the system trust store.
    /// </summary>
    private static RemoteCertificateValidationCallback CreateCaValidationCallback(X509Certificate2 caCert)
    {
        return (sender, certificate, chain, sslPolicyErrors) =>
        {
            if (certificate == null)
                return false;

            using var serverCert = new X509Certificate2(certificate);
            using var validationChain = new X509Chain();

            validationChain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
            validationChain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
            validationChain.ChainPolicy.CustomTrustStore.Add(caCert);

            return validationChain.Build(serverCert);
        };
    }
}
