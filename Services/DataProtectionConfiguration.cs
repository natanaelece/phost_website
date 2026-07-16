using System;
using System.IO;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace PremierAPI.Services
{
    public static class DataProtectionConfiguration
    {
        public static void AddPremierDataProtection(
            IServiceCollection services,
            IConfiguration configuration,
            string contentRootPath)
        {
            string keyRingPath = configuration["DataProtection:KeyRingPath"] ?? ".data-protection-keys";
            if (!Path.IsPathRooted(keyRingPath))
                keyRingPath = Path.Combine(contentRootPath, keyRingPath);
            Directory.CreateDirectory(keyRingPath);

            string passwordSource = configuration["DataProtection:CertificatePassword"] ?? "";
            if (string.IsNullOrWhiteSpace(passwordSource))
                passwordSource = configuration["AdminToken"] ?? "";
            if (string.IsNullOrWhiteSpace(passwordSource))
                throw new InvalidOperationException("Configure DataProtection:CertificatePassword ou AdminToken para proteger o key ring.");

            string certificatePassword = Convert.ToHexString(SHA256.HashData(
                Encoding.UTF8.GetBytes("PremierHost.DataProtection.Certificate.v1:" + passwordSource)));
            string certificatePath = Path.Combine(keyRingPath, "key-encryption.pfx");
            X509Certificate2 certificate = LoadOrCreateCertificate(certificatePath, certificatePassword);

            services.AddDataProtection()
                .PersistKeysToFileSystem(new DirectoryInfo(keyRingPath))
                .ProtectKeysWithCertificate(certificate)
                .SetApplicationName("PremierHost.PremierAPI");
        }

        private static X509Certificate2 LoadOrCreateCertificate(string path, string password)
        {
            if (!File.Exists(path))
            {
                using RSA rsa = RSA.Create(3072);
                var request = new CertificateRequest(
                    "CN=PremierHost Data Protection",
                    rsa,
                    HashAlgorithmName.SHA256,
                    RSASignaturePadding.Pkcs1);
                using X509Certificate2 generated = request.CreateSelfSigned(
                    DateTimeOffset.UtcNow.AddMinutes(-5),
                    DateTimeOffset.UtcNow.AddYears(10));
                File.WriteAllBytes(path, generated.Export(X509ContentType.Pfx, password));
                if (!OperatingSystem.IsWindows())
                    File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }

            return new X509Certificate2(
                File.ReadAllBytes(path),
                password,
                X509KeyStorageFlags.EphemeralKeySet | X509KeyStorageFlags.Exportable);
        }
    }
}
