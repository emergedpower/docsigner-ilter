using docsigner_ilter.Services; // ReceptServiceApp ve SignatureDeviceDto için
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Serilog;
using System.ComponentModel.DataAnnotations;
using System.Diagnostics;
using System.Globalization;
using System.Threading;
using Microsoft.AspNetCore.Localization;
using System.Collections.Generic;
using System.Windows.Forms;
using System.Drawing;
using Microsoft.AspNetCore.Http;
using static Microsoft.AspNetCore.Http.Results;
using System.Threading.Tasks;
using Microsoft.Win32;
using System.Reflection;
using System;
using System.IO;

namespace docsigner_ilter
{
    // API Request/Response DTO'ları
    public class SignXmlRequest
    {
        [Required]
        public string XmlContent { get; set; } = string.Empty;

        [Required, MinLength(4)]
        public string Pin { get; set; } = string.Empty;

        public int? SlotIndex { get; set; } = 0;

        public bool UseRawXml { get; set; } = false;

        public string LicenseType { get; set; } = "Free";

        public bool ForceFreshLogin { get; set; } = false; // Cache bypass
    }

    public class SignXmlResponse
    {
        public string SignedXml { get; set; } = string.Empty;
        public string SignedFileContent { get; set; } = string.Empty;
        public string FilePath { get; set; } = string.Empty;
        public string Status { get; set; } = "Başarılı";
        public string Error { get; set; } = string.Empty;
        public List<ReceptServiceApp.SignatureDeviceDto>? Devices { get; set; } = null;
    }

    public class SignPdfRequest
    {
        [Required]
        public string PdfBase64 { get; set; } = string.Empty;

        [Required, MinLength(4)]
        public string Pin { get; set; } = string.Empty;

        public int? SlotIndex { get; set; } = 0;
        public bool ForceFreshLogin { get; set; } = false;
        public int? PageNumber { get; set; } = null;
        public float? X { get; set; } = null;
        public float? Y { get; set; } = null;
        public float? Width { get; set; } = null;
        public float? Height { get; set; } = null;
        public string? SignatureFieldName { get; set; } = null;
        public string? SignerDisplayName { get; set; } = null;
        public string? Reason { get; set; } = null;
        public string? Location { get; set; } = null;
        public bool? AddTimestamp { get; set; } = null;
        public bool? AutoSetupTrustChain { get; set; } = null;
        public bool? TryInstallTrustToLocalMachine { get; set; } = null;
        public bool? ConfigureAcrobatWindowsStoreIntegration { get; set; } = null;
        public string? FileName { get; set; } = null;
    }

    public class SignPdfResponse
    {
        public string SignedPdfBase64 { get; set; } = string.Empty;
        public string FilePath { get; set; } = string.Empty;
        public string SignerName { get; set; } = string.Empty;
        public bool TimestampApplied { get; set; } = false;
        public string Status { get; set; } = "Başarılı";
        public string Error { get; set; } = string.Empty;
    }

    static class StartupHelper
    {
        private const string RunKeyPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
        private const string AppRunName = "docsigner-ILTER";
        private const string LegacyRunName = "esigner-ILTER";

        private static string GetExePath()
        {
            var p = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(p)) return p;

            try { return Process.GetCurrentProcess().MainModule?.FileName ?? ""; } catch { }
            var asmLoc = Assembly.GetExecutingAssembly().Location;
            return string.IsNullOrEmpty(asmLoc) ? AppContext.BaseDirectory : asmLoc;
        }

        public static void Register(string extraArgs = "--minimized")
        {
            try
            {
                var exe = GetExePath();
                if (string.IsNullOrWhiteSpace(exe)) return;

                var value = $"\"{exe}\" {extraArgs}".Trim();

                using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true)
                              ?? Registry.CurrentUser.CreateSubKey(RunKeyPath);

                var current = key?.GetValue(AppRunName) as string;
                if (!string.Equals(current, value, StringComparison.Ordinal))
                    key!.SetValue(AppRunName, value, RegistryValueKind.String);

                // Eski uygulama adıyla kalan başlangıç kaydını temizle.
                try { key?.DeleteValue(LegacyRunName, throwOnMissingValue: false); } catch { }
            }
            catch { /* sessiz */ }
        }

        public static bool IsRegistered()
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, false);
                return key?.GetValue(AppRunName) is string s && !string.IsNullOrWhiteSpace(s);
            }
            catch { return false; }
        }
    }

    class Program
    {
        private static Mutex? _single;
        private static bool EnsureSingle(string name = "Global\\docsigner_ilter_mutex")
        {
            _single = new Mutex(true, name, out bool createdNew);
            return createdNew;
        }

        private static bool ShouldSkipSingleInstanceForDebug()
        {
#if DEBUG
            return true;
#else
            return false;
#endif
        }

        internal static bool StartMinimized { get; private set; } = false;

        private static void RegisterGlobalExceptionHandlers()
        {
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);

            Application.ThreadException += (_, e) =>
            {
                LogFatalException("UI ThreadException", e.Exception);
            };

            AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            {
                var ex = e.ExceptionObject as Exception ?? new Exception("Bilinmeyen AppDomain hatasi");
                LogFatalException("AppDomain UnhandledException", ex);
            };

            TaskScheduler.UnobservedTaskException += (_, e) =>
            {
                LogFatalException("TaskScheduler UnobservedTaskException", e.Exception);
                e.SetObserved();
            };
        }

        private static void LogFatalException(string source, Exception ex)
        {
            try
            {
                string logDir = Path.Combine(AppContext.BaseDirectory, "logs");
                Directory.CreateDirectory(logDir);
                string path = Path.Combine(logDir, $"fatal-{DateTime.Now:yyyyMMdd}.txt");

                string text =
                    $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {source}{Environment.NewLine}" +
                    $"{ex}{Environment.NewLine}" +
                    $"--------------------------------------------------{Environment.NewLine}";

                Console.Error.WriteLine(text);
                System.IO.File.AppendAllText(path, text);
                Log.Logger?.Error(ex, "{Source}", source);
            }
            catch
            {
                // Son çare: exception handler içinde tekrar exception çıkarmama
            }
        }

        private static string ResolveAppSettingsPath()
        {
            string baseDirPath = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
            if (System.IO.File.Exists(baseDirPath))
                return baseDirPath;

            string localPath = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "appsettings.json");
            localPath = Path.GetFullPath(localPath);
            if (System.IO.File.Exists(localPath))
                return localPath;

            return "appsettings.json";
        }

        [STAThread]
        static void Main(string[] args)
        {
            if (!ShouldSkipSingleInstanceForDebug() && !EnsureSingle())
            {
                Console.WriteLine("docsigner-ILTER zaten çalışıyor. Mevcut örnek kullanılacak.");
                return;
            }

            RegisterGlobalExceptionHandlers();

            StartMinimized = args != null && Array.Exists(args, a => string.Equals(a, "--minimized", StringComparison.OrdinalIgnoreCase));

            if (!StartupHelper.IsRegistered())
                StartupHelper.Register("--minimized");

            CultureInfo.DefaultThreadCurrentCulture = new CultureInfo("tr-TR");
            CultureInfo.DefaultThreadCurrentUICulture = new CultureInfo("tr-TR");
            Thread.CurrentThread.CurrentCulture = new CultureInfo("tr-TR");
            Thread.CurrentThread.CurrentUICulture = new CultureInfo("tr-TR");

            Log.Logger = new LoggerConfiguration()
                .MinimumLevel.Information()
                .WriteTo.Console()
                .WriteTo.Debug()
                .WriteTo.File("logs/docsigner-.txt", rollingInterval: RollingInterval.Day)
                .CreateLogger();

            var builder = WebApplication.CreateBuilder(args);

            builder.Configuration.AddJsonFile(ResolveAppSettingsPath(), optional: false, reloadOnChange: true);
            builder.Host.UseSerilog();

            // ✅ ReceptServiceApp
            builder.Services.AddScoped<ReceptServiceApp>();

            builder.Services.AddCors(options =>
            {
                options.AddPolicy("AllowWebApp", policy =>
                {
                    policy.WithOrigins("http://localhost:5246", "https://localhost:7081", "https://ilterisg.com", "https://www.ilterisg.com", "https://app.ilterisg.com", "https://www.app.ilterisg.com")
                          .AllowAnyHeader()
                          .AllowAnyMethod()
                          .AllowCredentials();
                });
            });

            builder.Services.Configure<RequestLocalizationOptions>(options =>
            {
                var supportedCultures = new[] { new CultureInfo("tr-TR"), new CultureInfo("en-US") };
                options.DefaultRequestCulture = new RequestCulture("tr-TR");
                options.SupportedCultures = supportedCultures;
                options.SupportedUICultures = supportedCultures;
            });

            var app = builder.Build();

            var config = app.Services.GetRequiredService<IConfiguration>();
            var kestrelUrl = config.GetValue<string>("Kestrel:Endpoints:Http:Url") ?? "http://localhost:5000";
            app.Urls.Add(kestrelUrl);

            var localizationOptions = new RequestLocalizationOptions
            {
                DefaultRequestCulture = new RequestCulture("tr-TR"),
                SupportedCultures = new List<CultureInfo> { new CultureInfo("tr-TR") },
                SupportedUICultures = new List<CultureInfo> { new CultureInfo("tr-TR") }
            };
            app.UseRequestLocalization(localizationOptions);

            app.UseCors("AllowWebApp");

            // Request logging middleware
            app.Use(async (context, next) =>
            {
                var logger = app.Services.GetRequiredService<ILogger<Program>>();
                var sw = Stopwatch.StartNew();
                var ip = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
                logger.LogInformation("İstek geldi: {Method} {Path} from IP {IP}", context.Request.Method, context.Request.Path, ip);
                await next();
                sw.Stop();
                logger.LogInformation("İstek tamamlandı: {Method} {Path} from {IP} in {Elapsed}ms (Status: {Status})",
                    context.Request.Method, context.Request.Path, ip, sw.ElapsedMilliseconds, context.Response.StatusCode);
            });

            app.MapGet("/health", () => Results.Ok(new { Status = "E-İmza Ajan App Çalışıyor!", Timestamp = DateTime.UtcNow }));

            // ✅ Cihaz listesi (Subject dahil)
            app.MapGet("/devices", (ReceptServiceApp service, ILogger<Program> logger) =>
            {
                try
                {
                    var devices = service.GetSignatureDevices();
                    return Results.Ok(devices);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Cihaz listesi hatası");
                    return Results.Problem(ex.Message);
                }
            });

            // ✅ Subject endpoint (SmartCardManagerApp yok!)
            // Not: /devices zaten subject veriyor; ama UI ayrı endpoint istiyorsa burada durabilir.
            app.MapGet("/get-subject", (int? slotIndex, ReceptServiceApp service, ILogger<Program> logger) =>
            {
                try
                {
                    int idx = slotIndex ?? 0;
                    var devices = service.GetSignatureDevices();

                    if (devices == null || devices.Count == 0)
                        return Results.Problem("Takılı e-imza kartı/token bulunamadı.");

                    if (idx < 0 || idx >= devices.Count)
                        return Results.Problem($"Geçersiz slotIndex: {idx}. Mevcut slot sayısı: {devices.Count}");

                    var subjectName = devices[idx].Subject ?? "Bilinmeyen NES";

                    logger.LogInformation("AJAN: Subject çekildi. Slot={Slot}, NES={Subject}", idx, subjectName);
                    return Results.Ok(new { subject = subjectName });
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "AJAN: Subject çekme hatası (Slot={Slot})", slotIndex);
                    return Results.Problem(ex.Message);
                }
            });

            // Ana endpoint: PDF PAdES imzala
            app.MapPost("/sign-pdf", async (HttpContext ctx, ReceptServiceApp service, ILogger<Program> logger, IConfiguration config) =>
            {
                try
                {
                    var request = await ctx.Request.ReadFromJsonAsync<SignPdfRequest>();
                    if (request == null || string.IsNullOrWhiteSpace(request.PdfBase64) || string.IsNullOrWhiteSpace(request.Pin))
                        return Results.BadRequest("PDF veya PIN eksik!");

                    string incomingBase64 = request.PdfBase64.Trim();
                    if (incomingBase64.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                    {
                        int separatorIndex = incomingBase64.IndexOf(',');
                        if (separatorIndex > 0 && separatorIndex + 1 < incomingBase64.Length)
                            incomingBase64 = incomingBase64.Substring(separatorIndex + 1);
                    }

                    byte[] pdfBytes;
                    try
                    {
                        pdfBytes = Convert.FromBase64String(incomingBase64);
                    }
                    catch
                    {
                        return Results.BadRequest("PDF Base64 formatı geçersiz.");
                    }

                    if (pdfBytes.Length < 5 || pdfBytes[0] != 0x25 || pdfBytes[1] != 0x50 || pdfBytes[2] != 0x44 || pdfBytes[3] != 0x46)
                        return Results.BadRequest("Gönderilen içerik geçerli bir PDF görünmüyor.");

                    bool enableTimestamp = request.AddTimestamp ?? config.GetValue("PdfSignature:EnableTimestamp", false);
                    bool autoSetupTrustChain = request.AutoSetupTrustChain ?? config.GetValue("PdfSignature:AutoSetupTrustChain", true);
                    bool tryInstallTrustToLocalMachine = request.TryInstallTrustToLocalMachine ?? config.GetValue("PdfSignature:TryInstallTrustToLocalMachine", true);
                    bool configureAcrobatWindowsStoreIntegration =
                        request.ConfigureAcrobatWindowsStoreIntegration ??
                        config.GetValue("PdfSignature:ConfigureAcrobatWindowsStoreIntegration", true);

                    var options = new ReceptServiceApp.PdfSignatureOptions
                    {
                        PageNumber = request.PageNumber,
                        X = request.X,
                        Y = request.Y,
                        Width = request.Width ?? config.GetValue<float?>("PdfSignature:DefaultWidth"),
                        Height = request.Height ?? config.GetValue<float?>("PdfSignature:DefaultHeight"),
                        SignatureFieldName = request.SignatureFieldName,
                        SignerDisplayName = request.SignerDisplayName,
                        Reason = string.IsNullOrWhiteSpace(request.Reason)
                            ? config.GetValue<string>("PdfSignature:DefaultReason")
                            : request.Reason,
                        Location = string.IsNullOrWhiteSpace(request.Location)
                            ? config.GetValue<string>("PdfSignature:DefaultLocation")
                            : request.Location,
                        FileName = request.FileName,
                        Margin = config.GetValue("PdfSignature:DefaultMargin", 24f),
                        EnableTimestamp = enableTimestamp,
                        TsaUrl = config.GetValue<string>("PdfSignature:TsaUrl"),
                        TsaUsername = config.GetValue<string>("PdfSignature:TsaUsername"),
                        TsaPassword = config.GetValue<string>("PdfSignature:TsaPassword"),
                        AutoSetupTrustChain = autoSetupTrustChain,
                        TryInstallTrustToLocalMachine = tryInstallTrustToLocalMachine,
                        ConfigureAcrobatWindowsStoreIntegration = configureAcrobatWindowsStoreIntegration
                    };

                    var (signedPdf, filePath, signerName, timestampApplied) = await service.SignPdf(
                        pdfBytes,
                        request.SlotIndex,
                        request.Pin,
                        options,
                        request.ForceFreshLogin);

                    logger.LogInformation(
                        "PDF imzalama başarılı: Boyut={Size}, Signer={Signer}, Timestamp={Timestamp}, Path={Path}",
                        signedPdf.Length, signerName, timestampApplied, filePath);

                    return Results.Ok(new SignPdfResponse
                    {
                        SignedPdfBase64 = Convert.ToBase64String(signedPdf),
                        FilePath = filePath,
                        SignerName = signerName,
                        TimestampApplied = timestampApplied,
                        Status = "PAdES imzalama başarılı"
                    });
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "PDF imzalama hatası: {Message}", ex.Message);
                    return Results.Problem(ex.Message);
                }
            });

            // Ana endpoint: XML imzala
            app.MapPost("/sign-xml", async (HttpContext ctx, ReceptServiceApp service, ILogger<Program> logger, IConfiguration config) =>
            {
                try
                {
                    var request = await ctx.Request.ReadFromJsonAsync<SignXmlRequest>();
                    if (request == null || string.IsNullOrWhiteSpace(request.XmlContent))
                        return Results.BadRequest("XML veya PIN eksik!");

                    var licenseType = config.GetValue<string>("AppSettings:LicenseType") ?? request.LicenseType;

                    var (signedXml, filePath, signedFileContent) = await service.SignRecept(
                        request.XmlContent,
                        request.SlotIndex,
                        request.Pin,
                        request.UseRawXml,
                        licenseType,
                        request.ForceFreshLogin
                    );

                    logger.LogInformation("İmzalama başarılı: XML uzunluğu {Length}, ForceFreshLogin={Force}, Dosya Yolu: {FilePath}",
                        signedXml.Length, request.ForceFreshLogin, filePath);

                    return Results.Ok(new SignXmlResponse
                    {
                        SignedXml = signedXml,
                        SignedFileContent = signedFileContent,
                        FilePath = filePath,
                        Status = "İmzalama başarılı"
                    });
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "İmzalama hatası: {Message}", ex.Message);
                    return Results.Problem(ex.Message);
                }
            });

            var loggerHost = app.Services.GetRequiredService<ILogger<Program>>();
            loggerHost.LogInformation("E-İmza Ajan App Başlatıldı! ({Url})", kestrelUrl);
            loggerHost.LogInformation("Endpoints: /health, /devices, /get-subject, /sign-xml (POST), /sign-pdf (POST). Bekleniyor...");
            loggerHost.LogInformation("Lisans gereksiz: uygulama manuel imzalama modunda çalışıyor.");

            _ = Task.Run(async () => await app.RunAsync());

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new SimpleForm());
        }
    }
}

