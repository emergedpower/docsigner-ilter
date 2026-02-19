using Microsoft.Extensions.Logging;
using Net.Pkcs11Interop.Common;
using Net.Pkcs11Interop.HighLevelAPI;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Numerics;
using System.Net.Http;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Security.Cryptography.Xml;
using System.Formats.Asn1;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using System.Text.RegularExpressions; // ✅ eklendi (TCKN parse)
using System.Security.Principal;
using Microsoft.Win32;
using iText.Bouncycastleconnector;
using iText.Commons.Bouncycastle.Cert;
using iText.Forms.Form.Element;
using iText.IO.Font;
using iText.IO.Font.Constants;
using iText.Kernel.Colors;
using iText.Kernel.Font;
using iText.Kernel.Pdf;
using iText.Layout.Borders;
using iText.Layout.Element;
using iText.Signatures;
using PdfRectangle = iText.Kernel.Geom.Rectangle;
using tr.gov.tubitak.uekae.esya.api.asn.x509;
using tr.gov.tubitak.uekae.esya.api.common.util;
using tr.gov.tubitak.uekae.esya.api.smartcard.pkcs11;
using tr.gov.tubitak.uekae.esya.api.smartcard.pkcs11.card.ops;

namespace docsigner_ilter.Services
{
    public class ReceptServiceApp
    {
        private readonly ILogger<ReceptServiceApp> _logger;
        private readonly Pkcs11InteropFactories _factories = new Pkcs11InteropFactories();

        private const string Pkcs11Dll = @"C:\Windows\System32\akisp11.dll";

        // ✅ Session cache (slot bazlı)
        private static readonly ConcurrentDictionary<int, ISession> _sessionCache =
            new ConcurrentDictionary<int, ISession>();

        // ✅ slot bazlı kilit (aynı kartta aynı anda login/sign çakışmasın)
        private static readonly ConcurrentDictionary<int, SemaphoreSlim> _slotLocks =
            new ConcurrentDictionary<int, SemaphoreSlim>();

        // ✅ PKCS#11 library’yi canlı tut (cache’li session’lar ölmesin)
        private static IPkcs11Library? _sharedLib;
        private static readonly object _libSync = new object();

        // ✅ AppType.SingleThreaded kullanıyorsun: PKCS#11 çağrılarını uygulama kilitlemeli
        private static readonly SemaphoreSlim _pkcs11GlobalLock = new SemaphoreSlim(1, 1);
        private static readonly HttpClient _httpClient = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(10)
        };

        public ReceptServiceApp(ILogger<ReceptServiceApp> logger)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        private IPkcs11Library GetOrLoadLibrary()
        {
            lock (_libSync)
            {
                if (_sharedLib != null)
                    return _sharedLib;

                _sharedLib = _factories.Pkcs11LibraryFactory.LoadPkcs11Library(
                    _factories,
                    Pkcs11Dll,
                    AppType.SingleThreaded);

                _logger.LogInformation("PKCS#11 library yüklendi ve paylaşımlı hale getirildi: {Dll}", Pkcs11Dll);
                return _sharedLib;
            }
        }

        // ✅ eklendi: Login policy (alreadyLoggedIn çözümü burada)
        private void LoginWithPolicy(ISession session, string pin, bool forceFreshLogin, int slotIdx)
        {
            byte[] pinBytes = Encoding.UTF8.GetBytes(pin);

            if (forceFreshLogin)
            {
                // Fresh istiyorsan önce logout dene
                try { session.Logout(); }
                catch (Pkcs11Exception ex) when (ex.RV == CKR.CKR_USER_NOT_LOGGED_IN) { }
                catch { /* bazı driver’lar burada hata verebilir, görmezden gel */ }

                try
                {
                    session.Login(CKU.CKU_USER, pinBytes);
                }
                catch (Pkcs11Exception ex) when (ex.RV == CKR.CKR_USER_ALREADY_LOGGED_IN)
                {
                    _logger.LogWarning("ForceFreshLogin istendi ama token zaten login (slot {Slot}). Devam ediyorum.", slotIdx);
                }
            }
            else
            {
                try
                {
                    session.Login(CKU.CKU_USER, pinBytes);
                }
                catch (Pkcs11Exception ex) when (ex.RV == CKR.CKR_USER_ALREADY_LOGGED_IN)
                {
                    _logger.LogInformation("Token zaten login (slot {Slot}). Devam ediyorum.", slotIdx);
                }
            }
        }

        // ✅ eklendi: Sertifikadan TCKN çıkarma
        // Not: DN içindeki SERIALNUMBER (OID 2.5.4.5) çoğu NES’te TCKN taşır.
        // Alternatif: UID (0.9.2342.19200300.100.1.1) gibi alanlarda da olabiliyor.
        private static string? ExtractTcknFromCert(X509Certificate2 cert)
        {
            var subject = cert.Subject ?? string.Empty;

            // 1) SERIALNUMBER=TR12345678901 veya SERIALNUMBER=12345678901
            var m = Regex.Match(subject, @"(?i)(?:SERIALNUMBER|2\.5\.4\.5)\s*=\s*(?:TR)?\s*(\d{11})");
            if (m.Success) return m.Groups[1].Value;

            // 2) UID=12345678901
            m = Regex.Match(subject, @"(?i)\bUID\s*=\s*(\d{11})\b");
            if (m.Success) return m.Groups[1].Value;

            // 3) TCKN=12345678901 gibi nadir format
            m = Regex.Match(subject, @"(?i)\bTCKN\s*=\s*(\d{11})\b");
            if (m.Success) return m.Groups[1].Value;

            // 4) Son çare: subject içinde 11 hane yakala (false-positive riski var)
            m = Regex.Match(subject, @"\b\d{11}\b");
            if (m.Success) return m.Value;

            return null;
        }

        // ✅ eklendi: log için maskeleme
        private static string MaskTckn(string? tckn)
        {
            if (string.IsNullOrWhiteSpace(tckn) || tckn.Length != 11)
                return "(yok)";

            // 12345678901 -> 123****8901
            return tckn.Substring(0, 3) + "****" + tckn.Substring(7, 4);
        }

        public class PdfSignatureOptions
        {
            public int? PageNumber { get; set; }
            public float? X { get; set; }
            public float? Y { get; set; }
            public float? Width { get; set; }
            public float? Height { get; set; }
            public string? SignatureFieldName { get; set; }
            public string? SignerDisplayName { get; set; }
            public string? Reason { get; set; }
            public string? Location { get; set; }
            public string? FileName { get; set; }
            public float Margin { get; set; } = 24f;
            public bool EnableTimestamp { get; set; } = false;
            public string? TsaUrl { get; set; }
            public string? TsaUsername { get; set; }
            public string? TsaPassword { get; set; }
            public bool AutoSetupTrustChain { get; set; } = true;
            public bool TryInstallTrustToLocalMachine { get; set; } = true;
            public bool ConfigureAcrobatWindowsStoreIntegration { get; set; } = true;
        }

        public class SignerTrustSetupResult
        {
            public bool Success { get; set; }
            public string Message { get; set; } = string.Empty;
            public string SignerSubject { get; set; } = string.Empty;
            public string SignerThumbprint { get; set; } = string.Empty;
            public bool ValidationSucceeded { get; set; }
            public string ValidationStatus { get; set; } = string.Empty;
            public int CandidateRootCount { get; set; }
            public int CandidateIntermediateCount { get; set; }
            public int AddedRootCount { get; set; }
            public int AddedIntermediateCount { get; set; }
            public int ExistingRootCount { get; set; }
            public int ExistingIntermediateCount { get; set; }
            public int AcrobatRegistryValuesWritten { get; set; }
            public bool CurrentUserChainReady { get; set; }
            public bool LocalMachineChainReady { get; set; }
            public bool AcrobatWindowsStoreReady { get; set; }
            public string ReadinessLevel { get; set; } = string.Empty;
            public List<string> Warnings { get; set; } = new List<string>();
        }

        public class PdfSignatureValidationResult
        {
            public bool Success { get; set; }
            public string Message { get; set; } = string.Empty;
            public int SignatureCount { get; set; }
            public int CryptographicallyValidCount { get; set; }
            public int TrustedChainCount { get; set; }
            public List<PdfSignatureValidationItem> Signatures { get; set; } = new List<PdfSignatureValidationItem>();
        }

        public class PdfSignatureValidationItem
        {
            public string FieldName { get; set; } = string.Empty;
            public string SignerSubject { get; set; } = string.Empty;
            public string SignerThumbprint { get; set; } = string.Empty;
            public bool CoversWholeDocument { get; set; }
            public bool IntegrityValid { get; set; }
            public bool TrustedChain { get; set; }
            public string ChainStatus { get; set; } = string.Empty;
        }

        #region Cihaz Listeleme
        public List<SignatureDeviceDto> GetSignatureDevices()
        {
            var devices = new List<SignatureDeviceDto>();

            // SingleThreaded => global lock
            _pkcs11GlobalLock.Wait();
            try
            {
                var lib = GetOrLoadLibrary();
                var slots = lib.GetSlotList(SlotsType.WithTokenPresent);

                if (slots == null || slots.Count == 0)
                {
                    _logger.LogInformation("Takılı e-imza kartı/token bulunamadı.");
                    return devices;
                }

                for (int i = 0; i < slots.Count; i++)
                {
                    try
                    {
                        var slot = slots[i];
                        var tokenInfo = slot.GetTokenInfo();
                        string serial = (tokenInfo.SerialNumber ?? string.Empty).Trim(); // token/kart seri (TCKN değil)
                        string label = (tokenInfo.Label ?? string.Empty).Trim();

                        using var session = slot.OpenSession(SessionType.ReadOnly);

                        var searchTemplate = new List<IObjectAttribute>
                        {
                            session.Factories.ObjectAttributeFactory.Create(CKA.CKA_CLASS, CKO.CKO_CERTIFICATE),
                            session.Factories.ObjectAttributeFactory.Create(CKA.CKA_CERTIFICATE_TYPE, CKC.CKC_X_509)
                        };

                        var certObjects = session.FindAllObjects(searchTemplate);
                        string subject;
                        string? tckn = null;

                        if (certObjects.Count > 0)
                        {
                            var certValueAttr = session.GetAttributeValue(
                                certObjects[0],
                                new List<CKA> { CKA.CKA_VALUE })[0];

                            var certBytes = certValueAttr.GetValueAsByteArray();
                            var x509 = new X509Certificate2(certBytes);

                            if (IsCertDateValid(x509))
                            {
                                subject = x509.GetNameInfo(X509NameType.SimpleName, false) ?? x509.Subject;

                                // ✅ TCKN çek
                                tckn = ExtractTcknFromCert(x509);

                                // ✅ LOG: Sertifika subject + maskeli tckn
                                _logger.LogInformation("Slot {Index} sertifika okundu. Label={Label}, TokenSerial={TokenSerial}, Subject={Subject}, TCKN={TcknMasked}",
                                    i, label, serial, subject, MaskTckn(tckn));

                                // İstersen tam TCKN logla (KVKK risk!)
                                // _logger.LogWarning("Slot {Index} TCKN(UNMASKED)={Tckn}", i, tckn);
                            }
                            else
                            {
                                subject = "(Geçersiz tarihli sertifika)";
                                _logger.LogWarning("Slot {Index} sertifika tarih geçersiz. Label={Label}, TokenSerial={TokenSerial}, SubjectRaw={RawSubject}",
                                    i, label, serial, x509.Subject);
                            }
                        }
                        else
                        {
                            subject = "(Sertifika bulunamadı)";
                            _logger.LogWarning("Slot {Index} içinde sertifika bulunamadı. Label={Label}, TokenSerial={TokenSerial}",
                                i, label, serial);
                        }

                        devices.Add(new SignatureDeviceDto
                        {
                            Id = i,
                            Label = $"{label} ({serial}) - {subject}",
                            Serial = serial,
                            Subject = subject,

                            // ✅ web app’e gönderilecek alan
                            TcKimlikNo = tckn
                        });
                    }
                    catch (Exception exSlot)
                    {
                        _logger.LogWarning(exSlot, "Slot {Index} okunurken hata oluştu.", i);
                    }
                }

                return devices;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "GetSignatureDevices sırasında genel hata oluştu.");
                return devices;
            }
            finally
            {
                _pkcs11GlobalLock.Release();
            }
        }

        private bool IsCertDateValid(X509Certificate2 cert)
        {
            var nb = cert.NotBefore.ToUniversalTime();
            var na = cert.NotAfter.ToUniversalTime();
            var now = DateTime.UtcNow;
            return now > nb && now < na;
        }

        public SignerTrustSetupResult EnsureSignerTrust(int? slotIndex, bool applyChanges = true)
        {
            var result = new SignerTrustSetupResult();

            if (!slotIndex.HasValue || slotIndex.Value < 0)
            {
                result.Success = false;
                result.Message = "Geçersiz slot bilgisi.";
                return result;
            }

            _pkcs11GlobalLock.Wait();
            try
            {
                var lib = GetOrLoadLibrary();
                var slots = lib.GetSlotList(SlotsType.WithTokenPresent);
                if (slots == null || slots.Count == 0)
                    throw new Exception("Takılı e-imza kartı/token bulunamadı.");

                int slotIdx = slotIndex.Value;
                if (slotIdx < 0 || slotIdx >= slots.Count)
                    throw new Exception("Geçersiz slot.");

                using var session = slots[slotIdx].OpenSession(SessionType.ReadOnly);
                var signerCert = GetBestSigningCertificateFromSession(session);
                var tokenCertificates = GetAllCertificatesFromSession(session);
                result.SignerSubject = signerCert.Subject;
                result.SignerThumbprint = signerCert.Thumbprint ?? string.Empty;

                var chainCandidates = BuildTrustChainCandidates(signerCert, result, tokenCertificates);
                result.CandidateRootCount = chainCandidates.Count(x =>
                    !string.Equals(x.Thumbprint, signerCert.Thumbprint, StringComparison.OrdinalIgnoreCase) &&
                    DistinguishedNameEquals(x.Subject, x.Issuer));
                result.CandidateIntermediateCount = chainCandidates.Count(x =>
                    !string.Equals(x.Thumbprint, signerCert.Thumbprint, StringComparison.OrdinalIgnoreCase) &&
                    !DistinguishedNameEquals(x.Subject, x.Issuer));
                if (result.CandidateRootCount == 0 && result.CandidateIntermediateCount == 0)
                {
                    result.Warnings.Add("İmzalayan sertifikası dışında zincir adayı bulunamadı (AIA/store erişimini kontrol edin).");
                }
                if (applyChanges)
                {
                    InstallChainCertificatesToStores(chainCandidates, signerCert, result, tryLocalMachine: true);
                    EnsureAcrobatWindowsStoreIntegration(result);
                }
                else
                {
                    CountChainCertificatesInStoreLocation(
                        chainCandidates,
                        signerCert,
                        StoreLocation.CurrentUser,
                        out int existingRoot,
                        out int existingIntermediate);
                    result.ExistingRootCount = existingRoot;
                    result.ExistingIntermediateCount = existingIntermediate;
                }
                result.CurrentUserChainReady = AreChainCertificatesPresentInStoreLocation(
                    chainCandidates,
                    signerCert,
                    StoreLocation.CurrentUser);
                result.LocalMachineChainReady = AreChainCertificatesPresentInStoreLocation(
                    chainCandidates,
                    signerCert,
                    StoreLocation.LocalMachine);
                result.AcrobatWindowsStoreReady = IsAcrobatWindowsStoreIntegrationReady(result);

                using var validationChain = new X509Chain();
                validationChain.ChainPolicy.RevocationMode = X509RevocationMode.Online;
                validationChain.ChainPolicy.RevocationFlag = X509RevocationFlag.EntireChain;
                validationChain.ChainPolicy.VerificationFlags = X509VerificationFlags.NoFlag;
                validationChain.ChainPolicy.DisableCertificateDownloads = false;
                validationChain.ChainPolicy.UrlRetrievalTimeout = TimeSpan.FromSeconds(8);

                bool valid = validationChain.Build(signerCert);
                result.ValidationSucceeded = valid;
                result.ValidationStatus = validationChain.ChainStatus == null || validationChain.ChainStatus.Length == 0
                    ? "OK"
                    : string.Join(", ", validationChain.ChainStatus.Select(x => x.Status.ToString()).Distinct());
                result.ReadinessLevel = EvaluateTrustReadiness(result);

                result.Success = true;
                result.Message =
                    $"Güven zinciri kontrolü tamamlandı. Aday Root: {result.CandidateRootCount}, Aday Ara: {result.CandidateIntermediateCount}. " +
                    $"Root eklendi: {result.AddedRootCount} (mevcut: {result.ExistingRootCount}), " +
                    $"Ara eklendi: {result.AddedIntermediateCount} (mevcut: {result.ExistingIntermediateCount}). " +
                    $"Doğrulama: {(result.ValidationSucceeded ? "Başarılı" : "Başarısız")} ({result.ValidationStatus}). " +
                    $"Acrobat ayar yazımı: {result.AcrobatRegistryValuesWritten}. " +
                    $"Hazırlık: {result.ReadinessLevel} (CU Zincir={(result.CurrentUserChainReady ? "OK" : "Eksik")}, " +
                    $"LM Zincir={(result.LocalMachineChainReady ? "OK" : "Eksik")}, " +
                    $"Acrobat Windows Store={(result.AcrobatWindowsStoreReady ? "OK" : "Eksik")}).";

                _logger.LogInformation(
                    "Trust setup tamamlandı. Slot={Slot}, Subject={Subject}, CandidateRoot={CandidateRoot}, CandidateInt={CandidateInt}, RootAdded={RootAdded}, IntAdded={IntAdded}, Validation={Validation}, Readiness={Readiness}, CUReady={CUReady}, LMReady={LMReady}, AcrobatReady={AcrobatReady}",
                    slotIdx, result.SignerSubject, result.CandidateRootCount, result.CandidateIntermediateCount, result.AddedRootCount, result.AddedIntermediateCount, result.ValidationStatus, result.ReadinessLevel, result.CurrentUserChainReady, result.LocalMachineChainReady, result.AcrobatWindowsStoreReady);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "EnsureSignerTrust sırasında hata.");
                result.Success = false;
                result.Message = ex.Message;
            }
            finally
            {
                _pkcs11GlobalLock.Release();
            }

            return result;
        }
        #endregion

        #region İMZALAMA
        public async Task<(byte[] signedPdf, string filePath, string signerName, bool timestampApplied)> SignPdf(
            byte[] pdfContent,
            int? slotIndex,
            string pin,
            PdfSignatureOptions? options = null,
            bool forceFreshLogin = false)
        {
            if (pdfContent == null || pdfContent.Length == 0)
                throw new ArgumentException("PDF boş");
            if (string.IsNullOrWhiteSpace(pin))
                throw new ArgumentException("PIN boş");
            if (!slotIndex.HasValue || slotIndex.Value < 0)
                throw new Exception("Kart bulunamadı");

            options ??= new PdfSignatureOptions();

            string workDir = Path.Combine(AppContext.BaseDirectory, "SignedDocuments");
            Directory.CreateDirectory(workDir);

            int slotIdx = slotIndex.Value;
            var gate = _slotLocks.GetOrAdd(slotIdx, _ => new SemaphoreSlim(1, 1));
            await gate.WaitAsync().ConfigureAwait(false);

            try
            {
                await _pkcs11GlobalLock.WaitAsync().ConfigureAwait(false);
                try
                {
                    var lib = GetOrLoadLibrary();
                    int retryCount = 0;
                    const int maxRetries = 2;

                    while (retryCount <= maxRetries)
                    {
                        try
                        {
                            var slots = lib.GetSlotList(SlotsType.WithTokenPresent);
                            if (slots == null || slots.Count == 0)
                                throw new Exception("Kart bulunamadı");
                            if (slotIdx < 0 || slotIdx >= slots.Count)
                                throw new Exception("Kart bulunamadı");

                            bool attemptForceFreshLogin = forceFreshLogin || retryCount > 0;
                            var session = GetOrCreateSigningSession(
                                slots,
                                slotIdx,
                                pin,
                                forceFreshLogin: attemptForceFreshLogin,
                                forPdfSigning: true);

                            var signingMaterial = GetSigningMaterial(session);
                            var cert = signingMaterial.certificate;
                            var key = signingMaterial.privateKey;
                            var placement = ResolveSignaturePlacement(pdfContent, options);
                            var signerName = ResolveSignerName(cert, options.SignerDisplayName);
                            var fieldName = BuildFieldName(options.SignatureFieldName);
                            var chainWithSource = BuildCertificateChainWithSource(cert, signingMaterial.certificates);
                            var chain = chainWithSource.chain;
                            var chainSourceCertificates = chainWithSource.sourceCertificates;
                            LogChainEmbeddingDiagnostics(cert, signingMaterial.certificates, chainSourceCertificates);
                            bool shouldUseTimestamp = options.EnableTimestamp && !string.IsNullOrWhiteSpace(options.TsaUrl);

                            if (options.AutoSetupTrustChain)
                            {
                                try
                                {
                                    var trustWarmup = new SignerTrustSetupResult();
                                    var trustCandidates = BuildTrustChainCandidates(cert, trustWarmup, signingMaterial.certificates);
                                    InstallChainCertificatesToStores(
                                        trustCandidates,
                                        cert,
                                        trustWarmup,
                                        options.TryInstallTrustToLocalMachine);
                                    if (options.ConfigureAcrobatWindowsStoreIntegration)
                                        EnsureAcrobatWindowsStoreIntegration(trustWarmup);

                                    _logger.LogInformation(
                                        "PDF imza öncesi trust warm-up tamamlandı. AddedRoot={Root}, AddedInt={Int}, Validation={Validation}, AcrobatReg={AcrobatReg}",
                                        trustWarmup.AddedRootCount,
                                        trustWarmup.AddedIntermediateCount,
                                        trustWarmup.ValidationStatus,
                                        trustWarmup.AcrobatRegistryValuesWritten);
                                }
                                catch (Exception trustEx)
                                {
                                    _logger.LogWarning(trustEx, "PDF imza öncesi trust warm-up sırasında hata oluştu, imzalama devam edecek.");
                                }
                            }

                            var signatureAppearance = BuildSignatureAppearance(
                                fieldName,
                                signerName,
                                cert,
                                shouldUseTimestamp);

                            var signerProperties = new SignerProperties()
                                .SetFieldName(fieldName)
                                .SetPageNumber(placement.PageNumber)
                                .SetPageRect(placement.Rect)
                                .SetSignDate(DateTime.Now)
                                .SetSignatureCreator("docsigner-ILTER")
                                .SetReason(string.IsNullOrWhiteSpace(options.Reason) ? "Elektronik imza" : options.Reason)
                                .SetLocation(string.IsNullOrWhiteSpace(options.Location) ? "Türkiye" : options.Location)
                                .SetSignatureAppearance(signatureAppearance);

                            var externalSignature = new Pkcs11ExternalSignature(session, key);

                            var twoPhaseSigner = new PadesTwoPhaseSigningHelper()
                                .SetEstimatedSize(24000)
                                .SetStampingProperties(new StampingProperties().UseAppendMode());

                            bool timestampApplied = false;
                            bool tryLtProfile = false;
                            if (shouldUseTimestamp)
                            {
                                var tsa = new TSAClientBouncyCastle(
                                    options.TsaUrl,
                                    options.TsaUsername,
                                    options.TsaPassword,
                                    8192,
                                    "SHA-256");

                                twoPhaseSigner.SetTSAClient(tsa);
                                twoPhaseSigner.SetOcspClient(new OcspClientBouncyCastle());
                                twoPhaseSigner.SetCrlClient(new CrlClientOnline());
                                twoPhaseSigner.SetTrustedCertificates(chain.ToList());
                                timestampApplied = true;
                                tryLtProfile = true;
                            }

                            byte[] preparedPdfBytes;
                            var cmsContainer = default(iText.Signatures.Cms.CMSContainer)!;

                            using (var prepareInputReader = new PdfReader(new MemoryStream(pdfContent)))
                            using (var preparedOutputStream = new MemoryStream())
                            {
                                cmsContainer = twoPhaseSigner.CreateCMSContainerWithoutSignature(
                                    chain,
                                    "SHA-256",
                                    prepareInputReader,
                                    preparedOutputStream,
                                    signerProperties);

                                preparedPdfBytes = preparedOutputStream.ToArray();
                            }

                            byte[] signedPdfBytes;
                            string profileUsed;
                            if (timestampApplied && tryLtProfile)
                            {
                                try
                                {
                                    using var ltReader = new PdfReader(new MemoryStream(preparedPdfBytes));
                                    using var ltOutputStream = new MemoryStream();
                                    twoPhaseSigner.SignCMSContainerWithBaselineLTProfile(
                                        externalSignature,
                                        ltReader,
                                        ltOutputStream,
                                        fieldName,
                                        cmsContainer);
                                    signedPdfBytes = ltOutputStream.ToArray();
                                    profileUsed = "LT";
                                }
                                catch (Exception ltEx)
                                {
                                    _logger.LogWarning(ltEx, "Baseline-LT imza başarısız oldu, Baseline-T profiline düşülüyor.");
                                    using var tReader = new PdfReader(new MemoryStream(preparedPdfBytes));
                                    using var tOutputStream = new MemoryStream();
                                    twoPhaseSigner.SignCMSContainerWithBaselineTProfile(
                                        externalSignature,
                                        tReader,
                                        tOutputStream,
                                        fieldName,
                                        cmsContainer);
                                    signedPdfBytes = tOutputStream.ToArray();
                                    profileUsed = "T";
                                }
                            }
                            else if (timestampApplied)
                            {
                                using var tReader = new PdfReader(new MemoryStream(preparedPdfBytes));
                                using var tOutputStream = new MemoryStream();
                                twoPhaseSigner.SignCMSContainerWithBaselineTProfile(
                                    externalSignature,
                                    tReader,
                                    tOutputStream,
                                    fieldName,
                                    cmsContainer);
                                signedPdfBytes = tOutputStream.ToArray();
                                profileUsed = "T";
                            }
                            else
                            {
                                using var bReader = new PdfReader(new MemoryStream(preparedPdfBytes));
                                using var bOutputStream = new MemoryStream();
                                twoPhaseSigner.SignCMSContainerWithBaselineBProfile(
                                    externalSignature,
                                    bReader,
                                    bOutputStream,
                                    fieldName,
                                    cmsContainer);
                                signedPdfBytes = bOutputStream.ToArray();
                                profileUsed = "B";
                            }

                            string outputPath = Path.Combine(workDir, BuildSignedPdfFileName(options.FileName));
                            File.WriteAllBytes(outputPath, signedPdfBytes);
                            LogSignedPdfEmbeddingDiagnostics(signedPdfBytes, fieldName, chainSourceCertificates);

                            _logger.LogInformation(
                                "PDF imzalama başarılı. Dosya={Path}, Slot={Slot}, Signer={Signer}, Timestamp={Timestamp}, PAdESProfile={Profile}, EmbedChainTotal={ChainTotal}",
                                outputPath, slotIdx, signerName, timestampApplied, profileUsed, chainSourceCertificates.Count);

                            return (signedPdfBytes, outputPath, signerName, timestampApplied);
                        }
                        catch (Pkcs11Exception p11Ex) when (IsSessionHandleInvalid(p11Ex))
                        {
                            retryCount++;
                            if (retryCount > maxRetries)
                            {
                                _logger.LogError(p11Ex, "PDF imza sırasında session invalid hatası tekrarladı (slot {Slot}).", slotIdx);
                                throw new Exception("Session handle geçersiz (PDF). Lütfen kartı çıkarıp takın, cihazları yenileyin ve tekrar deneyin.", p11Ex);
                            }

                            _logger.LogWarning(
                                p11Ex,
                                "PDF imza sırasında session invalid. Retry {Retry}/{Max} (slot {Slot})",
                                retryCount,
                                maxRetries,
                                slotIdx);

                            RemoveCachedSession(slotIdx);
                            await Task.Delay(600 * retryCount).ConfigureAwait(false);
                        }
                        catch (Exception ex) when (IsSessionHandleInvalidMessage(ex.Message))
                        {
                            retryCount++;
                            if (retryCount > maxRetries)
                            {
                                _logger.LogError(ex, "PDF imza sırasında session invalid mesajı tekrarladı (slot {Slot}).", slotIdx);
                                throw;
                            }

                            _logger.LogWarning(
                                ex,
                                "PDF imza sırasında session invalid mesajı. Retry {Retry}/{Max} (slot {Slot})",
                                retryCount,
                                maxRetries,
                                slotIdx);

                            RemoveCachedSession(slotIdx);
                            await Task.Delay(600 * retryCount).ConfigureAwait(false);
                        }
                    }

                    throw new Exception("PDF imzalama sırasında beklenmeyen session hatası oluştu.");
                }
                catch (Pkcs11Exception p11Ex) when (p11Ex.RV == CKR.CKR_PIN_INCORRECT)
                {
                    _logger.LogError(p11Ex, "PDF imza PIN hatalı: Slot {Slot}", slotIdx);
                    throw new Exception("PIN hatalı. Lütfen e-imza kartı PIN'inizi kontrol edin.", p11Ex);
                }
                catch (Pkcs11Exception p11Ex) when (p11Ex.RV == CKR.CKR_PIN_LOCKED)
                {
                    _logger.LogError(p11Ex, "PDF imza PIN kilitli: Slot {Slot}", slotIdx);
                    throw new Exception("PIN kilitli. Lütfen NES üreticinizle iletişime geçin.", p11Ex);
                }
                finally
                {
                    _pkcs11GlobalLock.Release();
                }
            }
            finally
            {
                gate.Release();
            }
        }

        public PdfSignatureValidationResult ValidatePdfSignatures(string pdfPath)
        {
            if (string.IsNullOrWhiteSpace(pdfPath))
                throw new ArgumentException("PDF yolu boş", nameof(pdfPath));

            if (!File.Exists(pdfPath))
                throw new FileNotFoundException("PDF dosyası bulunamadı.", pdfPath);

            return ValidatePdfSignatures(File.ReadAllBytes(pdfPath));
        }

        public PdfSignatureValidationResult ValidatePdfSignatures(byte[] pdfBytes)
        {
            var result = new PdfSignatureValidationResult();

            try
            {
                if (pdfBytes == null || pdfBytes.Length == 0)
                {
                    result.Success = false;
                    result.Message = "PDF içeriği boş.";
                    return result;
                }

                using var reader = new PdfReader(new MemoryStream(pdfBytes));
                using var pdfDoc = new PdfDocument(reader);
                var signatureUtil = new SignatureUtil(pdfDoc);
                var signatureNames = signatureUtil.GetSignatureNames();

                if (signatureNames == null || signatureNames.Count == 0)
                {
                    result.Success = true;
                    result.Message = "PDF içinde imza alanı bulunamadı.";
                    return result;
                }

                foreach (var signatureName in signatureNames)
                {
                    var item = new PdfSignatureValidationItem
                    {
                        FieldName = signatureName,
                        CoversWholeDocument = signatureUtil.SignatureCoversWholeDocument(signatureName)
                    };

                    var pkcs7 = signatureUtil.ReadSignatureData(signatureName);
                    item.IntegrityValid = pkcs7.VerifySignatureIntegrityAndAuthenticity();

                    var signerCert = ToX509Certificate2(pkcs7.GetSigningCertificate());
                    var embeddedCerts = ToX509Certificates(pkcs7.GetCertificates());

                    if (signerCert != null)
                    {
                        item.SignerSubject = signerCert.Subject;
                        item.SignerThumbprint = signerCert.Thumbprint ?? string.Empty;

                        using var chain = new X509Chain();
                        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
                        chain.ChainPolicy.RevocationFlag = X509RevocationFlag.EntireChain;
                        chain.ChainPolicy.VerificationFlags = X509VerificationFlags.NoFlag;
                        chain.ChainPolicy.DisableCertificateDownloads = false;
                        chain.ChainPolicy.UrlRetrievalTimeout = TimeSpan.FromSeconds(5);

                        foreach (var extraCert in embeddedCerts)
                        {
                            if (string.Equals(extraCert.Thumbprint, signerCert.Thumbprint, StringComparison.OrdinalIgnoreCase))
                                continue;

                            chain.ChainPolicy.ExtraStore.Add(extraCert);
                        }

                        item.TrustedChain = chain.Build(signerCert);
                        item.ChainStatus = chain.ChainStatus == null || chain.ChainStatus.Length == 0
                            ? "OK"
                            : string.Join(", ", chain.ChainStatus.Select(x => x.Status.ToString()).Distinct());
                    }
                    else
                    {
                        item.TrustedChain = false;
                        item.ChainStatus = "İmzacı sertifikası okunamadı.";
                    }

                    if (item.IntegrityValid)
                        result.CryptographicallyValidCount++;
                    if (item.TrustedChain)
                        result.TrustedChainCount++;

                    result.Signatures.Add(item);
                }

                result.SignatureCount = result.Signatures.Count;
                result.Success = true;

                if (result.SignatureCount == 0)
                {
                    result.Message = "PDF içinde imza alanı bulunamadı.";
                }
                else if (result.TrustedChainCount == result.SignatureCount)
                {
                    result.Message = "Tüm imzalar güvenilir zincire bağlandı.";
                }
                else if (result.CryptographicallyValidCount == result.SignatureCount)
                {
                    result.Message = "İmzalar matematiksel olarak geçerli, ancak bazı zincirler güvenilir değil.";
                }
                else
                {
                    result.Message = "Bazı imzalar doğrulanamadı.";
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "PDF imza doğrulama sırasında hata oluştu.");
                result.Success = false;
                result.Message = ex.Message;
            }

            return result;
        }

        private static X509Certificate2? ToX509Certificate2(IX509Certificate? certificate)
        {
            if (certificate == null)
                return null;

            try
            {
                return new X509Certificate2(certificate.GetEncoded());
            }
            catch
            {
                return null;
            }
        }

        private static IReadOnlyList<X509Certificate2> ToX509Certificates(IX509Certificate[]? certificates)
        {
            var result = new List<X509Certificate2>();
            if (certificates == null || certificates.Length == 0)
                return result;

            foreach (var cert in certificates)
            {
                var converted = ToX509Certificate2(cert);
                if (converted != null)
                    result.Add(converted);
            }

            return result;
        }

        private static bool IsSessionHandleInvalid(Pkcs11Exception ex)
        {
            string message = ex.Message ?? string.Empty;
            return ex.RV == CKR.CKR_SESSION_HANDLE_INVALID ||
                   ex.RV == CKR.CKR_TOKEN_NOT_RECOGNIZED ||
                   message.Contains("CKR_SESSION_HANDLE_INVALID", StringComparison.OrdinalIgnoreCase) ||
                   message.Contains("session", StringComparison.OrdinalIgnoreCase) && message.Contains("invalid", StringComparison.OrdinalIgnoreCase) ||
                   message.Contains("handle", StringComparison.OrdinalIgnoreCase) && message.Contains("invalid", StringComparison.OrdinalIgnoreCase) ||
                   message.Contains("closed", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsSessionHandleInvalidMessage(string? message)
        {
            if (string.IsNullOrWhiteSpace(message))
                return false;

            return message.Contains("CKR_SESSION_HANDLE_INVALID", StringComparison.OrdinalIgnoreCase) ||
                   message.Contains("session handle", StringComparison.OrdinalIgnoreCase) ||
                   message.Contains("session invalid", StringComparison.OrdinalIgnoreCase);
        }

        private void RemoveCachedSession(int slotIdx)
        {
            if (_sessionCache.TryRemove(slotIdx, out var oldSession))
            {
                try { oldSession.Logout(); } catch { }
                try { oldSession.Dispose(); } catch { }
            }
        }

        private ISession GetOrCreateSigningSession(
            IList<ISlot> slots,
            int slotIdx,
            string pin,
            bool forceFreshLogin,
            bool forPdfSigning)
        {
            if (forceFreshLogin)
                RemoveCachedSession(slotIdx);

            if (!forceFreshLogin && _sessionCache.TryGetValue(slotIdx, out var cachedSession))
            {
                if (forPdfSigning)
                    _logger.LogInformation("PDF imza için cache session kullanılıyor (slot {Slot}).", slotIdx);

                return cachedSession;
            }

            var session = slots[slotIdx].OpenSession(SessionType.ReadWrite);
            LoginWithPolicy(session, pin, forceFreshLogin, slotIdx);
            _sessionCache[slotIdx] = session;
            return session;
        }

        public async Task<(string signedXml, string filePath, string signedFileContent)> SignRecept(
            string xmlContent,
            int? slotIndex,
            string pin,
            bool useRawXml = false,
            string licenseType = "Free",
            bool forceFreshLogin = false)
        {
            if (string.IsNullOrWhiteSpace(xmlContent))
                throw new ArgumentException("XML boş");
            if (string.IsNullOrWhiteSpace(pin))
                throw new ArgumentException("PIN boş");

            string workDir = Path.Combine(AppContext.BaseDirectory, "SignedDocuments");
            Directory.CreateDirectory(workDir);

            if (useRawXml)
            {
                var b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(xmlContent), Base64FormattingOptions.InsertLineBreaks);
                string path = Path.Combine(workDir, $"raw-{DateTime.Now:yyyyMMddHHmmssfff}.b64");
                File.WriteAllText(path, b64, new UTF8Encoding(false));
                return (b64, path, b64);
            }

            string tempPath = Path.Combine(workDir, $"erecete-{DateTime.Now:yyyyMMddHHmmssfff}.xml");
            File.WriteAllText(tempPath, xmlContent, new UTF8Encoding(false));
            _logger.LogInformation("Orijinal XML kaydedildi: {TempPath}", tempPath);

            if (!slotIndex.HasValue || slotIndex.Value < 0)
                throw new Exception("Kart bulunamadı");

            int slotIdx = slotIndex.Value;

            // ✅ aynı slotta eşzamanlı request çakışmasın
            var gate = _slotLocks.GetOrAdd(slotIdx, _ => new SemaphoreSlim(1, 1));
            await gate.WaitAsync().ConfigureAwait(false);

            try
            {
                ISession session;

                // ✅ SingleThreaded PKCS#11 => global lock (slot lock + global lock beraber)
                await _pkcs11GlobalLock.WaitAsync().ConfigureAwait(false);
                try
                {
                    var lib = GetOrLoadLibrary();
                    var slots = lib.GetSlotList(SlotsType.WithTokenPresent);

                    if (slots == null || slots.Count == 0)
                        throw new Exception("Kart bulunamadı");

                    if (slotIdx < 0 || slotIdx >= slots.Count)
                        throw new Exception("Kart bulunamadı");

                    if (forceFreshLogin)
                    {
                        if (_sessionCache.TryRemove(slotIdx, out var oldSession))
                        {
                            try { oldSession.Logout(); } catch { }
                            try { oldSession.Dispose(); } catch { }
                        }

                        session = slots[slotIdx].OpenSession(SessionType.ReadWrite);

                        try
                        {
                            LoginWithPolicy(session, pin, forceFreshLogin: true, slotIdx: slotIdx);
                        }
                        catch (Pkcs11Exception p11Ex) when (p11Ex.RV == CKR.CKR_PIN_INCORRECT)
                        {
                            _logger.LogError(p11Ex, "PIN hatalı: Slot {Slot}", slotIdx);
                            throw new Exception("PIN hatalı. Lütfen e-imza kartı PIN'inizi kontrol edin.", p11Ex);
                        }
                        catch (Pkcs11Exception p11Ex) when (p11Ex.RV == CKR.CKR_PIN_LOCKED)
                        {
                            _logger.LogError(p11Ex, "PIN kilitli: Slot {Slot}", slotIdx);
                            throw new Exception("PIN kilitli. Lütfen NES üreticinizle iletişime geçin.", p11Ex);
                        }

                        _sessionCache.TryAdd(slotIdx, session);
                        _logger.LogInformation("Force fresh login: session açıldı (slot {Slot})", slotIdx);
                    }
                    else if (_sessionCache.TryGetValue(slotIdx, out session))
                    {
                        _logger.LogInformation("Cache'ten session alındı (slot {Slot})", slotIdx);
                    }
                    else
                    {
                        session = slots[slotIdx].OpenSession(SessionType.ReadWrite);

                        try
                        {
                            LoginWithPolicy(session, pin, forceFreshLogin: false, slotIdx: slotIdx);
                        }
                        catch (Pkcs11Exception p11Ex) when (p11Ex.RV == CKR.CKR_PIN_INCORRECT)
                        {
                            _logger.LogError(p11Ex, "PIN hatalı: Slot {Slot}", slotIdx);
                            throw new Exception("PIN hatalı. Lütfen e-imza kartı PIN'inizi kontrol edin.", p11Ex);
                        }
                        catch (Pkcs11Exception p11Ex) when (p11Ex.RV == CKR.CKR_PIN_LOCKED)
                        {
                            _logger.LogError(p11Ex, "PIN kilitli: Slot {Slot}", slotIdx);
                            throw new Exception("PIN kilitli. Lütfen NES üreticinizle iletişime geçin.", p11Ex);
                        }

                        _sessionCache.TryAdd(slotIdx, session);
                        _logger.LogInformation("Yeni session açıldı ve cache'e eklendi (slot {Slot})", slotIdx);
                    }

                    // ---- İmzalama + retry ----
                    int retryCount = 0;
                    const int maxRetries = 2;

                    while (retryCount <= maxRetries)
                    {
                        try
                        {
                            var signingMaterial = GetSigningMaterial(session);
                            var cert = signingMaterial.certificate;
                            var key = signingMaterial.privateKey;
                            var mech = session.Factories.MechanismFactory.Create(CKM.CKM_RSA_PKCS);

                            string signedXml = CreateFinalMedulaXadesBes(xmlContent, cert, session, key, mech);
                            string signedPath = tempPath.Replace(".xml", ".xsig");
                            File.WriteAllText(signedPath, signedXml, new UTF8Encoding(false));
                            _logger.LogInformation("İmzalı XML kaydedildi: {SignedPath}", signedPath);

                            return (signedXml, signedPath, signedXml);
                        }
                        catch (Pkcs11Exception p11Ex) when (
                            p11Ex.RV == CKR.CKR_SESSION_HANDLE_INVALID ||
                            p11Ex.RV == CKR.CKR_TOKEN_NOT_RECOGNIZED ||
                            p11Ex.Message.Contains("session") ||
                            p11Ex.Message.Contains("handle") ||
                            p11Ex.Message.Contains("invalid") ||
                            p11Ex.Message.Contains("closed"))
                        {
                            retryCount++;
                            if (retryCount > maxRetries)
                            {
                                _logger.LogError(p11Ex, "Max retry aşıldı (session invalid): Slot {Slot}", slotIdx);
                                throw new Exception("Session handle geçersiz (max retry denendi). Lütfen kartı çıkarıp takın ve forceFreshLogin=true ile deneyin.", p11Ex);
                            }

                            _logger.LogWarning(p11Ex,
                                "Session invalid, retry {Retry}/{Max}. Session sıfırlanıyor (slot {Slot})",
                                retryCount, maxRetries, slotIdx);

                            if (_sessionCache.TryRemove(slotIdx, out var oldSession))
                            {
                                try { oldSession.Logout(); } catch { }
                                try { oldSession.Dispose(); } catch { }
                            }

                            var lib2 = GetOrLoadLibrary();
                            var slots2 = lib2.GetSlotList(SlotsType.WithTokenPresent);
                            if (slots2 == null || slots2.Count == 0 || slotIdx >= slots2.Count)
                                throw new Exception("Kart bulunamadı (retry sırasında).");

                            session = slots2[slotIdx].OpenSession(SessionType.ReadWrite);

                            try
                            {
                                LoginWithPolicy(session, pin, forceFreshLogin: forceFreshLogin, slotIdx: slotIdx);
                            }
                            catch (Pkcs11Exception pinEx) when (pinEx.RV == CKR.CKR_PIN_INCORRECT)
                            {
                                _logger.LogError(pinEx, "PIN hatalı: Slot {Slot}", slotIdx);
                                throw new Exception("PIN hatalı. Lütfen e-imza kartı PIN'inizi kontrol edin.", pinEx);
                            }
                            catch (Pkcs11Exception pinEx) when (pinEx.RV == CKR.CKR_PIN_LOCKED)
                            {
                                _logger.LogError(pinEx, "PIN kilitli: Slot {Slot}", slotIdx);
                                throw new Exception("PIN kilitli. Lütfen NES üreticinizle iletişime geçin.", pinEx);
                            }

                            _sessionCache.TryAdd(slotIdx, session);
                            Thread.Sleep(1500 * retryCount);
                        }
                    }

                    throw new Exception("İmzalama sırasında beklenmeyen hata oluştu.");
                }
                finally
                {
                    _pkcs11GlobalLock.Release();
                }
            }
            finally
            {
                gate.Release();
            }
        }

        private (X509Certificate2 certificate, IObjectHandle privateKey, List<X509Certificate2> certificates) GetSigningMaterial(ISession session)
        {
            var certTemplate = new List<IObjectAttribute>
            {
                session.Factories.ObjectAttributeFactory.Create(CKA.CKA_CLASS, CKO.CKO_CERTIFICATE),
                session.Factories.ObjectAttributeFactory.Create(CKA.CKA_CERTIFICATE_TYPE, CKC.CKC_X_509)
            };
            var certObjects = session.FindAllObjects(certTemplate);
            if (certObjects.Count == 0)
                throw new Exception("İmzalama sertifikası bulunamadı.");

            var keyTemplate = new List<IObjectAttribute>
            {
                session.Factories.ObjectAttributeFactory.Create(CKA.CKA_CLASS, CKO.CKO_PRIVATE_KEY),
                session.Factories.ObjectAttributeFactory.Create(CKA.CKA_KEY_TYPE, CKK.CKK_RSA)
            };
            var keyObjects = session.FindAllObjects(keyTemplate);
            if (keyObjects.Count == 0)
                throw new Exception("İmzalama private key bulunamadı.");

            var tokenCertificates = new List<X509Certificate2>();

            var keyById = new Dictionary<string, IObjectHandle>(StringComparer.OrdinalIgnoreCase);
            foreach (var keyObject in keyObjects)
            {
                var keyIdAttr = session.GetAttributeValue(keyObject, new List<CKA> { CKA.CKA_ID })[0];
                byte[] keyIdBytes = keyIdAttr.GetValueAsByteArray() ?? Array.Empty<byte>();
                string keyIdHex = Convert.ToHexString(keyIdBytes);
                if (string.IsNullOrWhiteSpace(keyIdHex))
                    continue;

                if (!keyById.ContainsKey(keyIdHex))
                    keyById[keyIdHex] = keyObject;
            }

            X509Certificate2? bestCert = null;
            IObjectHandle? bestKey = null;
            int bestScore = int.MinValue;

            foreach (var certObject in certObjects)
            {
                var attrs = session.GetAttributeValue(certObject, new List<CKA> { CKA.CKA_VALUE, CKA.CKA_ID });
                byte[] certBytes = attrs[0].GetValueAsByteArray() ?? Array.Empty<byte>();
                if (certBytes.Length == 0)
                    continue;

                byte[] certIdBytes = attrs[1].GetValueAsByteArray() ?? Array.Empty<byte>();
                string certIdHex = Convert.ToHexString(certIdBytes);

                var cert = new X509Certificate2(certBytes);
                AddUniqueCertificate(tokenCertificates, cert);
                IObjectHandle? matchedKey = null;
                if (!string.IsNullOrWhiteSpace(certIdHex) && keyById.TryGetValue(certIdHex, out var byId))
                {
                    matchedKey = byId;
                }
                else if (keyObjects.Count == 1)
                {
                    matchedKey = keyObjects[0];
                }

                if (matchedKey == null)
                    continue;

                int score = ScoreCertificateForSigning(cert);
                if (score <= bestScore)
                    continue;

                bestCert = cert;
                bestKey = matchedKey;
                bestScore = score;
            }

            if (bestCert != null && bestKey != null)
            {
                _logger.LogInformation("İmzalama sertifikası seçildi. Subject={Subject}, Score={Score}, Serial={Serial}",
                    bestCert.Subject, bestScore, bestCert.SerialNumber);
                return (bestCert, bestKey, tokenCertificates);
            }

            // Son çare: eski davranış (ilk sertifika + ilk private key)
            _logger.LogWarning("Uygun eşleşmiş imza sertifikası bulunamadı, fallback kullanılıyor.");
            byte[] fallbackCertData = session.GetAttributeValue(certObjects[0], new List<CKA> { CKA.CKA_VALUE })[0].GetValueAsByteArray() ?? Array.Empty<byte>();
            var fallbackCert = new X509Certificate2(fallbackCertData);
            AddUniqueCertificate(tokenCertificates, fallbackCert);
            return (fallbackCert, keyObjects[0], tokenCertificates);
        }

        private static int ScoreCertificateForSigning(X509Certificate2 cert)
        {
            int score = 0;
            DateTime utcNow = DateTime.UtcNow;

            if (utcNow >= cert.NotBefore.ToUniversalTime() && utcNow <= cert.NotAfter.ToUniversalTime())
                score += 50;
            else
                score -= 200;

            var keyUsage = cert.Extensions.OfType<X509KeyUsageExtension>().FirstOrDefault();
            if (keyUsage != null)
            {
                if ((keyUsage.KeyUsages & X509KeyUsageFlags.DigitalSignature) != 0)
                    score += 60;

                if ((keyUsage.KeyUsages & X509KeyUsageFlags.NonRepudiation) != 0)
                    score += 80;

                if ((keyUsage.KeyUsages & X509KeyUsageFlags.KeyEncipherment) != 0)
                    score -= 5;
            }
            else
            {
                score += 5;
            }

            // QCStatements (qualified cert göstergesi)
            if (cert.Extensions["1.3.6.1.5.5.7.1.3"] != null)
                score += 20;

            // Certificate Policies varsa hafif pozitif.
            if (cert.Extensions["2.5.29.32"] != null)
                score += 5;

            return score;
        }

        private X509Certificate2 GetBestSigningCertificateFromSession(ISession session)
        {
            var certTemplate = new List<IObjectAttribute>
            {
                session.Factories.ObjectAttributeFactory.Create(CKA.CKA_CLASS, CKO.CKO_CERTIFICATE),
                session.Factories.ObjectAttributeFactory.Create(CKA.CKA_CERTIFICATE_TYPE, CKC.CKC_X_509)
            };

            var certObjects = session.FindAllObjects(certTemplate);
            if (certObjects.Count == 0)
                throw new Exception("Sertifika bulunamadı.");

            X509Certificate2? best = null;
            int bestScore = int.MinValue;

            foreach (var certObject in certObjects)
            {
                byte[] certBytes = session.GetAttributeValue(certObject, new List<CKA> { CKA.CKA_VALUE })[0].GetValueAsByteArray() ?? Array.Empty<byte>();
                if (certBytes.Length == 0)
                    continue;

                var cert = new X509Certificate2(certBytes);
                int score = ScoreCertificateForSigning(cert);
                if (score <= bestScore)
                    continue;

                best = cert;
                bestScore = score;
            }

            if (best == null)
                throw new Exception("İmzalama sertifikası seçilemedi.");

            return best;
        }

        private List<X509Certificate2> BuildTrustChainCandidates(
            X509Certificate2 signerCert,
            SignerTrustSetupResult result,
            IReadOnlyCollection<X509Certificate2>? extraCandidates = null)
        {
            var chainCertificates = new List<X509Certificate2> { new X509Certificate2(signerCert.RawData) };
            if (extraCandidates != null)
            {
                foreach (var candidate in extraCandidates)
                    AddUniqueCertificate(chainCertificates, candidate);
            }

            using (var chain = new X509Chain())
            {
                chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
                chain.ChainPolicy.RevocationFlag = X509RevocationFlag.ExcludeRoot;
                chain.ChainPolicy.VerificationFlags = X509VerificationFlags.AllowUnknownCertificateAuthority;
                chain.ChainPolicy.UrlRetrievalTimeout = TimeSpan.FromSeconds(5);
                chain.ChainPolicy.DisableCertificateDownloads = false;
                if (extraCandidates != null)
                {
                    foreach (var extra in extraCandidates)
                        chain.ChainPolicy.ExtraStore.Add(extra);
                }

                chain.Build(signerCert);
                foreach (X509ChainElement element in chain.ChainElements)
                    AddUniqueCertificate(chainCertificates, element.Certificate);
            }

            for (int depth = 0; depth < 6; depth++)
            {
                bool addedAny = false;
                var snapshot = chainCertificates.ToList();

                foreach (var item in snapshot)
                {
                    if (DistinguishedNameEquals(item.Subject, item.Issuer))
                        continue;

                    bool hasIssuer = chainCertificates.Any(x => DistinguishedNameEquals(x.Subject, item.Issuer));
                    if (hasIssuer)
                        continue;

                    foreach (var issuerCert in FindIssuerCertificatesInStores(item))
                    {
                        if (AddUniqueCertificate(chainCertificates, issuerCert))
                            addedAny = true;
                    }

                    foreach (var issuerCert in FindIssuerCertificatesFromAia(item, result.Warnings))
                    {
                        if (AddUniqueCertificate(chainCertificates, issuerCert))
                            addedAny = true;
                    }
                }

                if (!addedAny)
                    break;
            }

            return chainCertificates;
        }

        private void InstallChainCertificatesToStores(
            List<X509Certificate2> chainCertificates,
            X509Certificate2 signerCert,
            SignerTrustSetupResult result,
            bool tryLocalMachine)
        {
            InstallChainCertificatesToStoreLocation(chainCertificates, signerCert, result, StoreLocation.CurrentUser, "CurrentUser");

            if (!tryLocalMachine)
                return;

            bool isAdmin = false;
            try
            {
                var identity = WindowsIdentity.GetCurrent();
                var principal = identity == null ? null : new WindowsPrincipal(identity);
                isAdmin = principal?.IsInRole(WindowsBuiltInRole.Administrator) == true;
            }
            catch
            {
                // Admin kontrolü yapılamazsa yine deneyeceğiz.
            }

            if (!isAdmin)
            {
                bool alreadyInstalled = AreChainCertificatesPresentInStoreLocation(
                    chainCertificates,
                    signerCert,
                    StoreLocation.LocalMachine);

                if (!alreadyInstalled)
                    result.Warnings.Add("LocalMachine store kurulumu atlandı (yönetici yetkisi yok).");

                return;
            }

            InstallChainCertificatesToStoreLocation(chainCertificates, signerCert, result, StoreLocation.LocalMachine, "LocalMachine");
        }

        private bool AreChainCertificatesPresentInStoreLocation(
            List<X509Certificate2> chainCertificates,
            X509Certificate2 signerCert,
            StoreLocation location)
        {
            foreach (var cert in chainCertificates)
            {
                if (string.Equals(cert.Thumbprint, signerCert.Thumbprint, StringComparison.OrdinalIgnoreCase))
                    continue;

                bool isRoot = DistinguishedNameEquals(cert.Subject, cert.Issuer);
                StoreName storeName = isRoot ? StoreName.Root : StoreName.CertificateAuthority;

                try
                {
                    using var store = new X509Store(storeName, location);
                    store.Open(OpenFlags.ReadOnly | OpenFlags.OpenExistingOnly);
                    bool exists = !string.IsNullOrWhiteSpace(cert.Thumbprint) &&
                        store.Certificates.Find(X509FindType.FindByThumbprint, cert.Thumbprint, false).Count > 0;

                    if (!exists)
                        return false;
                }
                catch
                {
                    return false;
                }
            }

            return true;
        }

        private void CountChainCertificatesInStoreLocation(
            List<X509Certificate2> chainCertificates,
            X509Certificate2 signerCert,
            StoreLocation location,
            out int existingRootCount,
            out int existingIntermediateCount)
        {
            existingRootCount = 0;
            existingIntermediateCount = 0;

            foreach (var cert in chainCertificates)
            {
                if (string.Equals(cert.Thumbprint, signerCert.Thumbprint, StringComparison.OrdinalIgnoreCase))
                    continue;

                bool isRoot = DistinguishedNameEquals(cert.Subject, cert.Issuer);
                StoreName storeName = isRoot ? StoreName.Root : StoreName.CertificateAuthority;

                try
                {
                    using var store = new X509Store(storeName, location);
                    store.Open(OpenFlags.ReadOnly | OpenFlags.OpenExistingOnly);
                    bool exists = !string.IsNullOrWhiteSpace(cert.Thumbprint) &&
                                  store.Certificates.Find(X509FindType.FindByThumbprint, cert.Thumbprint, false).Count > 0;
                    if (!exists)
                        continue;

                    if (isRoot) existingRootCount++;
                    else existingIntermediateCount++;
                }
                catch
                {
                    // erişilemeyen store sayımı atlanır
                }
            }
        }

        private void InstallChainCertificatesToStoreLocation(
            List<X509Certificate2> chainCertificates,
            X509Certificate2 signerCert,
            SignerTrustSetupResult result,
            StoreLocation location,
            string locationName)
        {
            foreach (var cert in chainCertificates)
            {
                if (string.Equals(cert.Thumbprint, signerCert.Thumbprint, StringComparison.OrdinalIgnoreCase))
                    continue;

                bool isRoot = DistinguishedNameEquals(cert.Subject, cert.Issuer);
                StoreName storeName = isRoot ? StoreName.Root : StoreName.CertificateAuthority;

                using var store = new X509Store(storeName, location);
                try
                {
                    store.Open(OpenFlags.ReadWrite);
                }
                catch (Exception openEx)
                {
                    result.Warnings.Add($"{locationName}/{storeName} açılamadı: {openEx.Message}");
                    continue;
                }

                bool exists = !string.IsNullOrWhiteSpace(cert.Thumbprint) &&
                    store.Certificates.Find(X509FindType.FindByThumbprint, cert.Thumbprint, false).Count > 0;

                if (exists)
                {
                    if (location == StoreLocation.CurrentUser)
                    {
                        if (isRoot) result.ExistingRootCount++;
                        else result.ExistingIntermediateCount++;
                    }
                    continue;
                }

                try
                {
                    store.Add(new X509Certificate2(cert.RawData));
                    if (location == StoreLocation.CurrentUser)
                    {
                        if (isRoot) result.AddedRootCount++;
                        else result.AddedIntermediateCount++;
                    }
                }
                catch (Exception ex)
                {
                    result.Warnings.Add($"{locationName}/{storeName} {cert.Subject}: {ex.Message}");
                }
            }
        }

        private static int ReadDword(RegistryKey key, string valueName, int defaultValue = 0)
        {
            object? value = key.GetValue(valueName);
            if (value == null)
                return defaultValue;

            try
            {
                return Convert.ToInt32(value, CultureInfo.InvariantCulture);
            }
            catch
            {
                return defaultValue;
            }
        }

        private void EnsureAcrobatWindowsStoreIntegration(SignerTrustSetupResult result)
        {
            string[] productRoots =
            {
                @"SOFTWARE\Adobe\Adobe Acrobat\DC\Security",
                @"SOFTWARE\Adobe\Acrobat Reader\DC\Security"
            };

            const int windowsStoreTrustMask = 0x62; // Adobe PrefRef: Windows store + trusted roots.
            const int importEnable = 1;

            foreach (string productRoot in productRoots)
            {
                try
                {
                    using var securityKey = Registry.CurrentUser.CreateSubKey(productRoot, writable: true);
                    if (securityKey == null)
                    {
                        result.Warnings.Add($"Registry açılamadı: HKCU\\{productRoot}");
                        continue;
                    }

                    using (var mscapiKey = securityKey.CreateSubKey(@"cASPKI\cMSCAPI_DirectoryProvider", writable: true))
                    {
                        if (mscapiKey == null)
                        {
                            result.Warnings.Add($"Registry açılamadı: HKCU\\{productRoot}\\cASPKI\\cMSCAPI_DirectoryProvider");
                        }
                        else if (ReadDword(mscapiKey, "iMSStoreTrusted") != windowsStoreTrustMask)
                        {
                            mscapiKey.SetValue("iMSStoreTrusted", windowsStoreTrustMask, RegistryValueKind.DWord);
                            result.AcrobatRegistryValuesWritten++;
                        }
                    }

                    using (var ppkKey = securityKey.CreateSubKey("PPKHandler", writable: true))
                    {
                        if (ppkKey == null)
                        {
                            result.Warnings.Add($"Registry açılamadı: HKCU\\{productRoot}\\PPKHandler");
                        }
                        else if (ReadDword(ppkKey, "bCertStoreImportEnable") != importEnable)
                        {
                            ppkKey.SetValue("bCertStoreImportEnable", importEnable, RegistryValueKind.DWord);
                            result.AcrobatRegistryValuesWritten++;
                        }
                    }
                }
                catch (Exception ex)
                {
                    result.Warnings.Add($"Acrobat registry ayarı yazılamadı ({productRoot}): {ex.Message}");
                }
            }
        }

        private bool IsAcrobatWindowsStoreIntegrationReady(SignerTrustSetupResult? result = null)
        {
            string[] productRoots =
            {
                @"SOFTWARE\Adobe\Adobe Acrobat\DC\Security",
                @"SOFTWARE\Adobe\Acrobat Reader\DC\Security"
            };

            const int windowsStoreTrustMask = 0x62;
            const int importEnable = 1;
            bool foundAnyProduct = false;
            bool readyOnAnyProduct = false;

            foreach (string productRoot in productRoots)
            {
                try
                {
                    using var securityKey = Registry.CurrentUser.OpenSubKey(productRoot, writable: false);
                    if (securityKey == null)
                        continue;

                    foundAnyProduct = true;

                    using var mscapiKey = securityKey.OpenSubKey(@"cASPKI\cMSCAPI_DirectoryProvider", writable: false);
                    using var ppkKey = securityKey.OpenSubKey("PPKHandler", writable: false);

                    if (mscapiKey == null || ppkKey == null)
                        continue;

                    bool windowsStoreOk = ReadDword(mscapiKey, "iMSStoreTrusted") == windowsStoreTrustMask;
                    bool importOk = ReadDword(ppkKey, "bCertStoreImportEnable") == importEnable;
                    if (windowsStoreOk && importOk)
                        readyOnAnyProduct = true;
                }
                catch (Exception ex)
                {
                    result?.Warnings.Add($"Acrobat registry ayarı okunamadı ({productRoot}): {ex.Message}");
                }
            }

            if (!foundAnyProduct)
            {
                result?.Warnings.Add("Acrobat/Reader registry anahtarı bulunamadı (yüklenmemiş olabilir).");
            }

            return readyOnAnyProduct;
        }

        private static string EvaluateTrustReadiness(SignerTrustSetupResult result)
        {
            if (result.ValidationSucceeded && result.CurrentUserChainReady && result.AcrobatWindowsStoreReady)
                return "Hazır";

            if (result.ValidationSucceeded || result.CurrentUserChainReady || result.AcrobatWindowsStoreReady)
                return "Kısmi";

            return "Hazır Değil";
        }

        // (Aşağısı senin mevcut CreateFinalMedulaXadesBes + canonicalize + DigestInfoSHA256 aynen)
        private string CreateFinalMedulaXadesBes(string xml, X509Certificate2 cert, ISession session, IObjectHandle key, IMechanism mech)
        {
            var doc = new XmlDocument { PreserveWhitespace = false };
            string ds = "http://www.w3.org/2000/09/xmldsig#";
            string xades = "http://uri.etsi.org/01903/v1.3.2#";
            string sigId = "Signature-Id-" + Guid.NewGuid().ToString("N").Substring(0, 24);
            string spId = "Signed-Properties-Id-" + Guid.NewGuid().ToString("N").Substring(0, 24);
            string signedPropsRefId = "Reference-Id-" + Guid.NewGuid().ToString("N").Substring(0, 24);
            string dataRefId = "Reference-Id-" + Guid.NewGuid().ToString("N").Substring(0, 24);
            string sigValueId = "Signature-Value-Id-" + Guid.NewGuid().ToString("N").Substring(0, 24);
            string qpObjId = "Object-Id-" + Guid.NewGuid().ToString("N").Substring(0, 24);
            string dataObjId = "Object-Id-" + Guid.NewGuid().ToString("N").Substring(0, 24);

            var signature = doc.CreateElement("ds", "Signature", ds);
            signature.SetAttribute("Id", sigId);
            doc.AppendChild(signature);

            var signedInfo = doc.CreateElement("ds", "SignedInfo", ds);
            signature.AppendChild(signedInfo);

            var c14n = doc.CreateElement("ds", "CanonicalizationMethod", ds);
            c14n.SetAttribute("Algorithm", "http://www.w3.org/TR/2001/REC-xml-c14n-20010315");
            signedInfo.AppendChild(c14n);

            var sigMethod = doc.CreateElement("ds", "SignatureMethod", ds);
            sigMethod.SetAttribute("Algorithm", "http://www.w3.org/2001/04/xmldsig-more#rsa-sha256");
            signedInfo.AppendChild(sigMethod);

            var signedPropsRef = doc.CreateElement("ds", "Reference", ds);
            signedPropsRef.SetAttribute("Id", signedPropsRefId);
            signedPropsRef.SetAttribute("URI", "#" + spId);
            signedPropsRef.SetAttribute("Type", "http://uri.etsi.org/01903#SignedProperties");

            var transforms = doc.CreateElement("ds", "Transforms", ds);
            var transform = doc.CreateElement("ds", "Transform", ds);
            transform.SetAttribute("Algorithm", SignedXml.XmlDsigExcC14NTransformUrl);
            transforms.AppendChild(transform);
            signedPropsRef.AppendChild(transforms);

            var dmSp = doc.CreateElement("ds", "DigestMethod", ds);
            dmSp.SetAttribute("Algorithm", "http://www.w3.org/2001/04/xmlenc#sha256");
            signedPropsRef.AppendChild(dmSp);

            signedInfo.AppendChild(signedPropsRef);

            var dataRef = doc.CreateElement("ds", "Reference", ds);
            dataRef.SetAttribute("Id", dataRefId);
            dataRef.SetAttribute("URI", "#" + dataObjId);

            var dmData = doc.CreateElement("ds", "DigestMethod", ds);
            dmData.SetAttribute("Algorithm", "http://www.w3.org/2001/04/xmlenc#sha256");
            dataRef.AppendChild(dmData);

            signedInfo.AppendChild(dataRef);

            var sigValue = doc.CreateElement("ds", "SignatureValue", ds);
            sigValue.SetAttribute("Id", sigValueId);
            signature.AppendChild(sigValue);

            var keyInfo = doc.CreateElement("ds", "KeyInfo", ds);
            signature.AppendChild(keyInfo);

            var x509Data = doc.CreateElement("ds", "X509Data", ds);
            keyInfo.AppendChild(x509Data);

            var certNode = doc.CreateElement("ds", "X509Certificate", ds);
            certNode.InnerText = Convert.ToBase64String(cert.RawData);
            x509Data.AppendChild(certNode);

            var qpObj = doc.CreateElement("ds", "Object", ds);
            qpObj.SetAttribute("Id", qpObjId);
            signature.AppendChild(qpObj);

            var qp = doc.CreateElement("xades", "QualifyingProperties", xades);
            qp.SetAttribute("Target", "#" + sigId);
            qpObj.AppendChild(qp);

            var signedProps = doc.CreateElement("xades", "SignedProperties", xades);
            signedProps.SetAttribute("Id", spId);
            qp.AppendChild(signedProps);

            var ssp = doc.CreateElement("xades", "SignedSignatureProperties", xades);
            signedProps.AppendChild(ssp);

            var signingTime = doc.CreateElement("xades", "SigningTime", xades);
            signingTime.InnerText = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss.fffffffzzz");
            ssp.AppendChild(signingTime);

            var signingCert = doc.CreateElement("xades", "SigningCertificate", xades);
            ssp.AppendChild(signingCert);

            var certV2 = doc.CreateElement("xades", "Cert", xades);
            signingCert.AppendChild(certV2);

            var certDigest = doc.CreateElement("xades", "CertDigest", xades);
            certV2.AppendChild(certDigest);

            var dmCert = doc.CreateElement("ds", "DigestMethod", ds);
            dmCert.SetAttribute("Algorithm", "http://www.w3.org/2001/04/xmlenc#sha256");
            certDigest.AppendChild(dmCert);

            var dvCert = doc.CreateElement("ds", "DigestValue", ds);
            dvCert.InnerText = Convert.ToBase64String(SHA256.HashData(cert.RawData));
            certDigest.AppendChild(dvCert);

            var issuerSerial = doc.CreateElement("xades", "IssuerSerial", xades);
            certV2.AppendChild(issuerSerial);

            var issuerName = doc.CreateElement("ds", "X509IssuerName", ds);
            string issuer = cert.Issuer.Replace(", ", ",");
            issuerName.InnerText = issuer;
            issuerSerial.AppendChild(issuerName);

            var serialNumber = doc.CreateElement("ds", "X509SerialNumber", ds);
            BigInteger serialBig = BigInteger.Parse(cert.SerialNumber, NumberStyles.HexNumber);
            serialNumber.InnerText = serialBig.ToString();
            issuerSerial.AppendChild(serialNumber);

            var sdo = doc.CreateElement("xades", "SignedDataObjectProperties", xades);
            signedProps.AppendChild(sdo);

            var dof = doc.CreateElement("xades", "DataObjectFormat", xades);
            dof.SetAttribute("ObjectReference", "#" + dataRefId);
            sdo.AppendChild(dof);

            var mime = doc.CreateElement("xades", "MimeType", xades);
            mime.InnerText = "text/xml";
            dof.AppendChild(mime);

            var dataObj = doc.CreateElement("ds", "Object", ds);
            dataObj.SetAttribute("Id", dataObjId);
            dataObj.SetAttribute("Encoding", "http://www.w3.org/2000/09/xmldsig#base64");
            dataObj.InnerText = Convert.ToBase64String(Encoding.UTF8.GetBytes(xml));
            signature.AppendChild(dataObj);

            var spCanon = CanonicalizeExclusive(signedProps, doc);
            var spDv = doc.CreateElement("ds", "DigestValue", ds);
            spDv.InnerText = Convert.ToBase64String(SHA256.HashData(spCanon));
            signedPropsRef.AppendChild(spDv);

            var dataCanon = CanonicalizeInclusive(dataObj, doc);
            var dataDv = doc.CreateElement("ds", "DigestValue", ds);
            dataDv.InnerText = Convert.ToBase64String(SHA256.HashData(dataCanon));
            dataRef.AppendChild(dataDv);

            byte[] siC14n = CanonicalizeInclusive(signedInfo, doc);
            byte[] hash = SHA256.HashData(siC14n);
            byte[] toSign = DigestInfoSHA256(hash);

            byte[] sigBytes = session.Sign(mech, key, toSign);
            sigValue.InnerText = Convert.ToBase64String(sigBytes);

            var declaration = doc.CreateXmlDeclaration("1.0", "UTF-8", null);
            doc.InsertBefore(declaration, doc.FirstChild);

            return doc.OuterXml;
        }

        private byte[] CanonicalizeExclusive(XmlNode node, XmlDocument doc)
        {
            var namespaceList = new Dictionary<string, string>();
            CollectNamespaces(node, ref namespaceList);

            var clone = node.CloneNode(true) as XmlElement;
            foreach (var ns in namespaceList)
            {
                if (clone.GetAttribute("xmlns:" + ns.Key) == null &&
                    !(string.IsNullOrEmpty(ns.Key) && clone.HasAttribute("xmlns")))
                {
                    if (string.IsNullOrEmpty(ns.Key))
                        clone.SetAttribute("xmlns", ns.Value);
                    else
                        clone.SetAttribute("xmlns:" + ns.Key, ns.Value);
                }
            }

            var t = new XmlDsigExcC14NTransform();
            using var ms = new MemoryStream();
            using (var writer = XmlWriter.Create(ms, new XmlWriterSettings
            {
                Encoding = Encoding.UTF8,
                OmitXmlDeclaration = true,
                Indent = false,
                NewLineHandling = NewLineHandling.None,
                ConformanceLevel = ConformanceLevel.Fragment
            }))
            {
                clone.WriteTo(writer);
            }
            ms.Position = 0;
            t.LoadInput(ms);
            using var output = (Stream)t.GetOutput(typeof(Stream));
            using var result = new MemoryStream();
            output.CopyTo(result);
            return result.ToArray();
        }

        private byte[] CanonicalizeInclusive(XmlNode node, XmlDocument doc)
        {
            var namespaceList = new Dictionary<string, string>();
            CollectNamespaces(node, ref namespaceList);

            var clone = node.CloneNode(true) as XmlElement;
            foreach (var ns in namespaceList)
            {
                if (clone.GetAttribute("xmlns:" + ns.Key) == null &&
                    !(string.IsNullOrEmpty(ns.Key) && clone.HasAttribute("xmlns")))
                {
                    if (string.IsNullOrEmpty(ns.Key))
                        clone.SetAttribute("xmlns", ns.Value);
                    else
                        clone.SetAttribute("xmlns:" + ns.Key, ns.Value);
                }
            }

            var t = new XmlDsigC14NTransform(false);
            using var ms = new MemoryStream();
            using (var writer = XmlWriter.Create(ms, new XmlWriterSettings
            {
                Encoding = Encoding.UTF8,
                OmitXmlDeclaration = true,
                Indent = false,
                NewLineHandling = NewLineHandling.None,
                ConformanceLevel = ConformanceLevel.Fragment
            }))
            {
                clone.WriteTo(writer);
            }
            ms.Position = 0;
            t.LoadInput(ms);
            using var output = (Stream)t.GetOutput(typeof(Stream));
            using var result = new MemoryStream();
            output.CopyTo(result);
            return result.ToArray();
        }

        private void CollectNamespaces(XmlNode node, ref Dictionary<string, string> namespaceList)
        {
            if (node.NodeType == XmlNodeType.Element)
            {
                var element = node as XmlElement;
                if (element != null)
                {
                    foreach (XmlAttribute attr in element.Attributes)
                    {
                        if (attr.Name.StartsWith("xmlns:") || attr.Name == "xmlns")
                        {
                            string prefix = attr.Name == "xmlns" ? string.Empty : attr.Name.Substring(6);
                            if (!namespaceList.ContainsKey(prefix))
                                namespaceList.Add(prefix, attr.Value);
                        }
                    }
                }
            }

            if (node.ParentNode != null)
                CollectNamespaces(node.ParentNode, ref namespaceList);
        }

        private byte[] DigestInfoSHA256(byte[] hash)
        {
            byte[] prefix =
            {
                0x30, 0x31,
                0x30, 0x0d,
                0x06, 0x09,
                0x60, 0x86, 0x48, 0x01, 0x65, 0x03, 0x04, 0x02, 0x01,
                0x05, 0x00,
                0x04, 0x20
            };
            var di = new byte[prefix.Length + hash.Length];
            Buffer.BlockCopy(prefix, 0, di, 0, prefix.Length);
            Buffer.BlockCopy(hash, 0, di, prefix.Length, hash.Length);
            return di;
        }

        private void LogChainEmbeddingDiagnostics(
            X509Certificate2 signerCert,
            IReadOnlyCollection<X509Certificate2> tokenCertificates,
            IReadOnlyCollection<X509Certificate2> embeddingChainCertificates)
        {
            try
            {
                var tokenThumbprints = new HashSet<string>(
                    tokenCertificates
                        .Select(x => x.Thumbprint)
                        .Where(x => !string.IsNullOrWhiteSpace(x))!
                        .Select(x => x!),
                    StringComparer.OrdinalIgnoreCase);

                int intermediateCount = 0;
                int rootCount = 0;
                int index = 0;

                foreach (var cert in embeddingChainCertificates)
                {
                    bool isSigner = string.Equals(cert.Thumbprint, signerCert.Thumbprint, StringComparison.OrdinalIgnoreCase);
                    bool isRoot = DistinguishedNameEquals(cert.Subject, cert.Issuer);
                    bool fromToken = !string.IsNullOrWhiteSpace(cert.Thumbprint) && tokenThumbprints.Contains(cert.Thumbprint);

                    string role = isSigner ? "EE" : isRoot ? "Root" : "Ara";
                    if (!isSigner)
                    {
                        if (isRoot) rootCount++;
                        else intermediateCount++;
                    }

                    _logger.LogInformation(
                        "PDF zincir[{Index}] Role={Role}, TokenKaynak={TokenSource}, Subject={Subject}, Issuer={Issuer}, Thumbprint={Thumbprint}, NotAfter={NotAfter:yyyy-MM-dd}",
                        index++,
                        role,
                        fromToken,
                        cert.Subject,
                        cert.Issuer,
                        cert.Thumbprint,
                        cert.NotAfter);
                }

                _logger.LogInformation(
                    "PDF imza zinciri hazır. Toplam={Total}, EE=1, Ara={Intermediate}, Root={Root}, TokenSertifika={TokenCount}",
                    embeddingChainCertificates.Count,
                    intermediateCount,
                    rootCount,
                    tokenCertificates.Count);

                if (intermediateCount == 0)
                {
                    _logger.LogWarning("PDF imza zincirinde ara sertifika bulunamadı. Acrobat kimlik doğrulaması sorunlu olabilir.");
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "PDF imza zinciri loglama sırasında hata oluştu.");
            }
        }

        private void LogSignedPdfEmbeddingDiagnostics(
            byte[] signedPdfBytes,
            string preferredFieldName,
            IReadOnlyCollection<X509Certificate2> expectedChainSourceCertificates)
        {
            try
            {
                using var reader = new PdfReader(new MemoryStream(signedPdfBytes));
                using var pdfDoc = new PdfDocument(reader);
                var signatureUtil = new SignatureUtil(pdfDoc);
                var signatureNames = signatureUtil.GetSignatureNames();

                if (signatureNames == null || signatureNames.Count == 0)
                {
                    _logger.LogWarning("İmzalı PDF içinde imza alanı bulunamadı. Gömülü zincir kontrolü yapılamadı.");
                    return;
                }

                string selectedFieldName = preferredFieldName;
                bool foundPreferred = signatureNames.Any(x => string.Equals(x, preferredFieldName, StringComparison.Ordinal));
                if (!foundPreferred)
                    selectedFieldName = signatureNames[signatureNames.Count - 1];

                var pkcs7 = signatureUtil.ReadSignatureData(selectedFieldName);
                var embeddedCertificates = pkcs7.GetCertificates();
                int embeddedCount = embeddedCertificates?.Length ?? 0;
                int expectedCount = expectedChainSourceCertificates.Count;

                _logger.LogInformation(
                    "PDF gömülü zincir kontrolü: Field={Field}, EmbeddedCertCount={Embedded}, ExpectedChainCount={Expected}",
                    selectedFieldName,
                    embeddedCount,
                    expectedCount);

                if (embeddedCount < 2)
                {
                    _logger.LogWarning("PDF içindeki CMS sertifika sayısı {Count}. Ara sertifika gömülmemiş olabilir.", embeddedCount);
                }
                else if (embeddedCount < expectedCount)
                {
                    _logger.LogWarning(
                        "PDF içindeki CMS sertifika sayısı beklenenden düşük. Embedded={Embedded}, Expected={Expected}. Root dışarıda bırakılmış veya ara sertifika eksik olabilir.",
                        embeddedCount,
                        expectedCount);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "İmzalı PDF gömülü zincir kontrolü başarısız.");
            }
        }

        private static SignaturePlacement ResolveSignaturePlacement(byte[] pdfContent, PdfSignatureOptions options)
        {
            using var input = new MemoryStream(pdfContent);
            using var reader = new PdfReader(input);
            using var pdf = new PdfDocument(reader);

            int totalPages = pdf.GetNumberOfPages();
            int requestedPage = options.PageNumber.GetValueOrDefault(totalPages);
            int pageNumber = Math.Clamp(requestedPage, 1, totalPages);

            PdfRectangle pageRect = pdf.GetPage(pageNumber).GetPageSize();

            float margin = Math.Max(8f, options.Margin);
            float maxWidth = Math.Max(120f, pageRect.GetWidth() - (margin * 2f));
            float maxHeight = Math.Max(60f, pageRect.GetHeight() - (margin * 2f));

            float width = Math.Clamp(options.Width ?? 300f, 180f, maxWidth);
            float height = Math.Clamp(options.Height ?? 128f, 80f, maxHeight);

            float x = options.X ?? (pageRect.GetWidth() - width - margin);
            float y = options.Y ?? margin;

            float minX = margin;
            float maxX = Math.Max(minX, pageRect.GetWidth() - width - margin);
            float minY = margin;
            float maxY = Math.Max(minY, pageRect.GetHeight() - height - margin);

            x = Math.Clamp(x, minX, maxX);
            y = Math.Clamp(y, minY, maxY);

            return new SignaturePlacement(pageNumber, new PdfRectangle(x, y, width, height));
        }

        private static (IX509Certificate[] chain, List<X509Certificate2> sourceCertificates) BuildCertificateChainWithSource(
            X509Certificate2 cert,
            IReadOnlyCollection<X509Certificate2>? extraCertificates = null)
        {
            var sourceCertificates = BuildCertificateChainSourceCertificates(cert, extraCertificates);
            var chainCertificates = ConvertToITextCertificateChain(sourceCertificates);
            return (chainCertificates, sourceCertificates);
        }

        private static IX509Certificate[] BuildCertificateChain(
            X509Certificate2 cert,
            IReadOnlyCollection<X509Certificate2>? extraCertificates = null)
        {
            return BuildCertificateChainWithSource(cert, extraCertificates).chain;
        }

        private static List<X509Certificate2> BuildCertificateChainSourceCertificates(
            X509Certificate2 cert,
            IReadOnlyCollection<X509Certificate2>? extraCertificates = null)
        {
            // Bazı doğrulayıcılar (özellikle Adobe/WebView2) ara sertifika gömülü değilse
            // zinciri kuramayabiliyor. Bu yüzden mümkün olduğunda tam zinciri imzaya koyuyoruz.
            var sourceCertificates = new List<X509Certificate2> { cert };
            if (extraCertificates != null)
            {
                foreach (var item in extraCertificates)
                    AddUniqueCertificate(sourceCertificates, item);
            }

            using (var chain = new X509Chain())
            {
                chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
                chain.ChainPolicy.RevocationFlag = X509RevocationFlag.ExcludeRoot;
                chain.ChainPolicy.VerificationFlags = X509VerificationFlags.AllowUnknownCertificateAuthority;
                chain.ChainPolicy.UrlRetrievalTimeout = TimeSpan.FromSeconds(5);
                chain.ChainPolicy.DisableCertificateDownloads = false;
                if (extraCertificates != null)
                {
                    foreach (var extra in extraCertificates)
                        chain.ChainPolicy.ExtraStore.Add(extra);
                }

                chain.Build(cert);
                foreach (X509ChainElement element in chain.ChainElements)
                {
                    AddUniqueCertificate(sourceCertificates, element.Certificate);
                }
            }

            // Chain.Build tüm ara sertifikaları bulamayabilir; store'dan issuer takviyesi yap.
            for (int depth = 0; depth < 5; depth++)
            {
                bool addedAny = false;
                var snapshot = sourceCertificates.ToList();

                foreach (var item in snapshot)
                {
                    if (DistinguishedNameEquals(item.Subject, item.Issuer))
                        continue; // self-signed/root

                    bool hasIssuerAlready = sourceCertificates.Any(x => DistinguishedNameEquals(x.Subject, item.Issuer));
                    if (hasIssuerAlready)
                        continue;

                    foreach (var issuerCert in FindIssuerCertificatesInStores(item))
                    {
                        if (AddUniqueCertificate(sourceCertificates, issuerCert))
                            addedAny = true;
                    }

                    foreach (var issuerCert in FindIssuerCertificatesFromAia(item))
                    {
                        if (AddUniqueCertificate(sourceCertificates, issuerCert))
                            addedAny = true;
                    }
                }

                if (!addedAny)
                    break;
            }

            return sourceCertificates;
        }

        private static IX509Certificate[] ConvertToITextCertificateChain(IReadOnlyCollection<X509Certificate2> sourceCertificates)
        {
            var factory = BouncyCastleFactoryCreator.GetFactory();
            var unique = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var chainCertificates = new List<IX509Certificate>();

            foreach (var item in sourceCertificates)
            {
                string key = item.Thumbprint ?? Convert.ToBase64String(item.RawData);
                if (!unique.Add(key))
                    continue;

                using var certStream = new MemoryStream(item.RawData);
                chainCertificates.Add(factory.CreateX509Certificate(certStream));
            }

            return chainCertificates.ToArray();
        }

        private static List<X509Certificate2> GetAllCertificatesFromSession(ISession session)
        {
            var certs = new List<X509Certificate2>();
            var template = new List<IObjectAttribute>
            {
                session.Factories.ObjectAttributeFactory.Create(CKA.CKA_CLASS, CKO.CKO_CERTIFICATE),
                session.Factories.ObjectAttributeFactory.Create(CKA.CKA_CERTIFICATE_TYPE, CKC.CKC_X_509)
            };

            foreach (var certObject in session.FindAllObjects(template))
            {
                var value = session.GetAttributeValue(certObject, new List<CKA> { CKA.CKA_VALUE })[0];
                byte[] certBytes = value.GetValueAsByteArray() ?? Array.Empty<byte>();
                if (certBytes.Length == 0)
                    continue;

                try
                {
                    AddUniqueCertificate(certs, new X509Certificate2(certBytes));
                }
                catch
                {
                    // Token içindeki bozuk/uyumsuz sertifika obje kayıtlarını atla.
                }
            }

            return certs;
        }

        private static bool AddUniqueCertificate(List<X509Certificate2> target, X509Certificate2 candidate)
        {
            string thumbprint = candidate.Thumbprint ?? string.Empty;
            bool exists = !string.IsNullOrWhiteSpace(thumbprint) &&
                          target.Any(x => string.Equals(x.Thumbprint, thumbprint, StringComparison.OrdinalIgnoreCase));
            if (exists)
                return false;

            target.Add(new X509Certificate2(candidate.RawData));
            return true;
        }

        private static bool DistinguishedNameEquals(string? left, string? right)
        {
            if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
                return false;

            static string Normalize(string dn) => Regex.Replace(dn, @"\s+", string.Empty).Trim();
            return string.Equals(Normalize(left), Normalize(right), StringComparison.OrdinalIgnoreCase);
        }

        private static IReadOnlyList<X509Certificate2> FindIssuerCertificatesInStores(X509Certificate2 childCert)
        {
            string issuerDn = childCert.Issuer;
            var storeLocations = new[] { StoreLocation.CurrentUser, StoreLocation.LocalMachine };
            var storeNames = new[] { StoreName.CertificateAuthority, StoreName.Root };
            var results = new List<X509Certificate2>();
            var unique = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            byte[]? authorityKeyIdentifier = TryGetAuthorityKeyIdentifier(childCert);

            foreach (var location in storeLocations)
            {
                foreach (var storeName in storeNames)
                {
                    X509Store? store = null;
                    try
                    {
                        store = new X509Store(storeName, location);
                        store.Open(OpenFlags.ReadOnly | OpenFlags.OpenExistingOnly);

                        var exactMatches = store.Certificates.Find(
                            X509FindType.FindBySubjectDistinguishedName,
                            issuerDn,
                            validOnly: false);

                        foreach (var cert in exactMatches.OfType<X509Certificate2>())
                        {
                            if (!IsIssuerCandidate(childCert, cert, authorityKeyIdentifier))
                                continue;

                            string key = cert.Thumbprint ?? Convert.ToBase64String(cert.RawData);
                            if (unique.Add(key))
                                results.Add(new X509Certificate2(cert.RawData));
                        }

                        if (exactMatches.Count == 0)
                        {
                            foreach (var cert in store.Certificates.OfType<X509Certificate2>())
                            {
                                if (IsIssuerCandidate(childCert, cert, authorityKeyIdentifier))
                                {
                                    string key = cert.Thumbprint ?? Convert.ToBase64String(cert.RawData);
                                    if (unique.Add(key))
                                        results.Add(new X509Certificate2(cert.RawData));
                                }
                            }
                        }
                    }
                    catch
                    {
                        // Store erişimi her makinede garanti değil; atlayıp devam ediyoruz.
                    }
                    finally
                    {
                        store?.Close();
                    }
                }
            }

            return results;
        }

        private static IReadOnlyList<X509Certificate2> FindIssuerCertificatesFromAia(
            X509Certificate2 childCert,
            List<string>? warnings = null)
        {
            var results = new List<X509Certificate2>();
            var unique = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            byte[]? authorityKeyIdentifier = TryGetAuthorityKeyIdentifier(childCert);
            var urls = ExtractCaIssuerUrls(childCert);

            foreach (var url in urls)
            {
                try
                {
                    var payload = _httpClient.GetByteArrayAsync(url).GetAwaiter().GetResult();
                    foreach (var cert in ParseCertificates(payload))
                    {
                        if (!IsIssuerCandidate(childCert, cert, authorityKeyIdentifier))
                            continue;

                        string key = cert.Thumbprint ?? Convert.ToBase64String(cert.RawData);
                        if (unique.Add(key))
                            results.Add(new X509Certificate2(cert.RawData));
                    }
                }
                catch (Exception ex)
                {
                    warnings?.Add($"AIA indirilemedi ({url}): {ex.Message}");
                }
            }

            return results;
        }

        private static IReadOnlyList<string> ExtractCaIssuerUrls(X509Certificate2 cert)
        {
            var urls = new List<string>();
            var extension = cert.Extensions["1.3.6.1.5.5.7.1.1"]; // Authority Information Access
            if (extension == null)
                return urls;

            try
            {
                var reader = new AsnReader(extension.RawData, AsnEncodingRules.DER);
                var sequence = reader.ReadSequence();

                while (sequence.HasData)
                {
                    var accessDescription = sequence.ReadSequence();
                    string accessMethod = accessDescription.ReadObjectIdentifier();

                    if (accessMethod == "1.3.6.1.5.5.7.48.2" && accessDescription.HasData)
                    {
                        Asn1Tag tag = accessDescription.PeekTag();
                        if (tag.TagClass == TagClass.ContextSpecific && tag.TagValue == 6)
                        {
                            string url = accessDescription.ReadCharacterString(
                                UniversalTagNumber.IA5String,
                                new Asn1Tag(TagClass.ContextSpecific, 6));

                            if (!string.IsNullOrWhiteSpace(url) &&
                                (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                                 url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)))
                            {
                                urls.Add(url.Trim());
                            }
                        }
                    }
                }
            }
            catch
            {
                // AIA parse edilemezse sessizce geç.
            }

            return urls;
        }

        private static IReadOnlyList<X509Certificate2> ParseCertificates(byte[] payload)
        {
            var parsed = new List<X509Certificate2>();

            try
            {
                var collection = new X509Certificate2Collection();
                collection.Import(payload);
                parsed.AddRange(collection.Cast<X509Certificate2>().Select(x => new X509Certificate2(x.RawData)));
                if (parsed.Count > 0)
                    return parsed;
            }
            catch
            {
                // Aşağıdaki fallback denenecek.
            }

            try
            {
                var single = new X509Certificate2(payload);
                parsed.Add(new X509Certificate2(single.RawData));
            }
            catch
            {
                // Desteklenmeyen format olabilir.
            }

            return parsed;
        }

        private static bool IsIssuerCandidate(X509Certificate2 childCert, X509Certificate2 issuerCert, byte[]? authorityKeyIdentifier)
        {
            if (!DistinguishedNameEquals(issuerCert.Subject, childCert.Issuer))
                return false;

            if (authorityKeyIdentifier == null || authorityKeyIdentifier.Length == 0)
                return true;

            byte[]? subjectKeyIdentifier = TryGetSubjectKeyIdentifier(issuerCert);
            if (subjectKeyIdentifier == null || subjectKeyIdentifier.Length == 0)
                return true;

            return authorityKeyIdentifier.AsSpan().SequenceEqual(subjectKeyIdentifier);
        }

        private static byte[]? TryGetAuthorityKeyIdentifier(X509Certificate2 certificate)
        {
            var extension = certificate.Extensions["2.5.29.35"];
            if (extension == null)
                return null;

            try
            {
                var reader = new AsnReader(extension.RawData, AsnEncodingRules.DER);
                var sequence = reader.ReadSequence();

                while (sequence.HasData)
                {
                    Asn1Tag tag = sequence.PeekTag();
                    if (tag.TagClass == TagClass.ContextSpecific && tag.TagValue == 0)
                        return sequence.ReadOctetString(new Asn1Tag(TagClass.ContextSpecific, 0));

                    sequence.ReadEncodedValue();
                }
            }
            catch
            {
                // Bazı kart sertifikalarında AKI parse edilemeyebilir.
            }

            return null;
        }

        private static byte[]? TryGetSubjectKeyIdentifier(X509Certificate2 certificate)
        {
            var extension = certificate.Extensions.OfType<X509SubjectKeyIdentifierExtension>().FirstOrDefault();
            if (extension == null || string.IsNullOrWhiteSpace(extension.SubjectKeyIdentifier))
                return null;

            string hex = Regex.Replace(extension.SubjectKeyIdentifier, @"[^0-9A-Fa-f]", string.Empty);
            if (hex.Length == 0 || (hex.Length % 2) != 0)
                return null;

            try
            {
                return Convert.FromHexString(hex);
            }
            catch
            {
                return null;
            }
        }

        private static string ResolveSignerName(X509Certificate2 cert, string? preferred)
        {
            if (!string.IsNullOrWhiteSpace(preferred))
                return preferred.Trim();

            string simple = cert.GetNameInfo(X509NameType.SimpleName, false);
            if (!string.IsNullOrWhiteSpace(simple))
                return simple;

            return cert.Subject;
        }

        private static string BuildFieldName(string? requestedName)
        {
            string field = string.IsNullOrWhiteSpace(requestedName)
                ? $"Signature_{DateTime.Now:yyyyMMddHHmmssfff}"
                : requestedName.Trim();

            field = Regex.Replace(field, @"[^A-Za-z0-9_\-\.]", "_");

            if (field.Length > 64)
                field = field.Substring(0, 64);

            if (string.IsNullOrWhiteSpace(field))
                field = $"Signature_{DateTime.Now:yyyyMMddHHmmssfff}";

            return field;
        }

        private static string BuildSignedPdfFileName(string? originalFileName)
        {
            string baseName = Path.GetFileNameWithoutExtension(originalFileName ?? string.Empty);
            if (string.IsNullOrWhiteSpace(baseName))
                baseName = "signed-document";

            foreach (char ch in Path.GetInvalidFileNameChars())
                baseName = baseName.Replace(ch, '_');

            return $"{baseName}-{DateTime.Now:yyyyMMddHHmmssfff}.pdf";
        }

        private static SignatureFieldAppearance BuildSignatureAppearance(
            string fieldName,
            string signerName,
            X509Certificate2 cert,
            bool timestampEnabled)
        {
            var (regularFont, boldFont) = ResolveSignatureFonts();
            string signDateText = DateTime.Now.ToString("dd.MM.yyyy HH:mm:ss zzz", CultureInfo.GetCultureInfo("tr-TR"));
            string? tckn = ExtractTcknFromCert(cert);

            var signatureCard = new Div()
                .SetPadding(4f)
                .SetMargin(0f)
                .SetBackgroundColor(new DeviceRgb(248, 251, 255))
                .SetBorder(new SolidBorder(new DeviceRgb(28, 69, 120), 0.8f));

            signatureCard.Add(new Paragraph("ELEKTRONİK İMZA")
                .SetFont(boldFont)
                .SetFontSize(8.3f)
                .SetMargin(0f)
                .SetMultipliedLeading(1.0f)
                .SetFontColor(new DeviceRgb(21, 72, 128)));

            signatureCard.Add(new Paragraph($"İmza Sahibi: {signerName}")
                .SetFont(boldFont)
                .SetFontSize(9.7f)
                .SetMarginTop(2f)
                .SetMarginBottom(0f)
                .SetMultipliedLeading(1.0f));

            if (!string.IsNullOrWhiteSpace(tckn))
            {
                signatureCard.Add(new Paragraph($"TCKN: {tckn}")
                    .SetFont(regularFont)
                    .SetFontSize(8.8f)
                    .SetMargin(0f)
                    .SetMultipliedLeading(1.0f));
            }

            signatureCard.Add(new Paragraph($"İmza Tarihi: {signDateText}")
                .SetFont(regularFont)
                .SetFontSize(8.0f)
                .SetMarginTop(1.5f)
                .SetMarginBottom(0f)
                .SetMultipliedLeading(1.0f)
                .SetFontColor(new DeviceRgb(35, 57, 82)));

            signatureCard.Add(new Paragraph("Bu belge elektronik imza ile imzalanmıştır.")
                .SetFont(regularFont)
                .SetFontSize(8.0f)
                .SetMarginTop(1.5f)
                .SetMarginBottom(0f)
                .SetMultipliedLeading(1.0f)
                .SetFontColor(new DeviceRgb(35, 57, 82)));

            if (timestampEnabled)
            {
                signatureCard.Add(new Paragraph("Zaman damgası eklenmiştir.")
                    .SetFont(regularFont)
                    .SetFontSize(7.8f)
                    .SetMarginTop(1f)
                    .SetMarginBottom(0f)
                    .SetMultipliedLeading(1.0f)
                    .SetFontColor(new DeviceRgb(18, 109, 79)));
            }

            return new SignatureFieldAppearance(fieldName)
                .SetBorder(Border.NO_BORDER)
                .SetContent(signatureCard);
        }

        private static (PdfFont regular, PdfFont bold) ResolveSignatureFonts()
        {
            var regular = CreateEmbeddedUnicodeFont(new[]
            {
                @"C:\Windows\Fonts\segoeui.ttf",
                @"C:\Windows\Fonts\arial.ttf",
                @"C:\Windows\Fonts\calibri.ttf"
            });

            var bold = CreateEmbeddedUnicodeFont(new[]
            {
                @"C:\Windows\Fonts\segoeuib.ttf",
                @"C:\Windows\Fonts\arialbd.ttf",
                @"C:\Windows\Fonts\calibrib.ttf"
            });

            return (regular, bold);
        }

        private static PdfFont CreateEmbeddedUnicodeFont(IEnumerable<string> fontCandidates)
        {
            foreach (var fontPath in fontCandidates)
            {
                if (!File.Exists(fontPath))
                    continue;

                try
                {
                    return PdfFontFactory.CreateFont(
                        fontPath,
                        PdfEncodings.IDENTITY_H,
                        PdfFontFactory.EmbeddingStrategy.PREFER_EMBEDDED);
                }
                catch
                {
                    // Sonraki font adayına geç.
                }
            }

            return PdfFontFactory.CreateFont(StandardFonts.HELVETICA);
        }

        private sealed record SignaturePlacement(int PageNumber, PdfRectangle Rect);

        private sealed class Pkcs11ExternalSignature : IExternalSignature
        {
            private readonly ISession _session;
            private readonly IObjectHandle _privateKey;

            public Pkcs11ExternalSignature(ISession session, IObjectHandle privateKey)
            {
                _session = session ?? throw new ArgumentNullException(nameof(session));
                _privateKey = privateKey ?? throw new ArgumentNullException(nameof(privateKey));
            }

            public string GetDigestAlgorithmName() => "SHA-256";

            public string GetSignatureAlgorithmName() => "RSA";

            public ISignatureMechanismParams GetSignatureMechanismParameters() => null!;

            public byte[] Sign(byte[] message)
            {
                byte[] hash = SHA256.HashData(message);
                byte[] digestInfo = BuildDigestInfoSha256(hash);
                var mechanism = _session.Factories.MechanismFactory.Create(CKM.CKM_RSA_PKCS);
                return _session.Sign(mechanism, _privateKey, digestInfo);
            }

            private static byte[] BuildDigestInfoSha256(byte[] hash)
            {
                byte[] prefix =
                {
                    0x30, 0x31,
                    0x30, 0x0d,
                    0x06, 0x09,
                    0x60, 0x86, 0x48, 0x01, 0x65, 0x03, 0x04, 0x02, 0x01,
                    0x05, 0x00,
                    0x04, 0x20
                };

                var output = new byte[prefix.Length + hash.Length];
                Buffer.BlockCopy(prefix, 0, output, 0, prefix.Length);
                Buffer.BlockCopy(hash, 0, output, prefix.Length, hash.Length);
                return output;
            }
        }
        #endregion

        public class SignatureDeviceDto
        {
            public int Id { get; set; }
            public string Label { get; set; } = "";
            public string Serial { get; set; } = "";
            public string Subject { get; set; } = "";

            // ✅ eklendi: web app'e gidecek TCKN (sertifika DN içinden)
            public string? TcKimlikNo { get; set; } = null;
        }
    }
}

