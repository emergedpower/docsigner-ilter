using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks; // 🔑 Delay için (popup ikon fix)
using System.Windows.Forms;
using docsigner_ilter.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace docsigner_ilter
{
    public partial class SimpleForm : Form
    {
        private Panel card = null!;
        private Panel cardHeader = null!;
        private PictureBox pictureLogo = null!;
        private Label labelHosgeldiniz = null!; // Başlık (header) yazısı
        private Label labelDeviceLine = null!; // Cihaz bilgisi (tek satır)
        private Label labelMesaj = null!; // Merkez mesaj
        private Button btnGizle = null!; // Tek buton
        private Button btnYenile = null!; // Yenile butonu
        private NotifyIcon trayIcon = null!;
        private Panel signerCard = null!;
        private Label labelSignerTitle = null!;
        private Label labelSignerHint = null!;
        private Label labelTrustState = null!;
        private Label labelPdfPath = null!;
        private TextBox txtPdfPath = null!;
        private Button btnSelectPdf = null!;
        private Label labelSignerDevice = null!;
        private ComboBox cmbSignerDevice = null!;
        private Button btnRefreshSignerDevices = null!;
        private Button btnSetupTrust = null!;
        private Label labelPin = null!;
        private TextBox txtPin = null!;
        private CheckBox chkAddTimestamp = null!;
        private Label labelPage = null!;
        private NumericUpDown numPage = null!;
        private Button btnSignPdf = null!;
        private Label labelSignerStatus = null!;
        private Panel viewerCard = null!;
        private Label labelViewerTitle = null!;
        private Panel viewerToolbar = null!;
        private ComboBox cmbFileFilter = null!;
        private Button btnDocsRefresh = null!;
        private Button btnOpenSelected = null!;
        private Button btnOpenFolder = null!;
        private SplitContainer splitViewer = null!;
        private ListView listSignedDocs = null!;
        private Label labelPreviewHeader = null!;
        private Label labelPreviewInfo = null!;
        private RichTextBox txtPreview = null!;
        private WebView2? webPreviewPdf;
        private bool isWebView2Unavailable;
        private bool _webPreviewSuspendedByDeactivate;
        private bool _webPreviewSuspendedByResize;
        private bool _isWindowResizing;
        private string? signedDocsDir;
        private const int MaxPreviewChars = 15000;
        private const int PreferredSplitDistance = 360;
        private const int DesiredSplitMinLeft = 300;
        private const int DesiredSplitMinRight = 280;
        // 🔑 YENİ: Icon field'ları (dispose için, popup/tray için lifetime yönetimi)
        private Icon? _formIcon;
        private Icon? _trayIcon;
        // Device info for dynamic greeting
        private string deviceSubject = string.Empty;
        private readonly List<ReceptServiceApp.SignatureDeviceDto> signerDevices = new();
        private bool _isTrustReadyForSelectedDevice;
        private string? _trustReadyDeviceKey;
        private int _trustCheckVersion;
        private int _previewValidationVersion;
        public SimpleForm()
        {
            InitializeComponent();
            LoadLogoDynamic(); // 🔑 Önce ikon yükle (popup/tray için kritik)
            LoadDevicesAndImzaBilgisi();
            LoadSignedDocuments();
            // 🔑 YENİ: Program --minimized ile geldiyse ilk açılışta tepsiye gizle
            if (Program.StartMinimized)
                BeginInvoke((Action)MinimizeToTray);
        }
        private void InitializeComponent()
        {
            Text = "docsigner-ILTER | PDF PAdES";
            Size = new Size(1180, 780);
            MinimumSize = new Size(980, 700);
            StartPosition = FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.Sizable;
            MaximizeBox = true;
            SizeGripStyle = SizeGripStyle.Hide;
            ResizeRedraw = true;
            DoubleBuffered = true;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            UpdateStyles();
            BackColor = Color.FromArgb(242, 247, 252);
            Paint += (s, e) => DrawBackgroundGradient(e.Graphics);
            Deactivate += (s, e) => SuspendWebPreviewOnDeactivate();
            Activated += async (s, e) => await ResumeWebPreviewOnActivateAsync();
            ResizeBegin += (s, e) => OnResizeBeginForSmoothRendering();
            ResizeEnd += async (s, e) => await OnResizeEndForSmoothRenderingAsync();
            // FormClosing event for tray minimize on X
            FormClosing += (s, e) =>
            {
                if (e.CloseReason == CloseReason.UserClosing)
                {
                    e.Cancel = true;
                    MinimizeToTray();
                }
            };
            // KART
            card = new Panel
            {
                BackColor = Color.White,
                Size = new Size(ClientSize.Width - 40, 220),
                Location = new Point(20, 18),
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
            };
            card.Paint += Card_Paint;
            Controls.Add(card);
            // KART BAŞLIK (HEADER)
            cardHeader = new Panel
            {
                Height = 72,
                Dock = DockStyle.Top,
                BackColor = Color.FromArgb(235, 244, 255)
            };
            cardHeader.Paint += (s, e) =>
            {
                // Alt çizgi
                using var pen = new Pen(Color.FromArgb(215, 225, 240), 1);
                e.Graphics.DrawLine(pen, 0, cardHeader.Height - 1, cardHeader.Width, cardHeader.Height - 1);
            };
            card.Controls.Add(cardHeader);
            // LOGO (Header içinde, sola)
            pictureLogo = new PictureBox
            {
                SizeMode = PictureBoxSizeMode.Zoom,
                Size = new Size(40, 40),
                Location = new Point(16, (cardHeader.Height - 40) / 2),
                BackColor = Color.Transparent
            };
            cardHeader.Controls.Add(pictureLogo);
            // HOŞ GELDİNİZ (Header ortası) - Dinamik olarak ayarlanacak
            labelHosgeldiniz = new Label
            {
                Text = "Hoş geldiniz...", // Geçici, dinamik olarak değiştirilecek
                Font = new Font("Segoe UI", 12, FontStyle.Bold),
                ForeColor = Color.FromArgb(15, 45, 98),
                AutoSize = false,
                TextAlign = ContentAlignment.MiddleCenter,
                Dock = DockStyle.Fill
            };
            cardHeader.Controls.Add(labelHosgeldiniz);
            labelHosgeldiniz.BringToFront();
            // CİHAZ SATIRI (Header altı)
            labelDeviceLine = new Label
            {
                Text = "Cihaz bilgisi yükleniyor...",
                Font = new Font("Segoe UI", 10),
                ForeColor = Color.FromArgb(19, 92, 161),
                AutoSize = false,
                TextAlign = ContentAlignment.MiddleCenter,
                Size = new Size(card.Width - 40, 34),
                Location = new Point(20, cardHeader.Bottom + 16)
            };
            card.Controls.Add(labelDeviceLine);
            // MERKEZ MESAJ
            labelMesaj = new Label
            {
                Text = "PDF belgenizi seçip PAdES uyumlu elektronik imza atabilirsiniz.",
                Font = new Font("Segoe UI", 10),
                ForeColor = Color.FromArgb(44, 61, 80),
                AutoSize = false,
                TextAlign = ContentAlignment.MiddleCenter,
                Size = new Size(card.Width - 60, 44),
                Location = new Point(30, labelDeviceLine.Bottom + 8)
            };
            card.Controls.Add(labelMesaj);
            // YENİLE BUTONU (Gizle butonunun yanına)
            btnYenile = new Button
            {
                Text = "Cihazı Yenile",
                Font = new Font("Segoe UI", 10, FontStyle.Bold),
                BackColor = Color.FromArgb(33, 120, 196),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat,
                Size = new Size(120, 36),
                Location = new Point((card.Width - 120 - 120 - 10) / 2, labelMesaj.Bottom + 14),
                Cursor = Cursors.Hand
            };
            btnYenile.FlatAppearance.BorderSize = 0;
            btnYenile.Click += (s, e) =>
            {
                LoadDevicesAndImzaBilgisi();
                LoadSignedDocuments();
            };
            card.Controls.Add(btnYenile);
            // GİZLE (TEK) BUTON
            btnGizle = new Button
            {
                Text = "Gizle",
                Font = new Font("Segoe UI", 10, FontStyle.Bold),
                BackColor = Color.FromArgb(21, 91, 163),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat,
                Size = new Size(120, 36),
                Location = new Point(btnYenile.Right + 10, labelMesaj.Bottom + 14),
                Cursor = Cursors.Hand
            };
            btnGizle.FlatAppearance.BorderSize = 0;
            btnGizle.Click += (s, e) => MinimizeToTray();
            card.Controls.Add(btnGizle);
            // TRAY (başlangıçta Icon = null, LoadLogoDynamic'te set et)
            trayIcon = new NotifyIcon
            {
                Visible = false,
                Text = "docsigner-ILTER"
            };
            trayIcon.DoubleClick += (s, e) => RestoreFromTray();

            InitializePdfSignerUI();
            InitializeSignatureViewerUI();

            // Responsive hizalamalar
            Resize += (s, e) =>
            {
                card.Width = Math.Max(760, ClientSize.Width - 40);
                card.Location = new Point((ClientSize.Width - card.Width) / 2, 18);
                pictureLogo.Location = new Point(16, (cardHeader.Height - pictureLogo.Height) / 2);
                labelDeviceLine.Width = card.Width - 40;
                labelMesaj.Width = card.Width - 60;
                btnYenile.Left = (card.Width - 120 - 120 - 10) / 2;
                btnGizle.Left = btnYenile.Right + 10;
                labelDeviceLine.Top = cardHeader.Bottom + 16;
                labelMesaj.Top = labelDeviceLine.Bottom + 8;
                btnYenile.Top = labelMesaj.Bottom + 14;
                btnGizle.Top = labelMesaj.Bottom + 14;
                LayoutSignerCard();
                LayoutSignatureViewer();
                ResizeSignedDocsColumns();
                if (_isWindowResizing)
                {
                    card?.Invalidate();
                    signerCard?.Invalidate();
                    viewerCard?.Invalidate();
                }
            };

            LayoutSignerCard();
            LayoutSignatureViewer();
            ResizeSignedDocsColumns();
            EnableDoubleBufferingControlTree(this);
        }

        private void InitializePdfSignerUI()
        {
            signerCard = new Panel
            {
                BackColor = Color.White,
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
                Height = 188
            };
            signerCard.Paint += Card_Paint;
            Controls.Add(signerCard);

            labelSignerTitle = new Label
            {
                Text = "PDF İmzalama",
                Font = new Font("Segoe UI", 11, FontStyle.Bold),
                ForeColor = Color.FromArgb(15, 45, 98),
                AutoSize = false,
                Height = 28,
                Left = 16,
                Top = 12,
                Width = 220
            };
            signerCard.Controls.Add(labelSignerTitle);

            labelSignerHint = new Label
            {
                Text = "Belge seçin, PIN girin ve imzalayın. İmza alanı son sayfada sağ alta yerleştirilir.",
                Font = new Font("Segoe UI", 9),
                ForeColor = Color.FromArgb(80, 95, 112),
                AutoSize = false,
                Left = 16,
                Top = 40,
                Width = 760,
                Height = 20
            };
            signerCard.Controls.Add(labelSignerHint);

            labelPdfPath = new Label
            {
                Text = "PDF Belgesi",
                Font = new Font("Segoe UI", 9, FontStyle.Bold),
                ForeColor = Color.FromArgb(42, 60, 80),
                AutoSize = false,
                Left = 16,
                Top = 68,
                Width = 120,
                Height = 20
            };
            signerCard.Controls.Add(labelPdfPath);

            txtPdfPath = new TextBox
            {
                ReadOnly = true,
                Font = new Font("Segoe UI", 9),
                PlaceholderText = "İmzalanacak PDF belgesini seçin...",
                Left = 16,
                Top = 90,
                Height = 30,
                Width = 690,
                BackColor = Color.White
            };
            signerCard.Controls.Add(txtPdfPath);

            btnSelectPdf = new Button
            {
                Text = "Belge Seç",
                Font = new Font("Segoe UI", 9, FontStyle.Bold),
                BackColor = Color.FromArgb(33, 120, 196),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat,
                Width = 116,
                Height = 30,
                Top = 89
            };
            btnSelectPdf.FlatAppearance.BorderSize = 0;
            btnSelectPdf.Click += (s, e) => SelectPdfDocument();
            signerCard.Controls.Add(btnSelectPdf);

            labelSignerDevice = new Label
            {
                Text = "İmza Cihazı",
                Font = new Font("Segoe UI", 9, FontStyle.Bold),
                ForeColor = Color.FromArgb(42, 60, 80),
                AutoSize = false,
                Left = 16,
                Top = 128,
                Width = 120,
                Height = 20
            };
            signerCard.Controls.Add(labelSignerDevice);

            cmbSignerDevice = new ComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDownList,
                Font = new Font("Segoe UI", 9),
                Left = 16,
                Top = 148,
                Width = 420
            };
            cmbSignerDevice.SelectedIndexChanged += async (s, e) => await UpdateTrustButtonAvailabilityAsync();
            signerCard.Controls.Add(cmbSignerDevice);

            btnRefreshSignerDevices = new Button
            {
                Text = "Cihazları Yenile",
                Font = new Font("Segoe UI", 9, FontStyle.Bold),
                BackColor = Color.FromArgb(17, 105, 189),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat,
                Width = 128,
                Height = 30,
                Top = 147
            };
            btnRefreshSignerDevices.FlatAppearance.BorderSize = 0;
            btnRefreshSignerDevices.Click += (s, e) =>
            {
                LoadDevicesAndImzaBilgisi();
                SetSignerStatus("Cihaz listesi güncellendi.", false, false);
            };
            signerCard.Controls.Add(btnRefreshSignerDevices);

            btnSetupTrust = new Button
            {
                Text = "Güven Zinciri Kur",
                Font = new Font("Segoe UI", 8.5f, FontStyle.Bold),
                BackColor = Color.FromArgb(46, 120, 74),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat,
                Width = 148,
                Height = 28,
                Top = 34
            };
            btnSetupTrust.FlatAppearance.BorderSize = 0;
            btnSetupTrust.Enabled = false;
            btnSetupTrust.Click += async (s, e) => await SetupSignerTrustAsync();
            signerCard.Controls.Add(btnSetupTrust);

            labelTrustState = new Label
            {
                Text = string.Empty,
                Font = new Font("Segoe UI", 8.5f, FontStyle.Bold),
                ForeColor = Color.FromArgb(54, 108, 72),
                BackColor = Color.White,
                AutoSize = false,
                Width = 340,
                Height = 22,
                Top = 12,
                TextAlign = ContentAlignment.MiddleRight
            };
            signerCard.Controls.Add(labelTrustState);

            labelPin = new Label
            {
                Text = "PIN",
                Font = new Font("Segoe UI", 9, FontStyle.Bold),
                ForeColor = Color.FromArgb(42, 60, 80),
                AutoSize = false,
                Width = 50,
                Height = 20,
                Top = 128
            };
            signerCard.Controls.Add(labelPin);

            txtPin = new TextBox
            {
                UseSystemPasswordChar = true,
                Font = new Font("Segoe UI", 9),
                Width = 110,
                Height = 30,
                Top = 148
            };
            signerCard.Controls.Add(txtPin);

            labelPage = new Label
            {
                Text = "Sayfa",
                Font = new Font("Segoe UI", 9, FontStyle.Bold),
                ForeColor = Color.FromArgb(42, 60, 80),
                AutoSize = false,
                Width = 56,
                Height = 20,
                Top = 128
            };
            signerCard.Controls.Add(labelPage);

            numPage = new NumericUpDown
            {
                Minimum = 1,
                Maximum = 9999,
                Value = 1,
                Font = new Font("Segoe UI", 9),
                Width = 74,
                Height = 30,
                Top = 148
            };
            signerCard.Controls.Add(numPage);

            chkAddTimestamp = new CheckBox
            {
                Text = "Timestamp ekle (TSA ayarı varsa)",
                Font = new Font("Segoe UI", 8.5f),
                ForeColor = Color.FromArgb(54, 71, 92),
                AutoSize = true,
                Top = 152,
                Visible = false,
                Checked = false,
                Enabled = false
            };
            signerCard.Controls.Add(chkAddTimestamp);

            btnSignPdf = new Button
            {
                Text = "PDF İmzala",
                Font = new Font("Segoe UI", 9, FontStyle.Bold),
                BackColor = Color.FromArgb(21, 148, 94),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat,
                Width = 120,
                Height = 34,
                Top = 144
            };
            btnSignPdf.FlatAppearance.BorderSize = 0;
            btnSignPdf.Click += async (s, e) => await SignSelectedPdfAsync();
            signerCard.Controls.Add(btnSignPdf);

            labelSignerStatus = new Label
            {
                Text = "Belge seçilmedi.",
                Font = new Font("Segoe UI", 8.5f),
                ForeColor = Color.FromArgb(128, 85, 0),
                AutoSize = false,
                Height = 20,
                Left = 820,
                Top = 68,
                Width = 300,
                TextAlign = ContentAlignment.MiddleRight
            };
            signerCard.Controls.Add(labelSignerStatus);
        }

        private void LayoutSignerCard()
        {
            if (signerCard == null)
                return;

            signerCard.Left = card.Left;
            signerCard.Top = card.Bottom + 14;
            signerCard.Width = card.Width;

            if (btnSelectPdf != null && txtPdfPath != null)
                btnSelectPdf.Left = signerCard.Width - btnSelectPdf.Width - 16;

            if (txtPdfPath != null && btnSelectPdf != null)
                txtPdfPath.Width = Math.Max(320, btnSelectPdf.Left - txtPdfPath.Left - 10);

            if (btnRefreshSignerDevices != null && cmbSignerDevice != null)
                btnRefreshSignerDevices.Left = cmbSignerDevice.Right + 8;

            if (btnSetupTrust != null)
            {
                btnSetupTrust.Left = signerCard.Width - btnSetupTrust.Width - 16;

                int hintRightBoundary = btnSetupTrust.Left - 10;
                if (labelTrustState != null)
                {
                    // Durum metni butonun tam üstünde dursun, sağ kenarlar birebir hizalansın.
                    labelTrustState.Top = Math.Max(2, btnSetupTrust.Top - labelTrustState.Height - 2);
                    labelTrustState.Left = btnSetupTrust.Right - labelTrustState.Width;
                    labelTrustState.BringToFront();
                    hintRightBoundary = Math.Min(hintRightBoundary, labelTrustState.Left - 10);
                }

                labelSignerHint.Width = Math.Max(360, hintRightBoundary - labelSignerHint.Left);
            }
            else
            {
                labelSignerHint.Width = signerCard.Width - labelSignerHint.Left - 20;
            }

            int pinLeft = btnRefreshSignerDevices.Right + 12;
            labelPin.Left = pinLeft;
            txtPin.Left = pinLeft;

            int pageLeft = txtPin.Right + 10;
            labelPage.Left = pageLeft;
            numPage.Left = pageLeft;

            btnSignPdf.Left = signerCard.Width - btnSignPdf.Width - 16;

            labelSignerStatus.Top = labelPdfPath.Top;
            labelSignerStatus.Left = Math.Max(16, btnSelectPdf.Left - 360);
            labelSignerStatus.Width = Math.Max(140, signerCard.Width - labelSignerStatus.Left - 16);
        }
        private void InitializeSignatureViewerUI()
        {
            viewerCard = new Panel
            {
                BackColor = Color.White,
                Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right
            };
            viewerCard.Paint += Card_Paint;
            Controls.Add(viewerCard);

            labelViewerTitle = new Label
            {
                Text = "İmzalı Dosyalar",
                Font = new Font("Segoe UI", 11, FontStyle.Bold),
                ForeColor = Color.FromArgb(15, 45, 98),
                Dock = DockStyle.Top,
                Height = 36,
                Padding = new Padding(16, 10, 0, 0),
                BackColor = Color.White
            };

            viewerToolbar = new Panel
            {
                Dock = DockStyle.Top,
                Height = 48,
                BackColor = Color.White
            };

            cmbFileFilter = new ComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDownList,
                Font = new Font("Segoe UI", 9),
                Location = new Point(16, 10),
                Width = 130
            };
            cmbFileFilter.Items.AddRange(new object[] { "Tüm Dosyalar", "XML", "XSIG", "PDF" });
            cmbFileFilter.SelectedIndex = 0;
            cmbFileFilter.SelectedIndexChanged += (s, e) => LoadSignedDocuments();
            viewerToolbar.Controls.Add(cmbFileFilter);

            btnDocsRefresh = new Button
            {
                Text = "Dosyaları Yenile",
                Font = new Font("Segoe UI", 9, FontStyle.Bold),
                BackColor = Color.FromArgb(17, 105, 189),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat,
                Size = new Size(130, 30),
                Location = new Point(cmbFileFilter.Right + 10, 9)
            };
            btnDocsRefresh.FlatAppearance.BorderSize = 0;
            btnDocsRefresh.Click += (s, e) => LoadSignedDocuments();
            viewerToolbar.Controls.Add(btnDocsRefresh);

            btnOpenSelected = new Button
            {
                Text = "Seçileni Aç",
                Font = new Font("Segoe UI", 9, FontStyle.Bold),
                BackColor = Color.FromArgb(0, 122, 204),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat,
                Size = new Size(104, 30),
                Anchor = AnchorStyles.Top | AnchorStyles.Right
            };
            btnOpenSelected.FlatAppearance.BorderSize = 0;
            btnOpenSelected.Click += (s, e) => OpenSelectedSignedFile();
            viewerToolbar.Controls.Add(btnOpenSelected);

            btnOpenFolder = new Button
            {
                Text = "Klasörü Aç",
                Font = new Font("Segoe UI", 9, FontStyle.Bold),
                BackColor = Color.FromArgb(0, 122, 204),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat,
                Size = new Size(104, 30),
                Anchor = AnchorStyles.Top | AnchorStyles.Right
            };
            btnOpenFolder.FlatAppearance.BorderSize = 0;
            btnOpenFolder.Click += (s, e) => OpenSignedFolder();
            viewerToolbar.Controls.Add(btnOpenFolder);

            splitViewer = new SplitContainer
            {
                Dock = DockStyle.Fill,
                BackColor = Color.White
            };
            splitViewer.SplitterMoved += (s, e) =>
            {
                EnsureSplitViewerDistance();
                ResizeSignedDocsColumns();
            };
            splitViewer.SizeChanged += (s, e) => EnsureSplitViewerDistance();

            listSignedDocs = new ListView
            {
                Dock = DockStyle.Fill,
                View = View.Details,
                FullRowSelect = true,
                GridLines = true,
                HideSelection = false,
                MultiSelect = false,
                Font = new Font("Segoe UI", 9)
            };
            listSignedDocs.Columns.Add("Dosya");
            listSignedDocs.Columns.Add("Tur");
            listSignedDocs.Columns.Add("Boyut");
            listSignedDocs.Columns.Add("Tarih");
            listSignedDocs.Resize += (s, e) => ResizeSignedDocsColumns();
            listSignedDocs.SelectedIndexChanged += (s, e) => ShowSelectedFilePreview();
            listSignedDocs.DoubleClick += (s, e) => OpenSelectedSignedFile();
            splitViewer.Panel1.Controls.Add(listSignedDocs);

            txtPreview = new RichTextBox
            {
                Dock = DockStyle.Fill,
                ReadOnly = true,
                Font = new Font("Consolas", 9),
                BackColor = Color.White,
                BorderStyle = BorderStyle.FixedSingle,
                WordWrap = false
            };
            splitViewer.Panel2.Controls.Add(txtPreview);

            webPreviewPdf = new WebView2
            {
                Dock = DockStyle.Fill,
                Visible = false,
                DefaultBackgroundColor = Color.White
            };
            splitViewer.Panel2.Controls.Add(webPreviewPdf);

            labelPreviewInfo = new Label
            {
                Dock = DockStyle.Top,
                Height = 72,
                Font = new Font("Segoe UI", 8.5f),
                ForeColor = Color.FromArgb(80, 80, 80),
                Text = "Seçili dosya bilgisi burada görünür.",
                Padding = new Padding(8, 4, 8, 2),
                BackColor = Color.White
            };
            splitViewer.Panel2.Controls.Add(labelPreviewInfo);

            labelPreviewHeader = new Label
            {
                Dock = DockStyle.Top,
                Height = 30,
                Font = new Font("Segoe UI", 10, FontStyle.Bold),
                ForeColor = Color.FromArgb(20, 60, 130),
                Text = "Önizleme",
                Padding = new Padding(8, 6, 8, 0),
                BackColor = Color.White
            };
            splitViewer.Panel2.Controls.Add(labelPreviewHeader);

            // Dock sırası sabit: başlık + toolbar her zaman görünür, liste üstte kaybolmaz.
            viewerCard.Controls.Add(splitViewer);
            viewerCard.Controls.Add(viewerToolbar);
            viewerCard.Controls.Add(labelViewerTitle);
        }

        private void LayoutSignatureViewer()
        {
            if (viewerCard == null)
                return;

            viewerCard.Left = card.Left;
            int topAnchor = signerCard != null ? signerCard.Bottom + 14 : card.Bottom + 16;
            viewerCard.Top = topAnchor;
            viewerCard.Width = card.Width;
            viewerCard.Height = Math.Max(250, ClientSize.Height - viewerCard.Top - 16);

            if (viewerToolbar != null && btnOpenFolder != null && btnOpenSelected != null)
            {
                btnOpenFolder.Left = viewerToolbar.Width - btnOpenFolder.Width - 16;
                btnOpenFolder.Top = 9;
                btnOpenSelected.Left = btnOpenFolder.Left - btnOpenSelected.Width - 8;
                btnOpenSelected.Top = 9;
            }

            EnsureSplitViewerDistance();
        }

        private void ResizeSignedDocsColumns()
        {
            if (listSignedDocs == null || listSignedDocs.Columns.Count < 4)
                return;

            int width = Math.Max(220, listSignedDocs.ClientSize.Width - 8);
            listSignedDocs.Columns[0].Width = (int)(width * 0.45);
            listSignedDocs.Columns[1].Width = (int)(width * 0.13);
            listSignedDocs.Columns[2].Width = (int)(width * 0.18);
            listSignedDocs.Columns[3].Width = width - listSignedDocs.Columns[0].Width - listSignedDocs.Columns[1].Width - listSignedDocs.Columns[2].Width - 4;
        }

        private void EnsureSplitViewerDistance()
        {
            if (splitViewer == null || splitViewer.IsDisposed)
                return;

            int totalWidth = splitViewer.ClientSize.Width;
            if (totalWidth <= 1)
                return;

            int minLeft = DesiredSplitMinLeft;
            int minRight = DesiredSplitMinRight;

            // Küçük genişliklerde min değerleri daralt.
            if (minLeft + minRight >= totalWidth)
            {
                int available = Math.Max(2, totalWidth - 1);
                minLeft = Math.Max(80, available / 2);
                minRight = Math.Max(80, available - minLeft);

                if (minLeft + minRight >= totalWidth)
                {
                    minLeft = Math.Max(1, (totalWidth / 2) - 1);
                    minRight = Math.Max(1, totalWidth - minLeft - 1);
                }
            }

            int maxLeft = totalWidth - minRight;
            int target = splitViewer.SplitterDistance;

            if (target < minLeft || target > maxLeft)
                target = Math.Clamp(PreferredSplitDistance, minLeft, maxLeft);

            try
            {
                if (splitViewer.SplitterDistance != target)
                    splitViewer.SplitterDistance = target;

                if (splitViewer.Panel1MinSize != minLeft)
                    splitViewer.Panel1MinSize = minLeft;

                if (splitViewer.Panel2MinSize != minRight)
                    splitViewer.Panel2MinSize = minRight;
            }
            catch (InvalidOperationException)
            {
                // Layout henüz hazır değilse bir sonraki SizeChanged/Resize'da tekrar denenecek.
            }
        }

        private void EnableDoubleBufferingControlTree(Control root)
        {
            TryEnableDoubleBuffering(root);
            foreach (Control child in root.Controls)
                EnableDoubleBufferingControlTree(child);
        }

        private static void TryEnableDoubleBuffering(Control control)
        {
            try
            {
                typeof(Control)
                    .GetProperty("DoubleBuffered", BindingFlags.Instance | BindingFlags.NonPublic)?
                    .SetValue(control, true, null);
            }
            catch
            {
                // Bazı özel kontrollerde reflection ile set edilemeyebilir.
            }
        }

        private void SuspendWebPreviewOnDeactivate()
        {
            if (webPreviewPdf == null || !webPreviewPdf.Visible)
                return;

            _webPreviewSuspendedByDeactivate = true;
            webPreviewPdf.Visible = false;
            txtPreview.Visible = true;
        }

        private void OnResizeBeginForSmoothRendering()
        {
            _isWindowResizing = true;
            SuspendWebPreviewOnResizeBegin();
            Invalidate(true);
        }

        private void SuspendWebPreviewOnResizeBegin()
        {
            if (webPreviewPdf == null || !webPreviewPdf.Visible)
                return;

            _webPreviewSuspendedByResize = true;
            webPreviewPdf.Visible = false;
            txtPreview.Visible = true;
        }

        private async Task ResumeWebPreviewOnActivateAsync()
        {
            if (!_webPreviewSuspendedByDeactivate)
                return;

            _webPreviewSuspendedByDeactivate = false;
            string? selectedPath = GetSelectedSignedFilePath();
            if (string.IsNullOrWhiteSpace(selectedPath))
                return;

            if (!string.Equals(Path.GetExtension(selectedPath), ".pdf", StringComparison.OrdinalIgnoreCase))
                return;

            await TryShowPdfPreviewAsync(selectedPath);
        }

        private async Task OnResizeEndForSmoothRenderingAsync()
        {
            _isWindowResizing = false;
            await ResumeWebPreviewOnResizeEndAsync();
            Invalidate(true);
        }

        private async Task ResumeWebPreviewOnResizeEndAsync()
        {
            if (!_webPreviewSuspendedByResize)
                return;

            _webPreviewSuspendedByResize = false;
            string? selectedPath = GetSelectedSignedFilePath();
            if (string.IsNullOrWhiteSpace(selectedPath))
                return;

            if (!string.Equals(Path.GetExtension(selectedPath), ".pdf", StringComparison.OrdinalIgnoreCase))
                return;

            await TryShowPdfPreviewAsync(selectedPath);
        }

        // 🔑 YENİ: Icon dispose (ObjectDisposedException fix, popup/tray ikonlarını koru)
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _formIcon?.Dispose();
                _trayIcon?.Dispose();
                pictureLogo?.Image?.Dispose(); // PictureBox image'ını da temizle
                webPreviewPdf?.Dispose();
                trayIcon?.Dispose();
            }
            base.Dispose(disposing);
        }
        // Arka plan gradyanı
        private void DrawBackgroundGradient(Graphics g)
        {
            if (ClientRectangle.Width <= 1 || ClientRectangle.Height <= 1)
                return;

            g.SmoothingMode = SmoothingMode.AntiAlias;
            using var brush = new LinearGradientBrush(ClientRectangle,
                Color.FromArgb(244, 248, 252),
                Color.FromArgb(226, 238, 251),
                105f);
            g.FillRectangle(brush, ClientRectangle);
        }
        // Kartın rounded + shadow çizimi
        private void Card_Paint(object? sender, PaintEventArgs e)
        {
            if (sender is not Panel panel)
                return;

            var g = e.Graphics;
            g.Clear(panel.BackColor);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var r = panel.ClientRectangle;
            if (r.Width <= 4 || r.Height <= 4)
                return;

            var drawRect = new Rectangle(r.X, r.Y, r.Width - 1, r.Height - 1);
            int radius = 16;
            using var path = RoundedRect(drawRect, radius);
            using var bg = new SolidBrush(panel.BackColor);
            using var pen = new Pen(Color.FromArgb(215, 225, 240), 1);

            if (!_isWindowResizing && drawRect.Width > 14 && drawRect.Height > 14)
            {
                using var shadow = new SolidBrush(Color.FromArgb(26, 0, 0, 0));
                var shadowRect = new Rectangle(drawRect.X + 2, drawRect.Y + 4, drawRect.Width - 6, drawRect.Height - 6);
                if (shadowRect.Width > 4 && shadowRect.Height > 4)
                {
                    using var shadowPath = RoundedRect(shadowRect, radius + 1);
                    g.FillPath(shadow, shadowPath);
                }
            }

            g.FillPath(bg, path);
            g.DrawPath(pen, path);
        }
        private GraphicsPath RoundedRect(Rectangle bounds, int radius)
        {
            var path = new GraphicsPath();
            if (bounds.Width <= 1 || bounds.Height <= 1)
            {
                path.AddRectangle(bounds);
                return path;
            }

            radius = Math.Max(1, Math.Min(radius, Math.Min(bounds.Width, bounds.Height) / 2));
            int d = radius * 2;

            path.AddArc(bounds.Left, bounds.Top, d, d, 180, 90);
            path.AddArc(bounds.Right - d, bounds.Top, d, d, 270, 90);
            path.AddArc(bounds.Right - d, bounds.Bottom - d, d, d, 0, 90);
            path.AddArc(bounds.Left, bounds.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }
        // TRAY davranışı (popup için ikon kontrolü eklendi + fix)
        private async void MinimizeToTray()
        {
            // 1) Ikonun hazır olduğundan emin ol
            if (_trayIcon == null)
                _trayIcon = this.Icon ?? SystemIcons.Information;

            // 2) Refresh hilesi (Win11 bildirim ikonu için kritik)
            trayIcon.Visible = false;
            trayIcon.Icon = null;
            Application.DoEvents();           // Explorer'a nefes aldır
            trayIcon.Icon = _trayIcon;
            trayIcon.Visible = true;

            // 3) Balon ayarları (Visible TRUE olduktan sonra!)
            trayIcon.BalloonTipIcon = ToolTipIcon.Info;
            trayIcon.BalloonTipTitle = "docsigner-ILTER";
            trayIcon.BalloonTipText = "Pencere gizlendi. Geri getirmek için iki kez tıklayın.";

            await Task.Delay(100);            // Kısa gecikme, render için
            trayIcon.ShowBalloonTip(3000);

            Hide();
        }

        private void RestoreFromTray()
        {
            Show();
            WindowState = FormWindowState.Normal;
            Activate();
            trayIcon.Visible = false;
        }

        private void SelectPdfDocument()
        {
            try
            {
                if (InvokeRequired)
                {
                    BeginInvoke((Action)SelectPdfDocument);
                    return;
                }

                string? selectedPath = null;
                if (Thread.CurrentThread.GetApartmentState() == ApartmentState.STA)
                {
                    using var dialog = CreatePdfOpenFileDialog();
                    Cursor.Current = Cursors.Default;
                    if (dialog.ShowDialog() == DialogResult.OK)
                        selectedPath = dialog.FileName;
                }
                else
                {
                    selectedPath = SelectPdfInStaThread();
                }

                if (string.IsNullOrWhiteSpace(selectedPath))
                {
                    SetSignerStatus("Belge seçimi iptal edildi.", false, false);
                    return;
                }

                txtPdfPath.Text = selectedPath;
                SetSignerStatus($"Belge seçildi: {Path.GetFileName(selectedPath)}", false, false);
            }
            catch (Exception ex)
            {
                SetSignerStatus($"Belge seçme hatası: {ex.Message}", true, false);
                MessageBox.Show($"Belge seçme sırasında hata oluştu:{Environment.NewLine}{ex.Message}", "Hata", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private static OpenFileDialog CreatePdfOpenFileDialog()
        {
            return new OpenFileDialog
            {
                Filter = "PDF Dosyaları (*.pdf)|*.pdf",
                CheckFileExists = true,
                CheckPathExists = true,
                Multiselect = false,
                RestoreDirectory = true,
                AutoUpgradeEnabled = false,
                DereferenceLinks = true,
                InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                Title = "İmzalanacak PDF Belgesi Seçin"
            };
        }

        private static string? SelectPdfInStaThread()
        {
            string? selectedPath = null;
            Exception? capturedException = null;
            using var completed = new ManualResetEventSlim(false);

            var staThread = new Thread(() =>
            {
                try
                {
                    using var dialog = CreatePdfOpenFileDialog();
                    if (dialog.ShowDialog() == DialogResult.OK)
                        selectedPath = dialog.FileName;
                }
                catch (Exception ex)
                {
                    capturedException = ex;
                }
                finally
                {
                    completed.Set();
                }
            });

            staThread.IsBackground = true;
            staThread.SetApartmentState(ApartmentState.STA);
            staThread.Start();
            completed.Wait();

            if (capturedException != null)
                throw capturedException;

            return selectedPath;
        }

        private async Task SignSelectedPdfAsync()
        {
            string filePath = (txtPdfPath.Text ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
            {
                MessageBox.Show("Lütfen önce imzalanacak PDF belgesini seçin.", "Eksik Bilgi", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            if (cmbSignerDevice.SelectedIndex < 0 || cmbSignerDevice.SelectedIndex >= signerDevices.Count)
            {
                MessageBox.Show("Lütfen bir e-imza cihazı seçin.", "Eksik Bilgi", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            string pin = (txtPin.Text ?? string.Empty).Trim();
            if (pin.Length < 4)
            {
                MessageBox.Show("PIN en az 4 karakter olmalıdır.", "Eksik Bilgi", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                txtPin.Focus();
                return;
            }

            var selectedDevice = signerDevices[cmbSignerDevice.SelectedIndex];
            var service = new ReceptServiceApp(new DummyLogger<ReceptServiceApp>());
            int? pageNumber = (int)numPage.Value;

            var options = new ReceptServiceApp.PdfSignatureOptions
            {
                PageNumber = pageNumber,
                FileName = Path.GetFileName(filePath),
                EnableTimestamp = false
            };

            try
            {
                SetSignerBusy(true);
                SetSignerStatus("PDF imzalanıyor, lütfen bekleyin...", false, false);

                byte[] pdfBytes = await File.ReadAllBytesAsync(filePath);
                var result = await service.SignPdf(
                    pdfBytes,
                    selectedDevice.Id,
                    pin,
                    options,
                    forceFreshLogin: false);

                txtPin.Clear();
                LoadSignedDocuments(result.filePath);

                SetSignerStatus($"İmza tamamlandı: {Path.GetFileName(result.filePath)}", false, true);

                if (MessageBox.Show(
                        $"PDF başarıyla imzalandı.{Environment.NewLine}{Path.GetFileName(result.filePath)}{Environment.NewLine}{Environment.NewLine}Dosyayı açmak ister misiniz?",
                        "İmzalama Başarılı",
                        MessageBoxButtons.YesNo,
                        MessageBoxIcon.Information) == DialogResult.Yes)
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = result.filePath,
                        UseShellExecute = true
                    });
                }
            }
            catch (Exception ex)
            {
                SetSignerStatus($"İmzalama başarısız: {ex.Message}", true, false);
                MessageBox.Show($"PDF imzalama hatası:{Environment.NewLine}{ex.Message}", "Hata", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                SetSignerBusy(false);
            }
        }

        private async Task SetupSignerTrustAsync()
        {
            if (cmbSignerDevice.SelectedIndex < 0 || cmbSignerDevice.SelectedIndex >= signerDevices.Count)
            {
                MessageBox.Show("Lütfen önce bir e-imza cihazı seçin.", "Eksik Bilgi", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            var selectedDevice = signerDevices[cmbSignerDevice.SelectedIndex];
            string selectedDeviceKey = $"{selectedDevice.Id}:{selectedDevice.Serial}:{selectedDevice.Subject}";
            if (_isTrustReadyForSelectedDevice && string.Equals(_trustReadyDeviceKey, selectedDeviceKey, StringComparison.Ordinal))
            {
                MessageBox.Show(
                    "Seçili cihaz için güven zinciri zaten hazır. Yeniden kurmaya gerek yok.",
                    "Bilgi",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
                return;
            }

            var service = new ReceptServiceApp(new DummyLogger<ReceptServiceApp>());

            try
            {
                SetSignerBusy(true);
                SetSignerStatus("Sertifika zinciri kuruluyor, lütfen bekleyin...", false, false);

                var trustResult = await Task.Run(() => service.EnsureSignerTrust(selectedDevice.Id, applyChanges: true));

                if (!trustResult.Success)
                {
                    SetSignerStatus($"Güven zinciri kurulamadı: {trustResult.Message}", true, false);
                    MessageBox.Show(
                        $"Güven zinciri kurulamadı:{Environment.NewLine}{trustResult.Message}",
                        "Hata",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Error);
                    return;
                }

                string warnings = trustResult.Warnings.Count > 0
                    ? $"{Environment.NewLine}Uyarılar: {string.Join(" | ", trustResult.Warnings.Take(2))}"
                    : string.Empty;

                ApplyTrustReadinessState(selectedDevice, trustResult);

                SetSignerStatus(
                    $"Güven zinciri: {trustResult.ReadinessLevel} | CU: {(trustResult.CurrentUserChainReady ? "OK" : "Eksik")} | " +
                    $"LM: {(trustResult.LocalMachineChainReady ? "OK" : "Eksik")} | Acrobat: {(trustResult.AcrobatWindowsStoreReady ? "OK" : "Eksik")} | " +
                    $"Doğrulama: {trustResult.ValidationStatus}",
                    string.Equals(trustResult.ReadinessLevel, "Hazır Değil", StringComparison.OrdinalIgnoreCase),
                    string.Equals(trustResult.ReadinessLevel, "Hazır", StringComparison.OrdinalIgnoreCase));

                MessageBox.Show(
                    $"{trustResult.Message}{warnings}{Environment.NewLine}{Environment.NewLine}Yeni bir PDF imzalayıp tekrar doğrulayın.",
                    "Güven Zinciri",
                    MessageBoxButtons.OK,
                    string.Equals(trustResult.ReadinessLevel, "Hazır", StringComparison.OrdinalIgnoreCase)
                        ? MessageBoxIcon.Information
                        : MessageBoxIcon.Warning);
            }
            catch (Exception ex)
            {
                SetSignerStatus($"Güven zinciri hatası: {ex.Message}", true, false);
                MessageBox.Show($"Güven zinciri işlemi sırasında hata oluştu:{Environment.NewLine}{ex.Message}", "Hata", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                SetSignerBusy(false);
            }
        }

        private async Task UpdateTrustButtonAvailabilityAsync()
        {
            if (cmbSignerDevice == null || cmbSignerDevice.SelectedIndex < 0 || cmbSignerDevice.SelectedIndex >= signerDevices.Count)
            {
                _isTrustReadyForSelectedDevice = false;
                _trustReadyDeviceKey = null;
                if (labelTrustState != null)
                {
                    labelTrustState.Text = "Durum: Cihaz seçildiğinde kontrol yapılır";
                    labelTrustState.ForeColor = Color.FromArgb(128, 85, 0);
                }
                if (btnSetupTrust != null)
                {
                    btnSetupTrust.Enabled = false;
                    btnSetupTrust.Cursor = Cursors.No;
                }
                return;
            }

            var selectedDevice = signerDevices[cmbSignerDevice.SelectedIndex];
            int version = Interlocked.Increment(ref _trustCheckVersion);

            if (labelTrustState != null)
            {
                labelTrustState.Text = "Durum: Güven zinciri kontrol ediliyor...";
                labelTrustState.ForeColor = Color.FromArgb(128, 85, 0);
            }

            try
            {
                var service = new ReceptServiceApp(new DummyLogger<ReceptServiceApp>());
                var trustResult = await Task.Run(() => service.EnsureSignerTrust(selectedDevice.Id, applyChanges: false));

                if (version != _trustCheckVersion)
                    return;

                if (!trustResult.Success)
                {
                    _isTrustReadyForSelectedDevice = false;
                    _trustReadyDeviceKey = null;
                    if (labelTrustState != null)
                    {
                        labelTrustState.Text = "Durum: Zincir kontrolü yapılamadı";
                        labelTrustState.ForeColor = Color.FromArgb(180, 36, 36);
                    }
                    if (btnSetupTrust != null)
                    {
                        btnSetupTrust.Enabled = true;
                        btnSetupTrust.Cursor = Cursors.Hand;
                    }
                    return;
                }

                ApplyTrustReadinessState(selectedDevice, trustResult);
            }
            catch
            {
                if (version != _trustCheckVersion)
                    return;

                _isTrustReadyForSelectedDevice = false;
                _trustReadyDeviceKey = null;
                if (labelTrustState != null)
                {
                    labelTrustState.Text = "Durum: Zincir kontrolü yapılamadı";
                    labelTrustState.ForeColor = Color.FromArgb(180, 36, 36);
                }
                if (btnSetupTrust != null)
                {
                    btnSetupTrust.Enabled = true;
                    btnSetupTrust.Cursor = Cursors.Hand;
                }
            }
        }

        private void ApplyTrustReadinessState(
            ReceptServiceApp.SignatureDeviceDto selectedDevice,
            ReceptServiceApp.SignerTrustSetupResult trustResult)
        {
            string selectedDeviceKey = $"{selectedDevice.Id}:{selectedDevice.Serial}:{selectedDevice.Subject}";
            bool ready = string.Equals(trustResult.ReadinessLevel, "Hazır", StringComparison.OrdinalIgnoreCase);

            _isTrustReadyForSelectedDevice = ready;
            _trustReadyDeviceKey = ready ? selectedDeviceKey : null;

            if (labelTrustState != null)
            {
                if (ready)
                {
                    labelTrustState.Text = "Durum: Güven zinciri kuruldu.";
                    labelTrustState.ForeColor = Color.FromArgb(10, 128, 80);
                }
                else if (string.Equals(trustResult.ReadinessLevel, "Kısmi", StringComparison.OrdinalIgnoreCase))
                {
                    labelTrustState.Text = "Durum: Zincir kısmi hazır (kur butonuna basın)";
                    labelTrustState.ForeColor = Color.FromArgb(128, 85, 0);
                }
                else
                {
                    labelTrustState.Text = "Durum: Güven zinciri kurulmalı";
                    labelTrustState.ForeColor = Color.FromArgb(180, 36, 36);
                }
            }

            if (btnSetupTrust != null)
            {
                btnSetupTrust.Enabled = !ready;
                btnSetupTrust.Cursor = ready ? Cursors.No : Cursors.Hand;
            }
        }

        private void SetSignerBusy(bool busy)
        {
            btnSignPdf.Enabled = !busy;
            btnSelectPdf.Enabled = !busy;
            btnRefreshSignerDevices.Enabled = !busy;
            btnSetupTrust.Enabled = !busy && !_isTrustReadyForSelectedDevice;
            cmbSignerDevice.Enabled = !busy;
            txtPin.Enabled = !busy;
            numPage.Enabled = !busy;
            UseWaitCursor = busy;
        }

        private void SetSignerStatus(string message, bool isError, bool isSuccess)
        {
            labelSignerStatus.Text = message;
            labelSignerStatus.ForeColor = isError
                ? Color.FromArgb(180, 36, 36)
                : isSuccess
                    ? Color.FromArgb(10, 128, 80)
                    : Color.FromArgb(128, 85, 0);
        }

        private void BindSignerDevices(List<ReceptServiceApp.SignatureDeviceDto> devices)
        {
            if (cmbSignerDevice == null)
                return;

            int prevIndex = cmbSignerDevice.SelectedIndex;

            signerDevices.Clear();
            signerDevices.AddRange(devices);

            cmbSignerDevice.BeginUpdate();
            try
            {
                cmbSignerDevice.Items.Clear();

                foreach (var device in signerDevices)
                {
                    string itemText = $"{device.Subject} | Slot {device.Id} | {device.Serial}";
                    cmbSignerDevice.Items.Add(itemText);
                }

                if (cmbSignerDevice.Items.Count > 0)
                {
                    cmbSignerDevice.SelectedIndex = prevIndex >= 0 && prevIndex < cmbSignerDevice.Items.Count
                        ? prevIndex
                        : 0;
                }
            }
            finally
            {
                cmbSignerDevice.EndUpdate();
            }

            _ = UpdateTrustButtonAvailabilityAsync();
        }

        // LOGO klasörü yolu
        private string LogosDir()
        {
            // örnek: C:\...\bin\Debug\net8.0-windows\logos
            return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "logos");
        }
        // Dinamik logo & ikon yükleme (GÜNCELLENMİŞ: Multi-size extract + popup/tray fix)
        private void LoadLogoDynamic()
        {
            Console.WriteLine("🔍 LoadLogoDynamic başladı.");
            try
            {
                // Önce mevcut icon'ları temizle (ama SystemIcons'ı değil)
                _formIcon?.Dispose();
                _trayIcon?.Dispose();
                _formIcon = null;
                _trayIcon = null;

                var dir = LogosDir();
                Console.WriteLine($"🔍 Logos dizini: {dir} (Var mı? {Directory.Exists(dir)})");

                if (!Directory.Exists(dir))
                {
                    Console.WriteLine("❌ Logos dizini yok! Fallback ikonlar.");
                    SetFallbackIcons();
                    return;
                }

                var icoPath = Path.Combine(dir, "favicon.ico");
                Console.WriteLine($"🔍 ICO yolu: {icoPath} (Var mı? {File.Exists(icoPath)})");

                if (File.Exists(icoPath))
                {
                    Console.WriteLine("✅ ICO bulundu. Yükleniyor...");
                    // 🔑 YENİ: Multi-size ICO'dan spesifik boyutlar extract et (popup için 16x16 kritik)
                    Icon fullIconTemp = null;
                    try
                    {
                        fullIconTemp = new Icon(icoPath);
                        Console.WriteLine($"✅ Full ICO yüklendi. Boyutlar: {fullIconTemp.Width}x{fullIconTemp.Height}");

                        // Form/title bar için 32x32 (büyük) - field'a ata
                        _formIcon = new Icon(fullIconTemp, new Size(32, 32));
                        Console.WriteLine($"✅ Form ikonu: {_formIcon.Width}x{_formIcon.Height}");

                        // Tray & balloon tip (popup) için 16x16 (küçük, popup için kritik!)
                        _trayIcon = new Icon(fullIconTemp, new Size(16, 16));
                        Console.WriteLine($"✅ Tray/popup ikonu: {_trayIcon.Width}x{_trayIcon.Height}");

                        // Eğer ICO'da 16x16 yoksa, en yakını scale et (fallback)
                        if (_trayIcon.Width != 16)
                        {
                            _trayIcon?.Dispose();
                            _trayIcon = new Icon(fullIconTemp, new Size(16, 16)); // Zorla scale
                            Console.WriteLine($"⚠️ 16x16 yoktu, scale edildi: {_trayIcon.Width}x{_trayIcon.Height}");
                        }
                    }
                    finally
                    {
                        fullIconTemp?.Dispose(); // Temp'i hemen temizle
                    }

                    this.Icon = _formIcon ?? SystemIcons.Application; // Fallback (SystemIcons dispose edilmez)
                    trayIcon.Icon = _trayIcon ?? SystemIcons.Information; // Tray/popup için

                    Console.WriteLine("✅ İkonlar set edildi. Tray/popup: " + (trayIcon.Icon != null ? $"{trayIcon.Icon.Width}x{trayIcon.Icon.Height}" : "null"));

                    // PictureBox için bitmap (64x64 ile)
                    using var icoForBitmap = new Icon(icoPath, new Size(64, 64));
                    pictureLogo.Image?.Dispose();
                    pictureLogo.Image = icoForBitmap.ToBitmap();
                    Console.WriteLine("✅ PictureBox güncellendi.");

                    return; // PNG aramaya gerek yok
                }
                else
                {
                    Console.WriteLine("❌ ICO yok! PNG/SVG fallback deneniyor.");
                }

                // PNG/JPG/SVG fallback (eski kod aynı)
                var png = Path.Combine(dir, "logo.png");
                var jpg = Path.Combine(dir, "logo.jpg");
                var jpeg = Path.Combine(dir, "logo.jpeg");
                var svg = Path.Combine(dir, "kafalik5.svg");
                string pick = File.Exists(png) ? png : File.Exists(jpg) ? jpg : File.Exists(jpeg) ? jpeg : null;
                if (pick != null)
                {
                    Console.WriteLine($"✅ PNG/JPG bulundu: {pick}");
                    using var fs = new FileStream(pick, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    pictureLogo.Image?.Dispose();
                    pictureLogo.Image = Image.FromStream(fs);
                }
                else if (File.Exists(svg))
                {
                    Console.WriteLine("✅ SVG bulundu.");
                    pictureLogo.Image = TryLoadSvgAsBitmap(svg, 80, 80);
                }
                else
                {
                    Console.WriteLine("❌ Hiç logo yok! Varsayılan kullanılıyor.");
                }

                // Fallback: Eğer ICO/PNG yoksa, varsayılan ikon set et (dispose edilmez)
                SetFallbackIcons();
            }
            catch (Exception ex)
            {
                // 🔑 Log ekle (debug için)
                Console.WriteLine($"❌ Logo yükleme hatası: {ex.Message}\nStack: {ex.StackTrace}");
                // Fallback
                SetFallbackIcons();
            }
            Console.WriteLine("🔍 LoadLogoDynamic bitti.");
        }

        // 🔑 YENİ: Fallback ikon set et (yardımcı metod, popup/tray için)
        private void SetFallbackIcons()
        {
            this.Icon = SystemIcons.Application;
            trayIcon.Icon = SystemIcons.Information;
            Console.WriteLine("🔄 Fallback ikonlar set edildi (popup/tray için).");
        }

        // Svg.SvgDocument (NuGet: Svg) varsa reflection ile çiz
        private Bitmap? TryLoadSvgAsBitmap(string svgPath, int w, int h)
        {
            try
            {
                var t = Type.GetType("Svg.SvgDocument, Svg");
                if (t == null) return null;
                dynamic doc = t.GetMethod("Open", new[] { typeof(string) }).Invoke(null, new object[] { svgPath });
                var bmp = new Bitmap(w, h);
                using (var g = Graphics.FromImage(bmp))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.Clear(Color.Transparent);
                    doc.Draw(g);
                }
                return bmp;
            }
            catch { return null; }
        }
        // Tek format kaynağı: duplikasyon engelli
        private string FormatDeviceLine(string? label, string? serial, string? subject)
        {
            label = (label ?? "").Trim();
            serial = (serial ?? "").Trim();
            subject = (subject ?? "").Trim();

            if (string.IsNullOrWhiteSpace(serial))
            {
                var serialMatch = Regex.Match(label, @"\(([^)]+)\)");
                if (serialMatch.Success)
                    serial = serialMatch.Groups[1].Value.Trim();
            }

            if (string.IsNullOrWhiteSpace(subject))
            {
                int dashIndex = label.LastIndexOf(" - ", StringComparison.Ordinal);
                if (dashIndex >= 0 && dashIndex + 3 < label.Length)
                    subject = label.Substring(dashIndex + 3).Trim();
            }

            string deviceName = Regex.Replace(label, @"\s*\(.*?\)", string.Empty).Trim();
            int firstDashIndex = deviceName.IndexOf(" - ", StringComparison.Ordinal);
            if (firstDashIndex > 0)
                deviceName = deviceName.Substring(0, firstDashIndex).Trim();

            if (string.IsNullOrWhiteSpace(deviceName))
                deviceName = "Bilinmeyen cihaz";

            string display = deviceName;
            if (!string.IsNullOrWhiteSpace(serial))
                display += $" ({serial})";
            if (!string.IsNullOrWhiteSpace(subject))
                display += $" - {subject}";

            return display;
        }
        private void LoadDevicesAndImzaBilgisi()
        {
            try
            {
                var dummyLogger = new DummyLogger<ReceptServiceApp>();
                var service = new ReceptServiceApp(dummyLogger);
                var devices = service.GetSignatureDevices();
                BindSignerDevices(devices);

                if (devices.Count == 0)
                {
                    labelDeviceLine.Text = "Kart bulunamadı. Lütfen kartı takıp Yenile butonuna basın.";
                    labelDeviceLine.ForeColor = Color.FromArgb(190, 40, 40);
                    labelHosgeldiniz.Text = "Hoş geldiniz..."; // Varsayılan
                    labelMesaj.Text = "Smartcard algılanmadı. Yenile butonu ile tekrar deneyin.";
                    SetSignerStatus("İmza cihazı bulunamadı.", true, false);
                    return;
                }
                var d = devices.First();
                deviceSubject = d.Subject?.Trim() ?? "Kullanıcı"; // Subject'i sakla
                string line = FormatDeviceLine(d.Label, d.Serial, d.Subject);
                labelDeviceLine.Text = string.IsNullOrWhiteSpace(line) ? "Bilinmeyen cihaz" : line;
                labelDeviceLine.ForeColor = Color.FromArgb(0, 95, 170);
                // Dinamik hoş geldiniz mesajı
                labelHosgeldiniz.Text = $"Hoş geldiniz, {deviceSubject}";
                // Merkez mesaj sabit
                labelMesaj.Text = "PDF belgenizi güvenle seçip imza atabilirsiniz.";
                SetSignerStatus("Cihaz hazır. PDF seçip imzalayabilirsiniz.", false, false);
            }
            catch (Exception ex)
            {
                BindSignerDevices(new List<ReceptServiceApp.SignatureDeviceDto>());
                labelDeviceLine.Text = $"Hata: {ex.Message}";
                labelDeviceLine.ForeColor = Color.FromArgb(190, 40, 40);
                labelHosgeldiniz.Text = "Hoş geldiniz..."; // Varsayılan hata durumunda
                labelMesaj.Text = "Bir hata oluştu. Lütfen Yenile butonuna basın.";
                SetSignerStatus("Cihazlar okunamadı.", true, false);
            }
        }

        private void LoadSignedDocuments(string? preferredPath = null)
        {
            signedDocsDir = ResolveSignedDocumentsDirectory();

            if (listSignedDocs == null)
                return;

            listSignedDocs.BeginUpdate();
            try
            {
                listSignedDocs.Items.Clear();
                SetTextPreview(string.Empty);

                if (string.IsNullOrWhiteSpace(signedDocsDir) || !Directory.Exists(signedDocsDir))
                {
                    labelPreviewHeader.Text = "Önizleme";
                    labelPreviewInfo.Text = "SignedDocuments klasörü bulunamadı.";
                    SetTextPreview("İmzalı dosya oluştuğunda burada listelenecek.");
                    return;
                }

                string? preferredFullPath = string.IsNullOrWhiteSpace(preferredPath)
                    ? null
                    : Path.GetFullPath(preferredPath);

                var files = Directory
                    .EnumerateFiles(signedDocsDir, "*.*", SearchOption.TopDirectoryOnly)
                    .Where(ShouldShowFile)
                    .Select(p => new FileInfo(p))
                    .OrderByDescending(f =>
                        preferredFullPath != null &&
                        string.Equals(f.FullName, preferredFullPath, StringComparison.OrdinalIgnoreCase))
                    .ThenByDescending(f => f.LastWriteTimeUtc)
                    .ThenByDescending(f => f.CreationTimeUtc)
                    .ThenByDescending(f => f.Name, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                foreach (var file in files)
                {
                    var item = new ListViewItem(file.Name);
                    item.SubItems.Add(file.Extension.TrimStart('.').ToUpperInvariant());
                    item.SubItems.Add(FormatFileSize(file.Length));
                    item.SubItems.Add(file.LastWriteTime.ToString("dd.MM.yyyy HH:mm"));
                    item.Tag = file.FullName;
                    listSignedDocs.Items.Add(item);
                }

                if (files.Count == 0)
                {
                    labelPreviewHeader.Text = "Önizleme";
                    labelPreviewInfo.Text = $"Dosya bulunamadı. Klasör: {signedDocsDir}";
                    SetTextPreview("İmzalı XML/XSIG/PDF dosyaları burada görüntülenecek.");
                    return;
                }

                ListViewItem? selectedItem = null;
                if (!string.IsNullOrWhiteSpace(preferredPath))
                {
                    selectedItem = listSignedDocs.Items
                        .Cast<ListViewItem>()
                        .FirstOrDefault(i => string.Equals(i.Tag as string, preferredPath, StringComparison.OrdinalIgnoreCase));
                }

                selectedItem ??= listSignedDocs.Items[0];
                selectedItem.Selected = true;
                selectedItem.Focused = true;
                selectedItem.EnsureVisible();

                try
                {
                    listSignedDocs.TopItem = listSignedDocs.Items[0];
                }
                catch
                {
                    // TopItem handle bazı durumlarda henüz hazır olmayabilir.
                }

                ShowSelectedFilePreview();
            }
            catch (Exception ex)
            {
                labelPreviewHeader.Text = "Önizleme";
                labelPreviewInfo.Text = $"Dosya listesi yüklenemedi: {ex.Message}";
                SetTextPreview(ex.ToString());
            }
            finally
            {
                listSignedDocs.EndUpdate();
            }
        }

        private bool ShouldShowFile(string path)
        {
            string ext = Path.GetExtension(path).ToLowerInvariant();
            string filter = (cmbFileFilter?.SelectedItem?.ToString() ?? "Tüm Dosyalar").ToUpperInvariant();

            if (filter == "XML")
                return ext == ".xml";
            if (filter == "XSIG")
                return ext == ".xsig";
            if (filter == "PDF")
                return ext == ".pdf";

            return ext == ".xml" || ext == ".xsig" || ext == ".pdf" || ext == ".txt";
        }

        private async void ShowSelectedFilePreview()
        {
            string? selectedPath = GetSelectedSignedFilePath();
            if (string.IsNullOrWhiteSpace(selectedPath))
                return;

            try
            {
                var info = new FileInfo(selectedPath);
                if (string.Equals(info.Extension, ".pdf", StringComparison.OrdinalIgnoreCase))
                {
                    string baseInfo =
                        $"{info.FullName}{Environment.NewLine}" +
                        $"Boyut: {FormatFileSize(info.Length)} | Son Değişim: {info.LastWriteTime:dd.MM.yyyy HH:mm:ss} | PDF belgesi";

                    labelPreviewHeader.Text = info.Name;
                    labelPreviewInfo.Text = baseInfo + Environment.NewLine + "Uygulama doğrulaması: kontrol ediliyor...";

                    bool previewOk = await TryShowPdfPreviewAsync(selectedPath);
                    if (!previewOk)
                    {
                        SetTextPreview("PDF önizleme açılamadı. 'Seçileni Aç' ile Acrobat/varsayılan PDF görüntüleyicide açabilirsiniz.");
                    }

                    await UpdatePdfValidationStatusAsync(selectedPath, baseInfo);
                    return;
                }

                Interlocked.Increment(ref _previewValidationVersion);

                string content = File.ReadAllText(selectedPath);
                bool clipped = false;

                if (content.Length > MaxPreviewChars)
                {
                    content = content.Substring(0, MaxPreviewChars);
                    clipped = true;
                }

                string summary = BuildSignatureSummary(info.Extension, content);

                labelPreviewHeader.Text = info.Name;
                labelPreviewInfo.Text =
                    $"{info.FullName}{Environment.NewLine}" +
                    $"Boyut: {FormatFileSize(info.Length)} | Son Değişim: {info.LastWriteTime:dd.MM.yyyy HH:mm:ss} | {summary}";

                string previewText = clipped
                    ? content + Environment.NewLine + Environment.NewLine + $"... önizleme {MaxPreviewChars} karakterle sınırlandı."
                    : content;
                SetTextPreview(previewText);
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref _previewValidationVersion);
                labelPreviewHeader.Text = "Önizleme";
                labelPreviewInfo.Text = $"Dosya okunamadı: {selectedPath}";
                SetTextPreview(ex.ToString());
            }
        }

        private void SetTextPreview(string text)
        {
            if (webPreviewPdf != null)
                webPreviewPdf.Visible = false;

            txtPreview.Visible = true;
            txtPreview.Text = text;
            txtPreview.SelectionStart = 0;
            txtPreview.SelectionLength = 0;
            txtPreview.ScrollToCaret();
        }

        private async Task<bool> TryShowPdfPreviewAsync(string pdfPath)
        {
            if (webPreviewPdf == null || isWebView2Unavailable)
                return false;

            try
            {
                if (webPreviewPdf.CoreWebView2 == null)
                {
                    await webPreviewPdf.EnsureCoreWebView2Async();
                    if (webPreviewPdf.CoreWebView2 != null)
                    {
                        webPreviewPdf.CoreWebView2.Settings.AreDevToolsEnabled = false;
                        webPreviewPdf.CoreWebView2.Settings.IsStatusBarEnabled = false;
                    }
                }

                txtPreview.Visible = false;
                webPreviewPdf.Visible = true;
                webPreviewPdf.Source = new Uri(pdfPath);
                return true;
            }
            catch (WebView2RuntimeNotFoundException)
            {
                isWebView2Unavailable = true;
                return false;
            }
            catch
            {
                return false;
            }
        }

        private async Task UpdatePdfValidationStatusAsync(string pdfPath, string baseInfo)
        {
            int version = Interlocked.Increment(ref _previewValidationVersion);

            try
            {
                var service = new ReceptServiceApp(new DummyLogger<ReceptServiceApp>());
                var validation = await Task.Run(() => service.ValidatePdfSignatures(pdfPath));

                if (version != _previewValidationVersion)
                    return;

                string? stillSelected = GetSelectedSignedFilePath();
                if (!string.Equals(stillSelected, pdfPath, StringComparison.OrdinalIgnoreCase))
                    return;

                labelPreviewInfo.Text = baseInfo + Environment.NewLine + BuildPdfValidationSummary(validation);
            }
            catch (Exception ex)
            {
                if (version != _previewValidationVersion)
                    return;

                labelPreviewInfo.Text = baseInfo + Environment.NewLine + $"Uygulama doğrulaması: Hata ({ex.Message})";
            }
        }

        private static string BuildPdfValidationSummary(ReceptServiceApp.PdfSignatureValidationResult validation)
        {
            if (!validation.Success)
                return $"Uygulama doğrulaması: Başarısız ({validation.Message})";

            if (validation.SignatureCount == 0)
                return "Uygulama doğrulaması: PDF içinde imza bulunamadı.";

            if (validation.TrustedChainCount == validation.SignatureCount)
                return $"Uygulama doğrulaması: {validation.TrustedChainCount}/{validation.SignatureCount} imza güvenilir.";

            if (validation.CryptographicallyValidCount == validation.SignatureCount)
                return $"Uygulama doğrulaması: İmzalar geçerli, zincir güveni eksik ({validation.TrustedChainCount}/{validation.SignatureCount} güvenilir).";

            return $"Uygulama doğrulaması: {validation.CryptographicallyValidCount}/{validation.SignatureCount} imza geçerli, {validation.TrustedChainCount}/{validation.SignatureCount} güvenilir.";
        }

        private static string BuildSignatureSummary(string extension, string xmlText)
        {
            extension = extension.ToLowerInvariant();
            if (extension == ".pdf")
                return "PDF belgesi";

            if (extension != ".xml" && extension != ".xsig")
                return "Metin dosyası";

            int signatureCount = Regex.Matches(xmlText, @"<\s*Signature\b", RegexOptions.IgnoreCase).Count;
            int digestCount = Regex.Matches(xmlText, @"<\s*DigestValue\b", RegexOptions.IgnoreCase).Count;
            int certCount = Regex.Matches(xmlText, @"<\s*X509Certificate\b", RegexOptions.IgnoreCase).Count;
            string subject = ExtractXmlElementValue(xmlText, "X509SubjectName");
            string serial = ExtractXmlElementValue(xmlText, "X509SerialNumber");

            var parts = new List<string>
            {
                $"Signature: {signatureCount}",
                $"Digest: {digestCount}",
                $"Sertifika: {certCount}"
            };

            if (!string.IsNullOrWhiteSpace(subject))
                parts.Add($"Subject: {subject}");
            if (!string.IsNullOrWhiteSpace(serial))
                parts.Add($"Serial: {serial}");

            return string.Join(" | ", parts);
        }

        private static string ExtractXmlElementValue(string xml, string elementName)
        {
            var match = Regex.Match(
                xml,
                $@"<\s*{Regex.Escape(elementName)}\b[^>]*>(.*?)<\s*/\s*{Regex.Escape(elementName)}\s*>",
                RegexOptions.IgnoreCase | RegexOptions.Singleline);

            if (!match.Success)
                return string.Empty;

            string value = Regex.Replace(match.Groups[1].Value.Trim(), @"\s+", " ");
            if (value.Length > 80)
                value = value.Substring(0, 80) + "...";

            return value;
        }

        private void OpenSelectedSignedFile()
        {
            string? filePath = GetSelectedSignedFilePath();
            if (string.IsNullOrWhiteSpace(filePath))
            {
                MessageBox.Show("Lütfen önce bir dosya seçin.", "Bilgi", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = filePath,
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Dosya açılamadı: {ex.Message}", "Hata", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void OpenSignedFolder()
        {
            try
            {
                string folderPath = ResolveSignedDocumentsDirectory() ?? Path.Combine(AppContext.BaseDirectory, "SignedDocuments");
                if (!Directory.Exists(folderPath))
                    Directory.CreateDirectory(folderPath);

                Process.Start(new ProcessStartInfo
                {
                    FileName = folderPath,
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Klasör açılamadı: {ex.Message}", "Hata", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private string? GetSelectedSignedFilePath()
        {
            if (listSignedDocs == null || listSignedDocs.SelectedItems.Count == 0)
                return null;

            return listSignedDocs.SelectedItems[0].Tag as string;
        }

        private static string? ResolveSignedDocumentsDirectory()
        {
            string baseDir = AppContext.BaseDirectory;
            string[] candidates =
            {
                Path.Combine(baseDir, "SignedDocuments"),
                Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "SignedDocuments")),
                Path.Combine(Directory.GetCurrentDirectory(), "SignedDocuments")
            };

            return candidates.FirstOrDefault(Directory.Exists);
        }

        private static string FormatFileSize(long size)
        {
            const double kb = 1024d;
            const double mb = 1024d * 1024d;

            if (size < kb) return $"{size} B";
            if (size < mb) return $"{size / kb:0.0} KB";
            return $"{size / mb:0.00} MB";
        }

        // Basit dummy logger
        private class DummyLogger<T> : ILogger<T>
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                var msg = formatter(state, exception);
                if (exception != null) msg += $" | Exception: {exception.Message}";
                Console.WriteLine($"[{typeof(T).Name}] {logLevel}: {msg}");
            }
        }
    }
}

