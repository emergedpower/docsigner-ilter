using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks; // 🔑 Delay için (popup ikon fix)
using System.Windows.Forms;
using docsigner_ilter.Services;
using Microsoft.Extensions.Logging;

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
        private Label labelPdfPath = null!;
        private TextBox txtPdfPath = null!;
        private Button btnSelectPdf = null!;
        private Label labelSignerDevice = null!;
        private ComboBox cmbSignerDevice = null!;
        private Button btnRefreshSignerDevices = null!;
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
        private string? signedDocsDir;
        private const int MaxPreviewChars = 15000;
        // 🔑 YENİ: Icon field'ları (dispose için, popup/tray için lifetime yönetimi)
        private Icon? _formIcon;
        private Icon? _trayIcon;
        // Device info for dynamic greeting
        private string deviceSubject = string.Empty;
        private readonly List<ReceptServiceApp.SignatureDeviceDto> signerDevices = new();
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
            DoubleBuffered = true;
            BackColor = Color.FromArgb(242, 247, 252);
            Paint += (s, e) => DrawBackgroundGradient(e.Graphics);
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
                BackColor = Color.Transparent,
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
                Text = "PDF belgenizi secip PAdES uyumlu elektronik imza atabilirsiniz.",
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
                Text = "Cihaz Yenile",
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
            };

            LayoutSignerCard();
            LayoutSignatureViewer();
            ResizeSignedDocsColumns();
        }

        private void InitializePdfSignerUI()
        {
            signerCard = new Panel
            {
                BackColor = Color.Transparent,
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
                Height = 188
            };
            signerCard.Paint += Card_Paint;
            Controls.Add(signerCard);

            labelSignerTitle = new Label
            {
                Text = "PDF Imzalama",
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
                Text = "Belge secin, PIN girin ve imzalayin. Imza alani son sayfada sag-alta yerlestirilir.",
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
                Left = 16,
                Top = 90,
                Height = 30,
                Width = 690,
                BackColor = Color.White
            };
            signerCard.Controls.Add(txtPdfPath);

            btnSelectPdf = new Button
            {
                Text = "Belge Sec",
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
                Text = "Imza Cihazi",
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
            signerCard.Controls.Add(cmbSignerDevice);

            btnRefreshSignerDevices = new Button
            {
                Text = "Cihazlari Yenile",
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
                SetSignerStatus("Cihaz listesi guncellendi.", false, false);
            };
            signerCard.Controls.Add(btnRefreshSignerDevices);

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
                Minimum = 0,
                Maximum = 9999,
                Value = 0,
                Font = new Font("Segoe UI", 9),
                Width = 74,
                Height = 30,
                Top = 148
            };
            signerCard.Controls.Add(numPage);

            chkAddTimestamp = new CheckBox
            {
                Text = "Timestamp ekle (TSA ayari varsa)",
                Font = new Font("Segoe UI", 8.5f),
                ForeColor = Color.FromArgb(54, 71, 92),
                AutoSize = true,
                Top = 152
            };
            signerCard.Controls.Add(chkAddTimestamp);

            btnSignPdf = new Button
            {
                Text = "PDF Imzala",
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
                Text = "Belge secilmedi.",
                Font = new Font("Segoe UI", 8.5f),
                ForeColor = Color.FromArgb(128, 85, 0),
                AutoSize = false,
                Height = 20,
                Left = 820,
                Top = 93,
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
                txtPdfPath.Width = Math.Max(260, btnSelectPdf.Left - txtPdfPath.Left - 10);

            if (btnRefreshSignerDevices != null && cmbSignerDevice != null)
                btnRefreshSignerDevices.Left = cmbSignerDevice.Right + 8;

            int pinLeft = btnRefreshSignerDevices.Right + 12;
            labelPin.Left = pinLeft;
            txtPin.Left = pinLeft;

            int pageLeft = txtPin.Right + 10;
            labelPage.Left = pageLeft;
            numPage.Left = pageLeft;

            chkAddTimestamp.Left = numPage.Right + 10;
            btnSignPdf.Left = signerCard.Width - btnSignPdf.Width - 16;

            if (chkAddTimestamp.Right > btnSignPdf.Left - 10)
                chkAddTimestamp.Left = Math.Max(numPage.Right + 6, btnSignPdf.Left - chkAddTimestamp.Width - 10);

            labelSignerStatus.Left = Math.Max(16, btnSignPdf.Left - 340);
            labelSignerStatus.Width = signerCard.Width - labelSignerStatus.Left - 16;
        }
        private void InitializeSignatureViewerUI()
        {
            viewerCard = new Panel
            {
                BackColor = Color.Transparent,
                Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right
            };
            viewerCard.Paint += Card_Paint;
            Controls.Add(viewerCard);

            labelViewerTitle = new Label
            {
                Text = "Imzali Dosyalar",
                Font = new Font("Segoe UI", 11, FontStyle.Bold),
                ForeColor = Color.FromArgb(15, 45, 98),
                Dock = DockStyle.Top,
                Height = 36,
                Padding = new Padding(16, 10, 0, 0),
                BackColor = Color.Transparent
            };
            viewerCard.Controls.Add(labelViewerTitle);

            viewerToolbar = new Panel
            {
                Dock = DockStyle.Top,
                Height = 48,
                BackColor = Color.Transparent
            };
            viewerCard.Controls.Add(viewerToolbar);

            cmbFileFilter = new ComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDownList,
                Font = new Font("Segoe UI", 9),
                Location = new Point(16, 10),
                Width = 130
            };
            cmbFileFilter.Items.AddRange(new object[] { "Tum Dosyalar", "XML", "XSIG", "PDF" });
            cmbFileFilter.SelectedIndex = 0;
            cmbFileFilter.SelectedIndexChanged += (s, e) => LoadSignedDocuments();
            viewerToolbar.Controls.Add(cmbFileFilter);

            btnDocsRefresh = new Button
            {
                Text = "Dosyalari Yenile",
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
                Text = "Seciliyi Ac",
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
                Text = "Klasoru Ac",
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
                SplitterDistance = 360,
                BackColor = Color.White
            };
            viewerCard.Controls.Add(splitViewer);

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

            labelPreviewInfo = new Label
            {
                Dock = DockStyle.Top,
                Height = 50,
                Font = new Font("Segoe UI", 8.5f),
                ForeColor = Color.FromArgb(80, 80, 80),
                Text = "Secili dosya bilgisi burada gorunur.",
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
                Text = "Onizleme",
                Padding = new Padding(8, 6, 8, 0),
                BackColor = Color.White
            };
            splitViewer.Panel2.Controls.Add(labelPreviewHeader);
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

        // 🔑 YENİ: Icon dispose (ObjectDisposedException fix, popup/tray ikonlarını koru)
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _formIcon?.Dispose();
                _trayIcon?.Dispose();
                pictureLogo?.Image?.Dispose(); // PictureBox image'ını da temizle
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
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var r = panel.ClientRectangle;
            if (r.Width <= 4 || r.Height <= 4)
                return;

            int radius = 16;
            using var path = RoundedRect(r, radius);
            using var bg = new SolidBrush(Color.White);
            using var pen = new Pen(Color.FromArgb(215, 225, 240), 1);
            using var shadow = new SolidBrush(Color.FromArgb(30, 0, 0, 0));
            var shadowRect = new Rectangle(r.X + 6, r.Y + 8, r.Width - 2, r.Height - 2);
            using (var shadowPath = RoundedRect(shadowRect, radius + 2))
                g.FillPath(shadow, shadowPath);
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
                    SetSignerStatus("Belge secimi iptal edildi.", false, false);
                    return;
                }

                txtPdfPath.Text = selectedPath;
                SetSignerStatus($"Belge secildi: {Path.GetFileName(selectedPath)}", false, false);
            }
            catch (Exception ex)
            {
                SetSignerStatus($"Belge secme hatasi: {ex.Message}", true, false);
                MessageBox.Show($"Belge secme sirasinda hata olustu:{Environment.NewLine}{ex.Message}", "Hata", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private static OpenFileDialog CreatePdfOpenFileDialog()
        {
            return new OpenFileDialog
            {
                Filter = "PDF Dosyalari (*.pdf)|*.pdf",
                CheckFileExists = true,
                CheckPathExists = true,
                Multiselect = false,
                RestoreDirectory = true,
                AutoUpgradeEnabled = false,
                DereferenceLinks = true,
                InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                Title = "Imzalanacak PDF Belgesi Secin"
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
                MessageBox.Show("Lutfen once imzalanacak PDF belgesini secin.", "Eksik Bilgi", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            if (cmbSignerDevice.SelectedIndex < 0 || cmbSignerDevice.SelectedIndex >= signerDevices.Count)
            {
                MessageBox.Show("Lutfen bir e-imza cihazi secin.", "Eksik Bilgi", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            string pin = (txtPin.Text ?? string.Empty).Trim();
            if (pin.Length < 4)
            {
                MessageBox.Show("PIN en az 4 karakter olmalidir.", "Eksik Bilgi", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                txtPin.Focus();
                return;
            }

            var selectedDevice = signerDevices[cmbSignerDevice.SelectedIndex];
            var service = new ReceptServiceApp(new DummyLogger<ReceptServiceApp>());
            int? pageNumber = numPage.Value <= 0 ? null : (int)numPage.Value;

            var options = new ReceptServiceApp.PdfSignatureOptions
            {
                PageNumber = pageNumber,
                FileName = Path.GetFileName(filePath),
                EnableTimestamp = chkAddTimestamp.Checked
            };

            try
            {
                SetSignerBusy(true);
                SetSignerStatus("PDF imzalanıyor, lutfen bekleyin...", false, false);

                byte[] pdfBytes = await File.ReadAllBytesAsync(filePath);
                var result = await service.SignPdf(
                    pdfBytes,
                    selectedDevice.Id,
                    pin,
                    options,
                    forceFreshLogin: false);

                txtPin.Clear();
                LoadSignedDocuments(result.filePath);

                string timestampText = result.timestampApplied ? "Timestamp eklendi." : "Timestamp eklenmedi.";
                SetSignerStatus($"Imza tamamlandi: {Path.GetFileName(result.filePath)} | {timestampText}", false, true);

                if (MessageBox.Show(
                        $"PDF basariyla imzalandi.{Environment.NewLine}{Path.GetFileName(result.filePath)}{Environment.NewLine}{timestampText}{Environment.NewLine}{Environment.NewLine}Dosyayi acmak ister misiniz?",
                        "Imzalama Basarili",
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
                SetSignerStatus($"Imzalama basarisiz: {ex.Message}", true, false);
                MessageBox.Show($"PDF imzalama hatasi:{Environment.NewLine}{ex.Message}", "Hata", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                SetSignerBusy(false);
            }
        }

        private void SetSignerBusy(bool busy)
        {
            btnSignPdf.Enabled = !busy;
            btnSelectPdf.Enabled = !busy;
            btnRefreshSignerDevices.Enabled = !busy;
            cmbSignerDevice.Enabled = !busy;
            txtPin.Enabled = !busy;
            numPage.Enabled = !busy;
            chkAddTimestamp.Enabled = !busy;
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
            string display = string.IsNullOrWhiteSpace(label) ? "Bilinmeyen cihaz" : label;
            if (!string.IsNullOrEmpty(serial) && !display.Contains(serial))
                display += $" ({serial})";
            if (!string.IsNullOrEmpty(subject) && !display.Contains(subject))
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
                    SetSignerStatus("Imza cihazi bulunamadi.", true, false);
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
                labelMesaj.Text = "PDF belgenizi secip guvenli elektronik imza atabilirsiniz.";
                SetSignerStatus("Cihaz hazir. PDF secip imzalayabilirsiniz.", false, false);
            }
            catch (Exception ex)
            {
                BindSignerDevices(new List<ReceptServiceApp.SignatureDeviceDto>());
                labelDeviceLine.Text = $"Hata: {ex.Message}";
                labelDeviceLine.ForeColor = Color.FromArgb(190, 40, 40);
                labelHosgeldiniz.Text = "Hoş geldiniz..."; // Varsayılan hata durumunda
                labelMesaj.Text = "Bir hata oluştu. Lütfen Yenile butonuna basın.";
                SetSignerStatus("Cihazlar okunamadi.", true, false);
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
                txtPreview.Clear();

                if (string.IsNullOrWhiteSpace(signedDocsDir) || !Directory.Exists(signedDocsDir))
                {
                    labelPreviewHeader.Text = "Onizleme";
                    labelPreviewInfo.Text = "SignedDocuments klasoru bulunamadi.";
                    txtPreview.Text = "Imzali dosya olustugunda burada listelenecek.";
                    return;
                }

                var files = Directory
                    .EnumerateFiles(signedDocsDir, "*.*", SearchOption.TopDirectoryOnly)
                    .Where(ShouldShowFile)
                    .Select(p => new FileInfo(p))
                    .OrderByDescending(f => f.LastWriteTimeUtc)
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
                    labelPreviewHeader.Text = "Onizleme";
                    labelPreviewInfo.Text = $"Dosya bulunamadi. Klasor: {signedDocsDir}";
                    txtPreview.Text = "Imzali XML/XSIG/PDF dosyalari burada goruntulenecek.";
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
                ShowSelectedFilePreview();
            }
            catch (Exception ex)
            {
                labelPreviewHeader.Text = "Onizleme";
                labelPreviewInfo.Text = $"Dosya listesi yuklenemedi: {ex.Message}";
                txtPreview.Text = ex.ToString();
            }
            finally
            {
                listSignedDocs.EndUpdate();
            }
        }

        private bool ShouldShowFile(string path)
        {
            string ext = Path.GetExtension(path).ToLowerInvariant();
            string filter = (cmbFileFilter?.SelectedItem?.ToString() ?? "Tum Dosyalar").ToUpperInvariant();

            if (filter == "XML")
                return ext == ".xml";
            if (filter == "XSIG")
                return ext == ".xsig";
            if (filter == "PDF")
                return ext == ".pdf";

            return ext == ".xml" || ext == ".xsig" || ext == ".pdf" || ext == ".txt";
        }

        private void ShowSelectedFilePreview()
        {
            string? selectedPath = GetSelectedSignedFilePath();
            if (string.IsNullOrWhiteSpace(selectedPath))
                return;

            try
            {
                var info = new FileInfo(selectedPath);
                if (string.Equals(info.Extension, ".pdf", StringComparison.OrdinalIgnoreCase))
                {
                    labelPreviewHeader.Text = info.Name;
                    labelPreviewInfo.Text =
                        $"{info.FullName}{Environment.NewLine}" +
                        $"Boyut: {FormatFileSize(info.Length)} | Son Degisim: {info.LastWriteTime:dd.MM.yyyy HH:mm:ss} | PDF belgesi";
                    txtPreview.Text = "PDF onizleme bu panelde desteklenmiyor. 'Seciliyi Ac' ile Acrobat/varsayilan PDF goruntuleyicide acabilirsiniz.";
                    return;
                }

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
                    $"Boyut: {FormatFileSize(info.Length)} | Son Degisim: {info.LastWriteTime:dd.MM.yyyy HH:mm:ss} | {summary}";

                txtPreview.Text = clipped
                    ? content + Environment.NewLine + Environment.NewLine + $"... onizleme {MaxPreviewChars} karakterle sinirlandi."
                    : content;

                txtPreview.SelectionStart = 0;
                txtPreview.SelectionLength = 0;
                txtPreview.ScrollToCaret();
            }
            catch (Exception ex)
            {
                labelPreviewHeader.Text = "Onizleme";
                labelPreviewInfo.Text = $"Dosya okunamadi: {selectedPath}";
                txtPreview.Text = ex.ToString();
            }
        }

        private static string BuildSignatureSummary(string extension, string xmlText)
        {
            extension = extension.ToLowerInvariant();
            if (extension == ".pdf")
                return "PDF belgesi";

            if (extension != ".xml" && extension != ".xsig")
                return "Metin dosyasi";

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
                MessageBox.Show("Lutfen once bir dosya secin.", "Bilgi", MessageBoxButtons.OK, MessageBoxIcon.Information);
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
                MessageBox.Show($"Dosya acilamadi: {ex.Message}", "Hata", MessageBoxButtons.OK, MessageBoxIcon.Error);
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
                MessageBox.Show($"Klasor acilamadi: {ex.Message}", "Hata", MessageBoxButtons.OK, MessageBoxIcon.Error);
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

