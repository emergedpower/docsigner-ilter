using Microsoft.Extensions.Logging; // Tek ILogger kaynağı: Microsoft
using System;
using System.IO;
using System.Linq;
using System.Text.Json; // ✅ YENİ: Opsiyonel JSON log için (detaylı details)
using tr.gov.tubitak.uekae.esya.api.asn.x509;
using tr.gov.tubitak.uekae.esya.api.certificate.validation;
using tr.gov.tubitak.uekae.esya.api.certificate.validation.policy;

namespace docsigner_ilter.Helpers
{
    public static class EsyaValidationHelper
    {
        public static string ResolveConfigDir()
        {
            string baseDir = AppContext.BaseDirectory;
            string[] candidates =
            {
                Path.Combine(baseDir, "config"),
                Path.Combine(Directory.GetCurrentDirectory(), "config"),
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "config")
            };
            var found = candidates.FirstOrDefault(Directory.Exists);
            if (found == null)
                throw new DirectoryNotFoundException("config klasörü bulunamadı:\n" + string.Join("\n", candidates));
            return found;
        }

        public static ValidationPolicy LoadPolicy(string policyFileName, Microsoft.Extensions.Logging.ILogger logger = null)
        {
            var configDir = ResolveConfigDir();
            var policyPath = Path.Combine(configDir, policyFileName);
            if (!File.Exists(policyPath))
            {
                // ✅ İYİLEŞTİR: Detaylı hata log'la, ama throw et
                logger?.LogError("Policy dosyası yok: {PolicyPath}. Config dizini: {ConfigDir}", policyPath, configDir);
                throw new FileNotFoundException($"Policy dosyası yok: {policyPath}");
            }
            try
            {
                logger?.LogInformation("Policy yükleniyor: {PolicyPath}", policyPath);
                using var fs = new FileStream(policyPath, FileMode.Open, FileAccess.Read);
                return PolicyReader.readValidationPolicy(fs);
            }
            catch (Exception fsEx)
            {
                // ✅ YENİ: FileStream hatası yakala (Web API'de robust)
                logger?.LogError(fsEx, "Policy yükleme sırasında IO hatası: {PolicyPath}", policyPath);
                throw new InvalidOperationException($"Policy yüklenemedi: {fsEx.Message}", fsEx);
            }
        }

        public static void ValidateSignerCertificate(ECertificate signerCert, string policyFileName, Microsoft.Extensions.Logging.ILogger logger = null)
        {
            try
            {
                var policy = LoadPolicy(policyFileName, logger);
                var vs = CertificateValidation.createValidationSystem(policy);
                vs.setBaseValidationTime(DateTime.UtcNow);
                var result = CertificateValidation.validateCertificate(vs, signerCert);
                var status = result.getCertificateStatus(); // Enum: VALID / REVOKED / EXPIRED / UNKNOWN ...

                // Bazı sürümlerde sadece ToString() güvenilir şekilde mevcut.
                string details;
                try
                {
                    details = result.ToString();
                }
                catch
                {
                    details = $"Subject={signerCert.getSubject()}, Serial={signerCert.getSerialNumber()}";
                }

                // ✅ İYİLEŞTİR: JSON-like detay log (Web API monitoring için)
                var detailsObj = new { Status = status.ToString(), Subject = signerCert.getSubject()?.ToString(), Serial = signerCert.getSerialNumber() };
                logger?.LogInformation("Sertifika doğrulama sonucu: {Status} | {DetailsJson}", status, JsonSerializer.Serialize(detailsObj));

                if (status != CertificateStatus.VALID)
                    throw new Exception($"İmzacı sertifikası VALID değil: {status}. Detay: {details}");
            }
            catch (Exception valEx)
            {
                // ✅ YENİ: Validation hatası yakala, log'la ama throw et (API response için)
                logger?.LogError(valEx, "Sertifika validation hatası: Policy={Policy}, CertSubject={Subject}", policyFileName, signerCert?.getSubject());
                throw;
            }
        }
    }
}
