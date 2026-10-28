using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;

[assembly: System.Reflection.AssemblyTitle("curl2url")]
[assembly: System.Reflection.AssemblyDescription("cURL / HAR 解析与失败请求查看工具")]
[assembly: System.Reflection.AssemblyProduct("curl2url")]
[assembly: System.Reflection.AssemblyCompany("lizhuoyu")]
[assembly: System.Reflection.AssemblyCopyright("Copyright (c) 2026 lizhuoyu")]
[assembly: System.Reflection.AssemblyVersion("1.0.4.0")]
[assembly: System.Reflection.AssemblyFileVersion("1.0.4.0")]
[assembly: System.Reflection.AssemblyInformationalVersion("1.0.4")]

namespace Curl2Url
{
    internal static class Program
    {
        [STAThread]
        private static void Main(string[] args)
        {
            if (args.Length > 0)
            {
                if (args[0] == "--extract")
                {
                    RunExtractOnly();
                    return;
                }
                if (args[0] == "--details")
                {
                    RunDetailsOnly();
                    return;
                }
                if (args[0] == "--failures")
                {
                    RunFailuresOnly();
                    return;
                }
                if (args[0] == "--ocr")
                {
                    if (args.Length < 2)
                    {
                        WriteStderr("用法：curl2url.exe --ocr <图片路径>");
                        Environment.Exit(2);
                    }
                    Environment.Exit(RunOcrOnly(args[1]));
                }
                if (args[0] == "--selftest")
                {
                    Application.EnableVisualStyles();
                    Application.SetCompatibleTextRenderingDefault(false);
                    using (MainForm form = new MainForm())
                    {
                        form.CreateControl();
                    }
                    if (OcrRunner.NormalizeOcrText("系 统 繁 忙 ， 请 稍 后 重 试") != "系统繁忙，请稍后重试")
                    {
                        Console.WriteLine("OCR normalize failed");
                        Environment.Exit(1);
                    }
                    // 纯英文行把误识别的全角冒号转回半角，并去掉标点前空格
                    if (OcrRunner.NormalizeOcrText("GET https ：//api.example.com/v1") != "GET https://api.example.com/v1")
                    {
                        Console.WriteLine("OCR latin normalize failed");
                        Environment.Exit(1);
                    }
                    Console.WriteLine("OK");
                    Environment.Exit(0);
                }
            }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MainForm());
        }

        // GUI 程序默认没有控制台，Console 的编码设置不可靠，直接按 UTF-8 读写标准流
        private static string ReadStdinText()
        {
            using (StreamReader reader = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false)))
            {
                return reader.ReadToEnd();
            }
        }

        private static void WriteStdout(string text)
        {
            byte[] bytes = new UTF8Encoding(false).GetBytes(text);
            Stream stream = Console.OpenStandardOutput();
            stream.Write(bytes, 0, bytes.Length);
            stream.Flush();
        }

        private static void WriteStderr(string text)
        {
            byte[] bytes = new UTF8Encoding(false).GetBytes(text + "\r\n");
            Stream stream = Console.OpenStandardError();
            stream.Write(bytes, 0, bytes.Length);
            stream.Flush();
        }

        private static bool HasArg(string[] args, string name)
        {
            foreach (string arg in args)
            {
                if (string.Equals(arg, name, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        private static int RunOcrOnly(string path)
        {
            try
            {
                using (Image image = Image.FromFile(path))
                using (Bitmap bitmap = new Bitmap(image))
                {
                    string text;
                    string error;
                    if (!OcrRunner.TryRecognize(bitmap, out text, out error))
                    {
                        WriteStderr("OCR 失败：" + error);
                        return 1;
                    }
                    WriteStdout(text + "\r\n");
                    return 0;
                }
            }
            catch (Exception ex)
            {
                WriteStderr("OCR 失败：" + ex.Message);
                return 1;
            }
        }

        private static void RunExtractOnly()
        {
            try
            {
                string input = ReadStdinText();
                bool mask = HasArg(Environment.GetCommandLineArgs(), "--mask");
                List<string> urls = UrlExtractor.Unique(UrlExtractor.ExtractAny(input));
                if (mask)
                {
                    for (int i = 0; i < urls.Count; i++) urls[i] = Sensitive.MaskUrl(urls[i]);
                }
                if (urls.Count == 0)
                {
                    WriteStderr("没有找到 URL。请确认内容是 curl 命令或 HAR JSON。");
                    Environment.Exit(1);
                }
                WriteStdout(string.Join("\n", urls.ToArray()) + "\n");
                Environment.Exit(0);
            }
            catch (Exception ex)
            {
                WriteStderr(ex.Message);
                Environment.Exit(1);
            }
        }

        private static void RunDetailsOnly()
        {
            try
            {
                string input = ReadStdinText();
                bool mask = HasArg(Environment.GetCommandLineArgs(), "--mask");
                List<CurlRequest> requests = UrlExtractor.ParseAny(input);
                List<string> urls = UrlExtractor.Unique(UrlExtractor.ExtractAny(input));
                WriteStdout(UrlExtractor.FormatDetails(requests, urls, mask));
                Environment.Exit(0);
            }
            catch (Exception ex)
            {
                WriteStderr(ex.Message);
                Environment.Exit(1);
            }
        }

        private static void RunFailuresOnly()
        {
            try
            {
                string input = ReadStdinText();
                bool mask = HasArg(Environment.GetCommandLineArgs(), "--mask");
                List<CurlRequest> requests = UrlExtractor.ParseAny(input);
                WriteStdout(UrlExtractor.FormatFailures(requests, mask));
                Environment.Exit(0);
            }
            catch (Exception ex)
            {
                WriteStderr(ex.Message);
                Environment.Exit(1);
            }
        }
    }

    internal sealed class MainForm : Form
    {
        private static readonly Color PageBack = Color.FromArgb(245, 246, 248);
        private static readonly Color TitleColor = Color.FromArgb(48, 49, 51);
        private static readonly Color LabelColor = Color.FromArgb(96, 98, 102);
        private static readonly Color MutedColor = Color.FromArgb(144, 147, 153);
        private static readonly Color FaintColor = Color.FromArgb(192, 196, 204);
        private static readonly Color LineColor = Color.FromArgb(220, 223, 230);
        private static readonly Color PrimaryColor = Color.FromArgb(64, 158, 255);
        private static readonly Color PrimaryHover = Color.FromArgb(51, 137, 230);
        private static readonly Color OkColor = Color.FromArgb(46, 125, 50);
        private static readonly Color ErrorColor = Color.FromArgb(183, 28, 28);

        private readonly TextBox _inputBox;
        private readonly TextBox _resultBox;
        private readonly TextBox _detailBox;
        private readonly TextBox _failureBox;
        private readonly TextBox _responseBox;
        private readonly Label _statusLabel;
        private readonly CheckBox _keepDuplicates;
        private readonly CheckBox _maskSensitive;
        private readonly System.Windows.Forms.Timer _autoTimer;
        private TabPage _failurePage;
        private string _lastSignature = "";

        // 截图识别页签
        private TabControl _tabs;
        private TabPage _ocrPage;
        private PictureBox _ocrPreview;
        private TextBox _ocrBox;
        private Label _ocrStatusLabel;
        private bool _ocrBusy;

        public MainForm()
        {
            Text = "curl2url · cURL / HAR 解析";
            ClientSize = new Size(900, 700);
            MinimumSize = new Size(760, 580);
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = PageBack;
            Font = new Font("Microsoft YaHei UI", 9F);
            AutoScaleMode = AutoScaleMode.Dpi;

            // WinForms 窗体默认用框架自带图标，不会自动继承 exe 内嵌图标，
            // 这里显式取 exe 图标，标题栏 / 任务栏 / Alt+Tab 才会显示新图标
            try
            {
                Icon = System.Drawing.Icon.ExtractAssociatedIcon(Application.ExecutablePath);
            }
            catch (Exception)
            {
                // 取不到就保留系统默认图标，不影响功能
            }

            TableLayoutPanel root = new TableLayoutPanel();
            root.Dock = DockStyle.Fill;
            root.ColumnCount = 1;
            root.RowCount = 7;
            root.Padding = new Padding(18);
            root.BackColor = PageBack;
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 42F));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 58F));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            Controls.Add(root);

            FlowLayoutPanel header = new FlowLayoutPanel();
            header.FlowDirection = FlowDirection.TopDown;
            header.WrapContents = false;
            header.AutoSize = true;
            header.Dock = DockStyle.Fill;
            header.Margin = new Padding(0, 0, 0, 12);

            Label title = new Label();
            title.Text = "curl2url";
            title.Font = new Font("Microsoft YaHei UI", 15F, FontStyle.Bold);
            title.ForeColor = TitleColor;
            title.AutoSize = true;
            title.Margin = new Padding(0);

            Label subtitle = new Label();
            subtitle.Text = "粘贴 cURL 或 HAR，自动解析；请求详情、失败请求、响应同屏看。";
            subtitle.ForeColor = MutedColor;
            subtitle.AutoSize = true;
            subtitle.Margin = new Padding(1, 4, 0, 0);

            header.Controls.Add(title);
            header.Controls.Add(subtitle);
            root.Controls.Add(header, 0, 0);

            root.Controls.Add(MakeSectionLabel("curl 命令"), 0, 1);

            _inputBox = new TextBox();
            _inputBox.Multiline = true;
            _inputBox.AcceptsReturn = true;
            _inputBox.AcceptsTab = true;
            _inputBox.MaxLength = int.MaxValue; // 默认 32767，长 HAR 粘贴会被截断
            _inputBox.WordWrap = false;
            _inputBox.ScrollBars = ScrollBars.Both;
            _inputBox.Font = new Font("Consolas", 10F);
            _inputBox.BackColor = Color.White;
            _inputBox.ForeColor = TitleColor;
            _inputBox.BorderStyle = BorderStyle.FixedSingle;
            _inputBox.Dock = DockStyle.Fill;
            _inputBox.Margin = new Padding(0);
            root.Controls.Add(_inputBox, 0, 2);

            _autoTimer = new System.Windows.Forms.Timer();
            _autoTimer.Interval = 450;
            _autoTimer.Tick += delegate
            {
                _autoTimer.Stop();
                Analyze(true);
            };
            _inputBox.TextChanged += delegate
            {
                _autoTimer.Stop();
                _autoTimer.Start();
            };

            FlowLayoutPanel buttons = new FlowLayoutPanel();
            buttons.FlowDirection = FlowDirection.LeftToRight;
            buttons.WrapContents = true;
            buttons.AutoSize = true;
            buttons.Dock = DockStyle.Fill;
            buttons.Margin = new Padding(0, 12, 0, 12);

            Button pasteButton = MakeButton("从剪贴板粘贴", false, 112);
            pasteButton.Click += delegate { PasteFromClipboard(); };

            Button openButton = MakeButton("打开 HAR 文件", false, 112);
            openButton.Click += delegate { OpenHarFile(); };

            Button ocrButton = MakeButton("截图识别", false, 88);
            ocrButton.Click += delegate { ShowOcrPage(true); };

            Button copyButton = MakeButton("复制 URL 列表", false, 112);
            copyButton.Click += delegate { CopyResult(); };

            Button clearButton = MakeButton("清空", false, 64);
            clearButton.Click += delegate { ClearAll(); };

            _keepDuplicates = new CheckBox();
            _keepDuplicates.Text = "保留重复 URL";
            _keepDuplicates.AutoSize = true;
            _keepDuplicates.ForeColor = LabelColor;
            _keepDuplicates.Cursor = Cursors.Hand;
            _keepDuplicates.Margin = new Padding(4, 9, 8, 0);
            _keepDuplicates.CheckedChanged += delegate { Analyze(false); };

            _maskSensitive = new CheckBox();
            _maskSensitive.Text = "隐藏长参数 / Token";
            _maskSensitive.AutoSize = true;
            _maskSensitive.Checked = true;
            _maskSensitive.ForeColor = LabelColor;
            _maskSensitive.Cursor = Cursors.Hand;
            _maskSensitive.Margin = new Padding(4, 9, 8, 0);
            _maskSensitive.CheckedChanged += delegate { Analyze(false); };

            Label hint = new Label();
            hint.Text = "粘贴或拖入后自动解析，Ctrl+Enter 刷新；报错截图可直接 Ctrl+V 转文字";
            hint.ForeColor = MutedColor;
            hint.AutoSize = true;
            hint.Margin = new Padding(6, 9, 0, 0);

            buttons.Controls.Add(pasteButton);
            buttons.Controls.Add(openButton);
            buttons.Controls.Add(ocrButton);
            buttons.Controls.Add(copyButton);
            buttons.Controls.Add(clearButton);
            buttons.Controls.Add(_keepDuplicates);
            buttons.Controls.Add(_maskSensitive);
            buttons.Controls.Add(hint);
            root.Controls.Add(buttons, 0, 3);

            // 结果区：三个页签
            TabControl tabs = new TabControl();
            tabs.Dock = DockStyle.Fill;
            tabs.Font = new Font("Microsoft YaHei UI", 9F);
            tabs.Margin = new Padding(0);

            TabPage urlPage = new TabPage("URL");
            urlPage.BackColor = Color.White;
            urlPage.UseVisualStyleBackColor = false;
            urlPage.Padding = new Padding(8);

            _resultBox = new TextBox();
            _resultBox.Multiline = true;
            _resultBox.ReadOnly = true;
            _resultBox.WordWrap = false;
            _resultBox.ScrollBars = ScrollBars.Both;
            _resultBox.Font = new Font("Consolas", 10F);
            _resultBox.BackColor = Color.White;
            _resultBox.ForeColor = TitleColor;
            _resultBox.BorderStyle = BorderStyle.FixedSingle;
            _resultBox.Dock = DockStyle.Fill;
            AttachOutputMenu(_resultBox, false);
            urlPage.Controls.Add(_resultBox);

            TabPage detailPage = new TabPage("请求详情");
            detailPage.BackColor = Color.White;
            detailPage.UseVisualStyleBackColor = false;
            detailPage.Padding = new Padding(8);

            _detailBox = new TextBox();
            _detailBox.Multiline = true;
            _detailBox.ReadOnly = true;
            _detailBox.WordWrap = true;
            _detailBox.ScrollBars = ScrollBars.Vertical;
            _detailBox.Font = new Font("Consolas", 9.5F);
            _detailBox.BackColor = Color.White;
            _detailBox.ForeColor = TitleColor;
            _detailBox.BorderStyle = BorderStyle.FixedSingle;
            _detailBox.Dock = DockStyle.Fill;
            AttachOutputMenu(_detailBox, false);
            detailPage.Controls.Add(_detailBox);

            _failurePage = new TabPage("失败请求");
            _failurePage.BackColor = Color.White;
            _failurePage.UseVisualStyleBackColor = false;
            _failurePage.Padding = new Padding(8);

            _failureBox = new TextBox();
            _failureBox.Multiline = true;
            _failureBox.ReadOnly = true;
            _failureBox.WordWrap = true;
            _failureBox.ScrollBars = ScrollBars.Vertical;
            _failureBox.Font = new Font("Consolas", 9.5F);
            _failureBox.BackColor = Color.White;
            _failureBox.ForeColor = TitleColor;
            _failureBox.BorderStyle = BorderStyle.FixedSingle;
            _failureBox.Dock = DockStyle.Fill;
            AttachOutputMenu(_failureBox, false);
            _failurePage.Controls.Add(_failureBox);

            TabPage responsePage = new TabPage("响应");
            responsePage.BackColor = Color.White;
            responsePage.UseVisualStyleBackColor = false;
            responsePage.Padding = new Padding(8);

            TableLayoutPanel responseLayout = new TableLayoutPanel();
            responseLayout.Dock = DockStyle.Fill;
            responseLayout.ColumnCount = 1;
            responseLayout.RowCount = 3;
            responseLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            responseLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            responseLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            responseLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));

            Label responseHint = new Label();
            responseHint.Text = "粘贴 HAR 时，失败请求的状态码和响应会显示在「失败请求」页签；这里仍可手动粘贴单条返回值并格式化 JSON。";
            responseHint.ForeColor = MutedColor;
            responseHint.AutoSize = false;
            responseHint.Height = 36;
            responseHint.Dock = DockStyle.Fill;
            responseHint.Margin = new Padding(0, 0, 0, 8);
            responseLayout.Controls.Add(responseHint, 0, 0);

            FlowLayoutPanel responseButtons = new FlowLayoutPanel();
            responseButtons.FlowDirection = FlowDirection.LeftToRight;
            responseButtons.WrapContents = false;
            responseButtons.AutoSize = true;
            responseButtons.Dock = DockStyle.Fill;
            responseButtons.Margin = new Padding(0, 0, 0, 8);

            Button readResponseButton = MakeSmallButton("从剪贴板读取", 118);
            readResponseButton.Click += delegate { ReadResponseFromClipboard(); };

            Button formatResponseButton = MakeSmallButton("格式化 JSON", 104);
            formatResponseButton.Click += delegate { FormatResponse(); };

            Button copyResponseButton = MakeSmallButton("复制", 72);
            copyResponseButton.Click += delegate { CopyResponse(); };

            Button clearResponseButton = MakeSmallButton("清空", 72);
            clearResponseButton.Click += delegate { ClearResponse(); };

            responseButtons.Controls.Add(readResponseButton);
            responseButtons.Controls.Add(formatResponseButton);
            responseButtons.Controls.Add(copyResponseButton);
            responseButtons.Controls.Add(clearResponseButton);
            responseLayout.Controls.Add(responseButtons, 0, 1);

            _responseBox = new TextBox();
            _responseBox.Multiline = true;
            _responseBox.MaxLength = int.MaxValue;
            _responseBox.WordWrap = false;
            _responseBox.ScrollBars = ScrollBars.Both;
            _responseBox.Font = new Font("Consolas", 10F);
            _responseBox.BackColor = Color.White;
            _responseBox.ForeColor = TitleColor;
            _responseBox.BorderStyle = BorderStyle.FixedSingle;
            _responseBox.Dock = DockStyle.Fill;
            _responseBox.Margin = new Padding(0);
            AttachOutputMenu(_responseBox, true);
            responseLayout.Controls.Add(_responseBox, 0, 2);

            responsePage.Controls.Add(responseLayout);

            _tabs = tabs;
            _ocrPage = BuildOcrPage();

            tabs.TabPages.Add(urlPage);
            tabs.TabPages.Add(detailPage);
            tabs.TabPages.Add(_failurePage);
            tabs.TabPages.Add(responsePage);
            tabs.TabPages.Add(_ocrPage);
            root.Controls.Add(tabs, 0, 4);

            _statusLabel = new Label();
            _statusLabel.Text = "就绪";
            _statusLabel.ForeColor = MutedColor;
            _statusLabel.AutoSize = false;
            _statusLabel.Dock = DockStyle.Fill;
            _statusLabel.Height = 24;
            _statusLabel.TextAlign = ContentAlignment.MiddleLeft;
            _statusLabel.AutoEllipsis = true;
            _statusLabel.Margin = new Padding(0, 8, 0, 0);
            root.Controls.Add(_statusLabel, 0, 5);

            Label footnote = new Label();
            footnote.Text = "支持多条 curl、HAR（.har / JSON）、http/https/ws/wss、续行和 JSON 转义；长 Token 默认打码。";
            footnote.ForeColor = FaintColor;
            footnote.AutoSize = true;
            footnote.Margin = new Padding(0, 4, 0, 0);
            root.Controls.Add(footnote, 0, 6);
        }

        private static Label MakeSectionLabel(string text)
        {
            Label label = new Label();
            label.Text = text;
            label.ForeColor = LabelColor;
            label.AutoSize = true;
            label.Margin = new Padding(0, 0, 0, 6);
            return label;
        }

        private static Button MakeButton(string text, bool primary, int width)
        {
            Button button = new Button();
            button.Text = text;
            button.Width = width;
            button.Height = 34;
            button.FlatStyle = FlatStyle.Flat;
            button.Cursor = Cursors.Hand;
            button.Font = new Font("Microsoft YaHei UI", 9F);
            button.Margin = new Padding(0, 0, 8, 0);
            button.UseVisualStyleBackColor = false;
            button.FlatAppearance.BorderSize = primary ? 0 : 1;

            if (primary)
            {
                button.BackColor = PrimaryColor;
                button.ForeColor = Color.White;
                button.FlatAppearance.MouseOverBackColor = PrimaryHover;
                button.FlatAppearance.MouseDownBackColor = PrimaryHover;
            }
            else
            {
                button.BackColor = Color.White;
                button.ForeColor = LabelColor;
                button.FlatAppearance.BorderColor = LineColor;
                button.FlatAppearance.MouseOverBackColor = Color.FromArgb(240, 242, 245);
                button.FlatAppearance.MouseDownBackColor = Color.FromArgb(232, 235, 240);
            }
            return button;
        }

        private static Button MakeSmallButton(string text, int width)
        {
            Button button = MakeButton(text, false, width);
            button.Height = 30;
            button.Font = new Font("Microsoft YaHei UI", 8.5F);
            return button;
        }

        private TabPage BuildOcrPage()
        {
            TabPage page = new TabPage("截图识别");
            page.BackColor = Color.White;
            page.UseVisualStyleBackColor = false;
            page.Padding = new Padding(8);

            TableLayoutPanel layout = new TableLayoutPanel();
            layout.Dock = DockStyle.Fill;
            layout.ColumnCount = 1;
            layout.RowCount = 4;
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));

            Label hint = new Label();
            hint.Text = "微信 / QQ 截图后直接 Ctrl+V（中英文均可），或点「粘贴图片」「打开图片」；识别结果可一键复制给 AI。";
            hint.ForeColor = MutedColor;
            hint.AutoSize = false;
            hint.Height = 34;
            hint.Dock = DockStyle.Fill;
            hint.Margin = new Padding(0, 0, 0, 6);
            layout.Controls.Add(hint, 0, 0);

            FlowLayoutPanel buttons = new FlowLayoutPanel();
            buttons.FlowDirection = FlowDirection.LeftToRight;
            buttons.WrapContents = false;
            buttons.AutoSize = true;
            buttons.Dock = DockStyle.Fill;
            buttons.Margin = new Padding(0, 0, 0, 4);

            Button pasteImageButton = MakeSmallButton("粘贴图片", 96);
            pasteImageButton.Click += delegate { PasteImageFromClipboard(); };

            Button openImageButton = MakeSmallButton("打开图片", 96);
            openImageButton.Click += delegate { OpenImageFile(); };

            Button copyTextButton = MakeSmallButton("复制文字", 84);
            copyTextButton.Click += delegate { CopyOcrText(); };

            Button clearButton = MakeSmallButton("清空", 64);
            clearButton.Click += delegate { ClearOcr(); };

            buttons.Controls.Add(pasteImageButton);
            buttons.Controls.Add(openImageButton);
            buttons.Controls.Add(copyTextButton);
            buttons.Controls.Add(clearButton);
            layout.Controls.Add(buttons, 0, 1);

            _ocrStatusLabel = new Label();
            _ocrStatusLabel.Text = "等待图片。支持中英文截图，识别在本机完成，不会上传。";
            _ocrStatusLabel.ForeColor = MutedColor;
            _ocrStatusLabel.AutoSize = false;
            _ocrStatusLabel.Height = 22;
            _ocrStatusLabel.Dock = DockStyle.Fill;
            _ocrStatusLabel.AutoEllipsis = true;
            _ocrStatusLabel.Margin = new Padding(0, 0, 0, 6);
            layout.Controls.Add(_ocrStatusLabel, 0, 2);

            TableLayoutPanel content = new TableLayoutPanel();
            content.Dock = DockStyle.Fill;
            content.ColumnCount = 2;
            content.RowCount = 1;
            content.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 32F));
            content.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 68F));
            content.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
            content.Margin = new Padding(0);

            _ocrPreview = new PictureBox();
            _ocrPreview.Dock = DockStyle.Fill;
            _ocrPreview.SizeMode = PictureBoxSizeMode.Zoom;
            _ocrPreview.BackColor = Color.FromArgb(250, 251, 252);
            _ocrPreview.BorderStyle = BorderStyle.FixedSingle;
            _ocrPreview.Margin = new Padding(0, 0, 8, 0);
            _ocrPreview.AllowDrop = true;
            _ocrPreview.DragEnter += HandleDragEnter;
            _ocrPreview.DragDrop += HandleDragDrop;
            content.Controls.Add(_ocrPreview, 0, 0);

            _ocrBox = new TextBox();
            _ocrBox.Multiline = true;
            _ocrBox.ReadOnly = true;
            _ocrBox.WordWrap = true;
            _ocrBox.ScrollBars = ScrollBars.Vertical;
            _ocrBox.Font = new Font("Consolas", 10F);
            _ocrBox.BackColor = Color.White;
            _ocrBox.ForeColor = TitleColor;
            _ocrBox.BorderStyle = BorderStyle.FixedSingle;
            _ocrBox.Dock = DockStyle.Fill;
            _ocrBox.Margin = new Padding(0);
            AttachOutputMenu(_ocrBox, false);
            content.Controls.Add(_ocrBox, 1, 0);

            layout.Controls.Add(content, 0, 3);
            page.Controls.Add(layout);
            return page;
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (keyData == (Keys.Control | Keys.Enter))
            {
                _autoTimer.Stop();
                Analyze(false);
                return true;
            }

            // 剪贴板里是图片（例如微信截图）时，Ctrl+V 直接走 OCR；
            // 图片和文字同时存在时，只有在「截图识别」页签内才优先识别图片。
            if (keyData == (Keys.Control | Keys.V) && _ocrPage != null)
            {
                bool ocrTabActive = _tabs != null && _tabs.SelectedTab == _ocrPage;
                bool hasImage = false;
                try
                {
                    hasImage = Clipboard.ContainsImage();
                    if (hasImage && !ocrTabActive && Clipboard.ContainsText()) hasImage = false;
                }
                catch (ExternalException)
                {
                    // 剪贴板被其他程序占用，交给默认粘贴处理
                }
                catch (Exception)
                {
                }

                if (hasImage)
                {
                    Bitmap image = TryGetClipboardImage();
                    if (image != null)
                    {
                        StartOcr(image, "剪贴板图片");
                        return true;
                    }
                }
                else if (ocrTabActive)
                {
                    SetOcrStatus("剪贴板里没有图片。请先截图，再按 Ctrl+V 或点「粘贴图片」。", ErrorColor);
                    return true;
                }
            }

            // 结果框是只读的，这里显式兜底 Ctrl+A / Ctrl+C，避免个别环境里快捷键失效
            TextBox output = FocusedReadOnlyOutput();
            if (output != null && keyData == (Keys.Control | Keys.A))
            {
                output.SelectAll();
                SetStatus("已全选，Ctrl+C 复制。", MutedColor);
                return true;
            }
            if (output != null && keyData == (Keys.Control | Keys.C))
            {
                CopyBox(output, true);
                return true;
            }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        private TextBox FocusedReadOnlyOutput()
        {
            if (_resultBox != null && _resultBox.Focused) return _resultBox;
            if (_detailBox != null && _detailBox.Focused) return _detailBox;
            if (_failureBox != null && _failureBox.Focused) return _failureBox;
            if (_ocrBox != null && _ocrBox.Focused) return _ocrBox;
            return null;
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);

            if (!AllowDrop)
            {
                AllowDrop = true;
                DragEnter += HandleDragEnter;
                DragDrop += HandleDragDrop;
                _inputBox.AllowDrop = true;
                _inputBox.DragEnter += HandleDragEnter;
                _inputBox.DragDrop += HandleDragDrop;
            }

            try
            {
                string clip = GetClipboardText();
                if (!string.IsNullOrEmpty(clip) && LooksLikeInput(clip))
                {
                    _inputBox.Text = clip;
                }
            }
            catch (Exception)
            {
                // 剪贴板不可用时保持空白窗口
            }
        }

        private static bool LooksLikeInput(string text)
        {
            if (string.IsNullOrEmpty(text)) return false;
            if (HarParser.LooksLikeHar(text)) return true;
            if (RequestListParser.LooksLikeRequestList(text)) return true;
            return Regex.IsMatch(text, @"\bcurl(?:\.exe)?\b", RegexOptions.IgnoreCase);
        }

        private static void HandleDragEnter(object sender, DragEventArgs e)
        {
            e.Effect = (e.Data.GetDataPresent(DataFormats.FileDrop) || e.Data.GetDataPresent(DataFormats.Text))
                ? DragDropEffects.Copy
                : DragDropEffects.None;
        }

        private void HandleDragDrop(object sender, DragEventArgs e)
        {
            try
            {
                if (e.Data.GetDataPresent(DataFormats.FileDrop))
                {
                    string[] files = (string[])e.Data.GetData(DataFormats.FileDrop);
                    if (files != null && files.Length > 0)
                    {
                        if (IsImageFile(files[0]))
                        {
                            try
                            {
                                using (Image image = Image.FromFile(files[0]))
                                {
                                    StartOcr(new Bitmap(image), Path.GetFileName(files[0]));
                                }
                            }
                            catch (Exception ex)
                            {
                                SetOcrStatus("打开图片失败：" + ex.Message, ErrorColor);
                            }
                            return;
                        }

                        _inputBox.Text = File.ReadAllText(files[0], Encoding.UTF8);
                        SetStatus("已读取 " + Path.GetFileName(files[0]) + "，自动解析中…", MutedColor);
                    }
                }
                else if (e.Data.GetDataPresent(DataFormats.Text))
                {
                    _inputBox.Text = (string)e.Data.GetData(DataFormats.Text);
                }
            }
            catch (Exception ex)
            {
                SetStatus("读取拖入内容失败：" + ex.Message, ErrorColor);
            }
        }

        private void Analyze(bool auto)
        {
            if (_autoTimer == null || _maskSensitive == null || _failureBox == null) return;
            _autoTimer.Stop();

            string text = _inputBox.Text;
            if (string.IsNullOrWhiteSpace(text))
            {
                _resultBox.Text = "";
                _detailBox.Text = "";
                _failureBox.Text = "";
                _failurePage.Text = "失败请求";
                _lastSignature = "";
                SetStatus("就绪", MutedColor);
                return;
            }

            bool keepDuplicates = _keepDuplicates.Checked;
            bool mask = _maskSensitive.Checked;
            string signature = (keepDuplicates ? "1" : "0") + (mask ? "1" : "0") + "|" + text;
            if (auto && signature == _lastSignature) return;

            bool isHar = HarParser.LooksLikeHar(text);
            bool isList = !isHar && RequestListParser.LooksLikeRequestList(text);
            List<CurlRequest> requests;
            List<string> allUrls;

            try
            {
                if (isHar || isList)
                {
                    requests = isHar ? HarParser.Parse(text) : RequestListParser.Parse(text);
                    allUrls = new List<string>();
                    foreach (CurlRequest request in requests)
                    {
                        if (!string.IsNullOrEmpty(request.Url)) allUrls.Add(request.Url);
                    }
                }
                else
                {
                    allUrls = UrlExtractor.Extract(text);
                    requests = UrlExtractor.ParseRequests(text);
                }
            }
            catch (Exception ex)
            {
                string failureTitle = isHar
                    ? "HAR 解析失败"
                    : (isList ? "JSON 解析失败" : "解析失败");
                string failureHint = isHar
                    ? "如果是复制粘贴的 HAR，内容可能被截断或不是完整 JSON；建议点「打开 HAR 文件」，或把 .har 文件拖进窗口。"
                    : (isList
                        ? "如果是复制粘贴的请求列表 JSON，内容可能被截断或不是完整 JSON；建议把 .json 文件拖进窗口。"
                        : "请确认内容是完整的 curl 命令、HAR JSON 或请求列表 JSON。");

                _resultBox.Text = "";
                _failureBox.Text = "";
                _failurePage.Text = "失败请求";
                _detailBox.Text =
                    failureTitle + "\r\n\r\n" +
                    ex.Message + "\r\n\r\n" +
                    "已接收 " + text.Length + " 个字符。\r\n" + failureHint;
                _lastSignature = "";
                SetStatus(failureTitle + "（详情见第二个页签）", ErrorColor);
                return;
            }

            List<string> uniqueUrls = UrlExtractor.Unique(allUrls);
            List<string> urls = keepDuplicates ? allUrls : uniqueUrls;

            if (urls.Count == 0)
            {
                _resultBox.Text = "";
                _detailBox.Text = "";
                _failureBox.Text = "";
                _failurePage.Text = "失败请求";
                _lastSignature = signature;
                SetStatus(
                    isHar
                        ? "HAR 里没有请求记录。"
                        : (isList
                            ? "请求列表 JSON 里没有可用 URL。"
                            : "没有找到 URL，请确认内容里包含 curl 命令、HAR JSON 或完整链接。"),
                    ErrorColor);
                return;
            }

            List<string> displayUrls = new List<string>();
            foreach (string url in urls)
            {
                displayUrls.Add(mask ? Sensitive.MaskUrl(url) : url);
            }
            _resultBox.Text = string.Join("\r\n", displayUrls.ToArray());

            try
            {
                _detailBox.Text = UrlExtractor.FormatDetails(requests, allUrls, mask);
            }
            catch (Exception ex)
            {
                _detailBox.Text = "解析详情失败：" + ex.Message;
            }

            int failureCount = 0;
            for (int i = 0; i < requests.Count; i++)
            {
                if (requests[i].IsFailure()) failureCount++;
            }

            try
            {
                _failureBox.Text = UrlExtractor.FormatFailures(requests, mask);
            }
            catch (Exception ex)
            {
                _failureBox.Text = "解析失败请求失败：" + ex.Message;
            }

            _failurePage.Text = failureCount > 0
                ? "失败请求 (" + failureCount + ")"
                : "失败请求";

            string summary;
            if (requests.Count > 0)
            {
                summary = keepDuplicates
                    ? string.Format("来自 {0} 个请求，共 {1} 条 URL（含重复）", requests.Count, urls.Count)
                    : string.Format("来自 {0} 个请求，{1} 个不同 URL", requests.Count, uniqueUrls.Count);
            }
            else
            {
                summary = string.Format("提取到 {0} 个 URL", urls.Count);
            }

            if (isHar) summary = "HAR：" + summary;
            else if (isList) summary = "请求列表：" + summary;
            if (failureCount > 0) summary += string.Format("，失败 {0} 个", failureCount);
            if (mask) summary += "（长参数已隐藏）";

            bool websocketNoise =
                !keepDuplicates &&
                requests.Count > 1 &&
                uniqueUrls.Count == 1 &&
                IsWebSocketUrl(urls[0]);

            _lastSignature = signature;

            if (websocketNoise)
            {
                SetStatus(string.Format(
                    "{0} 条 WS 握手指向同一地址；要接口路径，请在 Network 面板切到 XHR 后重新复制",
                    requests.Count), MutedColor);
            }
            else
            {
                SetStatus(summary + "，已自动解析", failureCount > 0 ? ErrorColor : OkColor);
            }
        }

        private void PasteFromClipboard()
        {
            string clip = GetClipboardText();
            _inputBox.Text = clip;
            if (string.IsNullOrWhiteSpace(clip))
            {
                SetStatus("剪贴板里没有文本。", ErrorColor);
            }
            else
            {
                SetStatus("已粘贴，自动解析中…", MutedColor);
            }
        }

        private void OpenHarFile()
        {
            using (OpenFileDialog dialog = new OpenFileDialog())
            {
                dialog.Title = "打开 HAR / JSON 文件";
                dialog.Filter = "HAR / JSON (*.har;*.json)|*.har;*.json|所有文件 (*.*)|*.*";
                if (dialog.ShowDialog(this) != DialogResult.OK) return;

                try
                {
                    _inputBox.Text = File.ReadAllText(dialog.FileName, Encoding.UTF8);
                    SetStatus("已读取 " + Path.GetFileName(dialog.FileName) + "，自动解析中…", MutedColor);
                }
                catch (Exception ex)
                {
                    SetStatus("读取文件失败：" + ex.Message, ErrorColor);
                }
            }
        }

        private void ShowOcrPage(bool tryClipboardImage)
        {
            if (_tabs != null && _ocrPage != null) _tabs.SelectedTab = _ocrPage;
            if (!tryClipboardImage) return;

            if (_ocrBusy)
            {
                SetOcrStatus("正在识别上一张图片，请稍候。", MutedColor);
                return;
            }

            Bitmap image = TryGetClipboardImage();
            if (image != null)
            {
                StartOcr(image, "剪贴板图片");
                return;
            }
            SetOcrStatus("剪贴板里没有图片。截图后按 Ctrl+V，或点「打开图片」。", MutedColor);
        }

        private void PasteImageFromClipboard()
        {
            if (_ocrBusy)
            {
                SetOcrStatus("正在识别上一张图片，请稍候。", MutedColor);
                return;
            }

            Bitmap image = TryGetClipboardImage();
            if (image == null)
            {
                SetOcrStatus("剪贴板里没有图片。请先截图（微信 / QQ 截图会直接放进剪贴板）。", ErrorColor);
                return;
            }
            StartOcr(image, "剪贴板图片");
        }

        private void OpenImageFile()
        {
            if (_ocrBusy)
            {
                SetOcrStatus("正在识别上一张图片，请稍候。", MutedColor);
                return;
            }

            using (OpenFileDialog dialog = new OpenFileDialog())
            {
                dialog.Title = "打开截图";
                dialog.Filter = "图片 (*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.tif;*.tiff)|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.tif;*.tiff|所有文件 (*.*)|*.*";
                if (dialog.ShowDialog(this) != DialogResult.OK) return;

                try
                {
                    using (Image image = Image.FromFile(dialog.FileName))
                    {
                        StartOcr(new Bitmap(image), Path.GetFileName(dialog.FileName));
                    }
                }
                catch (Exception ex)
                {
                    SetOcrStatus("打开图片失败：" + ex.Message, ErrorColor);
                }
            }
        }

        private static bool IsImageFile(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            string extension = Path.GetExtension(path);
            if (string.IsNullOrEmpty(extension)) return false;
            extension = extension.ToLowerInvariant();
            return extension == ".png" || extension == ".jpg" || extension == ".jpeg" ||
                   extension == ".bmp" || extension == ".gif" ||
                   extension == ".tif" || extension == ".tiff";
        }

        private static Bitmap TryGetClipboardImage()
        {
            try
            {
                if (!Clipboard.ContainsImage()) return null;
                using (Image image = Clipboard.GetImage())
                {
                    if (image == null) return null;
                    // 剪贴板内容随时可能失效，先复制成独立位图再慢速识别
                    return new Bitmap(image);
                }
            }
            catch (ExternalException)
            {
                return null;
            }
            catch (Exception)
            {
                return null;
            }
        }

        private void StartOcr(Bitmap bitmap, string sourceLabel)
        {
            if (bitmap == null) return;
            if (_ocrPreview == null || _ocrBox == null)
            {
                bitmap.Dispose();
                return;
            }
            if (_ocrBusy)
            {
                bitmap.Dispose();
                SetOcrStatus("正在识别上一张图片，请稍候。", MutedColor);
                return;
            }

            if (_tabs != null && _ocrPage != null) _tabs.SelectedTab = _ocrPage;
            _ocrBusy = true;

            Image previous = _ocrPreview.Image;
            _ocrPreview.Image = bitmap;
            if (previous != null) previous.Dispose();

            _ocrBox.Text = "";
            SetOcrStatus("正在识别图片文字…（大截图可能需要几秒）", MutedColor);

            // OCR 比较慢，放到后台线程，界面不卡顿；工作线程用独立副本
            Bitmap work = new Bitmap(bitmap);
            ThreadPool.QueueUserWorkItem(delegate
            {
                string text;
                string error;
                bool ok = OcrRunner.TryRecognize(work, out text, out error);
                work.Dispose();

                try
                {
                    BeginInvoke(new MethodInvoker(delegate
                    {
                        _ocrBusy = false;
                        if (!ok)
                        {
                            _ocrBox.Text = "";
                            SetOcrStatus("识别失败：" + error, ErrorColor);
                            return;
                        }
                        if (string.IsNullOrWhiteSpace(text))
                        {
                            _ocrBox.Text = "";
                            SetOcrStatus("没有识别出文字。换一张更清晰、文字更大的截图试试。", ErrorColor);
                            return;
                        }

                        _ocrBox.Text = text;
                        SetOcrStatus(
                            string.Format("识别完成：{0}（{1} 个字符），可直接复制给 AI。", sourceLabel, text.Length),
                            OkColor);
                    }));
                }
                catch (InvalidOperationException)
                {
                    // 窗口已关闭
                }
            });
        }

        private void CopyOcrText()
        {
            if (string.IsNullOrWhiteSpace(_ocrBox.Text))
            {
                SetOcrStatus("还没有识别结果可复制。", ErrorColor);
                return;
            }

            try
            {
                SetClipboardText(_ocrBox.Text);
                SetOcrStatus("识别文字已复制到剪贴板。", OkColor);
            }
            catch (Exception ex)
            {
                SetOcrStatus(ex.Message, ErrorColor);
            }
        }

        private void ClearOcr()
        {
            if (_ocrBusy)
            {
                SetOcrStatus("正在识别，完成后再清空。", MutedColor);
                return;
            }

            _ocrBox.Text = "";
            Image previous = _ocrPreview.Image;
            _ocrPreview.Image = null;
            if (previous != null) previous.Dispose();
            SetOcrStatus("等待图片。支持中英文截图，识别在本机完成，不会上传。", MutedColor);
        }

        private void SetOcrStatus(string text, Color color)
        {
            if (_ocrStatusLabel == null) return;
            _ocrStatusLabel.Text = text;
            _ocrStatusLabel.ForeColor = color;
        }

        private void CopyResult()
        {
            if (string.IsNullOrWhiteSpace(_resultBox.Text))
            {
                SetStatus("还没有可复制的结果。", ErrorColor);
                return;
            }

            try
            {
                SetClipboardText(_resultBox.Text);
                SetStatus("URL 列表已复制到剪贴板。", OkColor);
            }
            catch (Exception ex)
            {
                SetStatus(ex.Message, ErrorColor);
            }
        }

        private void ClearAll()
        {
            _autoTimer.Stop();
            _lastSignature = "";
            _inputBox.Text = "";
            _resultBox.Text = "";
            _detailBox.Text = "";
            _failureBox.Text = "";
            _failurePage.Text = "失败请求";
            SetStatus("就绪", MutedColor);
            _inputBox.Focus();
        }

        private void ReadResponseFromClipboard()
        {
            string clip = GetClipboardText();
            _responseBox.Text = clip;
            if (string.IsNullOrWhiteSpace(clip))
            {
                SetStatus("剪贴板里没有文本。", ErrorColor);
            }
            else
            {
                SetStatus("已读取剪贴板内容，点击「格式化 JSON」。", MutedColor);
            }
        }

        private void FormatResponse()
        {
            string text = _responseBox.Text;
            if (string.IsNullOrWhiteSpace(text))
            {
                text = GetClipboardText();
                if (string.IsNullOrWhiteSpace(text))
                {
                    SetStatus("没有可格式化的内容，请先粘贴响应。", ErrorColor);
                    return;
                }
                _responseBox.Text = text;
            }

            string trimmed = text.Trim();
            bool looksJson =
                (trimmed.StartsWith("{") && trimmed.EndsWith("}")) ||
                (trimmed.StartsWith("[") && trimmed.EndsWith("]"));

            if (!looksJson)
            {
                SetStatus("这段内容不是 JSON 对象或数组，无法格式化。", ErrorColor);
                return;
            }

            try
            {
                _responseBox.Text = JsonPretty.Print(trimmed);
                SetStatus("已格式化 JSON 响应。", OkColor);
            }
            catch (Exception ex)
            {
                SetStatus("格式化失败：" + ex.Message, ErrorColor);
            }
        }

        private void CopyResponse()
        {
            if (string.IsNullOrWhiteSpace(_responseBox.Text))
            {
                SetStatus("响应内容为空。", ErrorColor);
                return;
            }

            try
            {
                SetClipboardText(_responseBox.Text);
                SetStatus("响应内容已复制到剪贴板。", OkColor);
            }
            catch (Exception ex)
            {
                SetStatus(ex.Message, ErrorColor);
            }
        }

        private void ClearResponse()
        {
            _responseBox.Text = "";
            SetStatus("响应内容已清空。", MutedColor);
        }

        private void AttachOutputMenu(TextBox box, bool editable)
        {
            box.ShortcutsEnabled = true;

            ContextMenuStrip menu = new ContextMenuStrip();
            menu.Font = new Font("Microsoft YaHei UI", 9F);

            ToolStripMenuItem selectAll = new ToolStripMenuItem("全选");
            selectAll.ShortcutKeyDisplayString = "Ctrl+A";
            selectAll.Click += delegate
            {
                box.Focus();
                box.SelectAll();
            };

            ToolStripMenuItem copy = new ToolStripMenuItem("复制");
            copy.ShortcutKeyDisplayString = "Ctrl+C";
            copy.Click += delegate { CopyBox(box, false); };

            ToolStripMenuItem copyAll = new ToolStripMenuItem("全选并复制");
            copyAll.Click += delegate
            {
                box.Focus();
                box.SelectAll();
                CopyBox(box, false);
            };

            menu.Items.Add(selectAll);
            menu.Items.Add(copy);
            menu.Items.Add(copyAll);

            if (editable)
            {
                menu.Items.Add(new ToolStripSeparator());

                ToolStripMenuItem paste = new ToolStripMenuItem("粘贴");
                paste.Click += delegate
                {
                    string clip = GetClipboardText();
                    if (clip.Length == 0) return;
                    box.Focus();
                    box.SelectedText = clip;
                };

                ToolStripMenuItem clear = new ToolStripMenuItem("清空");
                clear.Click += delegate { box.Text = ""; };

                menu.Items.Add(paste);
                menu.Items.Add(clear);
            }

            box.ContextMenuStrip = menu;
        }

        private void CopyBox(TextBox box, bool allWhenNoSelection)
        {
            string text = box.SelectedText;
            if (string.IsNullOrEmpty(text) && allWhenNoSelection) text = box.Text;
            if (string.IsNullOrEmpty(text))
            {
                SetStatus("没有可复制的内容。", MutedColor);
                return;
            }

            try
            {
                SetClipboardText(text);
                SetStatus("已复制 " + text.Length + " 个字符。", OkColor);
            }
            catch (Exception ex)
            {
                SetStatus(ex.Message, ErrorColor);
            }
        }

        private void SetStatus(string text, Color color)
        {
            _statusLabel.Text = text;
            _statusLabel.ForeColor = color;
        }

        private static bool IsWebSocketUrl(string url)
        {
            return url.StartsWith("ws://", StringComparison.OrdinalIgnoreCase) ||
                   url.StartsWith("wss://", StringComparison.OrdinalIgnoreCase);
        }

        private static string GetClipboardText()
        {
            try
            {
                if (Clipboard.ContainsText()) return Clipboard.GetText();
            }
            catch (ExternalException)
            {
                // 剪贴板被其他程序占用
            }
            catch (Exception)
            {
            }
            return "";
        }

        private static void SetClipboardText(string text)
        {
            Exception last = null;
            for (int i = 0; i < 3; i++)
            {
                try
                {
                    Clipboard.SetText(text);
                    return;
                }
                catch (Exception ex)
                {
                    last = ex;
                    Thread.Sleep(120);
                }
            }
            throw new InvalidOperationException("写入剪贴板失败（可能被其他程序占用），请点「复制」重试。", last);
        }
    }

    internal sealed class CurlRequest
    {
        public string Url;
        public string Method;
        public string Body;
        public readonly List<KeyValuePair<string, string>> QueryParams = new List<KeyValuePair<string, string>>();
        public readonly List<KeyValuePair<string, string>> Headers = new List<KeyValuePair<string, string>>();

        // HAR 专有信息；cURL 解析时保持默认值
        public string Source = "curl";
        public int Status = -1;                 // -1 = 未知，0 = 无响应/被拦截
        public string StatusText = "";
        public string ResponseBody = "";
        public readonly List<KeyValuePair<string, string>> ResponseHeaders = new List<KeyValuePair<string, string>>();
        public string Error = "";
        public string ResourceType = "";
        public string Started = "";
        public double TimeMs;

        public bool HasResponse()
        {
            return Source == "har" || Source == "list";
        }

        public bool IsFailure()
        {
            if (!HasResponse()) return false;
            if (IsWebSocketUrl()) return false;
            if (!string.IsNullOrEmpty(Error)) return true;
            if (Status == 0) return true;
            return Status >= 400;
        }

        private bool IsWebSocketUrl()
        {
            if (string.IsNullOrEmpty(Url)) return false;
            return Url.StartsWith("ws://", StringComparison.OrdinalIgnoreCase) ||
                   Url.StartsWith("wss://", StringComparison.OrdinalIgnoreCase);
        }
    }

    // 调用 Windows 自带的 OCR 引擎（Windows.Media.Ocr），在本机完成，不需要联网，也不用装额外组件。
    // 通过 PowerShell 5.1 的 WinRT 投影调用，避免额外依赖 Windows SDK / Windows.winmd。
    internal static class OcrRunner
    {
        private const int MaxScaledDimension = 9000;
        private const long MaxScaledPixels = 40000000L;
        private const int TimeoutMilliseconds = 60000;

        private static readonly Regex HanPattern =
            new Regex(@"[㐀-䶿一-鿿]", RegexOptions.Compiled);

        private const string OcrScript = @"$ErrorActionPreference = 'Stop'
try {
    Add-Type -AssemblyName System.Runtime.WindowsRuntime
    $asTaskGeneric = ([System.WindowsRuntimeSystemExtensions].GetMethods() | Where-Object { $_.Name -eq 'AsTask' -and $_.GetParameters().Count -eq 1 -and $_.GetParameters()[0].ParameterType.Name -like 'IAsyncOperation?1' })
    function Await($WinRtTask, $ResultType) {
        $asTask = $asTaskGeneric.MakeGenericMethod($ResultType)
        $netTask = $asTask.Invoke($null, @($WinRtTask))
        $netTask.Wait(-1) | Out-Null
        $netTask.Result
    }
    [Windows.Storage.StorageFile,Windows.Storage,ContentType=WindowsRuntime] | Out-Null
    [Windows.Graphics.Imaging.BitmapDecoder,Windows.Graphics,ContentType=WindowsRuntime] | Out-Null
    [Windows.Media.Ocr.OcrEngine,Windows.Media,ContentType=WindowsRuntime] | Out-Null
    [Windows.Globalization.Language,Windows.Globalization,ContentType=WindowsRuntime] | Out-Null
    $engine = [Windows.Media.Ocr.OcrEngine]::TryCreateFromUserProfileLanguages()
    if ($null -eq $engine) { $engine = [Windows.Media.Ocr.OcrEngine]::TryCreateFromLanguage([Windows.Globalization.Language]::new('zh-Hans-CN')) }
    if ($null -eq $engine) { $engine = [Windows.Media.Ocr.OcrEngine]::TryCreateFromLanguage([Windows.Globalization.Language]::new('en-US')) }
    if ($null -eq $engine) { throw '本机没有可用的 Windows OCR 语言包（设置 → 时间和语言 → 语言 → 添加中文/英文语言包）' }
    $file = Await ([Windows.Storage.StorageFile]::GetFileFromPathAsync($env:CURL2URL_OCR_IMG)) ([Windows.Storage.StorageFile])
    $stream = Await ($file.OpenAsync([Windows.Storage.FileAccessMode]::Read)) ([Windows.Storage.Streams.IRandomAccessStream])
    $decoder = Await ([Windows.Graphics.Imaging.BitmapDecoder]::CreateAsync($stream)) ([Windows.Graphics.Imaging.BitmapDecoder])
    $bitmap = Await ($decoder.GetSoftwareBitmapAsync()) ([Windows.Graphics.Imaging.SoftwareBitmap])
    $result = Await ($engine.RecognizeAsync($bitmap)) ([Windows.Media.Ocr.OcrResult])
    $lines = @($result.Lines | ForEach-Object { $_.Text })
    [System.IO.File]::WriteAllText($env:CURL2URL_OCR_OUT, ($lines -join [Environment]::NewLine), [System.Text.UTF8Encoding]::new($false))
}
catch {
    $parts = @()
    $ex = $_.Exception
    while ($null -ne $ex) {
        if (-not [string]::IsNullOrWhiteSpace($ex.Message)) { $parts += $ex.Message }
        $ex = $ex.InnerException
    }
    [System.IO.File]::WriteAllText($env:CURL2URL_OCR_ERR, ($parts -join ' -> '), [System.Text.UTF8Encoding]::new($false))
    exit 1
}
";

        public static bool TryRecognize(Bitmap source, out string text, out string error)
        {
            text = "";
            error = "";
            string imagePath = null;
            string outPath = null;
            string errPath = null;

            try
            {
                imagePath = TempPath(".png");
                outPath = TempPath(".txt");
                errPath = TempPath(".err.txt");

                using (Bitmap prepared = Prepare(source))
                {
                    prepared.Save(imagePath, ImageFormat.Png);
                }

                string stderr;
                int exitCode = RunScript(imagePath, outPath, errPath, out stderr);

                if (File.Exists(errPath))
                {
                    error = File.ReadAllText(errPath, Encoding.UTF8).Trim();
                }

                if (string.IsNullOrEmpty(error))
                {
                    if (exitCode != 0)
                    {
                        error = FirstUsefulLine(stderr);
                        if (string.IsNullOrEmpty(error))
                        {
                            error = "OCR 引擎异常退出（退出码 " + exitCode + "）。";
                        }
                    }
                    else if (!File.Exists(outPath))
                    {
                        error = "OCR 引擎没有返回结果。";
                    }
                }

                if (!string.IsNullOrEmpty(error)) return false;

                text = NormalizeOcrText(File.ReadAllText(outPath, Encoding.UTF8));
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
            finally
            {
                TryDelete(imagePath);
                TryDelete(outPath);
                TryDelete(errPath);
            }
        }

        // Windows OCR 对 12px 左右的小字识别很差，按图片宽度放大 2~3 倍后效果明显更好；
        // 大图保持原样，同时限制总像素，避免超大截图吃掉内存。
        private static Bitmap Prepare(Bitmap source)
        {
            int width = source.Width;
            int height = source.Height;
            int maxDimension = Math.Max(width, height);
            if (width <= 0 || height <= 0) throw new InvalidOperationException("图片尺寸无效。");

            double scale;
            if (width < 1000 && maxDimension * 3 <= MaxScaledDimension)
            {
                scale = 3;
            }
            else if (maxDimension * 2 <= MaxScaledDimension)
            {
                scale = 2;
            }
            else
            {
                scale = Math.Min(1.0, (double)MaxScaledDimension / maxDimension);
            }

            double pixels = (width * scale) * (height * scale);
            if (pixels > MaxScaledPixels)
            {
                scale *= Math.Sqrt(MaxScaledPixels / pixels);
            }

            int scaledWidth = Math.Max(1, (int)Math.Round(width * scale));
            int scaledHeight = Math.Max(1, (int)Math.Round(height * scale));

            Bitmap result = new Bitmap(scaledWidth, scaledHeight, PixelFormat.Format24bppRgb);
            try
            {
                using (Graphics graphics = Graphics.FromImage(result))
                {
                    graphics.CompositingQuality = CompositingQuality.HighQuality;
                    graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    graphics.SmoothingMode = SmoothingMode.HighQuality;
                    // 透明截图（如部分手机截图）铺白底，否则透明区域会变成黑块
                    graphics.Clear(Color.White);
                    graphics.DrawImage(source, new Rectangle(0, 0, scaledWidth, scaledHeight));
                }
                return result;
            }
            catch (Exception)
            {
                result.Dispose();
                throw;
            }
        }

        private static int RunScript(string imagePath, string outPath, string errPath, out string stderr)
        {
            string powershell = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.System),
                "WindowsPowerShell", "v1.0", "powershell.exe");
            if (!File.Exists(powershell)) powershell = "powershell.exe";

            ProcessStartInfo info = new ProcessStartInfo();
            info.FileName = powershell;
            info.Arguments = "-NoProfile -NonInteractive -EncodedCommand " +
                             Convert.ToBase64String(Encoding.Unicode.GetBytes(OcrScript));
            info.UseShellExecute = false;
            info.CreateNoWindow = true;
            info.RedirectStandardOutput = true;
            info.RedirectStandardError = true;
            info.StandardOutputEncoding = Encoding.UTF8;
            info.StandardErrorEncoding = Encoding.UTF8;
            info.EnvironmentVariables["CURL2URL_OCR_IMG"] = imagePath;
            info.EnvironmentVariables["CURL2URL_OCR_OUT"] = outPath;
            info.EnvironmentVariables["CURL2URL_OCR_ERR"] = errPath;

            using (Process process = new Process())
            {
                process.StartInfo = info;
                process.Start();

                Task<string> outputTask = process.StandardOutput.ReadToEndAsync();
                Task<string> errorTask = process.StandardError.ReadToEndAsync();

                if (!process.WaitForExit(TimeoutMilliseconds))
                {
                    try
                    {
                        process.Kill();
                    }
                    catch (Exception)
                    {
                    }
                    stderr = "识别超时（超过 60 秒），可以换一张更小、更清晰的截图。";
                    return -1;
                }

                stderr = errorTask.Result;
                string stdout = outputTask.Result;
                if (string.IsNullOrEmpty(stderr)) stderr = stdout;
                return process.ExitCode;
            }
        }

        // Windows OCR 会输出「系 统 繁 忙」这种字间空格，这里只去掉相邻中文 / 全角标点之间的空格，
        // 保留行与行、英文单词之间的空格。
        internal static string NormalizeOcrText(string text)
        {
            if (string.IsNullOrEmpty(text)) return "";

            text = text.Replace("\r\n", "\n").Replace('\r', '\n');
            text = Regex.Replace(
                text,
                @"(?<=[\u3000-\u303F\u3400-\u4DBF\u4E00-\u9FFF\uFF00-\uFFEF])[ \t\u00A0\u3000]+(?=[\u3000-\u303F\u3400-\u4DBF\u4E00-\u9FFF\uFF00-\uFFEF])",
                "");

            string[] lines = text.Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i].TrimEnd();
                // 纯英文 / 代码行里，中文 OCR 引擎偶尔会把 : ( ) 之类识别成全角；
                // 这里转回半角并去掉标点前的空格，URL、状态码、报错原文更接近截图。
                if (line.Length > 0 && !HanPattern.IsMatch(line)) line = NormalizeLatinLine(line);
                lines[i] = line;
            }
            return string.Join(Environment.NewLine, lines).Trim();
        }

        private static string NormalizeLatinLine(string line)
        {
            StringBuilder builder = new StringBuilder(line.Length);
            for (int i = 0; i < line.Length; i++)
            {
                char c = line[i];
                if (c >= '！' && c <= '～')
                {
                    // 全角 ASCII（！到 ～）转半角
                    builder.Append((char)(c - 0xFEE0));
                }
                else if (c == '　')
                {
                    builder.Append(' ');
                }
                else
                {
                    builder.Append(c);
                }
            }

            string result = builder.ToString();
            return Regex.Replace(result, @" +([,.;:!?)\]])", "$1");
        }

        private static string TempPath(string suffix)
        {
            return Path.Combine(Path.GetTempPath(), "curl2url-ocr-" + Guid.NewGuid().ToString("N") + suffix);
        }

        private static string FirstUsefulLine(string text)
        {
            if (string.IsNullOrEmpty(text)) return "";
            string[] lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            foreach (string line in lines)
            {
                string trimmed = line.Trim();
                if (trimmed.Length > 0 && trimmed.IndexOf("CLIXML", StringComparison.OrdinalIgnoreCase) < 0)
                {
                    return trimmed;
                }
            }
            return "";
        }

        private static void TryDelete(string path)
        {
            if (string.IsNullOrEmpty(path)) return;
            try
            {
                if (File.Exists(path)) File.Delete(path);
            }
            catch (Exception)
            {
                // 临时文件删不掉不影响结果
            }
        }
    }

    internal static class UrlExtractor
    {
        // 只把命令行里的 curl 当命令开头：URL 路径 / 参数里出现 "curl" 不能切分
        // 例如 https://host/api/only-curl 或 ?q=curl
        private static readonly Regex CurlPattern =
            new Regex(@"(?<![A-Za-z0-9_./\\:?&=%-])curl(?:\.exe)?\b", RegexOptions.IgnoreCase);

        private static readonly Regex UrlTokenPattern =
            new Regex(@"^(?:https?|wss?)://[^\s]+$", RegexOptions.IgnoreCase);

        private static readonly Regex AnyUrlPattern =
            new Regex(@"(?:https?|wss?)://[^\s'""<>\\)]+", RegexOptions.IgnoreCase);

        public static List<string> Extract(string text)
        {
            string source = Normalize(text);
            List<string> blocks = GetCurlBlocks(source);
            List<string> urls = new List<string>();

            // 不是 curl 文本，就按普通 URL 列表处理
            if (blocks.Count == 0)
            {
                foreach (Match match in AnyUrlPattern.Matches(source))
                {
                    urls.Add(match.Value);
                }
                return urls;
            }

            foreach (string block in blocks)
            {
                string url = PickUrlFromBlock(block);
                if (!string.IsNullOrEmpty(url)) urls.Add(url);
            }
            return urls;
        }

        public static List<string> Unique(List<string> urls)
        {
            HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal);
            List<string> result = new List<string>();
            foreach (string url in urls)
            {
                if (seen.Add(url)) result.Add(url);
            }
            return result;
        }

        public static List<CurlRequest> ParseRequests(string text)
        {
            string source = Normalize(text);
            List<CurlRequest> requests = new List<CurlRequest>();

            foreach (string block in GetCurlBlocks(source))
            {
                List<string> tokens = SplitTokens(block);
                CurlRequest request = new CurlRequest();
                List<string> bodyParts = new List<string>();

                for (int i = 0; i < tokens.Count; i++)
                {
                    string token = tokens[i];
                    string next = (i + 1 < tokens.Count) ? tokens[i + 1] : null;

                    if ((token == "-X" || token == "--request") && next != null)
                    {
                        request.Method = next.ToUpperInvariant();
                        i++;
                    }
                    else if ((token == "-H" || token == "--header") && next != null)
                    {
                        int colon = next.IndexOf(':');
                        if (colon > 0)
                        {
                            string name = next.Substring(0, colon).Trim();
                            string value = next.Substring(colon + 1).Trim();
                            request.Headers.Add(new KeyValuePair<string, string>(name, value));
                        }
                        i++;
                    }
                    else if ((token == "-d" || token == "--data" || token == "--data-raw" ||
                              token == "--data-binary" || token == "--data-urlencode") && next != null)
                    {
                        bodyParts.Add(next);
                        i++;
                    }
                    else if ((token == "--url" || token == "-url") && next != null)
                    {
                        if (request.Url == null && UrlTokenPattern.IsMatch(next))
                        {
                            request.Url = next;
                        }
                        i++;
                    }
                    else if (request.Url == null && i > 0 && UrlTokenPattern.IsMatch(token))
                    {
                        request.Url = token;
                    }
                }

                if (request.Url == null) continue;
                if (request.Method == null)
                {
                    request.Method = bodyParts.Count > 0 ? "POST" : "GET";
                }
                request.Body = string.Join("&", bodyParts.ToArray());
                request.QueryParams.AddRange(ParseQuery(request.Url));
                requests.Add(request);
            }

            return requests;
        }

        public static List<string> ExtractAny(string text)
        {
            if (HarParser.LooksLikeHar(text) || RequestListParser.LooksLikeRequestList(text))
            {
                List<CurlRequest> requests = HarParser.LooksLikeHar(text)
                    ? HarParser.Parse(text)
                    : RequestListParser.Parse(text);
                List<string> urls = new List<string>();
                foreach (CurlRequest request in requests)
                {
                    if (!string.IsNullOrEmpty(request.Url)) urls.Add(request.Url);
                }
                return urls;
            }
            return Extract(text);
        }

        public static List<CurlRequest> ParseAny(string text)
        {
            if (HarParser.LooksLikeHar(text)) return HarParser.Parse(text);
            if (RequestListParser.LooksLikeRequestList(text)) return RequestListParser.Parse(text);
            return ParseRequests(text);
        }

        public static string FormatDetails(List<CurlRequest> requests, List<string> urls, bool mask)
        {
            StringBuilder sb = new StringBuilder();

            if (requests.Count == 0)
            {
                sb.AppendLine("未识别到请求。");
                sb.AppendLine();
                sb.AppendLine("提取到的 URL：");
                if (urls.Count == 0)
                {
                    sb.AppendLine("  （无）");
                }
                else
                {
                    foreach (string url in urls)
                    {
                        sb.AppendLine("  " + (mask ? Sensitive.MaskUrl(url) : url));
                    }
                }
                return sb.ToString().TrimEnd();
            }

            string sourceLabel = "";
            if (requests[0].Source == "har") sourceLabel = "HAR：";
            else if (requests[0].Source == "list") sourceLabel = "请求列表：";

            sb.AppendLine(string.Format(
                "{0}共 {1} 个请求，{2} 个不同 URL。",
                sourceLabel,
                requests.Count,
                Unique(urls).Count));
            sb.AppendLine();

            for (int i = 0; i < requests.Count; i++)
            {
                if (requests.Count > 1) sb.AppendLine(string.Format("请求 #{0}", i + 1));
                AppendRequestDetails(sb, requests[i], mask);
                sb.AppendLine();
            }

            return sb.ToString().TrimEnd();
        }

        public static string FormatFailures(List<CurlRequest> requests, bool mask)
        {
            StringBuilder sb = new StringBuilder();
            int withResponse = 0;
            List<CurlRequest> failures = new List<CurlRequest>();

            foreach (CurlRequest request in requests)
            {
                if (request.HasResponse()) withResponse++;
                if (request.IsFailure()) failures.Add(request);
            }

            if (withResponse == 0)
            {
                sb.AppendLine("当前内容只有请求、没有响应状态（cURL 不包含响应），无法判断哪些请求失败。");
                sb.AppendLine();
                sb.AppendLine("识别错误请求的方法：");
                sb.AppendLine("  1. 开发者工具 Network 面板右键 → Save all as HAR");
                sb.AppendLine("  2. 把 .har 文件拖进窗口，或点「打开 HAR 文件」");
                return sb.ToString().TrimEnd();
            }

            if (failures.Count == 0)
            {
                sb.AppendLine(string.Format("共 {0} 个带响应的请求，没有失败请求（状态码均为 2xx/3xx）。", withResponse));
                return sb.ToString().TrimEnd();
            }

            sb.AppendLine(string.Format("失败请求 {0} 个 / 共 {1} 个带响应的请求。", failures.Count, withResponse));
            sb.AppendLine();

            for (int i = 0; i < failures.Count; i++)
            {
                CurlRequest request = failures[i];
                sb.AppendLine(new string('=', 72));
                sb.AppendLine(string.Format("#{0}  {1}  {2}  {3}",
                    i + 1,
                    StatusText(request),
                    request.Method,
                    mask ? Sensitive.MaskUrl(request.Url) : request.Url));
                sb.AppendLine();

                AppendRequestDetails(sb, request, mask);
                sb.AppendLine();

                sb.AppendLine("  响应头:");
                AppendHeaderBlock(sb, request.ResponseHeaders, mask, true);

                sb.AppendLine("  响应体:");
                AppendBody(sb, request.ResponseBody, mask, "    ");
                sb.AppendLine();
            }

            return sb.ToString().TrimEnd();
        }

        private static void AppendRequestDetails(StringBuilder sb, CurlRequest request, bool mask)
        {
            sb.AppendLine("  URL: " + (mask ? Sensitive.MaskUrl(request.Url) : request.Url));
            sb.AppendLine("  方法: " + request.Method);

            if (request.HasResponse())
            {
                sb.AppendLine("  状态: " + StatusText(request));
                if (!string.IsNullOrEmpty(request.ResourceType))
                    sb.AppendLine("  类型: " + request.ResourceType);
                if (!string.IsNullOrEmpty(request.Started))
                    sb.AppendLine("  时间: " + request.Started);
            }

            sb.AppendLine("  查询参数:");
            if (request.QueryParams.Count == 0)
            {
                sb.AppendLine("    （无）");
            }
            else
            {
                foreach (KeyValuePair<string, string> pair in request.QueryParams)
                {
                    string value = mask ? Sensitive.MaskValue(pair.Key, pair.Value) : pair.Value;
                    sb.AppendLine("    " + pair.Key + " = " + value);
                }
            }

            sb.AppendLine("  请求头:");
            AppendHeaderBlock(sb, request.Headers, mask, false);

            sb.AppendLine("  请求体:");
            AppendBody(sb, request.Body, mask, "    ");
        }

        private static void AppendHeaderBlock(
            StringBuilder sb,
            List<KeyValuePair<string, string>> headers,
            bool mask,
            bool response)
        {
            int hidden = 0;
            bool wrote = false;

            foreach (KeyValuePair<string, string> pair in headers)
            {
                bool noise = response
                    ? Sensitive.IsNoiseResponseHeader(pair.Key)
                    : Sensitive.IsNoiseRequestHeader(pair.Key);
                if (noise)
                {
                    hidden++;
                    continue;
                }

                string value = mask ? Sensitive.MaskHeader(pair.Key, pair.Value) : pair.Value;
                sb.AppendLine("    " + pair.Key + ": " + value);
                wrote = true;
            }

            if (!wrote) sb.AppendLine("    （无）");
            if (hidden > 0)
            {
                sb.AppendLine("    （已隐藏 " + hidden + " 个" + (response ? "通用响应头" : "浏览器通用头") + "）");
            }
        }

        private static void AppendBody(StringBuilder sb, string body, bool mask, string indent)
        {
            if (string.IsNullOrEmpty(body))
            {
                sb.AppendLine(indent + "（无）");
                return;
            }

            string prepared = body;
            string trimmed = prepared.Trim();
            if ((trimmed.StartsWith("{") && trimmed.EndsWith("}")) ||
                (trimmed.StartsWith("[") && trimmed.EndsWith("]")))
            {
                prepared = JsonPretty.Print(trimmed);
            }
            if (mask) prepared = Sensitive.MaskBody(prepared);

            string[] lines = prepared.Replace("\r\n", "\n").Split('\n');
            foreach (string line in lines)
            {
                sb.AppendLine(indent + line);
            }
        }

        private static string StatusText(CurlRequest request)
        {
            if (!string.IsNullOrEmpty(request.Error)) return "失败：" + request.Error;
            if (!request.HasResponse()) return "未知（cURL 不含响应）";
            if (request.Status < 0) return "未知";
            if (request.Status == 0) return "失败（无响应）";

            StringBuilder text = new StringBuilder();
            text.Append(request.Status);
            if (!string.IsNullOrEmpty(request.StatusText)) text.Append(' ').Append(request.StatusText);
            if (request.TimeMs > 0) text.AppendFormat("  {0:0} ms", request.TimeMs);
            return text.ToString();
        }

        internal static List<KeyValuePair<string, string>> ParseQueryPairs(string url)
        {
            return ParseQuery(url);
        }

        private static List<KeyValuePair<string, string>> ParseQuery(string url)
        {
            List<KeyValuePair<string, string>> result = new List<KeyValuePair<string, string>>();
            int question = url.IndexOf('?');
            if (question < 0) return result;

            string query = url.Substring(question + 1);
            int hash = query.IndexOf('#');
            if (hash >= 0) query = query.Substring(0, hash);
            if (query.Length == 0) return result;

            foreach (string part in query.Split('&'))
            {
                if (part.Length == 0) continue;
                int equals = part.IndexOf('=');
                if (equals >= 0)
                {
                    result.Add(new KeyValuePair<string, string>(
                        part.Substring(0, equals), part.Substring(equals + 1)));
                }
                else
                {
                    result.Add(new KeyValuePair<string, string>(part, ""));
                }
            }
            return result;
        }

        private static List<string> GetCurlBlocks(string source)
        {
            List<int> starts = new List<int>();
            foreach (Match match in CurlPattern.Matches(source))
            {
                starts.Add(match.Index);
            }

            List<string> blocks = new List<string>();
            for (int i = 0; i < starts.Count; i++)
            {
                int end = (i + 1 < starts.Count) ? starts[i + 1] : source.Length;
                blocks.Add(source.Substring(starts[i], end - starts[i]));
            }
            return blocks;
        }

        private static string Normalize(string text)
        {
            if (text == null) return string.Empty;
            if (text.Length > 0 && text[0] == '\uFEFF') text = text.Substring(1);

            text = Regex.Replace(text, @"\\u0026", "&", RegexOptions.IgnoreCase);
            text = Regex.Replace(text, @"\\u003d", "=", RegexOptions.IgnoreCase);
            text = text.Replace("\\/", "/");
            text = Regex.Replace(text, "&amp;", "&", RegexOptions.IgnoreCase);
            text = Regex.Replace(text, "\\\\\r?\n", " "); // bash 续行: \ + 换行
            text = Regex.Replace(text, "\\^\r?\n", " ");   // cmd 续行: ^ + 换行
            text = Regex.Replace(text, "`\r?\n", " ");     // PowerShell 续行: ` + 换行
            return text;
        }

        private static string PickUrlFromBlock(string block)
        {
            List<string> tokens = SplitTokens(block);

            int flagIndex = tokens.FindIndex(delegate(string token)
            {
                return token == "--url" || token == "-url";
            });
            if (flagIndex >= 0 && flagIndex + 1 < tokens.Count && UrlTokenPattern.IsMatch(tokens[flagIndex + 1]))
            {
                return tokens[flagIndex + 1];
            }

            for (int i = 1; i < tokens.Count; i++)
            {
                if (UrlTokenPattern.IsMatch(tokens[i])) return tokens[i];
            }
            return null;
        }

        private static List<string> SplitTokens(string block)
        {
            List<string> tokens = new List<string>();
            StringBuilder current = new StringBuilder();
            char quote = '\0';

            for (int i = 0; i < block.Length; i++)
            {
                char ch = block[i];

                if (quote != '\0')
                {
                    if (ch == quote)
                    {
                        quote = '\0';
                        continue;
                    }
                    if (ch == '\\' && quote == '"' && i + 1 < block.Length)
                    {
                        current.Append(block[++i]);
                        continue;
                    }
                    current.Append(ch);
                    continue;
                }

                if (ch == '"' || ch == '\'')
                {
                    quote = ch;
                    continue;
                }

                if (char.IsWhiteSpace(ch))
                {
                    if (current.Length > 0)
                    {
                        tokens.Add(current.ToString());
                        current.Length = 0;
                    }
                    continue;
                }

                current.Append(ch);
            }

            if (current.Length > 0) tokens.Add(current.ToString());
            return tokens;
        }
    }

    internal static class HarParser
    {
        public static bool LooksLikeHar(string text)
        {
            if (string.IsNullOrEmpty(text)) return false;

            int start = 0;
            while (start < text.Length && (char.IsWhiteSpace(text[start]) || text[start] == '﻿')) start++;
            if (start >= text.Length || text[start] != '{') return false;

            // 只看开头一段，避免对超大文件做全量扫描
            int length = Math.Min(text.Length - start, 200000);
            string head = text.Substring(start, length);
            return head.IndexOf("\"log\"", StringComparison.Ordinal) >= 0 &&
                   head.IndexOf("\"entries\"", StringComparison.Ordinal) >= 0;
        }

        public static List<CurlRequest> Parse(string text)
        {
            // 有些编辑器保存的 HAR 带 BOM，JavaScriptSerializer 会把它当成非法字符
            if (text.Length > 0 && text[0] == '﻿') text = text.Substring(1);

            JavaScriptSerializer serializer = new JavaScriptSerializer();
            serializer.MaxJsonLength = int.MaxValue;

            object rootObject = serializer.DeserializeObject(text);
            Dictionary<string, object> root = rootObject as Dictionary<string, object>;
            if (root == null) throw new InvalidOperationException("HAR 根节点不是 JSON 对象。");

            Dictionary<string, object> log = GetDict(root, "log");
            if (log == null) throw new InvalidOperationException("HAR 缺少 log 节点。");

            object[] entries = GetArray(log, "entries");
            if (entries == null) throw new InvalidOperationException("HAR 缺少 log.entries 数组。");

            List<CurlRequest> requests = new List<CurlRequest>();
            foreach (object entryObject in entries)
            {
                Dictionary<string, object> entry = entryObject as Dictionary<string, object>;
                if (entry == null) continue;
                CurlRequest request = ParseEntry(entry);
                if (request != null) requests.Add(request);
            }
            return requests;
        }

        private static CurlRequest ParseEntry(Dictionary<string, object> entry)
        {
            Dictionary<string, object> requestData = GetDict(entry, "request");
            if (requestData == null) return null;

            Dictionary<string, object> responseData = GetDict(entry, "response");
            CurlRequest request = new CurlRequest();
            request.Source = "har";
            request.Url = Str(requestData, "url");
            request.Method = Str(requestData, "method");
            if (string.IsNullOrEmpty(request.Method)) request.Method = "GET";
            request.Started = Str(entry, "startedDateTime");
            request.TimeMs = Num(entry, "time");
            request.ResourceType = Str(entry, "_resourceType");

            object[] query = GetArray(requestData, "queryString");
            if (query != null && query.Length > 0)
            {
                foreach (object queryObject in query)
                {
                    Dictionary<string, object> item = queryObject as Dictionary<string, object>;
                    if (item == null) continue;
                    request.QueryParams.Add(new KeyValuePair<string, string>(
                        Str(item, "name"), Str(item, "value")));
                }
            }
            else if (!string.IsNullOrEmpty(request.Url))
            {
                request.QueryParams.AddRange(UrlExtractor.ParseQueryPairs(request.Url));
            }

            object[] headers = GetArray(requestData, "headers");
            if (headers != null)
            {
                foreach (object headerObject in headers)
                {
                    Dictionary<string, object> item = headerObject as Dictionary<string, object>;
                    if (item == null) continue;
                    request.Headers.Add(new KeyValuePair<string, string>(
                        Str(item, "name"), Str(item, "value")));
                }
            }

            Dictionary<string, object> postData = GetDict(requestData, "postData");
            if (postData != null)
            {
                request.Body = Str(postData, "text");
                if (string.IsNullOrEmpty(request.Body))
                {
                    object[] formParams = GetArray(postData, "params");
                    if (formParams != null && formParams.Length > 0)
                    {
                        StringBuilder body = new StringBuilder();
                        foreach (object paramObject in formParams)
                        {
                            Dictionary<string, object> item = paramObject as Dictionary<string, object>;
                            if (item == null) continue;
                            if (body.Length > 0) body.Append('&');
                            body.Append(Str(item, "name")).Append('=').Append(Str(item, "value"));
                        }
                        request.Body = body.ToString();
                    }
                }
            }

            request.Status = -1;
            if (responseData != null)
            {
                int status;
                if (int.TryParse(Str(responseData, "status"), NumberStyles.Integer,
                        CultureInfo.InvariantCulture, out status))
                {
                    request.Status = status;
                }
                request.StatusText = Str(responseData, "statusText");

                object[] responseHeaders = GetArray(responseData, "headers");
                if (responseHeaders != null)
                {
                    foreach (object headerObject in responseHeaders)
                    {
                        Dictionary<string, object> item = headerObject as Dictionary<string, object>;
                        if (item == null) continue;
                        request.ResponseHeaders.Add(new KeyValuePair<string, string>(
                            Str(item, "name"), Str(item, "value")));
                    }
                }

                Dictionary<string, object> content = GetDict(responseData, "content");
                if (content != null) request.ResponseBody = ReadContent(content);

                string responseError = Str(responseData, "_error");
                if (!string.IsNullOrEmpty(responseError)) request.Error = responseError;
            }

            string entryError = Str(entry, "_error");
            if (!string.IsNullOrEmpty(entryError)) request.Error = entryError;

            return request;
        }

        private static string ReadContent(Dictionary<string, object> content)
        {
            string text = Str(content, "text");
            if (string.IsNullOrEmpty(text)) return "";

            string encoding = Str(content, "encoding");
            if (string.Equals(encoding, "base64", StringComparison.OrdinalIgnoreCase))
            {
                string mimeType = Str(content, "mimeType");
                if (LooksTextual(mimeType))
                {
                    try
                    {
                        text = Encoding.UTF8.GetString(Convert.FromBase64String(text));
                    }
                    catch (Exception)
                    {
                        return "(base64 响应，解码失败)";
                    }
                }
                else
                {
                    return "(二进制响应 " + mimeType + "，" + text.Length + " 个 base64 字符)";
                }
            }

            int originalLength = text.Length;
            if (originalLength > 100000)
            {
                text = text.Substring(0, 100000) + "\r\n...（已截断，共 " + originalLength + " 字符）";
            }
            return text;
        }

        private static bool LooksTextual(string mimeType)
        {
            if (string.IsNullOrEmpty(mimeType)) return true;
            string mime = mimeType.ToLowerInvariant();
            return mime.IndexOf("json") >= 0 ||
                   mime.IndexOf("text") >= 0 ||
                   mime.IndexOf("xml") >= 0 ||
                   mime.IndexOf("javascript") >= 0 ||
                   mime.IndexOf("x-www-form-urlencoded") >= 0;
        }

        internal static Dictionary<string, object> GetDict(Dictionary<string, object> dict, string key)
        {
            object value;
            if (dict != null && dict.TryGetValue(key, out value))
            {
                return value as Dictionary<string, object>;
            }
            return null;
        }

        internal static object[] GetArray(Dictionary<string, object> dict, string key)
        {
            object value;
            if (dict != null && dict.TryGetValue(key, out value))
            {
                return value as object[];
            }
            return null;
        }

        internal static string Str(Dictionary<string, object> dict, string key)
        {
            object value;
            if (dict != null && dict.TryGetValue(key, out value) && value != null)
            {
                return Convert.ToString(value, CultureInfo.InvariantCulture);
            }
            return "";
        }

        private static double Num(Dictionary<string, object> dict, string key)
        {
            object value;
            if (dict != null && dict.TryGetValue(key, out value) && value != null)
            {
                try
                {
                    return Convert.ToDouble(value, CultureInfo.InvariantCulture);
                }
                catch (Exception)
                {
                }
            }
            return 0;
        }
    }

    /// <summary>
    /// 兼容第三方「请求列表」JSON：顶层是数组，元素形如
    /// { curlRequest, method, url, requestHeaders, requestBody,
    ///   statusCode, responseHeaders, responseBody }
    /// 这类导出常见于浏览器抓包插件，字段不固定，所以按需取值。
    /// </summary>
    internal static class RequestListParser
    {
        public static bool LooksLikeRequestList(string text)
        {
            if (string.IsNullOrEmpty(text)) return false;

            int start = 0;
            while (start < text.Length && (char.IsWhiteSpace(text[start]) || text[start] == '﻿')) start++;
            if (start >= text.Length || text[start] != '[') return false;

            // 只看开头一段，避免对超大粘贴内容做全量扫描。
            // 有些导出的 responseBody 能到几 MB，url 字段会排得很后，
            // 所以这里同时接受“多个独有字段聚在一起”的特征。
            int length = Math.Min(text.Length - start, 1000000);
            string head = text.Substring(start, length);

            int markers = 0;
            if (head.IndexOf("\"curlRequest\"", StringComparison.Ordinal) >= 0) markers++;
            if (head.IndexOf("\"requestHeaders\"", StringComparison.Ordinal) >= 0) markers++;
            if (head.IndexOf("\"responseHeaders\"", StringComparison.Ordinal) >= 0) markers++;
            if (head.IndexOf("\"statusCode\"", StringComparison.Ordinal) >= 0) markers++;
            if (head.IndexOf("\"responseBody\"", StringComparison.Ordinal) >= 0) markers++;

            bool hasUrl = head.IndexOf("\"url\"", StringComparison.Ordinal) >= 0;
            return hasUrl ? markers >= 1 : markers >= 2;
        }

        public static List<CurlRequest> Parse(string text)
        {
            if (text.Length > 0 && text[0] == '﻿') text = text.Substring(1);

            JavaScriptSerializer serializer = new JavaScriptSerializer();
            serializer.MaxJsonLength = int.MaxValue;

            object rootObject = serializer.DeserializeObject(text);
            object[] entries = rootObject as object[];
            if (entries == null) throw new InvalidOperationException("JSON 根节点不是请求数组。");

            List<CurlRequest> requests = new List<CurlRequest>();
            foreach (object entryObject in entries)
            {
                Dictionary<string, object> entry = entryObject as Dictionary<string, object>;
                if (entry == null) continue;
                CurlRequest request = ParseEntry(entry);
                if (request != null) requests.Add(request);
            }
            return requests;
        }

        private static CurlRequest ParseEntry(Dictionary<string, object> entry)
        {
            string url = HarParser.Str(entry, "url");
            string method = HarParser.Str(entry, "method");
            string body = HarParser.Str(entry, "requestBody");
            List<KeyValuePair<string, string>> headers = ReadHeaders(entry, "requestHeaders");

            // 有些版本只给 curlRequest，就从 curl 里补全缺失的字段
            string curl = HarParser.Str(entry, "curlRequest");
            if (!string.IsNullOrEmpty(curl))
            {
                List<CurlRequest> parsed = UrlExtractor.ParseRequests(curl);
                if (parsed.Count > 0)
                {
                    CurlRequest fallback = parsed[0];
                    if (string.IsNullOrEmpty(url)) url = fallback.Url;
                    if (string.IsNullOrEmpty(method)) method = fallback.Method;
                    if (headers.Count == 0) headers.AddRange(fallback.Headers);
                    if (string.IsNullOrEmpty(body)) body = fallback.Body;
                }
            }

            if (string.IsNullOrEmpty(url)) return null;

            CurlRequest request = new CurlRequest();
            request.Source = "list";
            request.Url = url;
            request.Method = string.IsNullOrEmpty(method) ? "GET" : method.ToUpperInvariant();
            request.Body = body;
            request.Headers.AddRange(headers);
            request.QueryParams.AddRange(UrlExtractor.ParseQueryPairs(url));
            request.Status = -1;

            int status;
            string statusValue = HarParser.Str(entry, "statusCode");
            if (string.IsNullOrEmpty(statusValue)) statusValue = HarParser.Str(entry, "status");
            if (int.TryParse(statusValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out status))
            {
                request.Status = status;
            }

            request.StatusText = HarParser.Str(entry, "statusText");
            if (string.IsNullOrEmpty(request.StatusText)) request.StatusText = HarParser.Str(entry, "statusMessage");

            request.ResponseHeaders.AddRange(ReadHeaders(entry, "responseHeaders"));
            request.ResponseBody = Truncate(HarParser.Str(entry, "responseBody"));

            string error = HarParser.Str(entry, "error");
            if (!string.IsNullOrEmpty(error)) request.Error = error;

            return request;
        }

        private static List<KeyValuePair<string, string>> ReadHeaders(
            Dictionary<string, object> entry, string key)
        {
            List<KeyValuePair<string, string>> headers = new List<KeyValuePair<string, string>>();
            object raw;
            if (entry == null || !entry.TryGetValue(key, out raw) || raw == null) return headers;

            Dictionary<string, object> map = raw as Dictionary<string, object>;
            if (map != null)
            {
                foreach (KeyValuePair<string, object> pair in map)
                {
                    if (string.IsNullOrEmpty(pair.Key)) continue;
                    headers.Add(new KeyValuePair<string, string>(pair.Key, HeaderValue(pair.Value)));
                }
                return headers;
            }

            object[] array = raw as object[];
            if (array != null)
            {
                foreach (object itemObject in array)
                {
                    Dictionary<string, object> item = itemObject as Dictionary<string, object>;
                    if (item != null)
                    {
                        string name = HarParser.Str(item, "name");
                        if (string.IsNullOrEmpty(name)) continue;
                        headers.Add(new KeyValuePair<string, string>(name, HarParser.Str(item, "value")));
                    }
                    else if (itemObject != null)
                    {
                        // 少数实现直接给 "Name: Value" 字符串
                        string line = Convert.ToString(itemObject, CultureInfo.InvariantCulture);
                        int colon = line == null ? -1 : line.IndexOf(':');
                        if (colon > 0)
                        {
                            headers.Add(new KeyValuePair<string, string>(
                                line.Substring(0, colon).Trim(),
                                line.Substring(colon + 1).Trim()));
                        }
                    }
                }
            }
            return headers;
        }

        private static string HeaderValue(object value)
        {
            if (value == null) return "";
            object[] array = value as object[];
            if (array == null) return Convert.ToString(value, CultureInfo.InvariantCulture);

            StringBuilder sb = new StringBuilder();
            foreach (object item in array)
            {
                if (sb.Length > 0) sb.Append(", ");
                sb.Append(Convert.ToString(item, CultureInfo.InvariantCulture));
            }
            return sb.ToString();
        }

        private static string Truncate(string text)
        {
            if (string.IsNullOrEmpty(text)) return "";
            if (text.Length <= 100000) return text;
            return text.Substring(0, 100000) + "\r\n...（已截断，共 " + text.Length + " 字符）";
        }
    }

    internal static class Sensitive
    {
        private static readonly Regex JsonSensitive = new Regex(
            "(\"(?:accessToken|refreshToken|token|session|authorization|cookie|password|secret|appSecret|sign)\"\\s*:\\s*\")([^\"]*)(\")",
            RegexOptions.IgnoreCase);

        private static readonly Regex JsonLongString = new Regex("\"([^\"]{65,})\"");

        private static readonly Regex JsonOpenId = new Regex(
            "(\"(?:openid|open_id)\"\\s*:\\s*\")([^\"]*)(\")",
            RegexOptions.IgnoreCase);

        // 浏览器 / 网关的通用头，定位接口失败原因基本没用，默认不展示
        private static readonly HashSet<string> NoiseRequestHeaders = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "user-agent", "accept", "accept-encoding", "accept-language", "connection",
            "cache-control", "pragma", "upgrade-insecure-requests", "dnt", "host",
            "content-length", "te", "priority", "sec-gpc",
            ":authority", ":method", ":path", ":scheme",
            "sec-ch-ua", "sec-ch-ua-mobile", "sec-ch-ua-platform",
            "sec-fetch-site", "sec-fetch-mode", "sec-fetch-dest", "sec-fetch-user"
        };

        private static readonly HashSet<string> NoiseResponseHeaders = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "server", "date", "connection", "keep-alive", "transfer-encoding",
            "content-length", "content-encoding", "cache-control", "pragma", "expires",
            "vary", "x-powered-by", "x-frame-options", "x-content-type-options",
            "x-xss-protection", "x-download-options", "x-permitted-cross-domain-policies",
            "strict-transport-security", "referrer-policy", "x-ua-compatible",
            "etag", "last-modified", "accept-ranges", "age", "via", "alt-svc",
            "content-security-policy", ":status"
        };

        public static bool IsNoiseRequestHeader(string name)
        {
            return !string.IsNullOrEmpty(name) && NoiseRequestHeaders.Contains(name.Trim());
        }

        public static bool IsNoiseResponseHeader(string name)
        {
            return !string.IsNullOrEmpty(name) && NoiseResponseHeaders.Contains(name.Trim());
        }

        private static bool IsOpenIdKey(string key)
        {
            string k = (key ?? "").Trim().ToLowerInvariant();
            return k.IndexOf("openid") >= 0 || k.IndexOf("open_id") >= 0;
        }

        public static bool IsSensitiveKey(string key)
        {
            string k = (key ?? "").Trim().ToLowerInvariant();
            if (k.Length == 0) return false;
            return k.IndexOf("token") >= 0 ||
                   k.IndexOf("secret") >= 0 ||
                   k.IndexOf("password") >= 0 ||
                   k.IndexOf("session") >= 0 ||
                   k.IndexOf("cookie") >= 0 ||
                   k.IndexOf("authorization") >= 0 ||
                   k.IndexOf("sign") >= 0;
        }

        public static string MaskValue(string key, string value)
        {
            if (string.IsNullOrEmpty(value)) return value;
            // openid 对排查用户/授权问题很关键，始终完整展示
            if (IsOpenIdKey(key)) return value;
            if (IsSensitiveKey(key) || value.Length > 64)
            {
                return "***(" + value.Length + ")";
            }
            return value;
        }

        public static string MaskHeader(string name, string value)
        {
            if (string.IsNullOrEmpty(value)) return value;
            if (IsOpenIdKey(name)) return value;
            if (IsSensitiveKey(name) || value.Length > 96)
            {
                return "***(" + value.Length + ")";
            }
            return value;
        }

        public static string MaskUrl(string url)
        {
            if (string.IsNullOrEmpty(url)) return url;

            int question = url.IndexOf('?');
            if (question < 0) return url;

            string head = url.Substring(0, question + 1);
            string rest = url.Substring(question + 1);
            string fragment = "";
            int hash = rest.IndexOf('#');
            if (hash >= 0)
            {
                fragment = rest.Substring(hash);
                rest = rest.Substring(0, hash);
            }

            string[] parts = rest.Split('&');
            for (int i = 0; i < parts.Length; i++)
            {
                int equals = parts[i].IndexOf('=');
                if (equals <= 0) continue;
                string key = parts[i].Substring(0, equals);
                string value = parts[i].Substring(equals + 1);
                parts[i] = key + "=" + MaskValue(key, value);
            }
            return head + string.Join("&", parts) + fragment;
        }

        public static string MaskBody(string body)
        {
            if (string.IsNullOrEmpty(body)) return body;

            string trimmed = body.TrimStart();
            if (trimmed.StartsWith("{") || trimmed.StartsWith("["))
            {
                string masked = JsonSensitive.Replace(body, delegate(Match match)
                {
                    return match.Groups[1].Value + "***(" + match.Groups[2].Value.Length + ")" + match.Groups[3].Value;
                });
                // openid 是定位用户/授权问题的关键信息，先摘出来，
                // 避免被下面“超长字符串”规则连带打码，最后再原样放回
                List<string> openIds = new List<string>();
                masked = JsonOpenId.Replace(masked, delegate(Match match)
                {
                    openIds.Add(match.Groups[2].Value);
                    return match.Groups[1].Value + "" + (openIds.Count - 1) + "" + match.Groups[3].Value;
                });

                masked = JsonLongString.Replace(masked, delegate(Match match)
                {
                    return "\"***(" + match.Groups[1].Value.Length + ")\"";
                });

                for (int i = 0; i < openIds.Count; i++)
                {
                    masked = masked.Replace("" + i + "", openIds[i]);
                }
                return masked;
            }

            if (body.IndexOf('=') >= 0)
            {
                string[] parts = body.Split('&');
                for (int i = 0; i < parts.Length; i++)
                {
                    int equals = parts[i].IndexOf('=');
                    if (equals <= 0) continue;
                    string key = parts[i].Substring(0, equals);
                    string value = parts[i].Substring(equals + 1);
                    parts[i] = key + "=" + MaskValue(key, value);
                }
                return string.Join("&", parts);
            }

            return body;
        }
    }

    internal static class JsonPretty
    {
        public static string Print(string json)
        {
            StringBuilder sb = new StringBuilder();
            int indent = 0;
            bool inString = false;
            bool escaped = false;
            bool justOpened = false;

            foreach (char c in json)
            {
                if (inString)
                {
                    sb.Append(c);
                    if (escaped) escaped = false;
                    else if (c == '\\') escaped = true;
                    else if (c == '"') inString = false;
                    continue;
                }

                if (char.IsWhiteSpace(c)) continue;

                if (justOpened)
                {
                    if (c == '}' || c == ']')
                    {
                        // 空对象 / 空数组：{} 或 []
                        sb.Append(c);
                        indent = Math.Max(0, indent - 1);
                        justOpened = false;
                        continue;
                    }
                    sb.AppendLine();
                    sb.Append(new string(' ', indent * 2));
                    justOpened = false;
                }

                if (c == '"')
                {
                    inString = true;
                    sb.Append(c);
                    continue;
                }

                switch (c)
                {
                    case '{':
                    case '[':
                        sb.Append(c);
                        indent++;
                        justOpened = true;
                        break;

                    case '}':
                    case ']':
                        indent = Math.Max(0, indent - 1);
                        sb.AppendLine();
                        sb.Append(new string(' ', indent * 2));
                        sb.Append(c);
                        break;

                    case ',':
                        sb.Append(c);
                        sb.AppendLine();
                        sb.Append(new string(' ', indent * 2));
                        justOpened = false;
                        break;

                    case ':':
                        sb.Append(": ");
                        justOpened = false;
                        break;

                    default:
                        sb.Append(c);
                        justOpened = false;
                        break;
                }
            }

            return sb.ToString().Trim();
        }
    }
}
