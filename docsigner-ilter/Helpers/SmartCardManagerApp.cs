using log4net;
using Net.Pkcs11Interop.Common;
using Net.Pkcs11Interop.HighLevelAPI;
using System;
using System.Collections.Generic;
using System.IO;
using tr.gov.tubitak.uekae.esya.api.asn.x509;
using tr.gov.tubitak.uekae.esya.api.common;
using tr.gov.tubitak.uekae.esya.api.common.crypto;
using tr.gov.tubitak.uekae.esya.api.common.util;
using tr.gov.tubitak.uekae.esya.api.smartcard.pkcs11;

namespace docsigner_ilter.Helpers
{
    /// <summary>
    /// SmartCardManager – MAU3 bağımsız, PKCS11Interop tabanlı
    /// - SmartOp / P11SmartCard kullanılmıyor
    /// - Kart / slot / sertifika erişimi doğrudan akisp11.dll üzerinden
    /// - ReceptServiceApp imzalamayı kendi PKCS#11 akışı ile yapıyor
    /// - Burada sertifika okumak / subject almak gibi işler için helper var
    /// </summary>
    public class SmartCardManagerApp
    {
        private static readonly ILog LOGGER = LogManager.GetLogger(typeof(SmartCardManagerApp));
        private static SmartCardManagerApp mSCManager;

        // Akit PKCS#11 DLL yolu (istersen config'ten oku)
        private const string Pkcs11Dll = @"C:\Windows\System32\akisp11.dll";

        // PKCS11Interop objeleri
        private readonly Pkcs11InteropFactories _factories = new Pkcs11InteropFactories();
        private IPkcs11Library _library;
        private List<ISlot> _slots;
        private ISlot _selectedSlot;
        private ISession _session;
        private int _slotIndex;

        private int mSlotCount = 0;
        private string mSerialNumber;

        private ECertificate mSignatureCert;
        private ECertificate mEncryptionCert;

        private readonly object _sync = new object();
        private bool _loggedIn = false;
        private DateTime _lastUseUtc = DateTime.MinValue;

        public bool IsLoggedIn => _loggedIn;
        public DateTime? LastUseUtc => _lastUseUtc == DateTime.MinValue ? null : _lastUseUtc;

        public static SmartCardManagerApp getInstance(int? slotIndex = null)
        {
            if (mSCManager == null)
            {
                mSCManager = new SmartCardManagerApp(slotIndex);
                return mSCManager;
            }

            try
            {
                mSCManager.RefreshTokenState(slotIndex);
                return mSCManager;
            }
            catch (SmartCardException)
            {
                mSCManager = null;
                throw;
            }
        }

        private SmartCardManagerApp(int? slotIndex = null)
        {
            try
            {
                LOGGER.Debug("SmartCardManager başlatılıyor (PKCS11Interop)...");
                InitializePkcs11(slotIndex);
                LOGGER.Info("SmartCardManager başarıyla başlatıldı (PKCS11Interop).");
            }
            catch (SmartCardException)
            {
                throw;
            }
            catch (Exception e)
            {
                throw new SmartCardException("SmartCardManager başlatılırken hata: " + e.Message, e);
            }
        }

        /// <summary>
        /// PKCS#11 library + slot + session açma
        /// </summary>
        private void InitializePkcs11(int? slotIndex)
        {
            // Eski session/library temizle
            if (_session != null)
            {
                try { if (_loggedIn) _session.Logout(); } catch { }
                try { _session.Dispose(); } catch { }
                _session = null;
            }

            if (_library != null)
            {
                try { _library.Dispose(); } catch { }
                _library = null;
            }

            try
            {
                _library = _factories.Pkcs11LibraryFactory.LoadPkcs11Library(
                    _factories,
                    Pkcs11Dll,
                    AppType.SingleThreaded);

                _slots = _library.GetSlotList(SlotsType.WithTokenPresent);
                if (_slots == null || _slots.Count == 0)
                    throw new SmartCardException("Kart takılı kart okuyucu bulunamadı");

                mSlotCount = _slots.Count;

                int idx = slotIndex ?? 0;
                if (idx < 0 || idx >= _slots.Count)
                    throw new SmartCardException("Geçersiz slot index: " + idx + ". Mevcut slot sayısı: " + _slots.Count);

                _slotIndex = idx;
                _selectedSlot = _slots[idx];

                var tokenInfo = _selectedSlot.GetTokenInfo();
                mSerialNumber = (tokenInfo.SerialNumber ?? string.Empty).Trim();

                _session = _selectedSlot.OpenSession(SessionType.ReadWrite);
                _loggedIn = false;
                _lastUseUtc = DateTime.MinValue;

                mSignatureCert = null;
                mEncryptionCert = null;

                LOGGER.Debug("PKCS#11 slot seçildi. SlotIndex=" + _slotIndex + ", Serial=" + mSerialNumber);
            }
            catch (Pkcs11Exception ex)
            {
                throw new SmartCardException("PKCS#11 başlatma hatası: " + ex.Message, ex);
            }
        }

        /// <summary>
        /// Kart değişti mi / slot sayısı değişti mi kontrol et.
        /// Gerekirse yeniden InitializePkcs11 çağırır.
        /// </summary>
        private void RefreshTokenState(int? desiredSlotIndex)
        {
            try
            {
                if (_library == null)
                {
                    InitializePkcs11(desiredSlotIndex);
                    return;
                }

                var slotsNow = _library.GetSlotList(SlotsType.WithTokenPresent);
                if (slotsNow == null || slotsNow.Count == 0)
                    throw new SmartCardException("Kart takılı değil");

                if (slotsNow.Count != mSlotCount)
                {
                    LOGGER.Debug("Slot sayısı değişti, SmartCardManager yeniden başlatılıyor.");
                    InitializePkcs11(desiredSlotIndex);
                    return;
                }

                int idx = desiredSlotIndex ?? _slotIndex;
                if (idx < 0 || idx >= slotsNow.Count)
                    throw new SmartCardException("Geçersiz slot index: " + idx + ". Mevcut slot sayısı: " + slotsNow.Count);

                var tokenInfo = slotsNow[idx].GetTokenInfo();
                string serial = (tokenInfo.SerialNumber ?? string.Empty).Trim();

                if (!string.Equals(serial, mSerialNumber, StringComparison.OrdinalIgnoreCase))
                {
                    LOGGER.Debug("Kart değişti, SmartCardManager yeniden başlatılıyor.");
                    InitializePkcs11(idx);
                }
                else
                {
                    _slots = slotsNow;
                    _selectedSlot = slotsNow[idx];
                    _slotIndex = idx;
                }
            }
            catch (Pkcs11Exception e)
            {
                throw new SmartCardException("PKCS#11 hatası: " + e.Message, e);
            }
        }

        /// <summary>
        /// Kart üzerindeki imza sertifikasını (ECertificate) döner.
        /// </summary>
        public ECertificate getSignatureCertificate(bool checkIsQualified, bool checkBeingNonQualified)
        {
            try
            {
                if (mSignatureCert == null)
                {
                    LOGGER.Debug("İmza sertifikaları okunuyor (PKCS11Interop)...");
                    List<byte[]> allCerts = ReadAllCertificates();
                    mSignatureCert = selectCertificate(checkIsQualified, checkBeingNonQualified, allCerts);
                    LOGGER.Debug("Sertifika seçildi: " + mSignatureCert.getSubject().ToString());
                }
                return mSignatureCert;
            }
            catch (Exception e)
            {
                LOGGER.Error("Sertifika alınırken hata: " + e.Message + " - Detay: " + e.ToString());
                throw new SmartCardException("Sertifika alınırken hata: " + e.Message, e);
            }
        }

        /// <summary>
        /// Encryption sertifikası – şu an aynı listeden seçiyor.
        /// </summary>
        public ECertificate getEncryptionCertificate(bool checkIsQualified, bool checkBeingNonQualified)
        {
            try
            {
                if (mEncryptionCert == null)
                {
                    LOGGER.Debug("Encryption sertifikaları okunuyor (PKCS11Interop)...");
                    List<byte[]> allCerts = ReadAllCertificates();
                    mEncryptionCert = selectCertificate(checkIsQualified, checkBeingNonQualified, allCerts);
                }
                return mEncryptionCert;
            }
            catch (Exception e)
            {
                LOGGER.Error("Encryption sertifikası alınırken hata: " + e.Message + " - Detay: " + e.ToString());
                throw new SmartCardException("Encryption sertifikası alınırken hata: " + e.Message, e);
            }
        }

        /// <summary>
        /// Token üzerindeki tüm X.509 sertifikaları byte[] olarak okur.
        /// </summary>
        private List<byte[]> ReadAllCertificates()
        {
            if (_session == null)
                throw new SmartCardException("PKCS#11 oturumu açık değil.");

            var result = new List<byte[]>();

            var searchTemplate = new List<IObjectAttribute>
            {
                _session.Factories.ObjectAttributeFactory.Create(CKA.CKA_CLASS, CKO.CKO_CERTIFICATE),
                _session.Factories.ObjectAttributeFactory.Create(CKA.CKA_CERTIFICATE_TYPE, CKC.CKC_X_509)
            };

            var objects = _session.FindAllObjects(searchTemplate);
            foreach (var obj in objects)
            {
                var attrs = _session.GetAttributeValue(obj, new List<CKA> { CKA.CKA_VALUE });
                if (attrs != null && attrs.Count > 0)
                {
                    var raw = attrs[0].GetValueAsByteArray();
                    if (raw != null && raw.Length > 0)
                        result.Add(raw);
                }
            }

            if (result.Count == 0)
                throw new ESYAException("Kartta X.509 sertifika bulunamadı");

            return result;
        }

        private ECertificate selectCertificate(bool checkIsQualified, bool checkBeingNonQualified, List<byte[]> aCerts)
        {
            if (aCerts == null || aCerts.Count == 0)
                throw new ESYAException("Kartta sertifika bulunmuyor");
            if (checkIsQualified && checkBeingNonQualified)
                throw new ESYAException("Bir sertifika ya nitelikli sertifikadır, ya niteliksiz sertifikadır. Hem nitelikli hem niteliksiz olamaz");

            List<ECertificate> certs = new List<ECertificate>();
            foreach (byte[] bs in aCerts)
            {
                ECertificate cert = new ECertificate(bs);

                // Tarih kontrolü
                if (!checkIsDateValid(cert))
                {
                    LOGGER.Debug("Sertifika tarih geçersiz, atlandı: " + cert.getSubject().ToString());
                    continue;
                }

                if (checkIsQualified)
                {
                    if (cert.isQualifiedCertificate())
                        certs.Add(cert);
                }
                else if (checkBeingNonQualified)
                {
                    if (!cert.isQualifiedCertificate())
                        certs.Add(cert);
                }
                else
                {
                    certs.Add(cert);
                }
            }

            if (certs.Count == 0)
            {
                if (checkIsQualified)
                    throw new ESYAException("Kartta nitelikli sertifika bulunmuyor");
                else if (checkBeingNonQualified)
                    throw new ESYAException("Kartta niteliksiz sertifika bulunmuyor");
            }

            return certs[0];
        }

        // Sertifika tarih helper
        private bool checkIsDateValid(ECertificate cert)
        {
            DateTime? certStartTime = cert.getNotBefore();
            DateTime? certEndTime = cert.getNotAfter();
            DateTime now = DateTime.UtcNow;

            if (!certStartTime.HasValue || !certEndTime.HasValue)
                return false;

            return now > certStartTime && now < certEndTime;
        }

        private string getSelectedSerialNumber() => mSerialNumber;
        private int getSlotCount() => mSlotCount;

        /// <summary>
        /// Eski mimariyi bozmamak için imza ile ilgili API'ler bırakıldı fakat
        /// artık MAU3 ile imzalama yapılmadığı için kullanıldığında exception atıyor.
        /// Yeni imzalama akışı: ReceptServiceApp.SignRecept (PKCS11Interop).
        /// </summary>
        [Obsolete("Bu versiyonda imzalama için SmartCardManagerApp.getSigner kullanılmıyor. ReceptServiceApp.SignRecept metodunu kullanın.")]
        public BaseSigner getSigner(string aCardPIN, ECertificate aCert, bool forceFreshLogin = false, int? slotIndex = null)
        {
            throw new SmartCardException("SmartCardManagerApp.getSigner bu versiyonda desteklenmiyor. Lütfen imzalama için ReceptServiceApp.SignRecept metodunu kullanın.");
        }

        [Obsolete("Bu versiyonda kullanılmıyor.")]
        public void LoginOnce(string pin, ECertificate cert, bool forceFreshLogin = false)
        {
            throw new SmartCardException("SmartCardManagerApp.LoginOnce bu versiyonda kullanılmıyor.");
        }

        [Obsolete("Bu versiyonda kullanılmıyor.")]
        public BaseSigner GetCachedSigner(ECertificate cert)
        {
            throw new SmartCardException("SmartCardManagerApp.GetCachedSigner bu versiyonda kullanılmıyor.");
        }

        /// <summary>
        /// Yeni PKCS11 tabanlı yapıda idle logout'a özel ihtiyacın yok;
        /// ReceptServiceApp kendi session'ını yönetiyor.
        /// Burayı ister boş bırak, ister ileride token idle takip için kullan.
        /// </summary>
        public void LogoutIfIdle(TimeSpan idleThreshold = default)
        {
            // Boş
        }

        public void logout()
        {
            lock (_sync)
            {
                try
                {
                    if (_session != null)
                    {
                        try { if (_loggedIn) _session.Logout(); } catch { }
                        try { _session.Dispose(); } catch { }
                        _session = null;
                    }

                    if (_library != null)
                    {
                        try { _library.Dispose(); } catch { }
                        _library = null;
                    }

                    _loggedIn = false;
                    _lastUseUtc = DateTime.MinValue;
                    mSignatureCert = null;
                    mEncryptionCert = null;

                    LOGGER.Debug("SmartCardManager logout yapıldı (PKCS11Interop).");
                }
                catch (Exception e)
                {
                    LOGGER.Error("Logout sırasında hata: " + e.Message + " - Detay: " + e.ToString());
                }
            }
        }

        public static void reset() => mSCManager = null;
    }
}

