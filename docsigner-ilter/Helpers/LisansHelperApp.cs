//using System;
//using System.IO;
//using System.Reflection;
//using tr.gov.tubitak.uekae.esya.api.common.util;
//using Microsoft.Extensions.Logging; // Bu zaten var

//namespace docsigner_ilter.Helpers
//{
//    public static class LisansHelperApp
//    {
//        private static bool licenseLoaded = false;
//        private const string LicenseFileName = "lisans.xml";
//        private const string CertificatesFolderName = "Certificates";

//        // ❌ Hatalı: ILogger logger = null (ambiguous)
//        // ✅ Düzeltilmiş: Tam namespace ile
//        public static void LoadLicense(string licenseType = "Free", Microsoft.Extensions.Logging.ILogger logger = null)
//        {
//            if (licenseLoaded)
//            {
//                logger?.LogDebug("Lisans zaten yüklenmiş, atlanıyor.");
//                return;
//            }

//            string assemblyLocation = Assembly.GetExecutingAssembly().Location;
//            string appDirectory = Path.GetDirectoryName(assemblyLocation);
//            string certificatesPath = Path.Combine(appDirectory, CertificatesFolderName);
//            string licenseFilePath = Path.Combine(certificatesPath, LicenseFileName);

//            logger?.LogInformation("Lisans yükleniyor: {LicenseType} | Path: {Path}", licenseType, licenseFilePath);

//            if (!Directory.Exists(certificatesPath))
//            {
//                Directory.CreateDirectory(certificatesPath);
//                logger?.LogWarning("Certificates klasörü oluşturuldu: {Path}", certificatesPath);
//            }

//            if (!File.Exists(licenseFilePath))
//                throw new FileNotFoundException($"Lisans dosyası bulunamadı: {licenseFilePath}. Lütfen TÜBİTAK'tan 'lisans.xml' indirip Certificates klasörüne koyun.");

//            try
//            {
//                using (var stream = new FileStream(licenseFilePath, FileMode.Open, FileAccess.Read))
//                {
//                    LicenseUtil.setLicenseXml(stream);
//                }
//                licenseLoaded = true;
//                logger?.LogInformation("Lisans başarıyla yüklendi: {LicenseType}", licenseType);
//            }
//            catch (Exception ex)
//            {
//                logger?.LogError(ex, "Lisans yükleme hatası: {LicenseType}", licenseType);
//                throw new Exception($"Lisans dosyası yüklenirken hata oluştu: {ex.Message}", ex);
//            }
//        }
//    }
//}
