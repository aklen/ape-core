using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Ape.Core.Network.Transport;

/// <summary>
/// Helper for generating self-signed certificates for QUIC development.
/// </summary>
public static class CertificateHelper
{
    /// <summary>
    /// Generate a self-signed certificate for development/testing.
    /// Not suitable for production use.
    /// </summary>
    public static X509Certificate2 GenerateSelfSignedCertificate(string subjectName)
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest(
            $"CN={subjectName}",
            rsa,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);

        // Add key usage
        request.CertificateExtensions.Add(
            new X509KeyUsageExtension(
                X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment,
                critical: true));

        // Add enhanced key usage for server authentication
        request.CertificateExtensions.Add(
            new X509EnhancedKeyUsageExtension(
                new OidCollection { new Oid("1.3.6.1.5.5.7.3.1") }, // Server Authentication
                critical: true));

        // Add subject alternative name
        var sanBuilder = new SubjectAlternativeNameBuilder();
        sanBuilder.AddDnsName("localhost");
        sanBuilder.AddDnsName(subjectName);
        sanBuilder.AddIpAddress(System.Net.IPAddress.Loopback);
        sanBuilder.AddIpAddress(System.Net.IPAddress.IPv6Loopback);
        sanBuilder.AddIpAddress(System.Net.IPAddress.Any);
        request.CertificateExtensions.Add(sanBuilder.Build());

        // Create self-signed certificate
        var certificate = request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddDays(-1),
            DateTimeOffset.UtcNow.AddYears(1));

        // Export with private key
        return new X509Certificate2(
            certificate.Export(X509ContentType.Pfx, ""),
            "",
            X509KeyStorageFlags.Exportable);
    }
}
