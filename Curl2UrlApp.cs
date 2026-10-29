using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Net;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
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
[assembly: System.Reflection.AssemblyVersion("1.0.9.0")]
[assembly: System.Reflection.AssemblyFileVersion("1.0.9.0")]
[assembly: System.Reflection.AssemblyInformationalVersion("1.0.9")]

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
                if (args[0] == "--ocr-ai")
                {
                    if (args.Length < 2)
                    {
                        WriteStderr("用法：curl2url.exe --ocr-ai <图片路径>");
                        Environment.Exit(2);
                    }
                    Environment.Exit(RunOcrAiOnly(args[1]));
                }
                if (args[0] == "--selftest")
                {
                    Application.EnableVisualStyles();
                    Application.SetCompatibleTextRenderingDefault(false);
                    using (MainForm form = new MainForm())
                    {
                        form.CreateControl();
                        string logUiError = form.RunLogUiSelftest();
                        if (logUiError.Length > 0)
                        {
                            WriteStderr("Log UI selftest failed: " + logUiError);
                            Environment.Exit(1);
                        }
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
                    // 词框间距小于半个字高时拼回一个单词，正常词间距保留空格
                    List<OcrRunner.OcrWordBox> words = new List<OcrRunner.OcrWordBox>();
                    words.Add(new OcrRunner.OcrWordBox("deserial", 0, 160, 30));
                    words.Add(new OcrRunner.OcrWordBox("ize:", 173, 70, 33));
                    if (OcrRunner.JoinOcrWords(words) != "deserialize:")
                    {
                        Console.WriteLine("OCR word merge failed");
                        Environment.Exit(1);
                    }
                    words = new List<OcrRunner.OcrWordBox>();
                    words.Add(new OcrRunner.OcrWordBox("Cannot", 0, 120, 30));
                    words.Add(new OcrRunner.OcrWordBox("deserialize", 142, 160, 30));
                    if (OcrRunner.JoinOcrWords(words) != "Cannot deserialize")
                    {
                        Console.WriteLine("OCR word spacing failed");
                        Environment.Exit(1);
                    }
                    RunLogSelftest();
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

        private static void WriteHarWarning()
        {
            if (HarParser.LastWarning.Length == 0) return;
            WriteStderr("提示：" + HarParser.LastWarning);
        }

        private static void LogRequire(bool condition, string message)
        {
            if (condition) return;
            WriteStderr("Log selftest failed: " + message);
            Environment.Exit(1);
        }

        private static void RunLogSelftest()
        {
            LogOptions options = new LogOptions();
            LogOptions rawOptions = new LogOptions();
            rawOptions.FilterNoise = false;

            string smsLog =
@"2026-10-28 17:00:00.001 INFO [http-nio-8080-exec-1] [traceId=abc123456789] [UHLOG]开始请求URI[/gift-service/guangqi/sendMessage]
2026-10-28 17:00:00.002 INFO [http-nio-8080-exec-1] [traceId=abc123456789] [UHLOG]请求参数 phone=15219896454,type=P-1708928214000
2026-10-28 17:00:00.003 INFO [http-nio-8080-exec-1] [traceId=abc123456789] c.w.w.g.controller.GuangqiController - sendMessage
2026-10-28 17:00:00.004 INFO [http-nio-8080-exec-1] [traceId=abc123456789] POST /gift-service/guangqi/sendMessage
2026-10-28 17:00:00.005 INFO [scheduling-1] size: 0
2026-10-28 17:00:00.006 INFO [http-nio-8080-exec-1] Creating a new SqlSession
2026-10-28 17:00:00.007 INFO [http-nio-8080-exec-1] Closing non transactional SqlSession [org.apache.ibatis.session.defaults.DefaultSqlSession@1]
2026-10-28 17:00:00.008 INFO [scheduling-1] size: 0
2026-10-28 17:00:00.009 INFO [main] spring.application.name=welfare-gift-service";

            LogAnalysis sms = LogExplainer.Analyze(smsLog, options);
            LogRequire(sms.Report.Contains("152****6454"), "phone should be masked");
            LogRequire(!sms.Report.Contains("15219896454"), "raw phone leaked into report");
            LogRequire(!sms.AiExcerpt.Contains("15219896454"), "raw phone leaked into AI excerpt");
            LogRequire(sms.Report.Contains("GuangqiController.sendMessage"), "controller not recognized");
            LogRequire(sms.Report.Contains("服务/模块：welfare-gift-service"), "service name not recognized");
            LogRequire(sms.Report.Contains("POST /gift-service/guangqi/sendMessage"), "POST request not recognized");
            LogRequire(sms.Report.Contains("识别到 1 次接口调用"), "URI start line and access line should merge into one call");
            LogRequire(sms.Report.Contains("没有看到 ERROR/Exception"), "INFO-only conclusion missing");
            LogRequire(sms.NoiseHeartbeat >= 2, "heartbeat/size 0 lines not filtered");
            LogRequire(sms.NoiseFramework >= 2, "framework lines not filtered");
            LogRequire(sms.KeptLines < sms.TotalLines, "noise should reduce kept lines");
            LogRequire(!sms.AiExcerpt.Contains("Creating a new SqlSession"), "noise leaked into AI excerpt");

            LogAnalysis smsRaw = LogExplainer.Analyze(smsLog, rawOptions);
            LogRequire(smsRaw.KeptLines == smsRaw.TotalLines, "FilterNoise=false should keep every line");

            string uriOnlyLog =
@"2026-10-28 17:00:10.000 INFO [http-nio-8080-exec-1] [traceId=uri123456789] [UHLOG]开始请求URI[/gift-service/guangqi/sendMessage]";
            LogAnalysis uriOnly = LogExplainer.Analyze(uriOnlyLog, options);
            LogRequire(uriOnly.Report.Contains("正常处理 /gift-service/guangqi/sendMessage 请求"),
                "UHLOG URI[...] request not recognized");

            LogOptions onlyErrors = new LogOptions();
            onlyErrors.OnlyErrors = true;
            LogAnalysis smsOnly = LogExplainer.Analyze(smsLog, onlyErrors);
            LogRequire(smsOnly.KeptLines == 0, "INFO-only log should have no error context");
            LogRequire(smsOnly.Report.Contains("只看错误"), "only-errors notice missing");

            string npeLog =
@"2026-10-28 17:01:00.000 ERROR [http-nio-8080-exec-2] [traceId=def987654321] Servlet.service() for servlet [dispatcherServlet] threw exception
java.lang.NullPointerException: Cannot invoke getName because user is null
    at com.uhplus.welfare.gift.service.SmsService.send(SmsService.java:42)
    at com.uhplus.welfare.gift.controller.GuangqiController.sendMessage(GuangqiController.java:88)
    at org.springframework.aop.framework.CglibAopProxy.invoke(CglibAopProxy.java:1)
Caused by: java.sql.SQLException: Connection refused
    at com.uhplus.welfare.gift.dao.UserDao.find(UserDao.java:31)";

            LogAnalysis npe = LogExplainer.Analyze(npeLog, options);
            LogRequire(npe.ErrorCount >= 1, "NPE error count");
            LogRequire(npe.ExceptionCount >= 2, "Caused by not counted");
            LogRequire(npe.Report.Contains("NullPointerException"), "NPE class missing");
            LogRequire(npe.Report.Contains("空指针"), "NPE explanation missing");
            LogRequire(npe.Report.Contains("SmsService.java:42"), "business stack frame missing");
            LogRequire(npe.Report.Contains("Controller：GuangqiController.sendMessage"), "controller should be extracted from stack frame");
            LogRequire(npe.Report.Contains("SQLException"), "Caused by missing");
            LogRequire(npe.Report.Contains("底层 Caused by：SQLException（Connection refused）"), "root cause missing from one-liner");
            LogRequire(npe.Report.Contains("底层原因：SQLException"), "root cause missing from trace story");
            LogRequire(npe.Report.Contains("数据库连接失败"), "connection refused should not be explained as SQL syntax");
            LogRequire(npe.Report.Contains("确认数据库地址/端口"), "connection refused suggestion missing");
            LogRequire(npe.TraceCount == 1, "NPE trace should be inherited");

            LogAnalysis npeOnly = LogExplainer.Analyze(npeLog, onlyErrors);
            LogRequire(npeOnly.KeptLines > 0, "only-errors should keep the NPE context");

            string signatureLog =
@"2026-10-28 17:02:00.000 ERROR [http-nio-8080-exec-3] [traceId=sig123456789] AppKey error: invalid signature, appKey=test, sign=abc";
            LogAnalysis signature = LogExplainer.Analyze(signatureLog, options);
            LogRequire(signature.HasSignatureError, "signature error not recognized");
            LogRequire(signature.Report.Contains("签名"), "signature explanation missing");

            string multiTraceLog =
@"2026-10-28 17:03:00.000 INFO [http-1] [traceId=aaa111111111] POST /one
2026-10-28 17:03:00.001 INFO [http-1] [traceId=aaa111111111] done
2026-10-28 17:03:00.002 INFO [http-2] [traceId=bbb222222222] POST /two
2026-10-28 17:03:00.003 ERROR [http-2] [traceId=bbb222222222] java.lang.IllegalArgumentException: bad param";
            LogAnalysis multi = LogExplainer.Analyze(multiTraceLog, options);
            LogRequire(multi.TraceCount == 2, "two trace ids expected");
            LogRequire(multi.Report.Contains("共 2 个 traceId"), "trace directory missing");
            LogRequire(multi.Report.Contains("aaa111111111") && multi.Report.Contains("bbb222222222"), "trace ids missing");
            LogRequire(multi.Report.Contains("IllegalArgumentException"), "trace exception missing");
            LogRequire(multi.Report.Contains("处理 POST /two 时出现 IllegalArgumentException"), "one-liner should follow the failing trace");

            string secretLog =
@"2026-10-28 17:04:00.000 INFO [main] Authorization: Bearer abcdef123456
2026-10-28 17:04:00.001 INFO [main] accessToken=zzz999";
            LogAnalysis secret = LogExplainer.Analyze(secretLog, options);
            LogRequire(!secret.AiExcerpt.Contains("abcdef123456"), "authorization token leaked");
            LogRequire(!secret.AiExcerpt.Contains("zzz999"), "access token leaked");
            LogRequire(secret.AiExcerpt.Contains("***"), "masking marker missing");

            string repeatedLog =
@"2026-10-28 17:05:00.001 INFO [main] cache warmup done
2026-10-28 17:05:00.002 INFO [main] cache warmup done
2026-10-28 17:05:00.003 INFO [main] cache warmup done
2026-10-28 17:05:00.004 INFO [main] cache warmup done";
            LogAnalysis repeated = LogExplainer.Analyze(repeatedLog, options);
            LogRequire(repeated.NoiseDuplicate == 2, "duplicate INFO lines should be folded");
            LogRequire(repeated.KeptLines == 2, "first two duplicate lines should stay");
            LogAnalysis repeatedRaw = LogExplainer.Analyze(repeatedLog, rawOptions);
            LogRequire(repeatedRaw.KeptLines == 4, "FilterNoise=false should keep duplicates");

            string sqlLog =
@"2026-10-28 17:06:00.000 INFO [http-1] [traceId=sql123456789] ==>  Preparing: select id, name from user where phone = ? and status = ?
2026-10-28 17:06:00.001 INFO [http-1] [traceId=sql123456789] ==> Parameters: 15219896454(String), 1(Integer)
2026-10-28 17:06:00.002 INFO [http-1] [traceId=sql123456789] <==      Total: 1";
            LogAnalysis sql = LogExplainer.Analyze(sqlLog, options);
            LogRequire(sql.SqlCount == 1, "MyBatis Preparing should count one SQL");
            LogRequire(sql.Report.Contains("SQL：执行 1 条"), "SQL summary missing");
            LogRequire(sql.Report.Contains("参数示例：152****6454"), "MyBatis Parameters missing or not masked");
            LogRequire(!sql.Report.Contains("15219896454"), "raw phone leaked from SQL parameters");
            LogRequire(!sql.AiExcerpt.Contains("15219896454"), "raw phone leaked from SQL parameters into AI excerpt");

            string serializationLog =
@"requestId: 4043748ecacd9
Cannot deserialize; nested exception is org.springframework.core.serializer.support.SerializationFailedException: Failed to deserialize payload. Is the byte array a result of corresponding serialization for DefaultDeserializer?; nested exception is org.springframework.core.NestedIOException: Failed to deserialize object type; nested exception is java.lang.ClassNotFoundException: cn.uhplus.welfare.taopiaopiao.dto.AreaRspData
Cannot deserialize; nested exception is org.springframework.core.serializer.support.SerializationFailedException: Failed to deserialize payload. Is the byte array a result of corresponding serialization for DefaultDeserializer?; nested exception is org.springframework.core.NestedIOException: Failed to deserialize object type; nested exception is java.lang.ClassNotFoundException: cn.uhplus.welfare.taopiaopiao.dto.AreaRspData";
            LogAnalysis serialization = LogExplainer.Analyze(serializationLog, options);
            LogRequire(serialization.ExceptionCount == 3, "nested Spring exception chain should have 3 distinct entries");
            LogRequire(serialization.Report.Contains("ClassNotFoundException"), "nested root cause missing");
            LogRequire(serialization.Report.Contains("1 条异常链（共 3 层"), "exception chain layer summary missing");
            LogRequire(serialization.Report.Contains("nested ClassNotFoundException") &&
                       !serialization.Report.Contains("Caused by ClassNotFoundException"),
                "nested chain should not be labeled as Caused by");
            LogRequire(serialization.Report.Contains("缺失的类：cn.uhplus.welfare.taopiaopiao.dto.AreaRspData"),
                "missing class not extracted");
            LogRequire(serialization.Report.Contains("JDK 反序列化失败"), "JDK serialization conclusion missing");
            LogRequire(serialization.Report.Contains("Redis 旧缓存"), "Redis stale-cache hint missing");
            LogRequire(serialization.Report.Contains("不是 JSON 字段不匹配"), "serialization failure misclassified as JSON");
            LogRequire(serialization.Report.Contains("同一异常链在日志里重复 2 次"), "duplicate exception chain count wrong");
            LogRequire(!serialization.Report.Contains("JSON 的结构、字段类型和 Java 对象对不上"),
                "JDK serialization should not use JSON field-mismatch explanation");

            string warning;
            string brokenHar =
                "{\"log\":{\"entries\":[{\"request\":{\"method\":\"POST\",\"url\":\"https://example.com/ok\"," +
                "\"postData\":{\"mimeType\":\"application/json\",\"text\":\"body-start\n" +
                "{\"log\":{\"entries\":[]}";
            List<CurlRequest> repaired = HarParser.Parse(brokenHar, out warning);
            LogRequire(repaired.Count == 1 && repaired[0].Url == "https://example.com/ok",
                "malformed HAR string should keep complete requests");
            LogRequire(warning.Length > 0, "malformed HAR should return a warning");

            string truncatedHar =
                "{\"log\":{\"entries\":[{\"request\":{\"method\":\"GET\"," +
                "\"url\":\"https://example.com/truncated\",\"headers\":[]}}";
            List<CurlRequest> truncated = HarParser.Parse(truncatedHar, out warning);
            LogRequire(truncated.Count == 1 && truncated[0].Url == "https://example.com/truncated",
                "truncated HAR should be closed and parsed");
            LogRequire(warning.Length > 0, "truncated HAR should return a warning");

            string validHar =
                "{\"log\":{\"entries\":[{\"request\":{\"method\":\"GET\",\"url\":\"https://example.com/valid\"}}]}}";
            List<CurlRequest> valid = HarParser.Parse(validHar, out warning);
            LogRequire(valid.Count == 1 && warning.Length == 0, "valid HAR should not warn");
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

        private static int RunOcrAiOnly(string path)
        {
            AiOcrSettings settings = AiOcrSettings.Load();
            if (!settings.IsConfigured)
            {
                WriteStderr("AI 识别还没有配置：请打开程序 → 截图识别 → AI 设置，填写接口地址、模型和 API Key。");
                return 2;
            }

            try
            {
                using (Image image = Image.FromFile(path))
                using (Bitmap bitmap = new Bitmap(image))
                {
                    string text;
                    string error;
                    if (!AiOcrRunner.TryRecognize(bitmap, settings, out text, out error))
                    {
                        WriteStderr("AI 识别失败：" + error);
                        return 1;
                    }
                    WriteStdout(text + "\r\n");
                    return 0;
                }
            }
            catch (Exception ex)
            {
                WriteStderr("AI 识别失败：" + ex.Message);
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
                WriteHarWarning();
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
                bool includeResponses = HasArg(Environment.GetCommandLineArgs(), "--responses");
                List<CurlRequest> requests = UrlExtractor.ParseAny(input);
                List<string> urls = UrlExtractor.Unique(UrlExtractor.ExtractAny(input));
                WriteHarWarning();
                WriteStdout(UrlExtractor.FormatDetails(requests, urls, mask, includeResponses));
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
                WriteHarWarning();
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
        private readonly CheckBox _keepResponses;
        private readonly System.Windows.Forms.Timer _autoTimer;
        private TabPage _failurePage;
        private string _lastSignature = "";
        private Form _helpDialog;

        // 日志人话页签需要临时收起上方 curl 输入区，给日志让出高度
        private TableLayoutPanel _rootLayout;
        private Label _curlSectionLabel;
        private FlowLayoutPanel _curlToolbar;

        // 截图识别页签
        private TabControl _tabs;
        private TabPage _ocrPage;
        private PictureBox _ocrPreview;
        private TextBox _ocrBox;
        private Label _ocrStatusLabel;
        private ComboBox _ocrModeBox;
        private AiOcrSettings _aiSettings;
        private bool _ocrModeChanging;
        private bool _ocrBusy;

        // 日志人话页签
        private TabPage _logPage;
        private TextBox _logInput;
        private TextBox _logOutput;
        private Label _logStatusLabel;
        private CheckBox _logFilterNoise;
        private CheckBox _logOnlyErrors;
        private CheckBox _logGroupTrace;
        private Button _logAiButton;
        private bool _logBusy;
        private int _logGeneration;
        private int _logBusyGeneration;

        public MainForm()
        {
            Text = "curl2url · cURL / HAR 解析 / 日志人话";
            ClientSize = new Size(900, 700);
            MinimumSize = new Size(760, 580);
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = PageBack;
            Font = new Font("Microsoft YaHei UI", 9F);
            AutoScaleMode = AutoScaleMode.Dpi;

            // 教程窗口内容较长，启动后空闲时先建好，第一次点按钮不再卡一下
            Shown += delegate { BeginInvoke(new Action(PreloadHelpDialog)); };

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
            _rootLayout = root;
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

            TableLayoutPanel header = new TableLayoutPanel();
            header.ColumnCount = 2;
            header.RowCount = 1;
            header.AutoSize = true;
            header.Dock = DockStyle.Fill;
            header.Margin = new Padding(0, 0, 0, 12);
            header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

            FlowLayoutPanel titleBlock = new FlowLayoutPanel();
            titleBlock.FlowDirection = FlowDirection.TopDown;
            titleBlock.WrapContents = false;
            titleBlock.AutoSize = true;
            titleBlock.Dock = DockStyle.Fill;
            titleBlock.Margin = new Padding(0);

            Label title = new Label();
            title.Text = "curl2url";
            title.Font = new Font("Microsoft YaHei UI", 15F, FontStyle.Bold);
            title.ForeColor = TitleColor;
            title.AutoSize = true;
            title.Margin = new Padding(0);

            Label subtitle = new Label();
            subtitle.Text = "粘贴 cURL 或 HAR，自动解析；报错截图可识别，日志可翻成人话。";
            subtitle.ForeColor = MutedColor;
            subtitle.AutoSize = true;
            subtitle.Margin = new Padding(1, 4, 0, 0);

            titleBlock.Controls.Add(title);
            titleBlock.Controls.Add(subtitle);
            header.Controls.Add(titleBlock, 0, 0);

            Button helpButton = MakeSmallButton("使用教程", 88);
            helpButton.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            helpButton.Margin = new Padding(12, 0, 0, 0);
            helpButton.Click += delegate { ShowCopyFormatHelp(); };
            header.Controls.Add(helpButton, 1, 0);

            root.Controls.Add(header, 0, 0);

            _curlSectionLabel = MakeSectionLabel("curl 命令");
            root.Controls.Add(_curlSectionLabel, 0, 1);

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

            _keepResponses = new CheckBox();
            _keepResponses.Text = "保留响应";
            _keepResponses.AutoSize = true;
            _keepResponses.ForeColor = LabelColor;
            _keepResponses.Cursor = Cursors.Hand;
            _keepResponses.Margin = new Padding(4, 9, 8, 0);
            _keepResponses.CheckedChanged += delegate { Analyze(false); };

            Label hint = new Label();
            hint.Text = "粘贴或拖入后自动解析，Ctrl+Enter 刷新；勾选「保留响应」可看 HAR 响应；报错截图可直接 Ctrl+V 转文字";
            hint.ForeColor = MutedColor;
            hint.AutoSize = true;
            hint.Margin = new Padding(6, 9, 0, 0);

            buttons.Controls.Add(pasteButton);
            buttons.Controls.Add(openButton);
            buttons.Controls.Add(ocrButton);
            buttons.Controls.Add(clearButton);
            buttons.Controls.Add(_keepDuplicates);
            buttons.Controls.Add(_maskSensitive);
            buttons.Controls.Add(_keepResponses);
            buttons.Controls.Add(hint);
            _curlToolbar = buttons;
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
            _logPage = BuildLogPage();

            tabs.TabPages.Add(urlPage);
            tabs.TabPages.Add(detailPage);
            tabs.TabPages.Add(_failurePage);
            tabs.TabPages.Add(responsePage);
            tabs.TabPages.Add(_ocrPage);
            tabs.TabPages.Add(_logPage);
            tabs.SelectedIndexChanged += delegate { UpdateWorkspaceForTab(); };
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
            footnote.Text = "支持多条 curl、HAR（.har / JSON）、http/https/ws/wss；日志支持 Spring / Java / K8s / MyBatis 常见格式；长 Token 默认打码。";
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
            _aiSettings = AiOcrSettings.Load();

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
            hint.Text = "微信 / QQ 截图后直接 Ctrl+V（中英文均可），或点「粘贴图片」「打开图片」；双击左侧图片可放大。识别结果可一键复制给 AI。";
            hint.ForeColor = MutedColor;
            hint.AutoSize = false;
            hint.Height = 40;
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

            Label modeLabel = new Label();
            modeLabel.Text = "识别方式";
            modeLabel.AutoSize = true;
            modeLabel.ForeColor = LabelColor;
            modeLabel.Margin = new Padding(10, 8, 4, 0);

            _ocrModeBox = new ComboBox();
            _ocrModeBox.DropDownStyle = ComboBoxStyle.DropDownList;
            _ocrModeBox.Width = 150;
            _ocrModeBox.Font = new Font("Microsoft YaHei UI", 8.5F);
            _ocrModeBox.Margin = new Padding(0, 4, 8, 0);
            _ocrModeBox.Items.Add("本地 OCR（离线）");
            _ocrModeBox.Items.Add("AI 识别（联网，更准）");
            _ocrModeBox.SelectedIndex = (_aiSettings.UseAi && _aiSettings.IsConfigured) ? 1 : 0;
            _ocrModeBox.SelectedIndexChanged += delegate { OcrModeChanged(); };

            Button aiSettingsButton = MakeSmallButton("AI 设置", 78);
            aiSettingsButton.Click += delegate { ShowAiSettings(); };

            buttons.Controls.Add(pasteImageButton);
            buttons.Controls.Add(openImageButton);
            buttons.Controls.Add(copyTextButton);
            buttons.Controls.Add(clearButton);
            buttons.Controls.Add(modeLabel);
            buttons.Controls.Add(_ocrModeBox);
            buttons.Controls.Add(aiSettingsButton);
            layout.Controls.Add(buttons, 0, 1);

            _ocrStatusLabel = new Label();
            _ocrStatusLabel.Text = _ocrModeBox.SelectedIndex == 1
                ? "等待图片。AI 识别会把图片发送到所配置的服务。"
                : "等待图片。本地识别在本机完成，不联网；需要更高准确率可切到 AI 识别。";
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
            _ocrPreview.Cursor = Cursors.Hand;
            _ocrPreview.DragEnter += HandleDragEnter;
            _ocrPreview.DragDrop += HandleDragDrop;
            _ocrPreview.DoubleClick += delegate { ShowOcrPreview(); };
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

        private TabPage BuildLogPage()
        {
            TabPage page = new TabPage("日志人话");
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
            hint.Text = "把控制台、Grafana 或 K8s 日志整段粘进来，点「开始分析」或 Ctrl+Enter。默认过滤心跳、框架刷屏和重复 INFO；本机分析不走网络，勾「AI 解读」才会联网。";
            hint.ForeColor = MutedColor;
            hint.AutoSize = false;
            hint.Height = 40;
            hint.Dock = DockStyle.Fill;
            hint.Margin = new Padding(0, 0, 0, 6);
            layout.Controls.Add(hint, 0, 0);

            FlowLayoutPanel buttons = new FlowLayoutPanel();
            buttons.FlowDirection = FlowDirection.LeftToRight;
            buttons.WrapContents = true;
            buttons.AutoSize = true;
            buttons.Dock = DockStyle.Fill;
            buttons.Margin = new Padding(0, 0, 0, 4);

            Button pasteButton = MakeSmallButton("从剪贴板粘贴", 118);
            pasteButton.Click += delegate { PasteLogFromClipboard(); };

            Button analyzeButton = MakeSmallButton("开始分析", 88);
            analyzeButton.BackColor = PrimaryColor;
            analyzeButton.ForeColor = Color.White;
            analyzeButton.FlatAppearance.BorderSize = 0;
            analyzeButton.FlatAppearance.MouseOverBackColor = PrimaryHover;
            analyzeButton.FlatAppearance.MouseDownBackColor = PrimaryHover;
            analyzeButton.Click += delegate { StartLogAnalysis(false); };

            _logAiButton = MakeSmallButton("AI 解读（联网）", 126);
            _logAiButton.Click += delegate { StartLogAnalysis(true); };

            Button copyButton = MakeSmallButton("复制结果", 84);
            copyButton.Click += delegate { CopyLogResult(); };

            Button clearButton = MakeSmallButton("清空", 64);
            clearButton.Click += delegate { ClearLog(); };

            _logFilterNoise = MakeLogCheckBox("过滤噪音", true);
            _logOnlyErrors = MakeLogCheckBox("只看错误", false);
            _logGroupTrace = MakeLogCheckBox("按 traceId 分组", true);
            _logFilterNoise.CheckedChanged += delegate { LogOptionsChanged(); };
            _logOnlyErrors.CheckedChanged += delegate { LogOptionsChanged(); };
            _logGroupTrace.CheckedChanged += delegate { LogOptionsChanged(); };

            buttons.Controls.Add(pasteButton);
            buttons.Controls.Add(analyzeButton);
            buttons.Controls.Add(_logAiButton);
            buttons.Controls.Add(copyButton);
            buttons.Controls.Add(clearButton);
            buttons.Controls.Add(_logFilterNoise);
            buttons.Controls.Add(_logOnlyErrors);
            buttons.Controls.Add(_logGroupTrace);
            layout.Controls.Add(buttons, 0, 1);

            _logStatusLabel = new Label();
            _logStatusLabel.Text = "等待日志。粘贴后点「开始分析」，或按 Ctrl+Enter。";
            _logStatusLabel.ForeColor = MutedColor;
            _logStatusLabel.AutoSize = false;
            _logStatusLabel.Height = 22;
            _logStatusLabel.Dock = DockStyle.Fill;
            _logStatusLabel.AutoEllipsis = true;
            _logStatusLabel.Margin = new Padding(0, 0, 0, 6);
            layout.Controls.Add(_logStatusLabel, 0, 2);

            SplitContainer split = new SplitContainer();
            split.Dock = DockStyle.Fill;
            split.Orientation = Orientation.Horizontal;
            split.SplitterWidth = 6;
            split.Panel1MinSize = 70;
            split.Panel2MinSize = 90;
            split.BackColor = PageBack;
            split.Margin = new Padding(0);

            TableLayoutPanel inputLayout = new TableLayoutPanel();
            inputLayout.Dock = DockStyle.Fill;
            inputLayout.ColumnCount = 1;
            inputLayout.RowCount = 2;
            inputLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            inputLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            inputLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
            inputLayout.Margin = new Padding(0);

            Label inputLabel = new Label();
            inputLabel.Text = "日志原文（几行到几千行都可以）";
            inputLabel.ForeColor = LabelColor;
            inputLabel.AutoSize = true;
            inputLabel.Margin = new Padding(0, 0, 0, 4);
            inputLayout.Controls.Add(inputLabel, 0, 0);

            _logInput = new TextBox();
            _logInput.Multiline = true;
            _logInput.AcceptsReturn = true;
            _logInput.AcceptsTab = true;
            _logInput.MaxLength = int.MaxValue;
            _logInput.WordWrap = false;
            _logInput.ScrollBars = ScrollBars.Both;
            _logInput.Font = new Font("Consolas", 9.5F);
            _logInput.BackColor = Color.White;
            _logInput.ForeColor = TitleColor;
            _logInput.BorderStyle = BorderStyle.FixedSingle;
            _logInput.Dock = DockStyle.Fill;
            _logInput.Margin = new Padding(0);
            _logInput.AllowDrop = true;
            _logInput.DragEnter += HandleDragEnter;
            _logInput.DragDrop += HandleDragDrop;
            _logInput.TextChanged += delegate
            {
                _logGeneration++;
                if (_logBusy) SetLogBusy(false);
                if (_logOutput != null && _logOutput.TextLength > 0 && _logInput.Focused)
                {
                    SetLogStatus("日志有修改，点「开始分析」更新摘要。", MutedColor);
                }
            };
            inputLayout.Controls.Add(_logInput, 0, 1);
            split.Panel1.Controls.Add(inputLayout);

            TableLayoutPanel outputLayout = new TableLayoutPanel();
            outputLayout.Dock = DockStyle.Fill;
            outputLayout.ColumnCount = 1;
            outputLayout.RowCount = 2;
            outputLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            outputLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            outputLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
            outputLayout.Margin = new Padding(0);

            Label outputLabel = new Label();
            outputLabel.Text = "人话摘要（本机规则分析；勾选「AI 解读」才会把去噪并打码后的日志发到你配置的服务）";
            outputLabel.ForeColor = LabelColor;
            outputLabel.AutoSize = true;
            outputLabel.Margin = new Padding(0, 0, 0, 4);
            outputLayout.Controls.Add(outputLabel, 0, 0);

            _logOutput = new TextBox();
            _logOutput.Multiline = true;
            _logOutput.ReadOnly = true;
            _logOutput.MaxLength = int.MaxValue;
            _logOutput.WordWrap = true;
            _logOutput.ScrollBars = ScrollBars.Vertical;
            _logOutput.Font = new Font("Microsoft YaHei UI", 9.5F);
            _logOutput.BackColor = Color.White;
            _logOutput.ForeColor = TitleColor;
            _logOutput.BorderStyle = BorderStyle.FixedSingle;
            _logOutput.Dock = DockStyle.Fill;
            _logOutput.Margin = new Padding(0);
            AttachOutputMenu(_logOutput, false);
            outputLayout.Controls.Add(_logOutput, 0, 1);
            split.Panel2.Controls.Add(outputLayout);

            bool splitInitialized = false;
            split.SizeChanged += delegate
            {
                if (splitInitialized || split.Height < 200) return;
                int distance = (int)(split.Height * 0.42);
                int max = split.Height - split.Panel2MinSize - split.SplitterWidth;
                if (distance < split.Panel1MinSize) distance = split.Panel1MinSize;
                if (distance > max) distance = max;
                if (distance < split.Panel1MinSize) return;
                try
                {
                    split.SplitterDistance = distance;
                    splitInitialized = true;
                }
                catch (InvalidOperationException)
                {
                }
            };

            layout.Controls.Add(split, 0, 3);
            page.Controls.Add(layout);
            return page;
        }

        private static CheckBox MakeLogCheckBox(string text, bool isChecked)
        {
            CheckBox box = new CheckBox();
            box.Text = text;
            box.Checked = isChecked;
            box.AutoSize = true;
            box.ForeColor = LabelColor;
            box.Cursor = Cursors.Hand;
            box.Margin = new Padding(10, 8, 4, 0);
            return box;
        }

        private void UpdateWorkspaceForTab()
        {
            if (_rootLayout == null || _tabs == null || _logPage == null) return;
            bool logActive = _tabs.SelectedTab == _logPage;
            if (_curlSectionLabel != null) _curlSectionLabel.Visible = !logActive;
            if (_inputBox != null) _inputBox.Visible = !logActive;
            if (_curlToolbar != null) _curlToolbar.Visible = !logActive;

            if (logActive)
            {
                _rootLayout.RowStyles[2].SizeType = SizeType.Absolute;
                _rootLayout.RowStyles[2].Height = 0F;
                _rootLayout.RowStyles[4].SizeType = SizeType.Percent;
                _rootLayout.RowStyles[4].Height = 100F;
            }
            else
            {
                _rootLayout.RowStyles[2].SizeType = SizeType.Percent;
                _rootLayout.RowStyles[2].Height = 42F;
                _rootLayout.RowStyles[4].SizeType = SizeType.Percent;
                _rootLayout.RowStyles[4].Height = 58F;
            }
            _rootLayout.PerformLayout();
            if (logActive && _logInput != null) _logInput.Focus();
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (keyData == (Keys.Control | Keys.Enter))
            {
                if (_tabs != null && _logPage != null && _tabs.SelectedTab == _logPage)
                {
                    StartLogAnalysis(false);
                    return true;
                }
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
            if (_logOutput != null && _logOutput.Focused) return _logOutput;
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
                bool logActive = _tabs != null && _logPage != null && _tabs.SelectedTab == _logPage;
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

                        if (logActive && _logInput != null)
                        {
                            _logInput.Text = ReadTextFile(files[0]);
                            SetLogStatus("已读取 " + Path.GetFileName(files[0]) + "，开始本机分析…", MutedColor);
                            StartLogAnalysis(false);
                            return;
                        }

                        _inputBox.Text = ReadTextFile(files[0]);
                        SetStatus("已读取 " + Path.GetFileName(files[0]) + "，自动解析中…", MutedColor);
                    }
                }
                else if (e.Data.GetDataPresent(DataFormats.Text))
                {
                    string text = (string)e.Data.GetData(DataFormats.Text);
                    if (logActive && _logInput != null)
                    {
                        _logInput.Text = text;
                        StartLogAnalysis(false);
                    }
                    else
                    {
                        _inputBox.Text = text;
                    }
                }
            }
            catch (Exception ex)
            {
                if (_tabs != null && _logPage != null && _tabs.SelectedTab == _logPage)
                {
                    SetLogStatus("读取拖入内容失败：" + ex.Message, ErrorColor);
                }
                else
                {
                    SetStatus("读取拖入内容失败：" + ex.Message, ErrorColor);
                }
            }
        }

        private static string ReadTextFile(string path)
        {
            byte[] bytes = File.ReadAllBytes(path);
            if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            {
                return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
            }
            if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
            {
                return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
            }
            if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
            {
                return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);
            }
            try
            {
                return new UTF8Encoding(false, true).GetString(bytes);
            }
            catch (DecoderFallbackException)
            {
                // Windows 中文环境的日志常是 GBK，UTF-8 解不开时按系统 ANSI 代码页读
                return Encoding.Default.GetString(bytes);
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
            string harWarning = "";

            try
            {
                if (isHar || isList)
                {
                    requests = isHar ? HarParser.Parse(text, out harWarning) : RequestListParser.Parse(text);
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
                string detailsText = UrlExtractor.FormatDetails(
                    requests, allUrls, mask, _keepResponses.Checked);
                if (harWarning.Length > 0)
                {
                    detailsText = "注意：" + harWarning + "\r\n\r\n" + detailsText;
                }
                _detailBox.Text = detailsText;
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

            if (isHar) summary = (harWarning.Length > 0 ? "HAR（部分损坏）：" : "HAR：") + summary;
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
                string done = harWarning.Length > 0 ? "，已按可用部分解析" : "，已自动解析";
                SetStatus(summary + done, failureCount > 0
                    ? ErrorColor
                    : (harWarning.Length > 0 ? MutedColor : OkColor));
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

            bool useAi = _ocrModeBox != null && _ocrModeBox.SelectedIndex == 1 &&
                         _aiSettings != null && _aiSettings.IsConfigured;
            AiOcrSettings settings = useAi ? _aiSettings : null;
            SetOcrStatus(
                useAi
                    ? "正在用 AI 识别图片文字…（图片会发送到 " + HostOf(settings.BaseUrl) + "）"
                    : "正在识别图片文字…（大截图可能需要几秒）",
                MutedColor);

            // OCR 比较慢，放到后台线程，界面不卡顿；工作线程用独立副本
            Bitmap work = new Bitmap(bitmap);
            ThreadPool.QueueUserWorkItem(delegate
            {
                string text;
                string error;
                string note = null;
                bool ok;
                if (useAi)
                {
                    ok = AiOcrRunner.TryRecognize(work, settings, out text, out error);
                    if (!ok)
                    {
                        // 网络 / Key / 限流等失败时自动回退本地，至少给用户一份可用文字
                        string localText;
                        string localError;
                        if (OcrRunner.TryRecognize(work, out localText, out localError) &&
                            !string.IsNullOrWhiteSpace(localText))
                        {
                            text = localText;
                            ok = true;
                            note = "AI 识别失败（" + error + "），已自动改用本地 OCR，准确率可能较低。";
                        }
                        else
                        {
                            error = "AI：" + error + "；本地：" +
                                    (string.IsNullOrEmpty(localError) ? "没有识别出文字" : localError);
                        }
                    }
                }
                else
                {
                    ok = OcrRunner.TryRecognize(work, out text, out error);
                }
                work.Dispose();

                try
                {
                    BeginInvoke(new MethodInvoker(delegate
                    {
                        _ocrBusy = false;
                        if (!ok)
                        {
                            _ocrBox.Text = "";
                            SetOcrStatus((useAi ? "AI 识别失败：" : "识别失败：") + error, ErrorColor);
                            return;
                        }
                        if (string.IsNullOrWhiteSpace(text))
                        {
                            _ocrBox.Text = "";
                            SetOcrStatus("没有识别出文字。换一张更清晰、文字更大的截图试试。", ErrorColor);
                            return;
                        }

                        _ocrBox.Text = text;
                        if (!string.IsNullOrEmpty(note))
                        {
                            SetOcrStatus(note, ErrorColor);
                        }
                        else
                        {
                            SetOcrStatus(
                                string.Format("{0}：{1}（{2} 个字符），可直接复制给 AI。",
                                    useAi ? "AI 识别完成" : "识别完成", sourceLabel, text.Length),
                                OkColor);
                        }
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
            SetOcrStatus(
                _ocrModeBox != null && _ocrModeBox.SelectedIndex == 1
                    ? "等待图片。AI 识别会把图片发送到所配置的服务。"
                    : "等待图片。本地识别在本机完成，不联网。",
                MutedColor);
        }

        private void SetOcrStatus(string text, Color color)
        {
            if (_ocrStatusLabel == null) return;
            _ocrStatusLabel.Text = text;
            _ocrStatusLabel.ForeColor = color;
        }

        private void OcrModeChanged()
        {
            if (_ocrModeChanging || _ocrModeBox == null) return;
            _ocrModeChanging = true;
            try
            {
                if (_ocrModeBox.SelectedIndex == 1)
                {
                    if (_aiSettings == null) _aiSettings = AiOcrSettings.Load();
                    if (!_aiSettings.IsConfigured)
                    {
                        SetOcrStatus("AI 识别需要先配置接口和 API Key。", MutedColor);
                        ShowAiSettings();
                    }
                    if (_aiSettings == null || !_aiSettings.IsConfigured)
                    {
                        _ocrModeBox.SelectedIndex = 0;
                        SetOcrStatus("已保持本地 OCR（离线，不上传图片）。", MutedColor);
                        return;
                    }

                    _aiSettings.UseAi = true;
                    _aiSettings.SaveUseAiFlag();
                    SetOcrStatus(
                        "已启用 AI 识别：图片会发送到 " + HostOf(_aiSettings.BaseUrl) + "，识别通常比本地更准。",
                        MutedColor);
                }
                else
                {
                    if (_aiSettings != null)
                    {
                        _aiSettings.UseAi = false;
                        _aiSettings.SaveUseAiFlag();
                    }
                    SetOcrStatus("已切换回本地 OCR（离线，不上传图片）。", MutedColor);
                }
            }
            finally
            {
                _ocrModeChanging = false;
            }
        }

        private bool ShowAiSettings()
        {
            if (_aiSettings == null) _aiSettings = AiOcrSettings.Load();
            using (AiOcrSettingsForm dialog = new AiOcrSettingsForm(_aiSettings))
            {
                if (dialog.ShowDialog(this) != DialogResult.OK)
                {
                    return _aiSettings != null && _aiSettings.IsConfigured;
                }

                _aiSettings = dialog.Settings;
                _aiSettings.UseAi = _ocrModeBox != null && _ocrModeBox.SelectedIndex == 1;
                string error;
                if (!_aiSettings.Save(out error))
                {
                    SetOcrStatus(error, ErrorColor);
                    return false;
                }
                SetOcrStatus("AI 设置已保存（API Key 已用 Windows DPAPI 加密）。", OkColor);
                return _aiSettings.IsConfigured;
            }
        }

        private void ShowOcrPreview()
        {
            if (_ocrPreview == null || _ocrPreview.Image == null)
            {
                SetOcrStatus("还没有图片，先粘贴或打开一张截图。", ErrorColor);
                return;
            }

            try
            {
                using (OcrPreviewForm preview = new OcrPreviewForm(_ocrPreview.Image, "截图识别"))
                {
                    preview.ShowDialog(this);
                }
            }
            catch (Exception ex)
            {
                SetOcrStatus("打开大图失败：" + ex.Message, ErrorColor);
            }
        }

        private static string HostOf(string url)
        {
            try
            {
                return new Uri(url).Host;
            }
            catch (Exception)
            {
                return url;
            }
        }

        private void SetLogStatus(string text, Color color)
        {
            if (_logStatusLabel == null) return;
            _logStatusLabel.Text = text;
            _logStatusLabel.ForeColor = color;
        }

        private void SetLogBusy(bool busy)
        {
            _logBusy = busy;
            if (_logAiButton == null) return;
            _logAiButton.Enabled = !busy;
            _logAiButton.Text = busy ? "AI 解读中…" : "AI 解读（联网）";
        }

        private void PasteLogFromClipboard()
        {
            if (_logInput == null) return;
            string text = GetClipboardText();
            if (string.IsNullOrEmpty(text))
            {
                SetLogStatus("剪贴板里没有文本。先把日志复制一下再试。", ErrorColor);
                return;
            }
            _logInput.Text = text;
            _logInput.SelectionStart = 0;
            _logInput.SelectionLength = 0;
            StartLogAnalysis(false);
        }

        private void CopyLogResult()
        {
            if (_logOutput == null || string.IsNullOrWhiteSpace(_logOutput.Text))
            {
                SetLogStatus("还没有分析结果可复制。", ErrorColor);
                return;
            }
            try
            {
                SetClipboardText(_logOutput.Text);
                SetLogStatus("分析结果已复制到剪贴板。", OkColor);
            }
            catch (Exception ex)
            {
                SetLogStatus(ex.Message, ErrorColor);
            }
        }

        private void ClearLog()
        {
            _logGeneration++;
            SetLogBusy(false);
            if (_logInput != null) _logInput.Text = "";
            if (_logOutput != null) _logOutput.Text = "";
            SetLogStatus("等待日志。粘贴或拖入 .log / .txt 文件。", MutedColor);
            if (_logInput != null && _logInput.CanFocus) _logInput.Focus();
        }

        private void LogOptionsChanged()
        {
            if (_logInput == null || _logOutput == null) return;
            if (string.IsNullOrWhiteSpace(_logInput.Text) || _logOutput.TextLength == 0) return;
            StartLogAnalysis(false);
        }

        internal string RunLogUiSelftest()
        {
            if (_tabs == null || _logPage == null || _logInput == null || _logOutput == null ||
                _logStatusLabel == null || _logOnlyErrors == null)
            {
                return "日志页控件没有创建";
            }

            _tabs.SelectedTab = _logPage;
            // 无窗口句柄时 TabControl 的 SelectedIndexChanged 不一定触发，这里直接调布局逻辑
            UpdateWorkspaceForTab();
            if (_rootLayout.RowStyles[2].Height > 0F)
            {
                return "切到日志页后 curl 输入区仍然占高度";
            }

            _logInput.Text =
                "2026-10-28 17:00:00.000 INFO [http-1] [traceId=uitest123456] POST /gift-service/guangqi/sendMessage phone=15219896454";
            StartLogAnalysis(false);
            if (_logOutput.TextLength == 0) return "分析结果为空：" + _logStatusLabel.Text;
            if (!_logOutput.Text.Contains("152****6454")) return "界面结果缺少手机号打码";
            if (!_logOutput.Text.Contains("POST /gift-service/guangqi/sendMessage")) return "界面结果缺少接口";
            if (!_logStatusLabel.Text.Contains("本机分析完成")) return "状态栏没有完成提示";

            _logOnlyErrors.Checked = true;
            if (!_logOutput.Text.Contains("只看错误")) return "只看错误视图没有生效";
            _logOnlyErrors.Checked = false;

            if (_keepResponses == null) return "保留响应勾选框没有创建";

            CurlRequest responseRequest = new CurlRequest();
            responseRequest.Source = "har";
            responseRequest.Url = "https://example.com/response-check";
            responseRequest.Method = "GET";
            responseRequest.Status = 500;
            responseRequest.StatusText = "Server Error";
            responseRequest.ResponseHeaders.Add(
                new KeyValuePair<string, string>("Content-Type", "application/json"));
            responseRequest.ResponseBody = "{\"code\":500}";
            List<CurlRequest> responseRequests = new List<CurlRequest>();
            responseRequests.Add(responseRequest);
            List<string> responseUrls = new List<string>();
            responseUrls.Add(responseRequest.Url);

            string compactDetails = UrlExtractor.FormatDetails(
                responseRequests, responseUrls, true, false);
            string fullDetails = UrlExtractor.FormatDetails(
                responseRequests, responseUrls, true, true);
            if (compactDetails.Contains("响应体:")) return "未勾选保留响应时仍然输出了响应";
            if (!fullDetails.Contains("响应体:") || !fullDetails.Contains("\"code\": 500"))
                return "勾选保留响应后没有输出响应体";
            if (!fullDetails.Contains("响应头:")) return "勾选保留响应后没有输出响应头";

            _tabs.SelectedIndex = 0;
            UpdateWorkspaceForTab();
            if (_rootLayout.RowStyles[2].Height <= 0F) return "切回其他页后 curl 输入区高度没有恢复";
            return "";
        }

        private void StartLogAnalysis(bool useAi)
        {
            if (_logInput == null || _logOutput == null) return;
            if (useAi && _logBusy)
            {
                SetLogStatus("AI 正在解读上一段日志，等这次完成再点。", MutedColor);
                return;
            }

            string raw = _logInput.Text;
            if (string.IsNullOrWhiteSpace(raw))
            {
                SetLogStatus("先粘贴一段日志。", ErrorColor);
                return;
            }

            LogOptions options = new LogOptions();
            options.FilterNoise = _logFilterNoise == null || _logFilterNoise.Checked;
            options.OnlyErrors = _logOnlyErrors != null && _logOnlyErrors.Checked;
            options.GroupByTrace = _logGroupTrace == null || _logGroupTrace.Checked;

            LogAnalysis result;
            Cursor previous = Cursor;
            Cursor = Cursors.WaitCursor;
            try
            {
                result = LogExplainer.Analyze(raw, options);
            }
            catch (Exception ex)
            {
                SetLogStatus("分析失败：" + ex.Message, ErrorColor);
                return;
            }
            finally
            {
                Cursor = previous;
            }

            int generation = ++_logGeneration;
            if (_logBusy) SetLogBusy(false);
            _logOutput.Text = result.Report;
            _logOutput.SelectionStart = 0;
            _logOutput.SelectionLength = 0;

            Color color = (result.ErrorCount > 0 || result.ExceptionCount > 0)
                ? ErrorColor
                : (result.WarnCount > 0 ? Color.FromArgb(230, 145, 0) : MutedColor);
            SetLogStatus(LogExplainer.DescribeResult(result), color);

            if (!useAi) return;

            if (_aiSettings == null) _aiSettings = AiOcrSettings.Load();
            if (!_aiSettings.IsConfigured)
            {
                SetLogStatus("AI 解读需要先配置接口和 API Key。本机分析结果已生成。", MutedColor);
                if (!ShowAiSettings() || _aiSettings == null || !_aiSettings.IsConfigured)
                {
                    SetLogStatus("本机分析结果已保留；AI 还没配置，联网解读已取消。", MutedColor);
                    return;
                }
            }

            AiOcrSettings settings = _aiSettings;
            string host = HostOf(settings.BaseUrl);
            string localReport = result.Report;
            string excerpt = result.AiExcerpt;
            int busyGeneration = generation;
            _logBusyGeneration = busyGeneration;
            SetLogBusy(true);
            SetLogStatus(
                "本机分析完成；正在请 AI 用中文解读（发送到 " + host + "，手机号等已打码）…",
                MutedColor);

            ThreadPool.QueueUserWorkItem(delegate
            {
                string aiText = "";
                string aiError = "";
                bool ok = false;
                try
                {
                    ok = LogExplainer.TryAiExplain(settings, localReport, excerpt, out aiText, out aiError);
                }
                catch (Exception ex)
                {
                    aiError = ex.Message;
                }

                try
                {
                    BeginInvoke((MethodInvoker)delegate
                    {
                        if (IsDisposed || Disposing) return;
                        if (_logBusyGeneration == busyGeneration) SetLogBusy(false);
                        if (busyGeneration != _logGeneration) return;

                        if (ok)
                        {
                            _logOutput.Text = aiText;
                            _logOutput.SelectionStart = 0;
                            _logOutput.SelectionLength = 0;
                            SetLogStatus(
                                "AI 解读完成（" + host + "）。点「开始分析」可切回本机摘要。",
                                OkColor);
                        }
                        else
                        {
                            SetLogStatus("AI 解读失败：" + aiError + " 本机分析结果已保留。", ErrorColor);
                        }
                    });
                }
                catch (ObjectDisposedException)
                {
                }
                catch (InvalidOperationException)
                {
                }
            });
        }

        private Form EnsureHelpDialog()
        {
            if (_helpDialog != null && !_helpDialog.IsDisposed) return _helpDialog;

            string help =
                "复制格式说明（Chrome / Edge 开发者工具）\r\n\r\n" +
                "先看场景：\r\n" +
                "· 只要链接 → Copy link address\r\n" +
                "· 把一个请求完整粘到本工具 → Copy as cURL (bash) 或 Copy as cURL (cmd)\r\n" +
                "· 要同时看请求和响应、状态码 → Copy all as HAR，或 Network 面板 Save all as HAR\r\n" +
                "· 只看某个接口的返回 → Copy response，再粘到「响应」页签格式化 JSON\r\n\r\n" +
                "一、单项复制（选中一条请求，右键）\r\n\r\n" +
                "Copy link address\r\n" +
                "  只复制 URL，不含请求头、Cookie、请求体和响应。适合拿链接。\r\n\r\n" +
                "Copy request headers\r\n" +
                "  只复制请求头（name: value 多行），不含 URL 和 body。适合排查鉴权、Cookie、Content-Type。\r\n\r\n" +
                "Copy response headers\r\n" +
                "  只复制响应头，不含响应体。适合看网关、CDN、跨域、traceId。\r\n\r\n" +
                "Copy response\r\n" +
                "  只复制响应体。JSON 可以粘到本工具「响应」页签格式化；不含状态码和响应头。\r\n\r\n" +
                "Copy as PowerShell\r\n" +
                "  生成 PowerShell 的 Invoke-WebRequest 命令，含 URL、方法、请求头、body；不含响应。\r\n\r\n" +
                "Copy as fetch\r\n" +
                "  生成浏览器 fetch 代码，含 URL、方法、请求头、body；不含响应。\r\n\r\n" +
                "Copy as Node.js fetch\r\n" +
                "  生成 Node.js 的 fetch 代码，含 URL、方法、请求头、body；不含响应。\r\n\r\n" +
                "Copy as cURL (cmd)\r\n" +
                "  生成 Windows cmd 风格的 curl 命令，含 URL、方法、请求头、body；不含响应。\r\n\r\n" +
                "Copy as cURL (bash)\r\n" +
                "  生成 bash 风格的 curl 命令，含 URL、方法、请求头、body；不含响应。\r\n" +
                "  粘到本工具最稳；cmd / bash 两种本工具都能识别。\r\n\r\n" +
                "二、批量复制（选中多条请求，右键）\r\n\r\n" +
                "Copy all as PowerShell / fetch / Node.js fetch / cURL (cmd) / cURL (bash)\r\n" +
                "  把选中的多条请求一次性生成对应脚本或命令。每条仍然只有请求侧信息，没有响应。\r\n" +
                "  一次粘贴多条 curl，本工具也能逐条拆开。\r\n\r\n" +
                "Copy all as HAR\r\n" +
                "  把选中的请求导出成 HAR：URL、方法、请求头、请求体、状态码、响应头、响应体、耗时都在里面。\r\n" +
                "  想看响应、判断失败请求，优先用这个；也可以用 Network 面板的 Save all as HAR 保存成文件后拖进来。\r\n\r\n" +
                "三、本工具怎么用\r\n\r\n" +
                "· 粘贴 HAR → 自动识别请求和响应；勾选「保留响应」后，「请求详情」会显示响应头和响应体。\r\n" +
                "· 粘贴 cURL → 能看请求参数和请求头，但看不到响应，因为 cURL 本身不含响应。\r\n" +
                "· 粘贴 response → 到「响应」页签，点格式化 JSON。\r\n" +
                "· HAR / cURL 里可能带 Cookie、Token、手机号；本工具默认打码，发给别人前仍建议自己再检查一遍。";

            Form dialog = new Form();
            dialog.Text = "复制格式说明";
            dialog.ClientSize = new Size(780, 660);
            dialog.MinimumSize = new Size(620, 480);
            dialog.StartPosition = FormStartPosition.CenterParent;
            dialog.MinimizeBox = false;
            dialog.MaximizeBox = false;
            dialog.ShowIcon = false;
            dialog.ShowInTaskbar = false;
            dialog.BackColor = Color.White;
            dialog.Font = Font;
            dialog.KeyPreview = true;
            dialog.KeyDown += delegate(object sender, KeyEventArgs e)
            {
                if (e.KeyCode != Keys.Escape) return;
                dialog.Hide();
                e.SuppressKeyPress = true;
            };
            dialog.FormClosing += delegate(object sender, FormClosingEventArgs e)
            {
                if (e.CloseReason == CloseReason.UserClosing || e.CloseReason == CloseReason.None)
                {
                    e.Cancel = true;
                    dialog.Hide();
                }
            };

            TextBox box = new TextBox();
            box.Multiline = true;
            box.ReadOnly = true;
            box.ScrollBars = ScrollBars.Vertical;
            box.Dock = DockStyle.Fill;
            box.BorderStyle = BorderStyle.None;
            box.BackColor = Color.White;
            box.ForeColor = TitleColor;
            box.Font = new Font("Microsoft YaHei UI", 9F);
            box.Text = help;
            box.SelectionStart = 0;
            box.SelectionLength = 0;

            Panel textPanel = new Panel();
            textPanel.Dock = DockStyle.Fill;
            textPanel.Padding = new Padding(18, 16, 18, 0);
            textPanel.Controls.Add(box);

            FlowLayoutPanel bottom = new FlowLayoutPanel();
            bottom.Dock = DockStyle.Bottom;
            bottom.Height = 54;
            bottom.FlowDirection = FlowDirection.RightToLeft;
            bottom.Padding = new Padding(0, 10, 18, 8);

            Button close = MakeButton("关闭", true, 88);
            close.DialogResult = DialogResult.None;
            close.Margin = new Padding(0);
            close.Click += delegate { dialog.Hide(); };
            bottom.Controls.Add(close);

            dialog.Controls.Add(textPanel);
            dialog.Controls.Add(bottom);
            _helpDialog = dialog;
            return dialog;
        }

        private void PreloadHelpDialog()
        {
            try
            {
                // 先创建窗口句柄并完成一次布局，把首次打开的 JIT / 布局开销挪到启动空闲期
                Form dialog = EnsureHelpDialog();
                if (dialog.Handle == IntPtr.Zero) return;
                dialog.PerformLayout();
            }
            catch (Exception)
            {
            }
        }

        private void ShowCopyFormatHelp()
        {
            Form dialog = EnsureHelpDialog();
            if (dialog.Visible)
            {
                dialog.BringToFront();
                dialog.Activate();
                return;
            }
            dialog.Show(this);
            dialog.BringToFront();
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
    $lineData = @()
    foreach ($ocrLine in $result.Lines) {
        $words = @()
        foreach ($word in $ocrLine.Words) {
            $rect = $word.BoundingRect
            $words += [pscustomobject]@{ t = $word.Text; x = $rect.X; w = $rect.Width; h = $rect.Height }
        }
        $lineData += [pscustomobject]@{ words = $words }
    }
    $json = [pscustomobject]@{ lines = $lineData } | ConvertTo-Json -Depth 5 -Compress
    [System.IO.File]::WriteAllText($env:CURL2URL_OCR_OUT, $json, [System.Text.UTF8Encoding]::new($false))
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

                string raw = File.ReadAllText(outPath, Encoding.UTF8);
                string merged = BuildTextFromJson(raw);
                text = NormalizeOcrText(merged == null ? raw : merged);
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

        // OCR 引擎会把 "deserial ize:" 这类单词中间的笔画断开、当成两个词。
        // 根据每个词的包围盒，把间距明显小于字高的相邻词重新拼回去；实测对这个报错截图
        // 的断词（deserial ize、ne sted、except i on、org. spr i ngframework）效果很明显。
        internal sealed class OcrWordBox
        {
            public readonly string Text;
            public readonly double X;
            public readonly double Width;
            public readonly double Height;

            public OcrWordBox(string text, double x, double width, double height)
            {
                Text = text;
                X = x;
                Width = width;
                Height = height;
            }
        }

        internal static string JoinOcrWords(IList<OcrWordBox> words)
        {
            if (words == null || words.Count == 0) return "";
            StringBuilder builder = new StringBuilder();
            for (int i = 0; i < words.Count; i++)
            {
                OcrWordBox current = words[i];
                if (i > 0)
                {
                    OcrWordBox previous = words[i - 1];
                    double gap = current.X - (previous.X + previous.Width);
                    if (gap > 0 && !HasWideCharacter(previous.Text) && !HasWideCharacter(current.Text))
                    {
                        // 正常单词间的空格约为字高的 0.5 倍以上；小于这个值多半是断词
                        double limit = 0.5 * Math.Min(previous.Height, current.Height);
                        if (gap > limit) builder.Append(' ');
                    }
                }
                builder.Append(current.Text);
            }
            return builder.ToString();
        }

        private static bool HasWideCharacter(string text)
        {
            if (string.IsNullOrEmpty(text)) return false;
            foreach (char c in text)
            {
                if ((c >= '　' && c <= '〿') ||
                    (c >= '㐀' && c <= '䶿') ||
                    (c >= '一' && c <= '鿿') ||
                    (c >= '＀' && c <= '￯')) return true;
            }
            return false;
        }

        // PowerShell 输出 { lines: [ { words: [ {t,x,w,h} ] } ] }；只有一行/一个词时
        // ConvertTo-Json 可能退化成对象而不是数组，所以统一用 AsObjectList 兼容。
        private static string BuildTextFromJson(string json)
        {
            if (string.IsNullOrEmpty(json)) return null;
            if (json.TrimStart().Length == 0 || json.TrimStart()[0] != '{') return null;
            try
            {
                JavaScriptSerializer serializer = new JavaScriptSerializer();
                Dictionary<string, object> root = serializer.Deserialize<Dictionary<string, object>>(json);
                if (root == null || !root.ContainsKey("lines")) return null;

                List<Dictionary<string, object>> lines = AsObjectList(root["lines"]);
                StringBuilder output = new StringBuilder();
                foreach (Dictionary<string, object> line in lines)
                {
                    if (!line.ContainsKey("words")) continue;
                    List<OcrWordBox> boxes = new List<OcrWordBox>();
                    foreach (Dictionary<string, object> word in AsObjectList(line["words"]))
                    {
                        string wordText = AsText(word, "t");
                        if (string.IsNullOrEmpty(wordText)) continue;
                        boxes.Add(new OcrWordBox(wordText, AsNumber(word, "x"), AsNumber(word, "w"), AsNumber(word, "h")));
                    }
                    if (boxes.Count == 0) continue;
                    string lineText = JoinOcrWords(boxes);
                    if (lineText.Length == 0) continue;
                    if (output.Length > 0) output.Append('\n');
                    output.Append(lineText);
                }
                return output.ToString();
            }
            catch (Exception)
            {
                return null;
            }
        }

        internal static List<Dictionary<string, object>> AsObjectList(object value)
        {
            List<Dictionary<string, object>> result = new List<Dictionary<string, object>>();
            if (value == null) return result;
            Dictionary<string, object> single = value as Dictionary<string, object>;
            if (single != null)
            {
                result.Add(single);
                return result;
            }
            IEnumerable list = value as IEnumerable;
            if (list == null) return result;
            foreach (object item in list)
            {
                Dictionary<string, object> dictionary = item as Dictionary<string, object>;
                if (dictionary != null) result.Add(dictionary);
            }
            return result;
        }

        private static string AsText(Dictionary<string, object> dictionary, string key)
        {
            if (dictionary == null || !dictionary.ContainsKey(key)) return "";
            object value = dictionary[key];
            return value == null ? "" : Convert.ToString(value, CultureInfo.InvariantCulture);
        }

        private static double AsNumber(Dictionary<string, object> dictionary, string key)
        {
            if (dictionary == null || !dictionary.ContainsKey(key)) return 0;
            object value = dictionary[key];
            if (value == null) return 0;
            try
            {
                return Convert.ToDouble(value, CultureInfo.InvariantCulture);
            }
            catch (Exception)
            {
                return 0;
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

    // 可选的 AI 识别：调用任意 OpenAI 兼容的视觉模型（豆包 / 通义千问 / OpenAI / 自建网关）。
    // Windows 自带 OCR 对 8~10px 的代码截图只能做到「大致可读」；视觉模型能结合上下文
    // 纠正断词、l/1、O/0 以及弹窗遮挡，适合把报错截图转成给 AI 的文本。
    internal sealed class AiOcrSettings
    {
        public const string ProviderDoubao = "doubao";
        public const string ProviderQwen = "qwen";
        public const string ProviderOpenAi = "openai";
        public const string ProviderCustom = "custom";

        private const string DoubaoUrl = "https://ark.cn-beijing.volces.com/api/v3/chat/completions";
        private const string DoubaoModel = "doubao-seed-2-0-lite-260215";
        private const string QwenUrl = "https://dashscope.aliyuncs.com/compatible-mode/v1/chat/completions";
        private const string QwenModel = "qwen-vl-max";
        private const string OpenAiUrl = "https://api.openai.com/v1/chat/completions";
        private const string OpenAiModel = "gpt-4o-mini";

        public string Provider = ProviderDoubao;
        public string BaseUrl = DoubaoUrl;
        public string Model = DoubaoModel;
        public string ApiKey = "";
        public int TimeoutSeconds = 60;
        public bool UseAi = false;

        public bool IsConfigured
        {
            get
            {
                if (string.IsNullOrWhiteSpace(BaseUrl) || string.IsNullOrWhiteSpace(Model)) return false;
                // 自建 / 本地 OpenAI 兼容服务可能不需要 Key；云端服务必须有
                return !string.IsNullOrEmpty(ApiKey) || Provider == ProviderCustom;
            }
        }

        public static string ConfigPath
        {
            get
            {
                string custom = Environment.GetEnvironmentVariable("CURL2URL_AI_CONFIG");
                if (!string.IsNullOrWhiteSpace(custom)) return custom;
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "curl2url", "ai-ocr.json");
            }
        }

        public static string DefaultBaseUrl(string provider)
        {
            if (provider == ProviderQwen) return QwenUrl;
            if (provider == ProviderOpenAi) return OpenAiUrl;
            if (provider == ProviderCustom) return "";
            return DoubaoUrl;
        }

        public static string DefaultModel(string provider)
        {
            if (provider == ProviderQwen) return QwenModel;
            if (provider == ProviderOpenAi) return OpenAiModel;
            if (provider == ProviderCustom) return "";
            return DoubaoModel;
        }

        // 允许用户只填 https://.../v1 这种基础地址，缺的 /chat/completions 自动补上
        public static string NormalizeEndpoint(string url)
        {
            url = (url ?? "").Trim();
            while (url.EndsWith("/")) url = url.Substring(0, url.Length - 1);
            if (url.Length == 0) return "";
            if (url.IndexOf("/chat/completions", StringComparison.OrdinalIgnoreCase) < 0)
            {
                url += "/chat/completions";
            }
            return url;
        }

        public static AiOcrSettings Load()
        {
            AiOcrSettings settings = new AiOcrSettings();
            try
            {
                string path = ConfigPath;
                if (File.Exists(path))
                {
                    JavaScriptSerializer serializer = new JavaScriptSerializer();
                    Dictionary<string, object> data = serializer.Deserialize<Dictionary<string, object>>(
                        File.ReadAllText(path, Encoding.UTF8));
                    if (data != null)
                    {
                        string provider = GetString(data, "provider");
                        if (provider == ProviderDoubao || provider == ProviderQwen ||
                            provider == ProviderOpenAi || provider == ProviderCustom)
                        {
                            settings.Provider = provider;
                        }

                        string baseUrl = GetString(data, "baseUrl");
                        settings.BaseUrl = baseUrl.Length > 0 ? baseUrl : DefaultBaseUrl(settings.Provider);
                        string model = GetString(data, "model");
                        settings.Model = model.Length > 0 ? model : DefaultModel(settings.Provider);
                        settings.ApiKey = Unprotect(GetString(data, "key"));
                        settings.TimeoutSeconds = Clamp(GetInt(data, "timeoutSeconds", 60), 10, 300);
                        settings.UseAi = GetBool(data, "useAi", false);
                    }
                }
            }
            catch (Exception)
            {
                // 配置损坏时退回默认值，不影响本地 OCR
                settings = new AiOcrSettings();
            }

            // 便于脚本 / 测试临时覆盖，不会写入配置文件
            string envUrl = Environment.GetEnvironmentVariable("CURL2URL_AI_URL");
            if (!string.IsNullOrWhiteSpace(envUrl)) settings.BaseUrl = envUrl;
            string envModel = Environment.GetEnvironmentVariable("CURL2URL_AI_MODEL");
            if (!string.IsNullOrWhiteSpace(envModel)) settings.Model = envModel;
            string envKey = Environment.GetEnvironmentVariable("CURL2URL_AI_KEY");
            if (!string.IsNullOrEmpty(envKey)) settings.ApiKey = envKey;

            return settings;
        }

        public bool Save(out string error)
        {
            error = "";
            try
            {
                BaseUrl = NormalizeEndpoint(BaseUrl);
                Dictionary<string, object> data = new Dictionary<string, object>();
                data["version"] = 1;
                data["provider"] = Provider;
                data["baseUrl"] = BaseUrl;
                data["model"] = Model;
                data["key"] = Protect(ApiKey);
                data["timeoutSeconds"] = TimeoutSeconds;
                data["useAi"] = UseAi;

                string path = ConfigPath;
                string directory = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

                JavaScriptSerializer serializer = new JavaScriptSerializer();
                File.WriteAllText(path, serializer.Serialize(data), new UTF8Encoding(false));
                return true;
            }
            catch (Exception ex)
            {
                error = "保存 AI 设置失败：" + ex.Message;
                return false;
            }
        }

        public void SaveQuietly()
        {
            string error;
            Save(out error);
        }

        // 只更新「是否启用 AI」开关，避免把 CURL2URL_AI_* 临时覆盖值写回配置文件
        public void SaveUseAiFlag()
        {
            try
            {
                string path = ConfigPath;
                Dictionary<string, object> data = null;
                if (File.Exists(path))
                {
                    try
                    {
                        JavaScriptSerializer reader = new JavaScriptSerializer();
                        data = reader.Deserialize<Dictionary<string, object>>(
                            File.ReadAllText(path, Encoding.UTF8));
                    }
                    catch (Exception)
                    {
                        data = null;
                    }
                }
                if (data == null) data = new Dictionary<string, object>();
                data["useAi"] = UseAi;

                string directory = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
                JavaScriptSerializer serializer = new JavaScriptSerializer();
                File.WriteAllText(path, serializer.Serialize(data), new UTF8Encoding(false));
            }
            catch (Exception)
            {
                // 开关保存失败不影响本次识别
            }
        }

        // API Key 只以当前 Windows 用户的 DPAPI 密文落盘，换用户 / 换机器都解不开
        private static string Protect(string value)
        {
            if (string.IsNullOrEmpty(value)) return "";
            byte[] data = ProtectedData.Protect(Encoding.UTF8.GetBytes(value), null, DataProtectionScope.CurrentUser);
            return Convert.ToBase64String(data);
        }

        private static string Unprotect(string value)
        {
            if (string.IsNullOrEmpty(value)) return "";
            try
            {
                byte[] data = ProtectedData.Unprotect(Convert.FromBase64String(value), null, DataProtectionScope.CurrentUser);
                return Encoding.UTF8.GetString(data);
            }
            catch (Exception)
            {
                // 换用户 / 换机器后 DPAPI 解不开，留空让用户重新填写
                return "";
            }
        }

        private static string GetString(Dictionary<string, object> data, string key)
        {
            if (data == null || !data.ContainsKey(key)) return "";
            object value = data[key];
            return value == null ? "" : Convert.ToString(value, CultureInfo.InvariantCulture).Trim();
        }

        private static int GetInt(Dictionary<string, object> data, string key, int fallback)
        {
            if (data == null || !data.ContainsKey(key)) return fallback;
            try
            {
                return Convert.ToInt32(data[key], CultureInfo.InvariantCulture);
            }
            catch (Exception)
            {
                return fallback;
            }
        }

        private static bool GetBool(Dictionary<string, object> data, string key, bool fallback)
        {
            if (data == null || !data.ContainsKey(key)) return fallback;
            try
            {
                return Convert.ToBoolean(data[key], CultureInfo.InvariantCulture);
            }
            catch (Exception)
            {
                return fallback;
            }
        }

        private static int Clamp(int value, int min, int max)
        {
            if (value < min) return min;
            if (value > max) return max;
            return value;
        }
    }

    internal static class AiOcrRunner
    {
        private const int MaxImageDimension = 2400;
        private const int MaxInlineBytes = 8 * 1024 * 1024;

        private const string Prompt =
            "请把这张截图里的所有文字逐字转写出来，保持原有的换行和缩进。\n" +
            "要求：\n" +
            "1. 报错信息、异常堆栈、类名、URL、JSON、代码必须原样保留，不要翻译、不要总结、不要解释。\n" +
            "2. 结合代码上下文纠正明显的识别歧义（例如 l/1、O/0、断开的单词），但不要改写原意。\n" +
            "3. 冒号（:）和分号（;）必须按原图严格区分，不要互相替换；问号、引号、括号同样按原图保留。\n" +
            "4. 如果同一段文字在图中重复出现（例如浮层和背景显示相同的报错），只保留最完整、最清晰的一份，忽略被遮挡的残缺副本，不要拼凑。\n" +
            "5. 不要输出 Markdown 代码块，不要添加任何前后缀。\n" +
            "6. 只输出转写结果。";

        public static bool TryRecognize(Bitmap source, AiOcrSettings settings, out string text, out string error)
        {
            text = "";
            error = "";

            string endpoint;
            if (!TryGetEndpoint(settings, out endpoint, out error)) return false;

            try
            {
                byte[] imageBytes;
                string mime;
                EncodeImage(source, out imageBytes, out mime);
                string dataUrl = "data:" + mime + ";base64," + Convert.ToBase64String(imageBytes);

                JavaScriptSerializer serializer = new JavaScriptSerializer();
                serializer.MaxJsonLength = 64 * 1024 * 1024;

                Dictionary<string, object> payload = new Dictionary<string, object>();
                payload["model"] = settings.Model;
                payload["temperature"] = 0;
                payload["stream"] = false;
                payload["messages"] = new object[]
                {
                    new Dictionary<string, object>
                    {
                        { "role", "user" },
                        { "content", new object[]
                            {
                                new Dictionary<string, object>
                                {
                                    { "type", "image_url" },
                                    { "image_url", new Dictionary<string, object> { { "url", dataUrl } } }
                                },
                                new Dictionary<string, object>
                                {
                                    { "type", "text" },
                                    { "text", Prompt }
                                }
                            }
                        }
                    }
                };

                string requestJson = serializer.Serialize(payload);
                string responseBody = Post(endpoint, settings, requestJson, out error);
                if (responseBody == null) return false;

                string content;
                if (!TryExtractContent(responseBody, serializer, out content, out error)) return false;
                content = CleanContent(content);
                if (string.IsNullOrWhiteSpace(content))
                {
                    error = "AI 返回了空内容，可以换一张截图或检查模型是否支持图片输入。";
                    return false;
                }

                text = content;
                return true;
            }
            catch (Exception ex)
            {
                error = "AI 识别失败：" + ex.Message;
                return false;
            }
        }

        public static bool TryChat(
            AiOcrSettings settings,
            string systemPrompt,
            string userPrompt,
            out string text,
            out string error)
        {
            text = "";
            error = "";

            string endpoint;
            if (!TryGetEndpoint(settings, out endpoint, out error)) return false;
            if (string.IsNullOrWhiteSpace(userPrompt))
            {
                error = "没有可发送给 AI 的日志内容。";
                return false;
            }

            try
            {
                JavaScriptSerializer serializer = new JavaScriptSerializer();
                serializer.MaxJsonLength = 64 * 1024 * 1024;

                List<object> messages = new List<object>();
                if (!string.IsNullOrWhiteSpace(systemPrompt))
                {
                    messages.Add(new Dictionary<string, object>
                    {
                        { "role", "system" },
                        { "content", systemPrompt }
                    });
                }
                messages.Add(new Dictionary<string, object>
                {
                    { "role", "user" },
                    { "content", userPrompt }
                });

                Dictionary<string, object> payload = new Dictionary<string, object>();
                payload["model"] = settings.Model;
                payload["temperature"] = 0;
                payload["stream"] = false;
                payload["messages"] = messages.ToArray();

                string requestJson = serializer.Serialize(payload);
                string responseBody = Post(endpoint, settings, requestJson, out error);
                if (responseBody == null) return false;

                string content;
                if (!TryExtractContent(responseBody, serializer, out content, out error)) return false;
                content = CleanContent(content);
                if (string.IsNullOrWhiteSpace(content))
                {
                    error = "AI 返回了空内容，可以稍后重试或换一个模型。";
                    return false;
                }

                text = content;
                return true;
            }
            catch (Exception ex)
            {
                error = "AI 请求失败：" + ex.Message;
                return false;
            }
        }

        private static bool TryGetEndpoint(AiOcrSettings settings, out string endpoint, out string error)
        {
            endpoint = "";
            error = "";
            if (settings == null || !settings.IsConfigured)
            {
                error = "AI 还没有配置：请在「AI 设置」里填写接口地址、模型和 API Key。";
                return false;
            }

            endpoint = AiOcrSettings.NormalizeEndpoint(settings.BaseUrl);
            if (!endpoint.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
                !endpoint.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                error = "接口地址必须以 http:// 或 https:// 开头。";
                return false;
            }
            return true;
        }

        private static void EncodeImage(Bitmap source, out byte[] bytes, out string mime)
        {
            if (source == null) throw new ArgumentNullException("source");
            int width = source.Width;
            int height = source.Height;
            if (width <= 0 || height <= 0) throw new InvalidOperationException("图片尺寸无效。");

            double scale = 1.0;
            int maxDimension = Math.Max(width, height);
            if (maxDimension > MaxImageDimension) scale = (double)MaxImageDimension / maxDimension;
            int scaledWidth = Math.Max(1, (int)Math.Round(width * scale));
            int scaledHeight = Math.Max(1, (int)Math.Round(height * scale));

            using (Bitmap prepared = new Bitmap(scaledWidth, scaledHeight, PixelFormat.Format24bppRgb))
            {
                using (Graphics graphics = Graphics.FromImage(prepared))
                {
                    graphics.CompositingQuality = CompositingQuality.HighQuality;
                    graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    graphics.SmoothingMode = SmoothingMode.HighQuality;
                    graphics.Clear(Color.White);
                    graphics.DrawImage(source, new Rectangle(0, 0, scaledWidth, scaledHeight));
                }

                using (MemoryStream stream = new MemoryStream())
                {
                    prepared.Save(stream, ImageFormat.Png);
                    bytes = stream.ToArray();
                }
                mime = "image/png";

                if (bytes.Length > MaxInlineBytes)
                {
                    using (MemoryStream stream = new MemoryStream())
                    {
                        ImageCodecInfo jpeg = FindJpegEncoder();
                        if (jpeg != null)
                        {
                            EncoderParameters parameters = new EncoderParameters(1);
                            parameters.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, 88L);
                            prepared.Save(stream, jpeg, parameters);
                            parameters.Dispose();
                        }
                        else
                        {
                            prepared.Save(stream, ImageFormat.Jpeg);
                        }
                        bytes = stream.ToArray();
                    }
                    mime = "image/jpeg";
                }
            }
        }

        private static ImageCodecInfo FindJpegEncoder()
        {
            ImageCodecInfo[] codecs = ImageCodecInfo.GetImageEncoders();
            foreach (ImageCodecInfo codec in codecs)
            {
                if (codec.FormatID == ImageFormat.Jpeg.Guid) return codec;
            }
            return null;
        }

        private static string Post(string endpoint, AiOcrSettings settings, string requestJson, out string error)
        {
            error = "";
            int timeout = Math.Max(10, Math.Min(300, settings.TimeoutSeconds)) * 1000;
            try
            {
                // .NET Framework 4.8 默认协议可能偏旧，显式打开 TLS 1.2
                ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            }
            catch (Exception)
            {
            }

            try
            {
                HttpWebRequest request = (HttpWebRequest)WebRequest.Create(endpoint);
                request.Method = "POST";
                request.ContentType = "application/json; charset=utf-8";
                request.Accept = "application/json";
                request.UserAgent = "curl2url/1.0.6";
                request.Timeout = timeout;
                request.ReadWriteTimeout = timeout;
                request.KeepAlive = false;
                request.AllowAutoRedirect = true;
                if (!string.IsNullOrEmpty(settings.ApiKey))
                {
                    request.Headers["Authorization"] = "Bearer " + settings.ApiKey.Trim();
                }

                byte[] body = Encoding.UTF8.GetBytes(requestJson);
                request.ContentLength = body.Length;
                using (Stream stream = request.GetRequestStream())
                {
                    stream.Write(body, 0, body.Length);
                }

                using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
                using (Stream stream = response.GetResponseStream())
                using (StreamReader reader = new StreamReader(stream, Encoding.UTF8))
                {
                    string responseBody = reader.ReadToEnd();
                    if (string.IsNullOrWhiteSpace(responseBody))
                    {
                        error = "AI 服务返回了空响应。";
                        return null;
                    }
                    return responseBody;
                }
            }
            catch (WebException ex)
            {
                error = DescribeWebError(ex, settings.TimeoutSeconds);
                return null;
            }
            catch (Exception ex)
            {
                error = "调用 AI 服务失败：" + ex.Message;
                return null;
            }
        }

        private static string DescribeWebError(WebException ex, int timeoutSeconds)
        {
            HttpWebResponse response = ex.Response as HttpWebResponse;
            int status = response == null ? 0 : (int)response.StatusCode;
            string body = "";
            if (response != null)
            {
                try
                {
                    using (Stream stream = response.GetResponseStream())
                    using (StreamReader reader = new StreamReader(stream, Encoding.UTF8))
                    {
                        body = reader.ReadToEnd();
                    }
                }
                catch (Exception)
                {
                }
            }

            string detail = ExtractApiError(body);
            string suffix = string.IsNullOrEmpty(detail) ? "" : "：" + detail;

            if (ex.Status == WebExceptionStatus.Timeout)
            {
                return "AI 请求超时（超过 " + timeoutSeconds + " 秒），可以缩短内容、换更小的截图或稍后重试。";
            }
            if (status == 401 || status == 403)
            {
                return "API Key 无效、过期或无权限（HTTP " + status + "）" + suffix;
            }
            if (status == 404)
            {
                return "接口地址或模型不存在（HTTP 404）" + suffix;
            }
            if (status == 429)
            {
                return "AI 服务限流（HTTP 429），请稍后再试" + suffix;
            }
            if (status > 0)
            {
                return "AI 服务返回 HTTP " + status + suffix;
            }
            return "无法连接 AI 服务：" + ex.Message + (string.IsNullOrEmpty(detail) ? "" : "（" + detail + "）");
        }

        private static string ExtractApiError(string body)
        {
            if (string.IsNullOrEmpty(body)) return "";
            try
            {
                JavaScriptSerializer serializer = new JavaScriptSerializer();
                Dictionary<string, object> root = serializer.Deserialize<Dictionary<string, object>>(body);
                if (root != null)
                {
                    object errorValue = null;
                    if (root.TryGetValue("error", out errorValue))
                    {
                        string direct = errorValue as string;
                        if (!string.IsNullOrEmpty(direct)) return Truncate(direct, 300);
                        Dictionary<string, object> map = errorValue as Dictionary<string, object>;
                        if (map != null)
                        {
                            string message = FirstString(map, "message", "msg", "detail", "code");
                            if (!string.IsNullOrEmpty(message)) return Truncate(message, 300);
                        }
                    }
                    string topMessage = FirstString(root, "message", "msg", "detail", "error_msg");
                    if (!string.IsNullOrEmpty(topMessage)) return Truncate(topMessage, 300);
                }
            }
            catch (Exception)
            {
            }
            return Truncate(body, 300);
        }

        private static string FirstString(Dictionary<string, object> data, params string[] keys)
        {
            foreach (string key in keys)
            {
                if (data != null && data.ContainsKey(key))
                {
                    string value = data[key] as string;
                    if (!string.IsNullOrWhiteSpace(value)) return value.Trim();
                }
            }
            return "";
        }

        private static string Truncate(string value, int length)
        {
            if (string.IsNullOrEmpty(value)) return "";
            value = value.Replace("\r", " ").Replace("\n", " ").Trim();
            if (value.Length <= length) return value;
            return value.Substring(0, length) + "…";
        }

        private static bool TryExtractContent(string body, JavaScriptSerializer serializer, out string content, out string error)
        {
            content = "";
            error = "";
            try
            {
                Dictionary<string, object> root = serializer.Deserialize<Dictionary<string, object>>(body);
                if (root == null)
                {
                    error = "AI 返回的内容无法解析。";
                    return false;
                }

                object choicesValue = null;
                if (root.ContainsKey("choices")) choicesValue = root["choices"];
                List<Dictionary<string, object>> choices = OcrRunner.AsObjectList(choicesValue);
                if (choices.Count > 0)
                {
                    Dictionary<string, object> message = null;
                    if (choices[0].ContainsKey("message")) message = choices[0]["message"] as Dictionary<string, object>;
                    if (message != null && message.ContainsKey("content"))
                    {
                        content = ExtractTextValue(message["content"]);
                    }
                    if (string.IsNullOrEmpty(content) && choices[0].ContainsKey("text"))
                    {
                        content = ExtractTextValue(choices[0]["text"]);
                    }
                }

                if (string.IsNullOrEmpty(content))
                {
                    string detail = ExtractApiError(body);
                    error = "AI 没有返回文字内容" + (string.IsNullOrEmpty(detail) ? "。" : "：" + detail);
                    return false;
                }
                return true;
            }
            catch (Exception ex)
            {
                error = "解析 AI 返回结果失败：" + ex.Message;
                return false;
            }
        }

        private static string ExtractTextValue(object value)
        {
            if (value == null) return "";
            string direct = value as string;
            if (direct != null) return direct;

            Dictionary<string, object> map = value as Dictionary<string, object>;
            if (map != null)
            {
                string text = FirstString(map, "text", "content");
                if (!string.IsNullOrEmpty(text)) return text;
                return "";
            }

            IEnumerable list = value as IEnumerable;
            if (list != null)
            {
                StringBuilder builder = new StringBuilder();
                foreach (object item in list)
                {
                    string part = ExtractTextValue(item);
                    if (!string.IsNullOrEmpty(part)) builder.Append(part);
                }
                return builder.ToString();
            }
            return "";
        }

        private static string CleanContent(string content)
        {
            if (string.IsNullOrEmpty(content)) return "";
            string text = content.Trim();

            // 有些模型会无视要求，包一层 Markdown 代码块
            if (text.StartsWith("```"))
            {
                int newline = text.IndexOf('\n');
                text = newline >= 0 ? text.Substring(newline + 1) : text.Substring(3);
                text = text.TrimEnd();
                if (text.EndsWith("```")) text = text.Substring(0, text.Length - 3);
            }

            text = text.Replace("\r\n", "\n").Replace('\r', '\n').Trim();
            return text.Replace("\n", Environment.NewLine);
        }
    }

    internal sealed class LogOptions
    {
        public bool FilterNoise = true;
        public bool OnlyErrors = false;
        public bool GroupByTrace = true;
    }

    internal sealed class LogAnalysis
    {
        public string Report = "";
        public string AiExcerpt = "";
        public int TotalLines;
        public int KeptLines;
        public int NoiseLines;
        public int NoiseFramework;
        public int NoiseHeartbeat;
        public int NoiseDuplicate;
        public int ErrorCount;
        public int WarnCount;
        public int ExceptionCount;
        public int SqlCount;
        public int TraceCount;
        public int HttpErrorCount;
        public bool HasSignatureError;
        public bool HasK8sProblem;
        public bool Truncated;
        public bool OnlyErrors;
        public bool FilterNoise;
        public long ElapsedMs;
    }

    internal static class LogExplainer
    {
        private const int MaxLines = 200000;
        private const int MaxCharsPerLine = 4000;
        private const int AiExcerptLimit = 24000;
        private const int ErrorContextLines = 5;
        private const int MaxTraceGroups = 500;

        private static readonly Regex AnsiRegex = new Regex(@"\x1B\[[0-9;]*[A-Za-z]", RegexOptions.Compiled);
        private static readonly Regex LevelRegex = new Regex(
            @"(?:^|[\s\[\|])(TRACE|DEBUG|INFO|WARN(?:ING)?|ERROR|FATAL|SEVERE)(?:[\s\]:\|]|$)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex ExceptionRegex = new Regex(
            @"(?<!\w)((?:[A-Za-z_$][\w$]*\.)*[A-Z][\w$]*(?:Exception|Error))\b(?:\s*:\s*(.*))?",
            RegexOptions.Compiled);
        // ExceptionRegex 的 (.*) 会把后面所有 "nested exception is ..." 一起吃进 message，
        // 导致 Matches 只返回最外层。这里用 [^;]* 截断 message，才能逐个拿到嵌套异常。
        private static readonly Regex NestedExceptionRegex = new Regex(
            @"(?<!\w)((?:[A-Za-z_$][\w$]*\.)*[A-Z][\w$]*(?:Exception|Error))\b(?:\s*:\s*([^;]*))?",
            RegexOptions.Compiled);
        private static readonly Regex StackRegex = new Regex(
            @"^\s*(?:at\s+[\w$.<>]+\s*\([^)]*\)|\.\.\.\s+\d+\s+more\b)",
            RegexOptions.Compiled);
        private static readonly Regex FrameRegex = new Regex(
            @"^\s*at\s+([\w$.<>]+)\(([^)]*)\)",
            RegexOptions.Compiled);
        private static readonly Regex TraceRegex = new Regex(
            @"\b(?:trace[_-]?id|traceid|x-b3-traceid|x-request-id|request[_-]?id|requestid)\b[""']?\s*[=:]\s*[""']?([A-Za-z0-9_.:-]{6,})",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex SleuthTraceRegex = new Regex(
            @"\[[^,\]\s]+,\s*([0-9a-fA-F]{8,32}),\s*[0-9a-fA-F]{8,32},\s*(?:true|false)\]",
            RegexOptions.Compiled);
        private static readonly Regex HttpRequestRegex = new Regex(
            @"\b(GET|POST|PUT|DELETE|PATCH|HEAD|OPTIONS)\s+[""']?((?:https?://[^\s""'<>]+)|(?:/[^\s""'<>]*))",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex UriRegex = new Regex(
            @"(?<![A-Za-z0-9_])(?:request[_-]?uri|uri|path)\s*[=:\[]\s*[""']?((?:https?://[^\s\]""'<>]+)|(?:/[^\s\]""'<>]+))",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex ControllerRegex = new Regex(
            @"(?<!\w)([A-Z][A-Za-z0-9_]*Controller)(?:[.#]([A-Za-z_$][\w$]*)|[^\r\n]{0,24}?\s-\s+([A-Za-z_$][\w$]*))",
            RegexOptions.Compiled);
        private static readonly Regex ServiceRegex = new Regex(
            @"\b([a-z0-9]+(?:-[a-z0-9]+)*-(?:service|gateway|api|svc))\b",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex ServiceNameRegex = new Regex(
            @"\b(?:spring\.application\.name|application[_-]?name|service[_-]?name)\b[""']?\s*[=:]\s*[""']?([A-Za-z0-9][A-Za-z0-9_-]{2,})",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex PhoneRegex = new Regex(
            @"(?<!\d)(?:\+?86[\s-]?)?(1[3-9]\d{9})(?!\d)",
            RegexOptions.Compiled);
        private static readonly Regex BizIdRegex = new Regex(
            @"\b(type|bizType|businessType|projectId|project_id|orderId|order_id|orderNo|order_no|tradeNo|trade_no|skuId|sku_id)\b[""']?\s*[=:]\s*[""']?([A-Za-z0-9_\-]{2,})",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex StatusRegex = new Regex(
            @"(?:\bstatus(?:[_-]?code)?\b[""']?\s*[=:]\s*[""']?([1-5]\d{2})\b)|(?:\bHTTP/[0-9.]+\b[^\r\n]{0,20}?\s([1-5]\d{2})\b)|(?:\bCompleted\s+([1-5]\d{2})\b)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex TimestampRegex = new Regex(
            @"(?<!\d)(\d{4}[-/]\d{1,2}[-/]\d{1,2}[ T]\d{1,2}:\d{2}:\d{2}(?:[.,]\d{1,6})?)(?!\d)",
            RegexOptions.Compiled);
        private static readonly Regex TimeOnlyRegex = new Regex(
            @"(?<!\d)(\d{1,2}:\d{2}:\d{2}(?:[.,]\d{1,6})?)(?!\d)",
            RegexOptions.Compiled);
        private static readonly Regex TimestampPrefixRegex = new Regex(
            @"^\s*[\[\(]?\d{4}[-/]\d{1,2}[-/]\d{1,2}[ T]\d{1,2}:\d{2}:\d{2}(?:[.,]\d{1,6})?[\]\)]?\s*",
            RegexOptions.Compiled);
        private static readonly Regex TimePrefixRegex = new Regex(
            @"^\s*[\[\(]?\d{1,2}:\d{2}:\d{2}(?:[.,]\d{1,6})?[\]\)]?\s*",
            RegexOptions.Compiled);
        private static readonly Regex ThreadPrefixRegex = new Regex(
            @"^\[[^\]]{0,80}\]\s*",
            RegexOptions.Compiled);
        private static readonly Regex ReadyRegex = new Regex(
            @"\bReady\s+(\d+)/(\d+)\b",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex RestartsRegex = new Regex(
            @"\bRestarts\b[^\d]{0,12}(\d+)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex SizeZeroRegex = new Regex(
            @"\bsize\s*[:=]\s*0\b",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex UrlSecretRegex = new Regex(
            @"(?i)([?&](?:token|access_token|refresh_token|session|sessionid|sign|signature|key|secret|password)=)([^&#\s]+)",
            RegexOptions.Compiled);
        private static readonly Regex SecretAuthorizationRegex = new Regex(
            @"(?i)(\bauthorization[""']?\s*[=:]\s*)([""']?)(?:bearer\s+)?[^\s,;""'}\r\n]+",
            RegexOptions.Compiled);
        private static readonly Regex SecretCookieRegex = new Regex(
            @"(?i)(\b(?:set-)?cookie[""']?\s*[=:]\s*)([""']?)[^\r\n]+",
            RegexOptions.Compiled);
        private static readonly Regex SecretValueRegex = new Regex(
            @"(?i)([""']?\b(?:access[_-]?token|refresh[_-]?token|token|session[_-]?id|session|password|passwd|pwd|secret|app[_-]?secret|api[_-]?key)[""']?\s*[=:]\s*[""']?)([^""'\s,;}\r\n]+)",
            RegexOptions.Compiled);

        private sealed class LogLine
        {
            public int Number;
            public string Text = "";
            public string Clean = "";
            public bool IsErrorLevel;
            public bool IsWarnLevel;
            public bool IsExceptionHeader;
            public bool IsCausedBy;
            public bool IsStack;
            public bool IsSql;
            public bool IsRequest;
            public bool IsNoise;
            public bool IsHttpError;
            public bool IsK8sProblem;
            public bool IsSignatureIssue;
            public bool Kept;
            public string NoiseKind = "";
            public string TraceId = "";
            public string Method = "";
            public string Uri = "";
            public string Controller = "";
            public string Sql = "";
            public string Service = "";
            public int Status;
            public LogExceptionEntry ExceptionEntry;
            public readonly List<LogExceptionEntry> ExceptionChain = new List<LogExceptionEntry>();
            public readonly List<string> Phones = new List<string>();
            public readonly List<string> BizIds = new List<string>();
        }

        private sealed class LogExceptionEntry
        {
            public string ClassName = "";
            public string SimpleName = "";
            public string Message = "";
            public string ShortLabel = "";
            public string Explanation = "";
            public string Suggestion = "";
            public int LineNumber;
            public string TraceId = "";
            public bool IsCausedBy;
            public bool ExplicitCausedBy;
            public int RepeatCount = 1;
            public readonly List<string> Frames = new List<string>();
        }

        private sealed class RequestStat
        {
            public string Method = "";
            public string Uri = "";
            public string TraceId = "";
            public int Count;
            public int FirstLine;
        }

        private sealed class LogTraceGroup
        {
            public string Key = "";
            public int FirstLine;
            public readonly List<LogLine> Lines = new List<LogLine>();
            public readonly List<LogExceptionEntry> Exceptions = new List<LogExceptionEntry>();
            public readonly List<string> Phones = new List<string>();
            public readonly List<string> BizIds = new List<string>();
            public string Method = "";
            public string Uri = "";
            public string Controller = "";
            public int SqlCount;
            public int ErrorLines;
            public int WarnLines;
        }

        private sealed class LogState
        {
            public LogOptions Options;
            public bool FilterNoise = true;
            public bool OnlyErrors;
            public readonly List<LogLine> Lines = new List<LogLine>();
            public readonly List<LogExceptionEntry> Exceptions = new List<LogExceptionEntry>();
            public readonly List<RequestStat> Requests = new List<RequestStat>();
            public readonly List<LogTraceGroup> Groups = new List<LogTraceGroup>();
            public readonly List<string> Services = new List<string>();
            public readonly List<string> Controllers = new List<string>();
            public readonly List<string> Phones = new List<string>();
            public readonly List<string> BizIds = new List<string>();
            public readonly List<string> K8sNotes = new List<string>();
            public readonly List<string> TraceIds = new List<string>();
            public readonly List<string> ErrorMessages = new List<string>();
            public readonly List<string> WarnMessages = new List<string>();
            public readonly HashSet<string> ServiceSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            public readonly HashSet<string> ControllerSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            public readonly HashSet<string> PhoneSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            public readonly HashSet<string> BizIdSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            public readonly HashSet<string> K8sNoteSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            public readonly HashSet<string> TraceSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            public readonly HashSet<string> MissingClassSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            public readonly List<string> MissingClasses = new List<string>();
            public readonly Dictionary<int, int> StatusCounts = new Dictionary<int, int>();
            public readonly Dictionary<string, RequestStat> RequestMap = new Dictionary<string, RequestStat>(StringComparer.Ordinal);
            public int TotalLines;
            public int ErrorLevelCount;
            public int WarnCount;
            public int ErrorCount;
            public int PrimaryExceptionCount;
            public int KeptCount;
            public int HttpErrorCount;
            public int SqlCount;
            public int TotalRequestCalls;
            public int HealthRequestCount;
            public int NoiseFramework;
            public int NoiseHeartbeat;
            public int NoiseDuplicate;
            public bool HasSignatureError;
            public bool HasK8sProblem;
            public bool HasSmsHint;
            public bool HasJdkSerializationFailure;
            public bool Truncated;
            public bool TruncatedLines;
            public bool RequestOverflow;
            public bool GroupsTruncated;
            public string FirstTimestamp = "";
            public string LastTimestamp = "";
            public string SqlExample = "";
            public string SqlParamExample = "";
        }

        public static LogAnalysis Analyze(string raw, LogOptions options)
        {
            Stopwatch watch = Stopwatch.StartNew();
            LogState state = new LogState();
            state.Options = options ?? new LogOptions();
            state.FilterNoise = state.Options.FilterNoise;
            state.OnlyErrors = state.Options.OnlyErrors;

            if (raw == null) raw = "";
            string normalized = raw.Replace("\r\n", "\n").Replace('\r', '\n');
            string[] rawLines = normalized.Split('\n');
            state.Truncated = rawLines.Length > MaxLines;
            int count = Math.Min(rawLines.Length, MaxLines);

            ParseLines(rawLines, count, state);
            FindTimestamps(state);
            BuildRequestStats(state);
            FinalizeCounts(state);
            BuildKeptLines(state);
            BuildTraceGroups(state);

            LogAnalysis result = new LogAnalysis();
            result.TotalLines = state.TotalLines;
            result.KeptLines = state.KeptCount;
            result.NoiseFramework = state.NoiseFramework;
            result.NoiseHeartbeat = state.NoiseHeartbeat;
            result.NoiseDuplicate = state.NoiseDuplicate;
            result.NoiseLines = state.NoiseFramework + state.NoiseHeartbeat + state.NoiseDuplicate;
            result.ErrorCount = state.ErrorCount;
            result.WarnCount = state.WarnCount;
            result.ExceptionCount = state.Exceptions.Count;
            result.SqlCount = state.SqlCount;
            result.TraceCount = state.TraceSet.Count;
            result.HttpErrorCount = state.HttpErrorCount;
            result.HasSignatureError = state.HasSignatureError;
            result.HasK8sProblem = state.HasK8sProblem;
            result.Truncated = state.Truncated;
            result.OnlyErrors = state.OnlyErrors;
            result.FilterNoise = state.FilterNoise;
            result.Report = BuildReport(state);
            result.AiExcerpt = BuildAiExcerpt(state);
            result.ElapsedMs = watch.ElapsedMilliseconds;
            return result;
        }

        public static string DescribeResult(LogAnalysis result)
        {
            if (result == null) return "";
            StringBuilder b = new StringBuilder();
            b.Append("本机分析完成：").Append(N0(result.TotalLines)).Append(" 行 → 显示 ").Append(N0(result.KeptLines)).Append(" 行");
            if (result.FilterNoise && result.NoiseLines > 0)
            {
                b.Append("，折叠 ").Append(N0(result.NoiseLines)).Append(" 行噪音");
            }
            if (result.OnlyErrors) b.Append("（只看错误）");
            b.Append("；");

            if (result.ErrorCount == 0 && result.ExceptionCount == 0 && result.WarnCount == 0 && result.HttpErrorCount == 0)
            {
                b.Append("没有 ERROR/WARN/Exception");
            }
            else
            {
                b.Append(N0(result.ErrorCount)).Append(" 条 ERROR");
                if (result.ExceptionCount > 0) b.Append("、").Append(N0(result.ExceptionCount)).Append(" 个异常");
                if (result.WarnCount > 0) b.Append("、").Append(N0(result.WarnCount)).Append(" 条 WARN");
                if (result.HttpErrorCount > 0) b.Append("、").Append(N0(result.HttpErrorCount)).Append(" 次 HTTP 4xx/5xx");
            }
            if (result.TraceCount > 0) b.Append("；").Append(N0(result.TraceCount)).Append(" 个 traceId");
            if (result.ElapsedMs > 0) b.Append("；耗时 ").Append(result.ElapsedMs).Append(" ms");
            b.Append("。");
            return b.ToString();
        }

        public static bool TryAiExplain(
            AiOcrSettings settings,
            string localReport,
            string excerpt,
            out string text,
            out string error)
        {
            text = "";
            error = "";
            if (string.IsNullOrWhiteSpace(excerpt))
            {
                error = "没有可发送给 AI 的日志内容。";
                return false;
            }

            string system =
                "你是资深 Java 后端 / 中间件 / SRE，熟悉 Spring、Redis、MyBatis、消息队列和 K8s，" +
                "擅长从日志推断故障场景。读者能改业务代码，但英文和底层术语较弱。" +
                "你只能依据用户提供的日志和本机分析回答，不要编造不存在的服务、接口、单号或原因。";

            string user =
                "请把下面这段日志用通顺中文讲清楚。严格按这五段输出，纯文本，不要 Markdown 代码块、不要表格：\n\n" +
                "一句话\n<这段日志里发生了什么、有没有错，先给结论>\n\n" +
                "发生了什么\n<2-5 句，按时间顺序；谁调用了什么接口/服务，关键参数是什么，异常链怎么往下走>\n\n" +
                "关键信息\n<服务 / 接口 / traceId / requestId / 手机号 / 订单号 / SQL / 状态码 / 缺失的类，只列日志里出现过的>\n\n" +
                "有没有报错\n<没有就明确说没有；有就写异常类名、中文解释、根因和关键代码位置>\n\n" +
                "建议下一步\n<3-5 条具体、可执行的排查动作，按优先级排序，尽量带检查点或命令>\n\n" +
                "要求：\n" +
                "1. 保留异常类名、接口路径、字段名原文，方便我回日志里搜索。\n" +
                "2. 手机号、Token 等已经打码，不要尝试还原。\n" +
                "3. 日志可能被截断；不确定的地方明确说不确定，不要猜。\n" +
                "4. 本机摘要可能有遗漏或误判，和原文冲突时以日志原文为准。\n" +
                "5. 区分日志事实和合理推断：推断要写明“很可能”并给出依据，不要冒充日志里写过。\n" +
                "6. 遇到反序列化失败 / ClassNotFoundException，优先考虑 Redis 旧缓存、旧消息、DTO 被删除/改名/拆模块、" +
                "发布包缺类；不要只归因为 JSON 字段类型不匹配。\n" +
                "7. requestId、traceId、X-Request-Id、X-Trace-Id 都视为追踪号，不要因为日志只写了 requestId 就说“没有 traceId”。\n" +
                "8. 总长度 1200 字以内，不要写客套话。\n\n" +
                "【本机规则分析（可参考）】\n" +
                LimitText(localReport, 8000) +
                "\n\n【去噪后的日志原文（敏感信息已打码）】\n" +
                excerpt;

            return AiOcrRunner.TryChat(settings, system, user, out text, out error);
        }

        private static void ParseLines(string[] rawLines, int count, LogState state)
        {
            string currentTrace = "";
            LogExceptionEntry lastException = null;
            Dictionary<string, int> duplicates = new Dictionary<string, int>(StringComparer.Ordinal);

            for (int i = 0; i < count; i++)
            {
                string text = rawLines[i] ?? "";
                if (text.IndexOf('\u001B') >= 0) text = AnsiRegex.Replace(text, "");
                if (text.Length > MaxCharsPerLine)
                {
                    text = text.Substring(0, MaxCharsPerLine) + "…";
                    state.TruncatedLines = true;
                }

                string clean = text.Trim().TrimStart('\uFEFF');
                if (clean.Length == 0) continue;

                state.TotalLines++;
                LogLine line = new LogLine();
                line.Number = i + 1;
                line.Text = text;
                line.Clean = clean;

                string lower = clean.ToLowerInvariant();
                string head = clean.Length > 160 ? clean.Substring(0, 160) : clean;

                Match level = LevelRegex.Match(head);
                if (level.Success)
                {
                    string value = level.Groups[1].Value.ToUpperInvariant();
                    if (value == "ERROR" || value == "FATAL" || value == "SEVERE") line.IsErrorLevel = true;
                    else if (value == "WARN" || value == "WARNING") line.IsWarnLevel = true;
                }

                line.IsStack = StackRegex.IsMatch(clean);

                Match exceptionMatch = null;
                if (clean.IndexOf("Exception", StringComparison.Ordinal) >= 0 ||
                    clean.IndexOf("Error", StringComparison.Ordinal) >= 0)
                {
                    exceptionMatch = ExceptionRegex.Match(clean);
                }
                bool hasExceptionHeader = exceptionMatch != null && exceptionMatch.Success && !line.IsStack;

                Match traceMatch = null;
                if (lower.Contains("trace") || lower.Contains("requestid") || lower.Contains("request_id") ||
                    lower.Contains("request-id") || lower.Contains("x-request-id"))
                {
                    traceMatch = TraceRegex.Match(clean);
                }
                if (traceMatch != null && traceMatch.Success)
                {
                    line.TraceId = traceMatch.Groups[1].Value;
                    currentTrace = line.TraceId;
                }
                else if (lower.Contains("true]") || lower.Contains("false]"))
                {
                    Match sleuth = SleuthTraceRegex.Match(clean);
                    if (sleuth.Success)
                    {
                        line.TraceId = sleuth.Groups[1].Value;
                        currentTrace = line.TraceId;
                    }
                }

                if (line.TraceId.Length == 0 &&
                    (line.IsStack || hasExceptionHeader || line.IsErrorLevel || line.IsWarnLevel))
                {
                    line.TraceId = currentTrace;
                }

                if (hasExceptionHeader)
                {
                    bool jdkSerialization = state.HasJdkSerializationFailure ||
                        lower.Contains("defaultdeserializer") ||
                        lower.Contains("serializationfailedexception") ||
                        lower.Contains("failed to deserialize object type") ||
                        lower.Contains("invalidclassexception");
                    if (jdkSerialization) state.HasJdkSerializationFailure = true;

                    // Spring 常把整条异常链塞进一行：
                    // Cannot deserialize; nested exception is ...; nested exception is ...
                    // 逐个提取，才能拿到最底层的 ClassNotFoundException。
                    List<Match> chainMatches = new List<Match>();
                    if (lower.Contains("nested exception is"))
                    {
                        foreach (Match match in NestedExceptionRegex.Matches(clean)) chainMatches.Add(match);
                    }
                    if (chainMatches.Count == 0) chainMatches.Add(exceptionMatch);

                    LogExceptionEntry primaryEntry = null;
                    LogExceptionEntry lastEntry = null;
                    for (int mi = 0; mi < chainMatches.Count; mi++)
                    {
                        Match match = chainMatches[mi];
                        string className = match.Groups[1].Value;
                        string message = match.Groups[2].Success ? match.Groups[2].Value.Trim() : "";
                        if (message.EndsWith("]") && clean.IndexOf("Resolved [", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            message = message.Substring(0, message.Length - 1).Trim();
                        }
                        int nestedAt = message.IndexOf("nested exception is", StringComparison.OrdinalIgnoreCase);
                        if (nestedAt > 0)
                        {
                            message = message.Substring(0, nestedAt).Trim().TrimEnd(';', ',', ' ');
                        }

                        bool explicitCausedBy =
                            clean.IndexOf("Caused by", StringComparison.OrdinalIgnoreCase) >= 0;
                        bool causedBy = mi > 0 || explicitCausedBy;
                        LogExceptionEntry entry = FindExistingException(state, className, message,
                            line.TraceId, causedBy);
                        if (entry != null)
                        {
                            entry.RepeatCount++;
                            if (explicitCausedBy) entry.ExplicitCausedBy = true;
                        }
                        else
                        {
                            entry = new LogExceptionEntry();
                            entry.ClassName = className;
                            entry.SimpleName = SimpleName(className);
                            entry.Message = message;
                            entry.Explanation = ExplainException(className, message, jdkSerialization);
                            entry.ShortLabel = ExtractShortLabel(entry.Explanation, entry.SimpleName);
                            entry.Suggestion = SuggestionForException(className, message, jdkSerialization);
                            entry.LineNumber = i + 1;
                            entry.TraceId = line.TraceId;
                            entry.IsCausedBy = causedBy;
                            entry.ExplicitCausedBy = explicitCausedBy;
                            state.Exceptions.Add(entry);
                            if (entry.SimpleName == "ClassNotFoundException" ||
                                entry.SimpleName == "NoClassDefFoundError")
                            {
                                AddMissingClass(state, message);
                            }
                        }

                        if (primaryEntry == null) primaryEntry = entry;
                        lastEntry = entry;
                        if (!line.ExceptionChain.Contains(entry)) line.ExceptionChain.Add(entry);
                    }

                    line.IsExceptionHeader = true;
                    line.ExceptionEntry = primaryEntry;
                    line.IsCausedBy = primaryEntry != null && primaryEntry.IsCausedBy;
                    line.TraceId = primaryEntry != null ? primaryEntry.TraceId : line.TraceId;
                    lastException = lastEntry;
                }
                else if (line.IsStack)
                {
                    Match frame = FrameRegex.Match(clean);
                    if (frame.Success)
                    {
                        string method = frame.Groups[1].Value;
                        if (IsAppFrame(method))
                        {
                            string frameController = ExtractControllerName(method);
                            if (frameController.Length > 0)
                            {
                                line.Controller = frameController;
                                AddUnique(state.Controllers, state.ControllerSet, frameController, 20);
                            }
                        }
                        if (lastException != null &&
                            (line.TraceId.Length == 0 || lastException.TraceId.Length == 0 ||
                             string.Equals(line.TraceId, lastException.TraceId, StringComparison.Ordinal)))
                        {
                            if (IsAppFrame(method) && lastException.Frames.Count < 3)
                            {
                                string location = frame.Groups[2].Value.Trim();
                                lastException.Frames.Add(location.Length == 0 ? method : method + "(" + location + ")");
                            }
                        }
                    }
                }

                if (!line.IsStack)
                {
                    Match request = HttpRequestRegex.Match(clean);
                    if (request.Success)
                    {
                        line.IsRequest = true;
                        line.Method = request.Groups[1].Value.ToUpperInvariant();
                        line.Uri = request.Groups[2].Value;
                    }
                    else if (lower.Contains("uri") || lower.Contains("path"))
                    {
                        Match uri = UriRegex.Match(clean);
                        if (uri.Success)
                        {
                            line.IsRequest = true;
                            line.Uri = uri.Groups[1].Value;
                        }
                    }

                    string controller = ExtractControllerName(clean);
                    if (controller.Length > 0)
                    {
                        line.Controller = controller;
                        AddUnique(state.Controllers, state.ControllerSet, line.Controller, 20);
                    }
                }

                if (lower.Contains("preparing:"))
                {
                    line.IsSql = true;
                    int index = lower.IndexOf("preparing:", StringComparison.Ordinal);
                    string sql = clean.Substring(index + "preparing:".Length).Trim();
                    sql = sql.TrimStart(new char[] { '=', '>', ' ', '-' });
                    if (sql.Length > 0)
                    {
                        line.Sql = sql;
                        state.SqlCount++;
                        if (state.SqlExample.Length == 0) state.SqlExample = sql;
                    }
                }
                else if (lower.Contains("parameters:"))
                {
                    line.IsSql = true;
                    int index = lower.IndexOf("parameters:", StringComparison.Ordinal);
                    string parameters = clean.Substring(index + "parameters:".Length).Trim();
                    if (parameters.Length > 0 && state.SqlParamExample.Length == 0)
                    {
                        state.SqlParamExample = parameters;
                    }
                }

                if (line.TraceId.Length == 0 && line.IsSql) line.TraceId = currentTrace;

                if (lower.Contains("status") || lower.Contains("http/") || lower.Contains("completed "))
                {
                    Match status = StatusRegex.Match(clean);
                    if (status.Success)
                    {
                        string value = status.Groups[1].Success
                            ? status.Groups[1].Value
                            : (status.Groups[2].Success ? status.Groups[2].Value : status.Groups[3].Value);
                        int code;
                        if (int.TryParse(value, out code))
                        {
                            line.Status = code;
                            line.IsHttpError = code >= 400;
                            int seen;
                            state.StatusCounts.TryGetValue(code, out seen);
                            state.StatusCounts[code] = seen + 1;
                        }
                    }
                }

                if (!line.IsRequest &&
                    (lower.Contains("service") || lower.Contains("gateway") || lower.Contains("api")))
                {
                    Match service = ServiceRegex.Match(clean);
                    if (service.Success)
                    {
                        line.Service = service.Groups[1].Value;
                        AddUnique(state.Services, state.ServiceSet, line.Service, 20);
                    }
                }
                if (lower.Contains("spring.application.name") || lower.Contains("application-name") ||
                    lower.Contains("application.name") || lower.Contains("service_name"))
                {
                    Match serviceName = ServiceNameRegex.Match(clean);
                    if (serviceName.Success)
                    {
                        line.Service = serviceName.Groups[1].Value;
                        AddUnique(state.Services, state.ServiceSet, line.Service, 20);
                    }
                }

                foreach (Match phone in PhoneRegex.Matches(clean))
                {
                    string masked = MaskPhoneNumber(phone.Groups[1].Value);
                    if (!line.Phones.Contains(masked) && line.Phones.Count < 4) line.Phones.Add(masked);
                    AddUnique(state.Phones, state.PhoneSet, masked, 1000);
                }

                if (lower.Contains("type=") || lower.Contains("type:") || lower.Contains("projectid") ||
                    lower.Contains("project_id") || lower.Contains("orderid") || lower.Contains("order_id") ||
                    lower.Contains("orderno") || lower.Contains("order_no") || lower.Contains("tradeno") ||
                    lower.Contains("trade_no") || lower.Contains("skuid") || lower.Contains("sku_id") ||
                    lower.Contains("biztype"))
                {
                    Match biz = BizIdRegex.Match(clean);
                    if (biz.Success)
                    {
                        string label = biz.Groups[1].Value;
                        string value = biz.Groups[2].Value;
                        if (!(label.Equals("type", StringComparison.OrdinalIgnoreCase) && IsMimeValue(value)))
                        {
                            string pair = label + "=" + Shorten(value, 40);
                            if (!line.BizIds.Contains(pair) && line.BizIds.Count < 4) line.BizIds.Add(pair);
                            AddUnique(state.BizIds, state.BizIdSet, pair, 100);
                        }
                    }
                }

                if (CaptureK8s(clean, state)) line.IsK8sProblem = true;

                bool signatureLine = lower.Contains("appkey") || lower.Contains("app key") ||
                                     lower.Contains("appsecret") || lower.Contains("签名") ||
                                     lower.Contains("signature") || lower.Contains("验签") ||
                                     lower.Contains("sign check");
                if (signatureLine &&
                    (line.IsErrorLevel || line.IsWarnLevel || lower.Contains("fail") ||
                     lower.Contains("error") || lower.Contains("invalid") || lower.Contains("失败") ||
                     lower.Contains("不通过")))
                {
                    line.IsSignatureIssue = true;
                    state.HasSignatureError = true;
                }

                if (lower.Contains("sendmessage") || lower.Contains("短信") || lower.Contains("sms") ||
                    lower.Contains("验证码") || lower.Contains("verifycode") || lower.Contains("verification code"))
                {
                    state.HasSmsHint = true;
                }

                bool hasFailureWords = lower.Contains("fail") || lower.Contains("error") ||
                                       lower.Contains("timeout") || lower.Contains("timed out") ||
                                       lower.Contains("refused") || lower.Contains("失败") ||
                                       lower.Contains("异常");
                bool canBeNoise = !line.IsErrorLevel && !line.IsWarnLevel && !line.IsExceptionHeader &&
                                  !line.IsStack && !line.IsSql && !line.IsSignatureIssue &&
                                  line.Status < 400 && !hasFailureWords;

                if (canBeNoise)
                {
                    bool healthRequest = line.IsRequest && IsHealthPath(line.Uri);
                    if (healthRequest)
                    {
                        line.IsNoise = true;
                        line.NoiseKind = "heartbeat";
                        state.NoiseHeartbeat++;
                        state.HealthRequestCount++;
                    }
                    else if (IsHeartbeatLine(lower))
                    {
                        line.IsNoise = true;
                        line.NoiseKind = "heartbeat";
                        state.NoiseHeartbeat++;
                    }
                    else if (!line.IsRequest && IsFrameworkNoise(lower))
                    {
                        line.IsNoise = true;
                        line.NoiseKind = "framework";
                        state.NoiseFramework++;
                    }
                }

                if (!line.IsNoise && !line.IsRequest && !line.IsSql && !line.IsStack &&
                    !line.IsExceptionHeader && !line.IsErrorLevel && !line.IsWarnLevel &&
                    !line.IsSignatureIssue && !line.IsK8sProblem)
                {
                    string key = NormalizeDuplicate(clean);
                    if (key.Length >= 10)
                    {
                        int seen;
                        if (duplicates.TryGetValue(key, out seen))
                        {
                            seen++;
                            duplicates[key] = seen;
                            if (seen > 2)
                            {
                                line.IsNoise = true;
                                line.NoiseKind = "duplicate";
                                state.NoiseDuplicate++;
                            }
                        }
                        else
                        {
                            duplicates[key] = 1;
                        }
                    }
                }

                state.Lines.Add(line);
                if (!line.IsStack && !line.IsExceptionHeader) lastException = null;
            }
        }

        private static void FindTimestamps(LogState state)
        {
            int count = state.Lines.Count;
            if (count == 0) return;

            string first = "";
            string last = "";
            int headEnd = Math.Min(count, 30);
            for (int i = 0; i < headEnd; i++)
            {
                Match match = TimestampRegex.Match(state.Lines[i].Text);
                if (match.Success)
                {
                    first = NormalizeTimestamp(match.Groups[1].Value);
                    break;
                }
            }
            for (int i = count - 1; i >= Math.Max(0, count - 30); i--)
            {
                Match match = TimestampRegex.Match(state.Lines[i].Text);
                if (match.Success)
                {
                    last = NormalizeTimestamp(match.Groups[1].Value);
                    break;
                }
            }

            if (first.Length == 0 && last.Length == 0)
            {
                for (int i = 0; i < headEnd; i++)
                {
                    Match match = TimeOnlyRegex.Match(state.Lines[i].Text);
                    if (match.Success)
                    {
                        first = match.Groups[1].Value;
                        break;
                    }
                }
                for (int i = count - 1; i >= Math.Max(0, count - 30); i--)
                {
                    Match match = TimeOnlyRegex.Match(state.Lines[i].Text);
                    if (match.Success)
                    {
                        last = match.Groups[1].Value;
                        break;
                    }
                }
            }

            state.FirstTimestamp = first;
            state.LastTimestamp = last;
        }

        private static void BuildRequestStats(LogState state)
        {
            foreach (LogLine line in state.Lines)
            {
                if (!line.IsRequest || line.Uri.Length == 0) continue;
                if (IsHealthPath(line.Uri))
                {
                    continue;
                }

                string uri = line.Uri.Length > 600 ? line.Uri.Substring(0, 600) : line.Uri;
                string key = line.Method + " " + uri;

                RequestStat stat;
                if (state.RequestMap.TryGetValue(key, out stat))
                {
                    stat.Count++;
                }
                else if (state.RequestMap.Count < 200)
                {
                    stat = new RequestStat();
                    stat.Method = line.Method;
                    stat.Uri = uri;
                    stat.TraceId = line.TraceId;
                    stat.Count = 1;
                    stat.FirstLine = line.Number;
                    state.RequestMap[key] = stat;
                }
                else
                {
                    state.RequestOverflow = true;
                }
            }

            // 同一个请求常常既打「开始请求 URI」又打访问日志；按 URI 合并一次，避免算成两次调用。
            Dictionary<string, RequestStat> merged = new Dictionary<string, RequestStat>(StringComparer.Ordinal);
            List<RequestStat> methodless = new List<RequestStat>();
            foreach (RequestStat stat in state.RequestMap.Values)
            {
                if (stat.Method.Length == 0) methodless.Add(stat);
                else merged[stat.Method + " " + stat.Uri] = stat;
            }
            foreach (RequestStat stat in methodless)
            {
                RequestStat target = null;
                foreach (RequestStat candidate in merged.Values)
                {
                    if (string.Equals(candidate.Uri, stat.Uri, StringComparison.Ordinal))
                    {
                        target = candidate;
                        break;
                    }
                }
                if (target == null)
                {
                    merged[" " + stat.Uri] = stat;
                }
                else
                {
                    if (stat.Count > target.Count) target.Count = stat.Count;
                    if (target.TraceId.Length == 0) target.TraceId = stat.TraceId;
                    if (stat.FirstLine < target.FirstLine) target.FirstLine = stat.FirstLine;
                }
            }

            state.Requests.AddRange(merged.Values);
            state.TotalRequestCalls = 0;
            foreach (RequestStat stat in state.Requests) state.TotalRequestCalls += stat.Count;
            state.Requests.Sort(delegate(RequestStat a, RequestStat b)
            {
                int result = b.Count.CompareTo(a.Count);
                if (result != 0) return result;
                return a.FirstLine.CompareTo(b.FirstLine);
            });
        }

        private static void FinalizeCounts(LogState state)
        {
            foreach (LogLine line in state.Lines)
            {
                if (line.IsErrorLevel)
                {
                    state.ErrorLevelCount++;
                    if (state.ErrorMessages.Count < 6)
                    {
                        state.ErrorMessages.Add(Shorten(MaskSensitiveText(line.Clean), 240));
                    }
                }
                if (line.IsWarnLevel)
                {
                    state.WarnCount++;
                    if (state.WarnMessages.Count < 6)
                    {
                        state.WarnMessages.Add(Shorten(MaskSensitiveText(line.Clean), 240));
                    }
                }
                if (line.Status >= 400) state.HttpErrorCount++;
                if (!line.IsNoise && line.TraceId.Length > 0 && !state.TraceSet.Contains(line.TraceId))
                {
                    state.TraceSet.Add(line.TraceId);
                    state.TraceIds.Add(line.TraceId);
                }
            }

            foreach (LogExceptionEntry entry in state.Exceptions)
            {
                if (!entry.IsCausedBy) state.PrimaryExceptionCount++;
            }

            if (state.ErrorLevelCount > 0) state.ErrorCount = state.ErrorLevelCount;
            else if (state.PrimaryExceptionCount > 0) state.ErrorCount = state.PrimaryExceptionCount;
            else if (state.Exceptions.Count > 0) state.ErrorCount = state.Exceptions.Count;

        }

        private static void BuildKeptLines(LogState state)
        {
            if (!state.OnlyErrors)
            {
                foreach (LogLine line in state.Lines)
                {
                    line.Kept = !state.FilterNoise || !line.IsNoise;
                    if (line.Kept) state.KeptCount++;
                }
                return;
            }

            bool[] keep = new bool[state.Lines.Count];
            bool hasAnchor = false;
            for (int i = 0; i < state.Lines.Count; i++)
            {
                LogLine line = state.Lines[i];
                if (line.IsErrorLevel || line.IsWarnLevel || line.IsExceptionHeader || line.Status >= 400 ||
                    line.IsK8sProblem || line.IsSignatureIssue)
                {
                    hasAnchor = true;
                    int from = Math.Max(0, i - ErrorContextLines);
                    int to = Math.Min(state.Lines.Count - 1, i + ErrorContextLines);
                    for (int j = from; j <= to; j++) keep[j] = true;
                }
            }

            if (hasAnchor)
            {
                for (int i = 0; i < state.Lines.Count; i++)
                {
                    state.Lines[i].Kept = keep[i];
                    if (keep[i]) state.KeptCount++;
                }
            }
        }

        private static void BuildTraceGroups(LogState state)
        {
            if (!state.Options.GroupByTrace || state.Lines.Count == 0) return;

            Dictionary<string, LogLine> traceRequests = new Dictionary<string, LogLine>(StringComparer.Ordinal);
            foreach (LogLine line in state.Lines)
            {
                if (line.IsNoise || line.TraceId.Length == 0 || !line.IsRequest) continue;
                if (IsHealthPath(line.Uri)) continue;
                LogLine existing;
                if (!traceRequests.TryGetValue(line.TraceId, out existing))
                {
                    traceRequests[line.TraceId] = line;
                }
                else if (existing.Method.Length == 0 && line.Method.Length > 0)
                {
                    traceRequests[line.TraceId] = line;
                }
            }

            Dictionary<string, LogTraceGroup> map = new Dictionary<string, LogTraceGroup>(StringComparer.Ordinal);
            foreach (LogLine line in state.Lines)
            {
                if (!line.Kept || line.TraceId.Length == 0) continue;

                LogTraceGroup group;
                if (!map.TryGetValue(line.TraceId, out group))
                {
                    if (map.Count >= MaxTraceGroups)
                    {
                        state.GroupsTruncated = true;
                        continue;
                    }
                    group = new LogTraceGroup();
                    group.Key = line.TraceId;
                    group.FirstLine = line.Number;
                    map[line.TraceId] = group;
                    state.Groups.Add(group);
                }

                group.Lines.Add(line);
                if (line.Controller.Length > 0 && group.Controller.Length == 0) group.Controller = line.Controller;
                if (line.IsSql && line.Sql.Length > 0) group.SqlCount++;
                if (line.IsErrorLevel) group.ErrorLines++;
                if (line.IsWarnLevel) group.WarnLines++;
                foreach (LogExceptionEntry entry in line.ExceptionChain)
                {
                    if (!group.Exceptions.Contains(entry)) group.Exceptions.Add(entry);
                }
                if (line.ExceptionEntry != null && !group.Exceptions.Contains(line.ExceptionEntry))
                {
                    group.Exceptions.Add(line.ExceptionEntry);
                }
                foreach (string phone in line.Phones)
                {
                    if (!group.Phones.Contains(phone)) group.Phones.Add(phone);
                }
                foreach (string biz in line.BizIds)
                {
                    if (!group.BizIds.Contains(biz)) group.BizIds.Add(biz);
                }
            }

            foreach (LogTraceGroup group in state.Groups)
            {
                if (group.Uri.Length == 0)
                {
                    LogLine request;
                    if (traceRequests.TryGetValue(group.Key, out request))
                    {
                        group.Method = request.Method;
                        group.Uri = request.Uri;
                        if (group.Controller.Length == 0 && request.Controller.Length > 0) group.Controller = request.Controller;
                        foreach (string phone in request.Phones)
                        {
                            if (!group.Phones.Contains(phone)) group.Phones.Add(phone);
                        }
                        foreach (string biz in request.BizIds)
                        {
                            if (!group.BizIds.Contains(biz)) group.BizIds.Add(biz);
                        }
                    }
                }
            }
        }

        private static string BuildReport(LogState state)
        {
            StringBuilder sb = new StringBuilder(8192);
            AppendSection(sb, "一句话", BuildOneLiner(state));
            AppendSection(sb, "发生了什么", BuildWhatHappened(state));
            AppendSection(sb, "关键信息", BuildKeyInfo(state));
            AppendSection(sb, "有没有报错", BuildErrors(state));
            AppendSection(sb, "建议下一步", BuildSuggestions(state));

            string traceSection = BuildTraceSection(state);
            if (traceSection.Length > 0)
            {
                AppendSection(sb, state.Groups.Count == 1 ? "本次请求" : "按 traceId 拆分", traceSection);
            }

            string footer = BuildFooter(state);
            if (footer.Length > 0) sb.AppendLine(footer);
            return sb.ToString().TrimEnd();
        }

        private static void AppendSection(StringBuilder builder, string title, string content)
        {
            builder.AppendLine("【" + title + "】");
            builder.AppendLine((content ?? "").TrimEnd());
            builder.AppendLine();
        }

        private static string BuildAiExcerpt(LogState state)
        {
            List<LogLine> source = new List<LogLine>();
            foreach (LogLine line in state.Lines)
            {
                if (!state.FilterNoise || !line.IsNoise) source.Add(line);
            }
            if (source.Count == 0) source = state.Lines;
            if (source.Count == 0) return "";

            int totalChars = 0;
            foreach (LogLine line in source) totalChars += line.Text.Length + 1;
            bool truncated = totalChars > AiExcerptLimit;

            StringBuilder builder = new StringBuilder(Math.Min(totalChars + 200, AiExcerptLimit + 200));
            if (truncated)
            {
                builder.AppendLine("（日志较长，以下为节选，优先保留错误、请求和 SQL 相关行）");
            }

            if (!truncated)
            {
                foreach (LogLine line in source)
                {
                    string text = MaskSensitiveText(line.Text.TrimEnd());
                    if (text.Length == 0) continue;
                    if (builder.Length + text.Length + 1 > AiExcerptLimit)
                    {
                        truncated = true;
                        break;
                    }
                    builder.AppendLine(text);
                }
            }
            else
            {
                bool[] take = new bool[source.Count];
                for (int i = 0; i < source.Count; i++)
                {
                    LogLine line = source[i];
                    bool high = line.IsErrorLevel || line.IsWarnLevel || line.IsExceptionHeader ||
                                line.IsStack || line.IsRequest || line.IsSql || line.Status >= 400 ||
                                line.IsK8sProblem || line.IsSignatureIssue;
                    if (!high) continue;
                    int from = Math.Max(0, i - 3);
                    int to = Math.Min(source.Count - 1, i + 3);
                    for (int j = from; j <= to; j++) take[j] = true;
                }
                for (int i = 0; i < Math.Min(15, source.Count); i++) take[i] = true;
                for (int i = Math.Max(0, source.Count - 15); i < source.Count; i++) take[i] = true;

                bool any = false;
                for (int i = 0; i < take.Length; i++) if (take[i]) { any = true; break; }
                if (!any)
                {
                    for (int i = 0; i < Math.Min(30, take.Length); i++) take[i] = true;
                }

                for (int i = 0; i < source.Count; i++)
                {
                    if (!take[i]) continue;
                    string text = MaskSensitiveText(source[i].Text.TrimEnd());
                    if (text.Length == 0) continue;
                    if (builder.Length + text.Length + 1 > AiExcerptLimit)
                    {
                        int room = AiExcerptLimit - builder.Length - 2;
                        if (room > 20) builder.AppendLine(text.Substring(0, room) + "…");
                        truncated = true;
                        break;
                    }
                    builder.AppendLine(text);
                }
            }

            if (truncated && builder.Length < AiExcerptLimit)
            {
                builder.AppendLine("…（已截断）");
            }
            return builder.ToString().TrimEnd();
        }

        private static string BuildOneLiner(LogState state)
        {
            if (state.TotalLines == 0) return "没有读到有效日志：粘贴内容为空，或全是空行。";
            if (state.OnlyErrors && state.KeptCount == 0)
            {
                return "当前勾选了「只看错误」，但这段日志里没有找到 ERROR/WARN/Exception，所以没有可显示的内容。";
            }

            LogExceptionEntry first = FirstPrimaryException(state);
            string phone = PhoneSummary(state);
            RequestStat main = null;
            if (first != null && first.TraceId.Length > 0)
            {
                foreach (RequestStat stat in state.Requests)
                {
                    if (string.Equals(stat.TraceId, first.TraceId, StringComparison.Ordinal) &&
                        stat.Method.Length > 0)
                    {
                        main = stat;
                        break;
                    }
                }
                if (main == null)
                {
                    foreach (RequestStat stat in state.Requests)
                    {
                        if (string.Equals(stat.TraceId, first.TraceId, StringComparison.Ordinal))
                        {
                            main = stat;
                            break;
                        }
                    }
                }
            }
            if (main == null)
            {
                foreach (RequestStat stat in state.Requests)
                {
                    if (stat.Method.Length > 0)
                    {
                        main = stat;
                        break;
                    }
                }
            }
            if (main == null && state.Requests.Count > 0) main = state.Requests[0];
            string request = main == null ? "" : FormatRequestBrief(main.Method, main.Uri, 90);
            bool hasHttpError = state.HttpErrorCount > 0;

            if (state.ErrorCount > 0 || state.Exceptions.Count > 0 || hasHttpError || state.HasSignatureError)
            {
                if (state.HasJdkSerializationFailure && state.MissingClasses.Count > 0)
                {
                    string missing = Shorten(MaskSensitiveText(state.MissingClasses[0]), 120);
                    string tail = "；通常是 Redis 旧缓存 / 旧消息里的 DTO 在当前版本被删除、改名或未引入（不是 JSON 字段不匹配）。";
                    if (request.Length > 0)
                    {
                        return "处理 " + request + " 时，JDK 反序列化失败，根因是找不到类 " + missing + phone + tail;
                    }
                    if (first != null && first.TraceId.Length > 0)
                    {
                        return "请求号 " + first.TraceId + " 的记录 JDK 反序列化失败，根因是找不到类 " +
                               missing + phone + tail;
                    }
                    return "这段日志 JDK 反序列化失败，根因是找不到类 " + missing + phone + tail;
                }
                if (first != null)
                {
                    string subject = request.Length == 0 ? "日志里出现 " : "处理 " + request + " 时出现 ";
                    string text = subject + first.SimpleName + "（" + first.ShortLabel + "）" + phone;

                    LogExceptionEntry caused = FindRootCause(state.Exceptions, first);
                    if (caused != null)
                    {
                        text += "；" + (caused.ExplicitCausedBy ? "底层 Caused by：" : "底层原因：") +
                                caused.SimpleName;
                        if (caused.Message.Length > 0)
                        {
                            text += "（" + Shorten(MaskSensitiveText(caused.Message), 60) + "）";
                        }
                        text += "。";
                        int rest = state.Exceptions.Count - 2;
                        if (rest > 0) text += "另有 " + N0(rest) + " 个异常。";
                    }
                    else
                    {
                        text += "。";
                        if (state.Exceptions.Count > 1)
                        {
                            text += "另外还有 " + N0(state.Exceptions.Count - 1) + " 个异常或底层原因。";
                        }
                    }
                    return text;
                }
                if (state.HasSignatureError)
                {
                    return "请求处理失败，日志里有 AppKey/签名校验错误的迹象" + phone +
                           "；优先核对 appKey/secret 和签名算法。";
                }
                if (hasHttpError)
                {
                    return "请求返回了 HTTP 错误（" + FormatStatuses(state, true) + "）" + phone +
                           "，但日志里没有可读的异常堆栈。";
                }
                if (request.Length > 0)
                {
                    return "处理 " + request + " 时出现 " + N0(state.ErrorCount) + " 条 ERROR" + phone +
                           "，但没识别到标准异常类。";
                }
                return "日志里有 " + N0(state.ErrorCount) + " 条 ERROR" + phone + "，没有读到完整的异常堆栈。";
            }

            if (state.HasK8sProblem)
            {
                return "日志里 K8s 实例状态不正常（" + K8sSummary(state) + "）" + phone +
                       "，先看实例为什么重启或未就绪。";
            }
            if (state.WarnCount > 0)
            {
                return "这段日志没有 ERROR/Exception，但有 " + N0(state.WarnCount) + " 条 WARN" + phone +
                       "；多半是重试、降级或配置提示，先确认是否影响业务。";
            }
            if (request.Length > 0)
            {
                string text = "这段日志在正常处理 " + request + " 请求" + phone + "，没有看到 ERROR/Exception。";
                if (state.HasSmsHint) text += "如果短信没收到，问题可能在本段之后或短信网关。";
                return text;
            }
            if (state.SqlCount > 0)
            {
                return "这段日志主要是数据库访问：执行了 " + N0(state.SqlCount) +
                       " 条 SQL，没有看到 ERROR/Exception。";
            }
            return "这段日志没有明显的报错，主要是普通 INFO / 心跳或框架日志，共 " +
                   N0(state.TotalLines) + " 行。";
        }

        private static string BuildWhatHappened(LogState state)
        {
            if (state.TotalLines == 0) return "没有读到有效日志。";
            if (state.OnlyErrors && state.KeptCount == 0)
            {
                return "当前勾选了「只看错误」，但没有找到 ERROR/WARN/Exception，所以没有保留任何行；取消勾选后可以看全部日志。";
            }

            List<string> parts = new List<string>();
            if (state.FirstTimestamp.Length > 0 && state.LastTimestamp.Length > 0)
            {
                if (!string.Equals(state.FirstTimestamp, state.LastTimestamp, StringComparison.Ordinal))
                {
                    parts.Add("日志时间范围：" + state.FirstTimestamp + " → " + state.LastTimestamp + "。");
                }
                else
                {
                    parts.Add("日志时间：" + state.FirstTimestamp + "。");
                }
            }
            else if (state.FirstTimestamp.Length > 0 || state.LastTimestamp.Length > 0)
            {
                parts.Add("日志时间：" + (state.FirstTimestamp.Length > 0 ? state.FirstTimestamp : state.LastTimestamp) +
                          "（只识别到一端）。");
            }

            if (state.TraceSet.Count > 0)
            {
                parts.Add("涉及 " + N0(state.TraceSet.Count) + " 个 traceId/请求号。");
            }
            if (state.TotalRequestCalls > 0)
            {
                string text = "识别到 " + N0(state.TotalRequestCalls) + " 次接口调用";
                if (state.Requests.Count > 0)
                {
                    text += "，涉及 " + N0(state.Requests.Count) + " 种接口";
                    if (state.RequestOverflow) text += "（只统计前 200 种）";
                }
                parts.Add(text + "。");
            }
            else
            {
                parts.Add("没有识别到明确的 HTTP 请求（可能只截取了业务日志或 SQL）。");
            }
            if (state.SqlCount > 0)
            {
                parts.Add("执行 SQL " + N0(state.SqlCount) + " 条。");
            }
            if (state.Phones.Count > 0)
            {
                parts.Add("出现手机号 " + JoinLimited(state.Phones, 3, "、") + "（已打码）。");
            }
            if (state.Controllers.Count > 0)
            {
                parts.Add("进入 " + JoinLimited(state.Controllers, 2, "、") + "。");
            }
            if (state.Exceptions.Count > 0)
            {
                string text;
                if (state.PrimaryExceptionCount == 1 && state.Exceptions.Count > 1)
                {
                    text = "发现 1 条异常链（共 " + N0(state.Exceptions.Count) + " 层，最底层是根因）";
                }
                else
                {
                    text = "发现 " + N0(state.Exceptions.Count) + " 个异常";
                    if (state.PrimaryExceptionCount > 0)
                    {
                        text += "，其中 " + N0(state.Exceptions.Count - state.PrimaryExceptionCount) +
                                " 个是 Caused by/nested（更接近根因）";
                    }
                }
                int repeats = 0;
                foreach (LogExceptionEntry entry in state.Exceptions) repeats = Math.Max(repeats, entry.RepeatCount);
                if (repeats > 1) text += "；同一异常链在日志里重复 " + N0(repeats) + " 次";
                parts.Add(text + "。");
            }
            if (state.HasJdkSerializationFailure)
            {
                parts.Add("这是 JDK 原生反序列化失败（DefaultDeserializer），常见于 Redis 旧缓存或旧消息，不是 JSON 字段解析问题。");
            }
            if (state.MissingClasses.Count > 0)
            {
                parts.Add("缺失的类：" + JoinLimited(state.MissingClasses, 3, "、") + "。");
            }
            if (state.ErrorLevelCount > 0)
            {
                parts.Add("ERROR 级 " + N0(state.ErrorLevelCount) + " 条。");
            }
            if (state.WarnCount > 0)
            {
                parts.Add("WARN 级 " + N0(state.WarnCount) + " 条。");
            }
            if (state.HttpErrorCount > 0)
            {
                parts.Add("HTTP 4xx/5xx " + N0(state.HttpErrorCount) + " 次（" + FormatStatuses(state, true) + "）。");
            }
            if (state.HasSignatureError)
            {
                parts.Add("出现 AppKey/签名相关错误关键词。");
            }
            if (state.HasK8sProblem)
            {
                parts.Add("K8s 实例状态异常：" + K8sSummary(state) + "。");
            }
            if (state.HasSmsHint)
            {
                parts.Add("日志里出现短信/验证码相关字样。");
            }
            int noise = state.NoiseFramework + state.NoiseHeartbeat + state.NoiseDuplicate;
            if (noise > 0)
            {
                parts.Add(state.FilterNoise
                    ? "已按设置折叠 " + N0(noise) + " 行噪音。"
                    : "检测到 " + N0(noise) + " 行可折叠噪音，按当前设置保留。");
            }
            if (state.OnlyErrors)
            {
                parts.Add("当前勾选「只看错误」，只保留错误前后各 " + ErrorContextLines + " 行。");
            }
            if (state.Truncated)
            {
                parts.Add("日志超过 " + N0(MaxLines) + " 行，只分析了前面部分。");
            }
            if (state.TruncatedLines)
            {
                parts.Add("个别超长行已截断。");
            }

            if (parts.Count == 0)
            {
                return "这段日志没有识别到接口、异常、SQL 或手机号，主要是普通文本。";
            }
            return string.Join(" ", parts.ToArray());
        }

        private static string BuildKeyInfo(LogState state)
        {
            if (state.TotalLines == 0) return "没有读到日志。";

            List<string> items = new List<string>();
            if (state.Services.Count > 0)
            {
                items.Add("服务/模块：" + JoinLimited(state.Services, 3, "、"));
            }
            if (state.Controllers.Count > 0)
            {
                items.Add("Controller：" + JoinLimited(state.Controllers, 3, "、"));
            }
            if (state.Requests.Count > 0)
            {
                StringBuilder b = new StringBuilder();
                int count = Math.Min(3, state.Requests.Count);
                for (int i = 0; i < count; i++)
                {
                    if (i > 0) b.Append("；");
                    RequestStat stat = state.Requests[i];
                    b.Append(FormatRequestBrief(stat.Method, stat.Uri, 110));
                    b.Append("（").Append(N0(stat.Count)).Append(" 次）");
                }
                if (state.Requests.Count > 3) b.Append("；等 ").Append(N0(state.Requests.Count)).Append(" 种接口");
                if (state.RequestOverflow) b.Append("（接口太多，只统计前 200 种）");
                items.Add("接口：" + b.ToString());
            }
            if (state.TraceIds.Count > 0)
            {
                items.Add("traceId/请求号：" + JoinLimited(state.TraceIds, 3, "、"));
            }
            if (state.MissingClasses.Count > 0)
            {
                items.Add("缺失的类：" + JoinLimited(state.MissingClasses, 3, "、"));
            }
            if (state.HasJdkSerializationFailure)
            {
                items.Add("反序列化：DefaultDeserializer / JDK 原生序列化（常见于 Redis 缓存、消息或 session）");
            }
            if (state.Phones.Count > 0)
            {
                items.Add("手机号：" + JoinLimited(state.Phones, 3, "、") + "（已打码）");
            }
            if (state.BizIds.Count > 0)
            {
                items.Add("业务标识：" + JoinLimited(state.BizIds, 4, "、"));
            }
            if (state.SqlCount > 0)
            {
                string text = "SQL：执行 " + N0(state.SqlCount) + " 条";
                if (state.SqlExample.Length > 0)
                {
                    text += "；示例：" + Shorten(MaskSensitiveText(state.SqlExample), 180);
                }
                if (state.SqlParamExample.Length > 0)
                {
                    text += "；参数示例：" + Shorten(MaskSensitiveText(state.SqlParamExample), 140);
                }
                items.Add(text);
            }
            string statuses = FormatStatuses(state, false);
            if (statuses.Length > 0)
            {
                items.Add("HTTP 状态：" + statuses);
            }
            if (state.K8sNotes.Count > 0)
            {
                items.Add("K8s：" + JoinLimited(state.K8sNotes, 3, "、"));
            }
            if (state.HasSignatureError)
            {
                items.Add("签名：检测到 AppKey/签名相关错误关键词");
            }
            if (state.HasSmsHint)
            {
                items.Add("短信线索：日志里出现短信/验证码相关字样");
            }
            if (state.HealthRequestCount > 0)
            {
                items.Add("健康检查/心跳：" + N0(state.HealthRequestCount) + " 次（按噪音处理）");
            }

            if (items.Count == 0)
            {
                return "没有识别到服务名、接口、traceId、手机号或单号；可以贴更靠前的请求日志。";
            }
            return "· " + string.Join("\n· ", items.ToArray());
        }

        private static string BuildErrors(LogState state)
        {
            if (state.TotalLines == 0) return "没有日志可分析。";
            if (state.OnlyErrors && state.KeptCount == 0)
            {
                return "勾选「只看错误」后没有找到 ERROR/WARN/Exception；取消勾选可看全部日志。";
            }

            bool hasSignal = state.ErrorLevelCount > 0 || state.Exceptions.Count > 0 ||
                             state.WarnCount > 0 || state.HttpErrorCount > 0 ||
                             state.HasSignatureError || state.HasK8sProblem;
            if (!hasSignal)
            {
                return "本段没有 ERROR/WARN/Exception，也没有 HTTP 4xx/5xx，没有发现明显报错。";
            }

            List<string> lines = new List<string>();
            List<string> summary = new List<string>();
            if (state.ErrorLevelCount > 0) summary.Add("ERROR 级 " + N0(state.ErrorLevelCount) + " 条");
            if (state.WarnCount > 0) summary.Add("WARN 级 " + N0(state.WarnCount) + " 条");
            if (state.Exceptions.Count > 0)
            {
                string text = (state.PrimaryExceptionCount == 1 && state.Exceptions.Count > 1)
                    ? "异常链 " + N0(state.Exceptions.Count) + " 层"
                    : "异常 " + N0(state.Exceptions.Count) + " 个";
                int repeats = 0;
                foreach (LogExceptionEntry entry in state.Exceptions) repeats = Math.Max(repeats, entry.RepeatCount);
                if (repeats > 1) text += "（同一异常链重复 " + N0(repeats) + " 次）";
                summary.Add(text);
            }
            if (state.HttpErrorCount > 0) summary.Add("HTTP 4xx/5xx " + N0(state.HttpErrorCount) + " 次");
            if (state.HasSignatureError) summary.Add("疑似签名错误");
            if (state.HasK8sProblem) summary.Add("K8s 状态异常");
            lines.Add("摘要：" + string.Join("；", summary.ToArray()) + "。");

            int shown = 0;
            foreach (LogExceptionEntry entry in state.Exceptions)
            {
                if (shown >= 6)
                {
                    lines.Add("……还有 " + N0(state.Exceptions.Count - 6) + " 个异常未展开。");
                    break;
                }

                string label = entry.SimpleName;
                if (entry.ShortLabel.Length > 0 && !string.Equals(entry.ShortLabel, entry.SimpleName, StringComparison.Ordinal))
                {
                    label += "（" + entry.ShortLabel + "）";
                }
                if (entry.ExplicitCausedBy) label = "Caused by " + label;
                else if (entry.IsCausedBy &&
                         !entry.SimpleName.StartsWith("Nested", StringComparison.OrdinalIgnoreCase))
                {
                    label = "nested " + label;
                }
                if (entry.RepeatCount > 1) label += "（出现 " + N0(entry.RepeatCount) + " 次）";

                StringBuilder b = new StringBuilder();
                b.AppendLine("· " + label);
                string trace = entry.TraceId.Length > 0 ? "，traceId " + entry.TraceId : "";
                if (entry.Message.Length > 0)
                {
                    b.AppendLine("  原文：" + Shorten(MaskSensitiveText(entry.Message), 180) +
                                 "（第 " + N0(entry.LineNumber) + " 行" + trace + "）");
                }
                else
                {
                    b.AppendLine("  位置：第 " + N0(entry.LineNumber) + " 行" + trace);
                }
                b.AppendLine("  意思：" + entry.Explanation);
                if (entry.Frames.Count > 0)
                {
                    b.AppendLine("  业务堆栈：" + string.Join(" ← ", entry.Frames.ToArray()));
                }
                b.AppendLine("  建议：" + entry.Suggestion);
                lines.Add(b.ToString().TrimEnd());
                shown++;
            }

            if (state.Exceptions.Count == 0)
            {
                if (state.ErrorMessages.Count > 0)
                {
                    lines.Add("ERROR 原文（前 " + Math.Min(3, state.ErrorMessages.Count) + " 条）：");
                    for (int i = 0; i < Math.Min(3, state.ErrorMessages.Count); i++)
                    {
                        lines.Add("· " + state.ErrorMessages[i]);
                    }
                }
                if (state.WarnMessages.Count > 0)
                {
                    lines.Add("WARN 原文（前 " + Math.Min(3, state.WarnMessages.Count) + " 条）：");
                    for (int i = 0; i < Math.Min(3, state.WarnMessages.Count); i++)
                    {
                        lines.Add("· " + state.WarnMessages[i]);
                    }
                }
            }
            else if (state.WarnCount > 0)
            {
                lines.Add("另有 " + N0(state.WarnCount) + " 条 WARN；如果和报错同时出现，可能是重试或降级。");
            }

            if (state.HttpErrorCount > 0)
            {
                lines.Add("HTTP 错误：" + FormatStatuses(state, true) + "。");
            }
            if (state.HasSignatureError)
            {
                lines.Add("签名/鉴权：出现 AppKey 或签名相关错误关键词，可能是 appKey/secret/时间戳对不上。");
            }
            if (state.HasK8sProblem)
            {
                lines.Add("K8s：" + JoinLimited(state.K8sNotes, 4, "；") + "。");
            }

            return string.Join("\n", lines.ToArray());
        }

        private static string BuildSuggestions(LogState state)
        {
            if (state.TotalLines == 0) return "没有日志可分析。";
            if (state.OnlyErrors && state.KeptCount == 0)
            {
                return "1. 取消「只看错误」后重新分析，先确认这段日志里到底有没有异常。";
            }

            List<string> tips = new List<string>();
            if (state.HasJdkSerializationFailure)
            {
                if (state.MissingClasses.Count > 0)
                {
                    string missing = state.MissingClasses[0];
                    AddTip(tips, "先全局搜索 " + missing + "，对照最近几次发布，确认类是否被删除、改名或移到了别的模块。");
                    AddTip(tips, "这类 DefaultDeserializer 报错通常来自 Redis 缓存、MQ 消息或 session；日志没给出具体 key，先按业务缓存前缀 + requestId 到 Redis/访问日志里关联，找到后先备份再清理或迁移。");
                    AddTip(tips, "解压线上运行的 jar，确认里面有 " + missing.Replace('.', '/') +
                                 ".class；排除编译有、打包漏、依赖版本不一致。");
                    AddTip(tips, "长期把跨版本缓存对象改成 JSON/Jackson 序列化，或给缓存 key 加版本号，避免类改名后整批反序列化失败。");
                }
                else
                {
                    AddTip(tips, "优先按 Redis 旧缓存 / 旧消息排查：清掉或迁移对应 key，再确认当前版本的对象类和 serialVersionUID 没有变化。");
                    AddTip(tips, "长期把跨版本缓存对象改成 JSON/Jackson 序列化，或给缓存 key 加版本号，避免版本升级后整批反序列化失败。");
                }
            }
            else
            {
                LogExceptionEntry first = FirstPrimaryException(state);
                if (first != null) AddTip(tips, first.Suggestion);

                int added = 0;
                foreach (LogExceptionEntry entry in state.Exceptions)
                {
                    if (entry == first) continue;
                    AddTip(tips, entry.Suggestion);
                    added++;
                    if (added >= 1) break;
                }
            }

            if (state.HasSignatureError)
            {
                AddTip(tips, "核对 appKey/secret、签名算法、时间戳和 nonce，确认双方配置一致；网关验签失败时先把原始请求参数和签名串对齐。");
            }
            if (state.HttpErrorCount > 0)
            {
                if (HasStatus(state, 502) || HasStatus(state, 503) || HasStatus(state, 504))
                {
                    AddTip(tips, "出现 502/503/504 说明网关或下游服务不稳定：先看下游健康检查、实例重启和超时设置。");
                }
                else
                {
                    AddTip(tips, "HTTP 4xx 先查鉴权、参数和权限；5xx 先看服务端日志、线程池和下游依赖。");
                }
            }
            if (state.HasK8sProblem)
            {
                AddTip(tips, "K8s 实例状态异常：先看 kubectl describe pod 的 Events、lastState 和重启原因，再针对 OOMKilled/探针失败处理。");
            }
            if (state.HasSmsHint)
            {
                AddTip(tips, "如果短信没收到，查短信网关回执、签名/模板报备和手机号状态；不要只凭 INFO 判断发送成功。");
            }
            if (state.Exceptions.Count > 0 && state.TraceSet.Count == 0)
            {
                AddTip(tips, "日志里没有 traceId/requestId；把网关入口日志或上游调用日志一起贴进来，才能串起一次请求。");
            }
            if (state.TraceSet.Count > 1)
            {
                AddTip(tips, "按 traceId 逐个看：先处理有 ERROR 的那条，再从请求入口看到最后一行。");
            }
            if (state.Truncated)
            {
                AddTip(tips, "日志被截断，后面可能有更关键的错误；按 traceId 过滤后重贴。");
            }
            if (state.ErrorLevelCount == 0 && state.Exceptions.Count == 0 &&
                state.WarnCount == 0 && state.HttpErrorCount == 0 && !state.HasK8sProblem && !state.HasSignatureError)
            {
                if (state.TotalRequestCalls > 0)
                {
                    AddTip(tips, "如果接口实际失败，失败可能发生在别的服务或本段之后；按 traceId 继续搜 ERROR/HTTP 5xx。");
                }
                else
                {
                    AddTip(tips, "这段没有报错；如果是在排查业务问题，建议直接按 traceId 搜完整链路。");
                }
            }

            if (state.OnlyErrors)
            {
                AddTip(tips, "当前是「只看错误」视图；如果上下文不够，取消勾选重新分析，或点「AI 解读」。");
            }
            if (tips.Count == 0)
            {
                AddTip(tips, "先按 traceId 搜完整链路，找到第一条 ERROR 和它下面的 Caused by，再回到业务代码。");
            }

            StringBuilder b = new StringBuilder();
            for (int i = 0; i < tips.Count; i++)
            {
                b.AppendLine((i + 1) + ". " + tips[i]);
            }
            return b.ToString().TrimEnd();
        }

        private static string BuildTraceSection(LogState state)
        {
            if (!state.Options.GroupByTrace || state.Groups.Count == 0) return "";

            StringBuilder b = new StringBuilder();
            if (state.Groups.Count == 1)
            {
                LogTraceGroup group = state.Groups[0];
                b.AppendLine("traceId/请求号：" + group.Key);
                b.AppendLine(BuildTraceStory(group));
                return b.ToString().TrimEnd();
            }

            b.AppendLine("共 " + N0(state.Groups.Count) + " 个 traceId/请求号（按当前显示范围统计，最多展开前 5 个）：");
            int listCount = Math.Min(10, state.Groups.Count);
            for (int i = 0; i < listCount; i++)
            {
                LogTraceGroup group = state.Groups[i];
                string request = FormatTraceRequest(group, 60);
                b.AppendLine((i + 1) + ". " + ShortId(group.Key) + " — " + request + "；" +
                             N0(group.Lines.Count) + " 行，" + TraceProblemText(group));
            }
            if (state.Groups.Count > listCount)
            {
                b.AppendLine("……还有 " + N0(state.Groups.Count - listCount) + " 个 traceId 未列出。");
            }
            if (state.GroupsTruncated)
            {
                b.AppendLine("traceId 太多，只按前 " + N0(MaxTraceGroups) + " 个分组。");
            }

            int detailCount = Math.Min(5, state.Groups.Count);
            for (int i = 0; i < detailCount; i++)
            {
                LogTraceGroup group = state.Groups[i];
                b.AppendLine();
                b.AppendLine("【" + (i + 1) + "】traceId/请求号：" + group.Key + "（" +
                             N0(group.Lines.Count) + " 行，" + TraceProblemText(group) + "）");
                b.AppendLine(BuildTraceStory(group));
            }
            if (state.Groups.Count > detailCount)
            {
                b.AppendLine();
                b.AppendLine("还有 " + N0(state.Groups.Count - detailCount) +
                             " 个 traceId 未展开；可以勾选「只看错误」缩小范围，或按 traceId 搜索。");
            }
            return b.ToString().TrimEnd();
        }

        private static string BuildTraceStory(LogTraceGroup group)
        {
            List<string> steps = new List<string>();
            if (group.Uri.Length > 0)
            {
                steps.Add("收到 " + FormatRequestBrief(group.Method, group.Uri, 80));
            }
            else
            {
                steps.Add("没有识别到明确的 HTTP 请求");
            }
            if (group.Controller.Length > 0) steps.Add("进入 " + Shorten(group.Controller, 80));
            if (group.Phones.Count == 1) steps.Add("手机号 " + group.Phones[0]);
            else if (group.Phones.Count > 1) steps.Add("涉及 " + group.Phones.Count + " 个手机号");
            if (group.BizIds.Count > 0) steps.Add("业务标识 " + group.BizIds[0]);
            if (group.SqlCount > 0) steps.Add("执行 SQL " + N0(group.SqlCount) + " 条");

            LogExceptionEntry first = null;
            foreach (LogExceptionEntry entry in group.Exceptions)
            {
                if (!entry.IsCausedBy)
                {
                    first = entry;
                    break;
                }
            }
            if (first == null && group.Exceptions.Count > 0) first = group.Exceptions[0];

            LogExceptionEntry rootCause = FindRootCause(group.Exceptions, first);

            if (group.Exceptions.Count > 0)
            {
                string text = "抛出 " + first.SimpleName;
                if (rootCause != null)
                {
                    text += "，" + (rootCause.ExplicitCausedBy ? "底层 Caused by：" : "底层原因：") +
                            rootCause.SimpleName;
                    if (rootCause.Message.Length > 0)
                    {
                        text += "（" + Shorten(MaskSensitiveText(rootCause.Message), 60) + "）";
                    }
                }
                else if (group.Exceptions.Count > 1)
                {
                    text += "（另有 " + N0(group.Exceptions.Count - 1) + " 个异常/Caused by）";
                }
                steps.Add(text);
            }
            else if (group.ErrorLines > 0)
            {
                steps.Add("出现 " + N0(group.ErrorLines) + " 条 ERROR");
            }
            if (group.WarnLines > 0) steps.Add("有 " + N0(group.WarnLines) + " 条 WARN");
            if (group.Exceptions.Count == 0 && group.ErrorLines == 0 && group.WarnLines == 0)
            {
                steps.Add("没有看到 ERROR/Exception");
            }

            StringBuilder b = new StringBuilder();
            b.AppendLine("经过：" + string.Join(" → ", steps.ToArray()) + "。");
            if (first != null)
            {
                string message = first.Message.Length > 0
                    ? Shorten(MaskSensitiveText(first.Message), 180)
                    : first.ShortLabel;
                b.AppendLine("报错：" + first.SimpleName + "：" + message + "（第 " + N0(first.LineNumber) + " 行）。");
                if (first.Frames.Count > 0)
                {
                    b.AppendLine("业务堆栈：" + string.Join(" ← ", first.Frames.ToArray()));
                }
                b.AppendLine("建议：" + Shorten(first.Suggestion, 240));
                if (rootCause != null)
                {
                    string rootMessage = rootCause.Message.Length > 0
                        ? Shorten(MaskSensitiveText(rootCause.Message), 140)
                        : rootCause.ShortLabel;
                    b.AppendLine("底层原因：" + rootCause.SimpleName + "：" + rootMessage +
                                 "；" + Shorten(rootCause.Suggestion, 200));
                }
            }
            else if (group.ErrorLines > 0)
            {
                b.AppendLine("报错：有 " + N0(group.ErrorLines) + " 条 ERROR，但没有识别到标准异常类。");
            }
            else if (group.WarnLines > 0)
            {
                b.AppendLine("报错：没有异常堆栈，有 " + N0(group.WarnLines) + " 条 WARN。");
            }
            else
            {
                b.AppendLine("报错：没有看到 ERROR/Exception。");
            }
            return b.ToString().TrimEnd();
        }

        private static string BuildFooter(LogState state)
        {
            StringBuilder b = new StringBuilder();
            int noise = state.NoiseFramework + state.NoiseHeartbeat + state.NoiseDuplicate;
            if (noise > 0)
            {
                List<string> kinds = new List<string>();
                if (state.NoiseFramework > 0) kinds.Add("框架刷屏 " + N0(state.NoiseFramework) + " 行");
                if (state.NoiseHeartbeat > 0) kinds.Add("心跳/空跑 " + N0(state.NoiseHeartbeat) + " 行");
                if (state.NoiseDuplicate > 0) kinds.Add("重复 INFO " + N0(state.NoiseDuplicate) + " 行");
                b.AppendLine("—— 噪音：识别到 " + N0(noise) + " 行（" + string.Join("、", kinds.ToArray()) + "），" +
                             (state.FilterNoise ? "已按设置折叠。" : "按当前设置保留。"));
            }
            else
            {
                b.AppendLine("—— 噪音：没有识别到可折叠的重复、心跳或框架刷屏。");
            }
            if (state.OnlyErrors)
            {
                b.AppendLine("—— 只看错误：保留 " + N0(state.KeptCount) + " 行（错误前后各 " + ErrorContextLines + " 行）。");
            }
            if (state.Truncated)
            {
                b.AppendLine("—— 日志过长：只分析了前 " + N0(MaxLines) + " 行。");
            }
            if (state.TruncatedLines)
            {
                b.AppendLine("—— 个别超长行已截断到 " + N0(MaxCharsPerLine) + " 字符。");
            }
            if (state.GroupsTruncated)
            {
                b.AppendLine("—— traceId 太多：只按前 " + N0(MaxTraceGroups) + " 个分组。");
            }
            if (state.RequestOverflow)
            {
                b.AppendLine("—— 接口种类过多：只统计前 200 个。");
            }
            return b.ToString().TrimEnd();
        }

        private static string TraceProblemText(LogTraceGroup group)
        {
            List<string> parts = new List<string>();
            if (group.ErrorLines > 0) parts.Add(N0(group.ErrorLines) + " 条 ERROR");
            if (group.WarnLines > 0) parts.Add(N0(group.WarnLines) + " 条 WARN");
            if (group.Exceptions.Count > 0)
            {
                LogExceptionEntry first = null;
                foreach (LogExceptionEntry entry in group.Exceptions)
                {
                    if (!entry.IsCausedBy) { first = entry; break; }
                }
                if (first == null) first = group.Exceptions[0];
                LogExceptionEntry root = FindRootCause(group.Exceptions, first);
                string text = N0(group.Exceptions.Count) + " 个异常";
                if (root != null) text += "（根因 " + root.SimpleName + "）";
                parts.Add(text);
            }
            if (parts.Count == 0) return "无 ERROR";
            return string.Join("、", parts.ToArray());
        }

        private static string FormatTraceRequest(LogTraceGroup group, int max)
        {
            if (group.Uri.Length == 0) return "未识别到接口";
            return FormatRequestBrief(group.Method, group.Uri, max);
        }

        private static string K8sSummary(LogState state)
        {
            if (state.K8sNotes.Count == 0) return "实例状态异常";
            return JoinLimited(state.K8sNotes, 3, "、");
        }

        private static string PhoneSummary(LogState state)
        {
            if (state.Phones.Count == 0) return "";
            if (state.Phones.Count == 1) return "，涉及手机号 " + state.Phones[0];
            return "，涉及 " + N0(state.Phones.Count) + " 个手机号";
        }

        private static LogExceptionEntry FirstPrimaryException(LogState state)
        {
            foreach (LogExceptionEntry entry in state.Exceptions)
            {
                if (!entry.IsCausedBy) return entry;
            }
            return state.Exceptions.Count > 0 ? state.Exceptions[0] : null;
        }

        private static string FormatStatuses(LogState state, bool errorsOnly)
        {
            List<int> codes = new List<int>(state.StatusCounts.Keys);
            codes.Sort();
            StringBuilder b = new StringBuilder();
            int shown = 0;
            foreach (int code in codes)
            {
                if (errorsOnly && code < 400) continue;
                if (shown > 0) b.Append("、");
                b.Append(code).Append("×").Append(N0(state.StatusCounts[code]));
                shown++;
                if (shown >= 8)
                {
                    b.Append(" 等");
                    break;
                }
            }
            return b.ToString();
        }

        private static bool HasStatus(LogState state, int code)
        {
            int count;
            return state.StatusCounts.TryGetValue(code, out count) && count > 0;
        }

        private static string FormatRequestBrief(string method, string uri, int max)
        {
            string safeUri = Shorten(MaskSensitiveText(uri), max);
            string safeMethod = (method ?? "").Trim().ToUpperInvariant();
            if (safeMethod.Length == 0) return safeUri;
            if (safeUri.Length == 0) return safeMethod;
            return safeMethod + " " + safeUri;
        }

        private static string JoinLimited(List<string> values, int max, string separator)
        {
            if (values.Count == 0) return "";
            StringBuilder b = new StringBuilder();
            int count = Math.Min(max, values.Count);
            for (int i = 0; i < count; i++)
            {
                if (i > 0) b.Append(separator);
                b.Append(values[i]);
            }
            if (values.Count > count) b.Append(" 等");
            return b.ToString();
        }

        private static void AddTip(List<string> tips, string tip)
        {
            if (string.IsNullOrEmpty(tip) || tips.Count >= 5 || tips.Contains(tip)) return;
            tips.Add(tip);
        }

        private static void AddUnique(List<string> list, HashSet<string> set, string value, int max)
        {
            if (string.IsNullOrEmpty(value) || set.Contains(value) || list.Count >= max) return;
            set.Add(value);
            list.Add(value);
        }

        private static string Shorten(string value, int max)
        {
            if (string.IsNullOrEmpty(value)) return "";
            StringBuilder b = new StringBuilder(value.Length);
            bool space = false;
            foreach (char c in value)
            {
                if (char.IsWhiteSpace(c))
                {
                    if (b.Length > 0 && !space) b.Append(' ');
                    space = true;
                }
                else
                {
                    b.Append(c);
                    space = false;
                }
            }
            string text = b.ToString().Trim();
            if (max > 0 && text.Length > max) text = text.Substring(0, max) + "…";
            return text;
        }

        private static string LimitText(string value, int max)
        {
            if (string.IsNullOrEmpty(value)) return "";
            value = value.Trim();
            if (max <= 0 || value.Length <= max) return value;
            return value.Substring(0, max) + "\n…（本机摘要已截断）";
        }

        private static string ShortId(string id)
        {
            if (string.IsNullOrEmpty(id)) return "";
            if (id.Length <= 16) return id;
            return id.Substring(0, 8) + "…" + id.Substring(id.Length - 4);
        }

        private static string N0(int value)
        {
            return value.ToString("N0");
        }

        private static string MaskSensitiveText(string text)
        {
            if (string.IsNullOrEmpty(text)) return text;
            text = SecretAuthorizationRegex.Replace(text, "$1$2***");
            text = SecretCookieRegex.Replace(text, "$1$2***");
            text = SecretValueRegex.Replace(text, "$1***");
            text = UrlSecretRegex.Replace(text, "$1***");
            text = PhoneRegex.Replace(text, new MatchEvaluator(MaskPhoneMatch));
            return text;
        }

        private static string MaskPhoneMatch(Match match)
        {
            string number = match.Groups[1].Value;
            string prefix = match.Value.Substring(0, match.Groups[1].Index - match.Index);
            return prefix + MaskPhoneNumber(number);
        }

        private static string MaskPhoneNumber(string number)
        {
            if (string.IsNullOrEmpty(number) || number.Length != 11) return number;
            return number.Substring(0, 3) + "****" + number.Substring(7);
        }

        private static bool IsHealthPath(string uri)
        {
            if (string.IsNullOrEmpty(uri)) return false;
            string path = uri;
            if (path.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                path.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    path = new Uri(uri).AbsolutePath;
                }
                catch
                {
                    int scheme = path.IndexOf("://", StringComparison.Ordinal);
                    int slash = scheme >= 0 ? path.IndexOf('/', scheme + 3) : -1;
                    path = slash >= 0 ? path.Substring(slash) : "";
                }
            }
            int cut = path.IndexOfAny(new char[] { '?', '#' });
            if (cut >= 0) path = path.Substring(0, cut);
            path = path.ToLowerInvariant();
            if (path.Length == 0 || path == "/") return false;
            return path.Contains("/health") || path.Contains("/actuator/health") ||
                   path.Contains("/readiness") || path.Contains("/liveness") ||
                   path.Contains("/readyz") || path.Contains("/livez") ||
                   path.Contains("/metrics") || path.Contains("/ping");
        }

        private static bool IsHeartbeatLine(string lower)
        {
            string text = lower.Replace('：', ':');
            if (SizeZeroRegex.IsMatch(text)) return true;
            if (text.Contains("heartbeat") || text.Contains("heart beat") || text.Contains("心跳") ||
                text.Contains("keepalive") || text.Contains("keep-alive") ||
                text.Contains("health check") || text.Contains("healthcheck") || text.Contains("健康检查") ||
                text.Contains("probe passed") || text.Contains("probe succeeded") ||
                text.Contains("无任务") || text.Contains("没有任务") || text.Contains("空跑") ||
                text.Contains("no task") || text.Contains("no tasks") ||
                text.Contains("nothing to process") || text.Contains("no data to process") ||
                text.Contains("no records to process"))
            {
                return true;
            }
            return false;
        }

        private static bool IsFrameworkNoise(string lower)
        {
            if (lower.Contains("creating a new sqlsession") ||
                lower.Contains("closing non transactional sqlsession") ||
                lower.Contains("registering transaction synchronization") ||
                lower.Contains("releasing transactional sqlsession") ||
                lower.Contains("transaction synchronization") ||
                lower.Contains("will not be managed by spring") ||
                lower.Contains("fetching jdbc connection") ||
                lower.Contains("hikaripool-") && (lower.Contains("start completed") ||
                    lower.Contains("added connection") || lower.Contains("connections: active=") ||
                    lower.Contains("pool stats")) ||
                lower.Contains("tomcat initialized with port") ||
                lower.Contains("tomcat started on port") ||
                lower.Contains("dispatcherservlet") ||
                lower.Contains("root webapplicationcontext") ||
                lower.Contains("completed initialization in") ||
                lower.Contains("initializing spring") ||
                lower.Contains("initialization completed") ||
                lower.Contains("the following") && lower.Contains("profile") && lower.Contains("active") ||
                lower.Contains("no active profile set") ||
                lower.Contains("mapped \"{") ||
                lower.Contains("exposing ") && lower.Contains("endpoint") ||
                lower.Contains("spring boot") && lower.Contains("::") ||
                lower.Contains("started application") && lower.Contains("jvm running for"))
            {
                return true;
            }
            return false;
        }

        private static bool IsAppFrame(string method)
        {
            if (string.IsNullOrEmpty(method)) return false;
            string[] skip =
            {
                "java.", "javax.", "sun.", "jdk.", "org.springframework.", "org.apache.",
                "org.mybatis.", "org.slf4j.", "ch.qos.logback.", "com.zaxxer.",
                "io.netty.", "reactor.", "com.fasterxml.", "org.hibernate.", "org.yaml.",
                "org.redisson.", "org.junit.", "com.baomidou.", "com.mysql.", "io.lettuce."
            };
            foreach (string prefix in skip)
            {
                if (method.StartsWith(prefix, StringComparison.Ordinal)) return false;
            }
            return true;
        }

        private static bool IsMimeValue(string value)
        {
            string lower = (value ?? "").ToLowerInvariant();
            return lower == "application" || lower == "text" || lower == "multipart" ||
                   lower == "image" || lower == "audio" || lower == "video" ||
                   lower == "message" || lower == "font";
        }

        private static bool CaptureK8s(string clean, LogState state)
        {
            string lower = clean.ToLowerInvariant();
            bool problem = false;

            if (lower.Contains("crashloopbackoff"))
            {
                AddK8sNote(state, "CrashLoopBackOff（容器启动后又退出，正在反复重启）");
                problem = true;
            }
            if (lower.Contains("oomkilled"))
            {
                AddK8sNote(state, "OOMKilled（容器内存超限被杀）");
                problem = true;
            }
            if (lower.Contains("imagepullbackoff") || lower.Contains("errimagepull"))
            {
                AddK8sNote(state, "镜像拉取失败（ImagePullBackOff/ErrImagePull）");
                problem = true;
            }
            if (lower.Contains("back-off restarting failed container"))
            {
                AddK8sNote(state, "容器启动失败后正在退避重启");
                problem = true;
            }
            if (lower.Contains("evicted"))
            {
                AddK8sNote(state, "Pod 被驱逐（Evicted）");
                problem = true;
            }
            if (lower.Contains("liveness probe failed") || lower.Contains("readiness probe failed") ||
                lower.Contains("probe failed") || lower.Contains("健康检查失败"))
            {
                AddK8sNote(state, "探针/健康检查失败");
                problem = true;
            }
            if (lower.Contains("failedmount") || lower.Contains("unable to attach or mount volumes"))
            {
                AddK8sNote(state, "存储卷挂载失败");
                problem = true;
            }

            if (lower.Contains("ready"))
            {
                Match ready = ReadyRegex.Match(clean);
                if (ready.Success)
                {
                    int up;
                    int total;
                    int.TryParse(ready.Groups[1].Value, out up);
                    int.TryParse(ready.Groups[2].Value, out total);
                    if (up == total)
                    {
                        AddK8sNote(state, "Ready " + up + "/" + total + "（已就绪）");
                    }
                    else
                    {
                        AddK8sNote(state, "Ready " + up + "/" + total + "（未就绪）");
                        problem = true;
                    }
                }
            }
            if (lower.Contains("restarts"))
            {
                Match restarts = RestartsRegex.Match(clean);
                if (restarts.Success)
                {
                    int count;
                    if (int.TryParse(restarts.Groups[1].Value, out count) && count > 0)
                    {
                        AddK8sNote(state, "重启 " + count + " 次");
                        problem = true;
                    }
                }
            }

            if (problem) state.HasK8sProblem = true;
            return problem;
        }

        private static void AddK8sNote(LogState state, string note)
        {
            if (string.IsNullOrEmpty(note) || state.K8sNotes.Count >= 8 || state.K8sNoteSet.Contains(note)) return;
            state.K8sNoteSet.Add(note);
            state.K8sNotes.Add(note);
        }

        private static string NormalizeDuplicate(string clean)
        {
            string text = TimestampPrefixRegex.Replace(clean, "");
            text = ThreadPrefixRegex.Replace(text, "");
            text = text.Trim();
            if (text.Length > 400) text = text.Substring(0, 400);
            return text;
        }

        private static string NormalizeTimestamp(string value)
        {
            return value.Trim().Replace('T', ' ').Replace('/', '-');
        }

        private static string SimpleName(string className)
        {
            if (string.IsNullOrEmpty(className)) return "";
            int dot = className.LastIndexOf('.');
            return dot >= 0 ? className.Substring(dot + 1) : className;
        }

        private static string ExtractControllerName(string text)
        {
            if (string.IsNullOrEmpty(text) || text.IndexOf("Controller", StringComparison.Ordinal) < 0) return "";
            Match controller = ControllerRegex.Match(text);
            if (!controller.Success) return "";
            string type = controller.Groups[1].Value;
            string method = controller.Groups[2].Success
                ? controller.Groups[2].Value
                : (controller.Groups[3].Success ? controller.Groups[3].Value : "");
            return method.Length == 0 ? type : type + "." + method;
        }

        private static LogExceptionEntry FindExistingException(
            LogState state, string className, string message, string traceId, bool causedBy)
        {
            foreach (LogExceptionEntry entry in state.Exceptions)
            {
                if (entry.IsCausedBy != causedBy) continue;
                if (!string.Equals(entry.ClassName, className, StringComparison.Ordinal)) continue;
                if (!string.Equals(entry.TraceId, traceId, StringComparison.Ordinal)) continue;
                if (!string.Equals(entry.Message, message, StringComparison.Ordinal)) continue;
                return entry;
            }
            return null;
        }

        private static LogExceptionEntry FindRootCause(IList<LogExceptionEntry> exceptions, LogExceptionEntry first)
        {
            LogExceptionEntry caused = null;
            foreach (LogExceptionEntry entry in exceptions)
            {
                if (entry != first && entry.IsCausedBy) caused = entry;
            }
            return caused;
        }

        private static void AddMissingClass(LogState state, string message)
        {
            string text = (message ?? "").Trim();
            if (text.Length == 0) return;
            int cut = text.IndexOfAny(new char[] { ' ', '\t', '\r', '\n', ',', ';', '(', ')' });
            if (cut > 0) text = text.Substring(0, cut);
            text = text.Trim('[', ']', '\'', '"');
            if (text.IndexOf('.') <= 0 || text.IndexOf('/') >= 0) return;
            AddUnique(state.MissingClasses, state.MissingClassSet, text, 10);
        }

        private static string ExtractShortLabel(string explanation, string fallback)
        {
            if (!string.IsNullOrEmpty(explanation))
            {
                int colon = explanation.IndexOf('：');
                if (colon > 0) return explanation.Substring(0, colon);
                colon = explanation.IndexOf(':');
                if (colon > 0) return explanation.Substring(0, colon);
            }
            return fallback;
        }

        private static string ExplainException(string className, string message, bool jdkSerialization)
        {
            string n = (className ?? "").ToLowerInvariant();
            string m = (message ?? "").ToLowerInvariant();
            if (jdkSerialization && (n.Contains("classnotfound") || n.Contains("noclassdeffound")))
                return "JDK 反序列化找不到类：这段字节流里记录的对象类型在当前运行环境的 classpath 不存在，常见于 Redis 旧缓存、旧消息，或 DTO 被删除/改名/拆到别的模块。";
            if (n.Contains("invalidclass") || m.Contains("serialversionuid"))
                return "JDK 序列化版本不一致：缓存/消息里的类版本和当前类定义对不上（serialVersionUID 变了），常见于发布新版本后读到旧对象。";
            bool serializationClass = n.Contains("serialization") || n.Contains("deserialize") ||
                                      n.Contains("nestedio") || n.Contains("invalidclass");
            if ((jdkSerialization && serializationClass) ||
                n.Contains("serializationfailed") || n.Contains("nestedioexception"))
                return "JDK/对象流反序列化失败：字节流和当前类定义或 classpath 对不上，最常见的是 Redis 旧缓存、旧消息，或对象类型在当前版本已经不存在。";
            if (n.Contains("sqlexception") || n.Contains("databasedriver") || n.Contains("mybatissystem") ||
                n.Contains("badsqlgrammar") || n.Contains("sqlsyntax"))
            {
                if (m.Contains("connection refused") || m.Contains("communications link failure") ||
                    m.Contains("connection reset") || m.Contains("could not connect") ||
                    m.Contains("connection is closed") || m.Contains("broken pipe") ||
                    m.Contains("unable to acquire jdbc connection"))
                    return "数据库连接失败：数据库没启动、地址/端口不对、网络或连接池有问题。";
                if (m.Contains("access denied") || m.Contains("authentication"))
                    return "数据库登录失败：账号、密码或权限不对。";
                if (m.Contains("lock wait timeout") || m.Contains("deadlock"))
                    return "数据库锁等待/死锁：事务互相等锁，或锁等待超时。";
                if (m.Contains("duplicate") || m.Contains("unique"))
                    return "数据库唯一键冲突：重复写入了同一条数据。";
                if (m.Contains("unknown column") || m.Contains("doesn't exist") || m.Contains("does not exist"))
                    return "数据库表或字段不存在：SQL 里的表名/字段名和实际结构对不上。";
                if (m.Contains("data too long") || m.Contains("incorrect string value"))
                    return "数据库字段长度/字符集不匹配：写入的值超长或编码不对。";
                if (m.Contains("cannot add or update a child row"))
                    return "数据库外键约束失败：关联的主表记录不存在，或顺序不对。";
                return "SQL 执行失败：数据库拒绝了这条语句，具体看原始 message。";
            }
            if (n.Contains("nullpointer")) return "空指针：代码访问了一个 null 对象（变量、查询结果或远程调用返回值为空）。";
            if (n.Contains("classnotfound")) return "找不到类：运行时 classpath 里没有这个类，常见于依赖没打进去或版本不一致。";
            if (n.Contains("noclassdeffound")) return "类加载失败：编译时有这个类，运行时依赖缺失或版本冲突。";
            if (n.Contains("nosuchmethod") || n.Contains("nosuchfield")) return "找不到方法/字段：编译和运行时的 jar 版本不一致，通常是对依赖版本冲突。";
            if (n.Contains("jsonserialize") || n.Contains("jsongeneration") ||
                n.Contains("httpmessagenotreadable") || n.Contains("jsonparse") || n.Contains("jsonmapping") ||
                n.Contains("mismatchedinput")) return "JSON 序列化/反序列化失败：JSON 的结构、字段类型和 Java 对象对不上。";
            if (n.Contains("duplicatekey") || n.Contains("dataintegrity") || n.Contains("constraintviolation")) return "数据库约束冲突：唯一键重复、非空字段为空或外键不满足。";
            if (n.Contains("deadlock") || n.Contains("cannotacquirelock")) return "数据库死锁/锁等待：两个事务互相等锁，或锁等待超时。";
            if (n.Contains("timeout") || n.Contains("timedout")) return "请求超时：调用下游或数据库在限定时间内没有返回。";
            if (n.Contains("connectexception") || n.Contains("connectionrefused")) return "连接被拒绝：目标服务没启动、端口不对或被防火墙拦截。";
            if (n.Contains("unknownhost")) return "域名解析失败：DNS 找不到目标主机，或容器网络配置有问题。";
            if (n.Contains("accessdenied") || n.Contains("authentication") || n.Contains("unauthorized") ||
                n.Contains("forbidden") || n.Contains("badcredentials")) return "鉴权/权限失败：登录态、Token、角色或接口权限不满足。";
            if (n.Contains("expiredjwt")) return "登录凭证过期：JWT/Token 已过期，需要重新登录或刷新。";
            if (n.Contains("signature")) return "签名校验失败：appKey/secret、签名算法、时间戳或 nonce 对不上。";
            if (n.Contains("outofmemory")) return "内存溢出：堆内存不够，或有超大对象/一次性加载了太多数据。";
            if (n.Contains("stackoverflow")) return "调用栈过深：递归没有正确结束，或对象序列化出现循环引用。";
            if (n.Contains("illegalargument")) return "参数不合法：调用方传了不允许的值、格式或空参数。";
            if (n.Contains("illegalstate")) return "对象状态不对：当前流程/连接/事务的状态不允许这个操作。";
            if (n.Contains("numberformat")) return "数字格式错误：把非数字字符串转成了数字。";
            if (n.Contains("datetimeparse") || n.Contains("parseexception")) return "日期/文本解析失败：入参格式和代码期望的格式不一致。";
            if (n.Contains("indexoutofbounds")) return "下标越界：访问了集合/数组不存在的元素。";
            if (n.Contains("filenotfound") || n.Contains("nosuchfile")) return "文件不存在：路径、挂载目录或运行用户权限有问题。";
            if (n.Contains("ssl") || n.Contains("certificate")) return "HTTPS/证书校验失败：证书过期、域名不匹配或信任链不完整。";
            if (n.Contains("ratelimit") || n.Contains("toomanyrequests")) return "被限流：调用频率超过服务方配额。";
            if (n.Contains("methodargumentnotvalid") || n.Contains("bind")) return "参数校验失败：请求参数没有通过 @Valid / 校验注解。";
            if (n.Contains("httprequestmethodnotsupported")) return "请求方法不支持：接口只接受某种 HTTP 方法，调用方用了别的。";
            if (n.Contains("maxuploadsize")) return "上传文件超过大小限制。";
            if (n.Contains("feign") || n.Contains("restclient") || n.Contains("resourceaccess")) return "调用下游 HTTP 服务失败：可能超时、连接失败或下游返回错误。";
            if (n.Contains("typeerror") || n.Contains("referenceerror")) return "脚本运行时错误：访问了 undefined/null 的属性或变量。";
            if (n.Contains("syntaxerror")) return "语法错误：代码或 JSON 的格式不合法。";
            if (n.Contains("assertionerror") || n.Contains("assertionfailed")) return "断言失败：代码或测试检查的条件不成立。";
            if (n.Contains("classcast")) return "类型转换失败：把对象强转成了不兼容的类型。";
            if (n.Contains("concurrentmodification")) return "集合被并发修改：遍历集合时又增删了元素。";
            if (n.Contains("unsupportedoperation")) return "不支持的操作：当前实现/集合不允许这个调用。";
            if (n.Contains("arithmetic")) return "算术错误：例如除以 0。";
            if (n.Contains("interrupted")) return "线程被中断：任务被取消或线程池正在关闭。";
            return "检测到 Java/框架异常：" + className + "。通常要结合它下面的 Caused by 和业务堆栈定位根因。";
        }

        private static string SuggestionForException(string className, string message, bool jdkSerialization)
        {
            string n = (className ?? "").ToLowerInvariant();
            string m = (message ?? "").ToLowerInvariant();
            if (jdkSerialization && (n.Contains("classnotfound") || n.Contains("noclassdeffound")))
                return "先全局搜索这个 DTO，确认它是否被删除、改名或移到别的模块；再查线上 jar 里有没有对应 .class，并按 requestId 找到 Redis/消息里的旧数据，清掉或迁移后重试。";
            if (n.Contains("invalidclass") || m.Contains("serialversionuid"))
                return "缓存/消息里很可能是旧版本对象：清掉旧 key 或做版本兼容，并确认 serialVersionUID 没有意外变化。";
            bool serializationClass = n.Contains("serialization") || n.Contains("deserialize") ||
                                      n.Contains("nestedio") || n.Contains("invalidclass");
            if ((jdkSerialization && serializationClass) ||
                n.Contains("serializationfailed") || n.Contains("nestedioexception"))
                return "优先按 Redis 旧缓存 / 旧消息排查：清掉或迁移对应的缓存 key，再确认发布包包含相关 DTO；长期改用 JSON 序列化，避免类改名后整批失败。";
            if (n.Contains("sqlexception") || n.Contains("databasedriver") || n.Contains("mybatissystem") ||
                n.Contains("badsqlgrammar") || n.Contains("sqlsyntax"))
            {
                if (m.Contains("connection refused") || m.Contains("communications link failure") ||
                    m.Contains("connection reset") || m.Contains("could not connect") ||
                    m.Contains("connection is closed") || m.Contains("broken pipe") ||
                    m.Contains("unable to acquire jdbc connection"))
                    return "确认数据库地址/端口、数据库是否启动、网络/安全组和连接池配置；看数据库端是否拒绝连接。";
                if (m.Contains("access denied") || m.Contains("authentication"))
                    return "核对数据库账号、密码和授权；确认连接串里的库名/用户没有配错。";
                if (m.Contains("lock wait timeout") || m.Contains("deadlock"))
                    return "看数据库死锁日志，统一事务加锁顺序，缩短事务范围。";
                if (m.Contains("duplicate") || m.Contains("unique"))
                    return "查唯一键约束和重复提交，确认是否并发写入了同一条数据。";
                if (m.Contains("unknown column") || m.Contains("doesn't exist") || m.Contains("does not exist"))
                    return "核对表名、字段名和数据库 migration，确认运行环境的库结构没有落后。";
                if (m.Contains("data too long") || m.Contains("incorrect string value"))
                    return "核对字段长度和字符集，确认写入值没有超长、编码一致。";
                if (m.Contains("cannot add or update a child row"))
                    return "先查关联主表记录是否存在，再检查写入顺序和外键配置。";
                return "把 Preparing 的 SQL 和 Parameters 放到数据库客户端执行一遍，先排除 SQL 和参数本身的问题。";
            }
            if (n.Contains("nullpointer")) return "从报错行往上找那个为 null 的变量，重点看接口入参、查库结果和远程调用返回值；用日志里的参数复现一次。";
            if (n.Contains("classnotfound") || n.Contains("noclassdeffound")) return "检查依赖版本和打包结果（mvn dependency:tree），确认运行时 classpath 里有这个类。";
            if (n.Contains("nosuchmethod") || n.Contains("nosuchfield")) return "多半是依赖版本冲突：对齐编译和运行时的 jar 版本，检查是否有旧版本被优先加载。";
            if (n.Contains("jsonserialize") || n.Contains("jsongeneration") ||
                n.Contains("httpmessagenotreadable") || n.Contains("jsonparse") || n.Contains("jsonmapping") ||
                n.Contains("mismatchedinput")) return "把请求/响应 JSON 和目标 DTO 字段逐项对照：字段名、类型、日期格式、枚举值；注意泛型和 @JsonProperty。";
            if (n.Contains("duplicatekey") || n.Contains("dataintegrity") || n.Contains("constraintviolation")) return "查唯一键/非空/外键约束；确认是否有重复提交或并发写入了同一条数据。";
            if (n.Contains("deadlock") || n.Contains("cannotacquirelock")) return "看数据库死锁日志，统一多个事务的加锁顺序，缩短事务范围。";
            if (n.Contains("timeout") || n.Contains("timedout")) return "确认下游服务是否健康、网络/安全组是否通、线程池是否打满；再评估超时阈值是否合理。";
            if (n.Contains("connectexception") || n.Contains("connectionrefused")) return "确认目标地址和端口、服务是否启动、DNS/网络是否可达。";
            if (n.Contains("unknownhost")) return "检查 DNS、容器网络和服务名解析，确认目标主机名没有拼错。";
            if (n.Contains("accessdenied") || n.Contains("authentication") || n.Contains("unauthorized") ||
                n.Contains("forbidden") || n.Contains("badcredentials")) return "检查登录态、Token 有效期、角色权限和网关鉴权配置。";
            if (n.Contains("expiredjwt")) return "让用户重新登录，检查 Token 有效期和服务器时钟是否同步。";
            if (n.Contains("signature")) return "核对 appKey/secret、签名算法、时间戳和 nonce，确认双方配置一致。";
            if (n.Contains("outofmemory")) return "看堆内存和 GC 日志，确认是否有大对象/一次性查全表；必要时调大堆或修泄漏。";
            if (n.Contains("stackoverflow")) return "检查递归终止条件和对象循环引用，必要时改用迭代。";
            if (n.Contains("illegalargument") || n.Contains("illegalstate")) return "回到调用方检查参数和对象状态，看是哪个校验或分支抛出的。";
            if (n.Contains("numberformat")) return "检查入参格式和空值，确认前端传的数字字符串合法。";
            if (n.Contains("datetimeparse") || n.Contains("parseexception")) return "确认前后端日期/数字格式一致，优先使用明确的格式串。";
            if (n.Contains("indexoutofbounds")) return "检查集合/数组长度和循环边界，确认下标从 0 开始且没有越界。";
            if (n.Contains("filenotfound") || n.Contains("nosuchfile")) return "确认文件路径、挂载目录和运行用户权限。";
            if (n.Contains("ssl") || n.Contains("certificate")) return "检查证书有效期、域名和信任链，确认客户端信任服务端证书。";
            if (n.Contains("ratelimit") || n.Contains("toomanyrequests")) return "降低调用频率、加重试退避，或找服务方提升配额。";
            if (n.Contains("methodargumentnotvalid") || n.Contains("bind")) return "看校验注解和请求参数，确认必填、长度和格式。";
            if (n.Contains("httprequestmethodnotsupported")) return "确认接口的 HTTP 方法和 Content-Type，前后端对齐。";
            if (n.Contains("maxuploadsize")) return "检查上传大小限制和网关/容器配置。";
            if (n.Contains("feign") || n.Contains("restclient") || n.Contains("resourceaccess")) return "看下游服务地址、超时、熔断和下游日志。";
            if (n.Contains("classcast")) return "检查集合泛型、反序列化目标类型和强转前的 instanceof 判断。";
            if (n.Contains("concurrentmodification")) return "遍历时不要直接增删集合，改用迭代器或先复制一份。";
            return "从第一条 ERROR 往下看 Caused by，最底层异常通常是根因；结合报错行上方的入参排查。";
        }

    }

    internal sealed class AiOcrSettingsForm : Form
    {
        private static readonly Color TextColor = Color.FromArgb(48, 49, 51);
        private static readonly Color MutedColor = Color.FromArgb(144, 147, 153);
        private static readonly Color ErrorColor = Color.FromArgb(183, 28, 28);
        private static readonly Color OkColor = Color.FromArgb(46, 125, 50);

        private readonly ComboBox _providerBox;
        private readonly TextBox _urlBox;
        private readonly TextBox _modelBox;
        private readonly TextBox _keyBox;
        private readonly Label _testLabel;
        private readonly Button _testButton;
        private readonly Button _saveButton;
        private bool _initializing;

        public AiOcrSettings Settings { get; private set; }

        public AiOcrSettingsForm(AiOcrSettings current)
        {
            Settings = current ?? new AiOcrSettings();
            Text = "AI 识别设置";
            ClientSize = new Size(620, 470);
            MinimumSize = new Size(620, 470);
            MaximumSize = new Size(620, 470);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            BackColor = Color.White;
            Font = new Font("Microsoft YaHei UI", 9F);
            AutoScaleMode = AutoScaleMode.Dpi;

            TableLayoutPanel layout = new TableLayoutPanel();
            layout.Dock = DockStyle.Fill;
            layout.ColumnCount = 2;
            layout.RowCount = 8;
            layout.Padding = new Padding(18, 14, 18, 14);
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 84F));
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            for (int i = 0; i < 6; i++) layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            Controls.Add(layout);

            _providerBox = new ComboBox();
            _providerBox.DropDownStyle = ComboBoxStyle.DropDownList;
            _providerBox.Dock = DockStyle.Fill;
            _providerBox.Font = new Font("Microsoft YaHei UI", 9F);
            _providerBox.Margin = new Padding(0, 3, 0, 5);
            _providerBox.Items.Add("豆包（火山方舟）");
            _providerBox.Items.Add("通义千问（阿里云）");
            _providerBox.Items.Add("OpenAI");
            _providerBox.Items.Add("自定义（OpenAI 兼容）");
            layout.Controls.Add(MakeFieldLabel("服务商"), 0, 0);
            layout.Controls.Add(_providerBox, 1, 0);

            _urlBox = MakeTextField();
            layout.Controls.Add(MakeFieldLabel("接口地址"), 0, 1);
            layout.Controls.Add(_urlBox, 1, 1);

            _modelBox = MakeTextField();
            layout.Controls.Add(MakeFieldLabel("模型"), 0, 2);
            layout.Controls.Add(_modelBox, 1, 2);

            _keyBox = MakeTextField();
            _keyBox.UseSystemPasswordChar = true;
            layout.Controls.Add(MakeFieldLabel("API Key"), 0, 3);
            layout.Controls.Add(_keyBox, 1, 3);

            Label note = new Label();
            note.Text = "• AI 识别会把截图发送到你配置的服务商，不便外发的截图请切回「本地 OCR（离线）」。\r\n" +
                        "• API Key 只保存在本机，并用 Windows DPAPI 加密：" + AiOcrSettings.ConfigPath + "\r\n" +
                        "• 模型 ID 会随服务商更新，请以控制台为准；也可以填写「接入点 ID」。";
            note.ForeColor = MutedColor;
            note.AutoSize = false;
            note.Height = 76;
            note.Dock = DockStyle.Fill;
            note.Margin = new Padding(0, 6, 0, 4);
            layout.Controls.Add(note, 0, 4);
            layout.SetColumnSpan(note, 2);

            _testLabel = new Label();
            _testLabel.Text = "填写后可以点「测试连接」验证接口、模型和 Key 是否可用。";
            _testLabel.ForeColor = MutedColor;
            _testLabel.AutoSize = false;
            _testLabel.Height = 48;
            _testLabel.Dock = DockStyle.Fill;
            _testLabel.Margin = new Padding(0, 2, 0, 0);
            layout.Controls.Add(_testLabel, 0, 5);
            layout.SetColumnSpan(_testLabel, 2);

            FlowLayoutPanel buttons = new FlowLayoutPanel();
            buttons.Dock = DockStyle.Fill;
            buttons.FlowDirection = FlowDirection.RightToLeft;
            buttons.WrapContents = false;
            buttons.AutoSize = true;
            buttons.Margin = new Padding(0, 8, 0, 0);

            _saveButton = MakeDialogButton("保存", true);
            _saveButton.Click += delegate { SaveSettings(); };
            Button cancelButton = MakeDialogButton("取消", false);
            cancelButton.DialogResult = DialogResult.Cancel;
            _testButton = MakeDialogButton("测试连接", false);
            _testButton.Click += delegate { TestConnection(); };

            buttons.Controls.Add(_saveButton);
            buttons.Controls.Add(cancelButton);
            buttons.Controls.Add(_testButton);
            layout.Controls.Add(buttons, 0, 7);
            layout.SetColumnSpan(buttons, 2);

            AcceptButton = _saveButton;
            CancelButton = cancelButton;

            _initializing = true;
            _providerBox.SelectedIndex = ProviderIndex(Settings.Provider);
            _urlBox.Text = Settings.BaseUrl;
            _modelBox.Text = Settings.Model;
            _keyBox.Text = Settings.ApiKey;
            _initializing = false;
            _providerBox.SelectedIndexChanged += delegate { ApplyProviderDefaults(); };
        }

        private static Label MakeFieldLabel(string text)
        {
            Label label = new Label();
            label.Text = text;
            label.ForeColor = TextColor;
            label.AutoSize = false;
            label.Dock = DockStyle.Fill;
            label.TextAlign = ContentAlignment.MiddleLeft;
            label.Margin = new Padding(0, 4, 8, 4);
            return label;
        }

        private static TextBox MakeTextField()
        {
            TextBox box = new TextBox();
            box.Dock = DockStyle.Fill;
            box.Font = new Font("Microsoft YaHei UI", 9F);
            box.BorderStyle = BorderStyle.FixedSingle;
            box.Margin = new Padding(0, 5, 0, 5);
            return box;
        }

        private static Button MakeDialogButton(string text, bool primary)
        {
            Button button = new Button();
            button.Text = text;
            button.Width = 88;
            button.Height = 32;
            button.FlatStyle = FlatStyle.Flat;
            button.Cursor = Cursors.Hand;
            button.Font = new Font("Microsoft YaHei UI", 9F);
            button.Margin = new Padding(8, 0, 0, 0);
            button.UseVisualStyleBackColor = false;
            button.FlatAppearance.BorderSize = primary ? 0 : 1;
            if (primary)
            {
                button.BackColor = Color.FromArgb(64, 158, 255);
                button.ForeColor = Color.White;
                button.FlatAppearance.MouseOverBackColor = Color.FromArgb(51, 137, 230);
            }
            else
            {
                button.BackColor = Color.White;
                button.ForeColor = TextColor;
                button.FlatAppearance.BorderColor = Color.FromArgb(220, 223, 230);
                button.FlatAppearance.MouseOverBackColor = Color.FromArgb(245, 247, 250);
            }
            return button;
        }

        private static int ProviderIndex(string provider)
        {
            if (provider == AiOcrSettings.ProviderQwen) return 1;
            if (provider == AiOcrSettings.ProviderOpenAi) return 2;
            if (provider == AiOcrSettings.ProviderCustom) return 3;
            return 0;
        }

        private static string ProviderAt(int index)
        {
            if (index == 1) return AiOcrSettings.ProviderQwen;
            if (index == 2) return AiOcrSettings.ProviderOpenAi;
            if (index == 3) return AiOcrSettings.ProviderCustom;
            return AiOcrSettings.ProviderDoubao;
        }

        private void ApplyProviderDefaults()
        {
            if (_initializing) return;
            string provider = ProviderAt(_providerBox.SelectedIndex);
            _urlBox.Text = AiOcrSettings.DefaultBaseUrl(provider);
            _modelBox.Text = AiOcrSettings.DefaultModel(provider);
            SetTest(provider == AiOcrSettings.ProviderCustom
                ? "填写完整的 OpenAI 兼容 /chat/completions 地址。"
                : "已填入常用地址和模型；模型以服务商控制台为准。", MutedColor);
        }

        private AiOcrSettings ReadSettings()
        {
            AiOcrSettings settings = new AiOcrSettings();
            settings.Provider = ProviderAt(_providerBox.SelectedIndex);
            settings.BaseUrl = AiOcrSettings.NormalizeEndpoint(_urlBox.Text);
            settings.Model = _modelBox.Text.Trim();
            settings.ApiKey = _keyBox.Text.Trim();
            settings.TimeoutSeconds = Settings.TimeoutSeconds;
            settings.UseAi = Settings.UseAi;
            return settings;
        }

        private static bool Validate(AiOcrSettings settings, out string error)
        {
            error = "";
            if (string.IsNullOrWhiteSpace(settings.BaseUrl))
            {
                error = "请填写接口地址。";
                return false;
            }
            Uri uri;
            if (!Uri.TryCreate(settings.BaseUrl, UriKind.Absolute, out uri) ||
                (!string.Equals(uri.Scheme, "http", StringComparison.OrdinalIgnoreCase) &&
                 !string.Equals(uri.Scheme, "https", StringComparison.OrdinalIgnoreCase)))
            {
                error = "接口地址必须是完整的 http:// 或 https:// 地址。";
                return false;
            }
            if (string.IsNullOrWhiteSpace(settings.Model))
            {
                error = "请填写模型 ID 或接入点 ID。";
                return false;
            }
            if (settings.Provider != AiOcrSettings.ProviderCustom && string.IsNullOrEmpty(settings.ApiKey))
            {
                error = "请填写 API Key。";
                return false;
            }
            return true;
        }

        private void SaveSettings()
        {
            AiOcrSettings settings = ReadSettings();
            string error;
            if (!Validate(settings, out error))
            {
                SetTest(error, ErrorColor);
                return;
            }
            Settings = settings;
            DialogResult = DialogResult.OK;
        }

        private void TestConnection()
        {
            AiOcrSettings settings = ReadSettings();
            string error;
            if (!Validate(settings, out error))
            {
                SetTest(error, ErrorColor);
                return;
            }

            _testButton.Enabled = false;
            _saveButton.Enabled = false;
            SetTest("正在测试接口，请稍候…", MutedColor);

            ThreadPool.QueueUserWorkItem(delegate
            {
                string text = "";
                string requestError = "";
                bool ok;
                using (Bitmap sample = BuildTestImage())
                {
                    ok = AiOcrRunner.TryRecognize(sample, settings, out text, out requestError);
                }

                try
                {
                    BeginInvoke(new MethodInvoker(delegate
                    {
                        _testButton.Enabled = true;
                        _saveButton.Enabled = true;
                        if (ok)
                        {
                            string preview = text.Replace("\r", " ").Replace("\n", " ");
                            if (preview.Length > 120) preview = preview.Substring(0, 120) + "…";
                            SetTest("连接成功，识别结果：" + preview, OkColor);
                        }
                        else
                        {
                            SetTest("测试失败：" + requestError, ErrorColor);
                        }
                    }));
                }
                catch (ObjectDisposedException)
                {
                    // 窗口已关闭
                }
                catch (InvalidOperationException)
                {
                    // 窗口已关闭
                }
            });
        }

        private static Bitmap BuildTestImage()
        {
            Bitmap bitmap = new Bitmap(560, 150, PixelFormat.Format24bppRgb);
            try
            {
                using (Graphics graphics = Graphics.FromImage(bitmap))
                {
                    graphics.Clear(Color.White);
                    using (Font title = new Font("Segoe UI", 18F, FontStyle.Bold))
                    using (Font body = new Font("Consolas", 14F))
                    {
                        graphics.DrawString("curl2url AI OCR test", title, Brushes.Black, 20, 18);
                        graphics.DrawString("Cannot deserialize: example 123", body, Brushes.Black, 20, 78);
                    }
                }
                return bitmap;
            }
            catch (Exception)
            {
                bitmap.Dispose();
                throw;
            }
        }

        private void SetTest(string text, Color color)
        {
            if (_testLabel == null) return;
            _testLabel.Text = text;
            _testLabel.ForeColor = color;
        }
    }

    internal sealed class OcrPreviewForm : Form
    {
        private readonly Bitmap _image;
        private readonly Panel _scroll;
        private readonly PictureBox _picture;
        private readonly TrackBar _zoomBar;
        private readonly Label _zoomLabel;
        private bool _syncingZoom;
        private bool _panning;
        private bool _fitMode = true;
        private Point _panStart;
        private Point _scrollStart;

        public OcrPreviewForm(Image image, string sourceLabel)
        {
            _image = new Bitmap(image);
            Text = "查看截图" + (string.IsNullOrEmpty(sourceLabel) ? "" : " - " + sourceLabel);
            StartPosition = FormStartPosition.CenterParent;
            Size = new Size(1100, 820);
            MinimumSize = new Size(900, 480);
            BackColor = Color.FromArgb(30, 31, 34);
            KeyPreview = true;
            Font = new Font("Microsoft YaHei UI", 9F);

            // 用 TableLayoutPanel 固定「工具栏在上、图片区域填满其余空间」，
            // 避免 Dock 与 Z 序互相覆盖导致工具栏被图片区域盖住。
            TableLayoutPanel layout = new TableLayoutPanel();
            layout.Dock = DockStyle.Fill;
            layout.ColumnCount = 1;
            layout.RowCount = 2;
            layout.Margin = Padding.Empty;
            layout.Padding = Padding.Empty;
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 44F));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
            Controls.Add(layout);

            Panel toolbar = new Panel();
            toolbar.Dock = DockStyle.Fill;
            toolbar.Margin = Padding.Empty;
            toolbar.BackColor = Color.FromArgb(45, 46, 50);
            layout.Controls.Add(toolbar, 0, 0);

            _scroll = new Panel();
            _scroll.Dock = DockStyle.Fill;
            _scroll.Margin = Padding.Empty;
            _scroll.AutoScroll = true;
            _scroll.BackColor = Color.FromArgb(30, 31, 34);
            layout.Controls.Add(_scroll, 0, 1);

            FlowLayoutPanel tools = new FlowLayoutPanel();
            tools.Dock = DockStyle.Fill;
            tools.FlowDirection = FlowDirection.LeftToRight;
            tools.WrapContents = false;
            tools.Padding = new Padding(8, 6, 8, 6);
            toolbar.Controls.Add(tools);

            Button fitButton = MakeToolButton("适应窗口");
            fitButton.Click += delegate { FitToWindow(); };
            Button actualButton = MakeToolButton("100%");
            actualButton.Click += delegate { SetZoom(1.0, false); };
            Button zoomOutButton = MakeToolButton("缩小");
            zoomOutButton.Click += delegate { SetZoom(CurrentZoom / 1.25, false); };
            Button zoomInButton = MakeToolButton("放大");
            zoomInButton.Click += delegate { SetZoom(CurrentZoom * 1.25, false); };

            _zoomBar = new TrackBar();
            _zoomBar.Minimum = 10;
            _zoomBar.Maximum = 800;
            _zoomBar.TickFrequency = 50;
            _zoomBar.SmallChange = 10;
            _zoomBar.LargeChange = 50;
            _zoomBar.Width = 170;
            _zoomBar.Margin = new Padding(8, 2, 4, 0);
            _zoomBar.ValueChanged += delegate
            {
                if (_syncingZoom) return;
                SetZoom(_zoomBar.Value / 100.0, false);
            };

            _zoomLabel = new Label();
            _zoomLabel.AutoSize = true;
            _zoomLabel.ForeColor = Color.FromArgb(220, 222, 226);
            _zoomLabel.Margin = new Padding(4, 8, 6, 0);
            _zoomLabel.Text = "100%";

            Label hint = new Label();
            hint.AutoSize = true;
            hint.ForeColor = Color.FromArgb(160, 163, 170);
            hint.Margin = new Padding(8, 8, 6, 0);
            hint.Text = "双击图片切换 100% / 适应窗口；Ctrl+滚轮缩放；拖动平移；Esc 关闭";

            tools.Controls.Add(fitButton);
            tools.Controls.Add(actualButton);
            tools.Controls.Add(zoomOutButton);
            tools.Controls.Add(zoomInButton);
            tools.Controls.Add(_zoomBar);
            tools.Controls.Add(_zoomLabel);
            tools.Controls.Add(hint);

            _picture = new PictureBox();
            _picture.Location = new Point(0, 0);
            _picture.SizeMode = PictureBoxSizeMode.Zoom;
            _picture.BackColor = Color.FromArgb(30, 31, 34);
            _picture.Image = _image;
            _picture.Cursor = Cursors.Hand;
            _picture.MouseDown += PictureMouseDown;
            _picture.MouseMove += PictureMouseMove;
            _picture.MouseUp += PictureMouseUp;
            _picture.DoubleClick += delegate { ToggleFit(); };
            _picture.MouseWheel += PictureMouseWheel;
            _scroll.Controls.Add(_picture);

            KeyDown += delegate(object sender, KeyEventArgs e)
            {
                if (e.KeyCode == Keys.Escape)
                {
                    Close();
                    e.Handled = true;
                }
            };
            Shown += delegate { BeginInvoke(new MethodInvoker(FitToWindow)); };
        }

        private static Button MakeToolButton(string text)
        {
            Button button = new Button();
            button.Text = text;
            button.AutoSize = true;
            button.Height = 28;
            button.Padding = new Padding(6, 0, 6, 0);
            button.FlatStyle = FlatStyle.Flat;
            button.FlatAppearance.BorderSize = 1;
            button.FlatAppearance.BorderColor = Color.FromArgb(90, 92, 98);
            button.BackColor = Color.FromArgb(60, 62, 68);
            button.ForeColor = Color.FromArgb(230, 231, 234);
            button.Font = new Font("Microsoft YaHei UI", 9F);
            button.Cursor = Cursors.Hand;
            button.Margin = new Padding(0, 0, 6, 0);
            button.UseVisualStyleBackColor = false;
            return button;
        }

        private double CurrentZoom
        {
            get { return _image.Width > 0 ? (double)_picture.Width / _image.Width : 1.0; }
        }

        private void SetZoom(double zoom, bool fitMode)
        {
            if (zoom < 0.1) zoom = 0.1;
            if (zoom > 8.0) zoom = 8.0;
            int width = Math.Max(1, (int)Math.Round(_image.Width * zoom));
            int height = Math.Max(1, (int)Math.Round(_image.Height * zoom));
            _picture.Size = new Size(width, height);
            _fitMode = fitMode;

            _syncingZoom = true;
            try
            {
                int value = (int)Math.Round(zoom * 100);
                if (value < _zoomBar.Minimum) value = _zoomBar.Minimum;
                if (value > _zoomBar.Maximum) value = _zoomBar.Maximum;
                _zoomBar.Value = value;
            }
            catch (ArgumentOutOfRangeException)
            {
            }
            _syncingZoom = false;
            _zoomLabel.Text = Math.Round(zoom * 100) + "%";
        }

        private void FitToWindow()
        {
            if (_scroll.ClientSize.Width <= 0 || _scroll.ClientSize.Height <= 0) return;
            int availableWidth = Math.Max(1, _scroll.ClientSize.Width - SystemInformation.VerticalScrollBarWidth - 6);
            int availableHeight = Math.Max(1, _scroll.ClientSize.Height - SystemInformation.HorizontalScrollBarHeight - 6);
            double zoom = Math.Min((double)availableWidth / _image.Width, (double)availableHeight / _image.Height);
            if (zoom > 4.0) zoom = 4.0;
            SetZoom(zoom, true);

            int x = Math.Max(0, (_scroll.ClientSize.Width - _picture.Width) / 2);
            int y = Math.Max(0, (_scroll.ClientSize.Height - _picture.Height) / 2);
            _picture.Location = new Point(x, y);
            _scroll.AutoScrollPosition = new Point(0, 0);
        }

        private void ToggleFit()
        {
            if (_fitMode) SetZoom(1.0, false);
            else FitToWindow();
        }

        private void PictureMouseDown(object sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left) return;
            _panning = true;
            _panStart = Cursor.Position;
            _scrollStart = new Point(-_scroll.AutoScrollPosition.X, -_scroll.AutoScrollPosition.Y);
            _picture.Cursor = Cursors.SizeAll;
        }

        private void PictureMouseMove(object sender, MouseEventArgs e)
        {
            if (!_panning) return;
            Point now = Cursor.Position;
            int x = _scrollStart.X - (now.X - _panStart.X);
            int y = _scrollStart.Y - (now.Y - _panStart.Y);
            _scroll.AutoScrollPosition = new Point(x, y);
        }

        private void PictureMouseUp(object sender, MouseEventArgs e)
        {
            if (!_panning) return;
            _panning = false;
            _picture.Cursor = Cursors.Hand;
        }

        private void PictureMouseWheel(object sender, MouseEventArgs e)
        {
            if ((ModifierKeys & Keys.Control) == Keys.Control)
            {
                double factor = e.Delta > 0 ? 1.15 : 1.0 / 1.15;
                SetZoom(CurrentZoom * factor, false);
                return;
            }

            int x = -_scroll.AutoScrollPosition.X;
            int y = -_scroll.AutoScrollPosition.Y;
            int delta = e.Delta > 0 ? -120 : 120;
            _scroll.AutoScrollPosition = new Point(x, Math.Max(0, y + delta));
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            _picture.Image = null;
            base.OnFormClosed(e);
            _image.Dispose();
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

        public static string FormatDetails(
            List<CurlRequest> requests, List<string> urls, bool mask, bool includeResponses = false)
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
                if (includeResponses)
                {
                    if (!requests[i].HasResponse())
                    {
                        sb.AppendLine("  响应: （当前内容没有响应；cURL 不含响应，HAR / 请求列表才会带响应）");
                    }
                    else
                    {
                        sb.AppendLine("  响应头:");
                        AppendHeaderBlock(sb, requests[i].ResponseHeaders, mask, true);
                        sb.AppendLine("  响应体:");
                        AppendBody(sb, requests[i].ResponseBody, mask, "    ");
                    }
                }
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

        public static string LastWarning = "";

        public static List<CurlRequest> Parse(string text)
        {
            string warning;
            return Parse(text, out warning);
        }

        public static List<CurlRequest> Parse(string text, out string warning)
        {
            warning = "";
            LastWarning = "";

            // 有些编辑器保存的 HAR 带 BOM，JavaScriptSerializer 会把它当成非法字符
            if (text.Length > 0 && text[0] == '﻿') text = text.Substring(1);

            JavaScriptSerializer serializer = new JavaScriptSerializer();
            serializer.MaxJsonLength = int.MaxValue;

            object rootObject;
            try
            {
                rootObject = serializer.DeserializeObject(text);
            }
            catch (Exception firstError)
            {
                string repaired;
                if (!TryRepairTruncatedHar(text, out repaired))
                {
                    throw new InvalidOperationException(ShortJsonError(firstError.Message), firstError);
                }

                try
                {
                    rootObject = serializer.DeserializeObject(repaired);
                }
                catch (Exception repairError)
                {
                    throw new InvalidOperationException(ShortJsonError(repairError.Message), repairError);
                }

                warning = "HAR 文件不完整，或某段请求体/响应体含有未转义的换行、引号；"
                    + "已忽略损坏位置之后的内容，只按可用部分解析。";
                LastWarning = warning;
            }

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

        private static string ShortJsonError(string message)
        {
            string text = (message ?? "").Trim();
            int lineBreak = text.IndexOfAny(new char[] { '\r', '\n' });
            if (lineBreak >= 0) text = text.Substring(0, lineBreak).Trim();
            if (text.Length > 300) text = text.Substring(0, 300) + "…";
            return text;
        }

        // HAR 导出工具偶尔会把嵌套 JSON 直接塞进字符串字段，既不转义换行/引号，文件还可能在
        // 这里被截断。严格 JSON 解析必然失败；这里按「第一个非法字符串之前的内容 + 补齐括号」
        // 恢复，至少保留已经完整的请求，而不是让整份 HAR 都打不开。
        private static bool TryRepairTruncatedHar(string text, out string repaired)
        {
            repaired = null;
            if (string.IsNullOrEmpty(text)) return false;

            bool inString = false;
            bool escaped = false;
            int stringStart = -1;
            int badStringStart = -1;

            for (int i = 0; i < text.Length; i++)
            {
                char ch = text[i];
                if (inString)
                {
                    if (escaped)
                    {
                        escaped = false;
                        continue;
                    }
                    if (ch == '\\')
                    {
                        escaped = true;
                        continue;
                    }
                    if (ch == '"')
                    {
                        inString = false;
                        continue;
                    }
                    if (ch == '\r' || ch == '\n' || ch < ' ')
                    {
                        badStringStart = stringStart;
                        break;
                    }
                    continue;
                }

                if (ch == '"')
                {
                    inString = true;
                    stringStart = i;
                }
            }

            if (badStringStart >= 0)
            {
                int cut = FindJsonValueKeyStart(text, badStringStart);
                string prefix = text.Substring(0, cut).TrimEnd();
                if (prefix.EndsWith(",", StringComparison.Ordinal))
                {
                    prefix = prefix.Substring(0, prefix.Length - 1).TrimEnd();
                }

                List<char> open = OpenJsonContainers(prefix);
                if (open.Count == 0) return false;
                repaired = prefix + JsonClosers(open);
                return true;
            }

            // 没有非法字符串，可能只是文件在末尾被截断：补上未闭合的 } / ]。
            List<char> remaining = OpenJsonContainers(text);
            if (remaining.Count == 0 || inString) return false;

            string trimmed = text.TrimEnd();
            if (trimmed.EndsWith(",", StringComparison.Ordinal))
            {
                trimmed = trimmed.Substring(0, trimmed.Length - 1).TrimEnd();
            }
            repaired = trimmed + JsonClosers(remaining);
            return true;
        }

        private static int FindJsonValueKeyStart(string text, int valueStart)
        {
            int p = valueStart - 1;
            while (p >= 0 && char.IsWhiteSpace(text[p])) p--;
            if (p < 0 || text[p] != ':') return valueStart; // 数组元素，直接从这里截

            p--;
            while (p >= 0 && char.IsWhiteSpace(text[p])) p--;
            if (p < 0 || text[p] != '"') return valueStart;

            int keyEnd = p;
            p--;
            while (p >= 0)
            {
                if (text[p] == '"')
                {
                    int slashes = 0;
                    int q = p - 1;
                    while (q >= 0 && text[q] == '\\')
                    {
                        slashes++;
                        q--;
                    }
                    if (slashes % 2 == 0) return p;
                }
                p--;
            }
            return keyEnd;
        }

        private static List<char> OpenJsonContainers(string text)
        {
            List<char> stack = new List<char>();
            bool inString = false;
            bool escaped = false;

            for (int i = 0; i < text.Length; i++)
            {
                char ch = text[i];
                if (inString)
                {
                    if (escaped)
                    {
                        escaped = false;
                        continue;
                    }
                    if (ch == '\\')
                    {
                        escaped = true;
                        continue;
                    }
                    if (ch == '"') inString = false;
                    continue;
                }

                if (ch == '"')
                {
                    inString = true;
                    continue;
                }
                if (ch == '{' || ch == '[')
                {
                    stack.Add(ch);
                }
                else if ((ch == '}' || ch == ']') && stack.Count > 0)
                {
                    stack.RemoveAt(stack.Count - 1);
                }
            }
            return stack;
        }

        private static string JsonClosers(List<char> open)
        {
            StringBuilder b = new StringBuilder();
            for (int i = open.Count - 1; i >= 0; i--)
            {
                b.Append(open[i] == '[' ? ']' : '}');
            }
            return b.ToString();
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
