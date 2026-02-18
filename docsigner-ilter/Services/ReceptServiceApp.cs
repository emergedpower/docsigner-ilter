using Microsoft.Extensions.Logging;
using Net.Pkcs11Interop.Common;
using Net.Pkcs11Interop.HighLevelAPI;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Numerics;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Security.Cryptography.Xml;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using System.Text.RegularExpressions; // ✅ eklendi (TCKN parse)
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
                ISession session;

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
                        LoginWithPolicy(session, pin, forceFreshLogin: true, slotIdx: slotIdx);
                        _sessionCache.TryAdd(slotIdx, session);
                    }
                    else if (_sessionCache.TryGetValue(slotIdx, out session))
                    {
                        _logger.LogInformation("PDF imza için cache session kullanılıyor (slot {Slot}).", slotIdx);
                    }
                    else
                    {
                        session = slots[slotIdx].OpenSession(SessionType.ReadWrite);
                        LoginWithPolicy(session, pin, forceFreshLogin: false, slotIdx: slotIdx);
                        _sessionCache.TryAdd(slotIdx, session);
                    }

                    var cert = GetSigningCertificate(session);
                    var key = GetPrivateKey(session);
                    var placement = ResolveSignaturePlacement(pdfContent, options);
                    var signerName = ResolveSignerName(cert, options.SignerDisplayName);
                    var fieldName = BuildFieldName(options.SignatureFieldName);
                    var chain = BuildCertificateChain(cert);
                    bool shouldUseTimestamp = options.EnableTimestamp && !string.IsNullOrWhiteSpace(options.TsaUrl);
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
                    if (shouldUseTimestamp)
                    {
                        var tsa = new TSAClientBouncyCastle(
                            options.TsaUrl,
                            options.TsaUsername,
                            options.TsaPassword,
                            8192,
                            "SHA-256");

                        twoPhaseSigner.SetTSAClient(tsa);
                        timestampApplied = true;
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
                    using (var preparedReader = new PdfReader(new MemoryStream(preparedPdfBytes)))
                    using (var finalOutputStream = new MemoryStream())
                    {
                        if (timestampApplied)
                        {
                            twoPhaseSigner.SignCMSContainerWithBaselineTProfile(
                                externalSignature,
                                preparedReader,
                                finalOutputStream,
                                fieldName,
                                cmsContainer);
                        }
                        else
                        {
                            twoPhaseSigner.SignCMSContainerWithBaselineBProfile(
                                externalSignature,
                                preparedReader,
                                finalOutputStream,
                                fieldName,
                                cmsContainer);
                        }

                        signedPdfBytes = finalOutputStream.ToArray();
                    }

                    string outputPath = Path.Combine(workDir, BuildSignedPdfFileName(options.FileName));
                    File.WriteAllBytes(outputPath, signedPdfBytes);

                    _logger.LogInformation(
                        "PDF imzalama başarılı. Dosya={Path}, Slot={Slot}, Signer={Signer}, Timestamp={Timestamp}",
                        outputPath, slotIdx, signerName, timestampApplied);

                    return (signedPdfBytes, outputPath, signerName, timestampApplied);
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
                            var cert = GetSigningCertificate(session);
                            var key = GetPrivateKey(session);
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

        private X509Certificate2 GetSigningCertificate(ISession s)
        {
            var attrs = new List<IObjectAttribute>
            {
                s.Factories.ObjectAttributeFactory.Create(CKA.CKA_CLASS, CKO.CKO_CERTIFICATE),
                s.Factories.ObjectAttributeFactory.Create(CKA.CKA_CERTIFICATE_TYPE, CKC.CKC_X_509)
            };
            var objs = s.FindAllObjects(attrs);
            if (objs.Count == 0)
                throw new Exception("Sertifika yok");
            var data = s.GetAttributeValue(objs[0], new List<CKA> { CKA.CKA_VALUE })[0].GetValueAsByteArray();
            return new X509Certificate2(data);
        }

        private IObjectHandle GetPrivateKey(ISession s)
        {
            var attrs = new List<IObjectAttribute>
            {
                s.Factories.ObjectAttributeFactory.Create(CKA.CKA_CLASS, CKO.CKO_PRIVATE_KEY),
                s.Factories.ObjectAttributeFactory.Create(CKA.CKA_KEY_TYPE, CKK.CKK_RSA)
            };
            var objs = s.FindAllObjects(attrs);
            if (objs.Count == 0)
                throw new Exception("Private key yok");
            return objs[0];
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

        private static IX509Certificate[] BuildCertificateChain(X509Certificate2 cert)
        {
            var factory = BouncyCastleFactoryCreator.GetFactory();
            using var certStream = new MemoryStream(cert.RawData);
            return new[] { factory.CreateX509Certificate(certStream) };
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

