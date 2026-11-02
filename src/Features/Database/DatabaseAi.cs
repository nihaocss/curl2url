using System;
using System.Collections.Generic;
using System.Data;
using System.Drawing;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Forms;

namespace Curl2Url
{
    // 把 SQL / 表结构 / 结果预览（敏感列已打码）交给现有 AI 配置做排查分析。
    // 只在用户显式点击时调用；发送前再确认一次。
    internal static class DatabaseAi
    {
        private const int PreviewRows = 15;
        private const int PreviewColumns = 25;
        private const int PreviewCellChars = 160;
        private static readonly object RunningGate = new object();
        private static bool _running;

        private static readonly Regex SensitiveLiteral = new Regex(
            @"(?i)\b(token|access_token|refresh_token|password|passwd|secret|session|sessionid|authorization|cookie|sign)\b" +
            @"(\s*(?:=|:|LIKE)\s*)'([^']{1,200})'",
            RegexOptions.Compiled);

        private static readonly Regex LongLiteral = new Regex(
            @"'([^']{65,2000})'", RegexOptions.Compiled);

        private const string SystemPrompt =
            "你是资深的后端 / 数据库排查助手，熟悉 MySQL（含 5.7/8.0）、Redis 和常见线上故障。\n" +
            "用户会给你连接环境、SQL 或 Redis 命令、错误信息、表结构、结果预览和日志线索。\n" +
            "要求：\n" +
            "1. 先用 1-3 句话给出最可能的结论，再列证据；不要泛泛而谈。\n" +
            "2. 证据必须引用具体的 SQL、错误码、字段名、索引、行数或 Redis 类型/TTL。\n" +
            "3. 给出可执行的下一步排查；SQL 默认只写只读语句，涉及写操作必须单独标注风险，并提醒先备份/确认。\n" +
            "4. 不确定就明确说不确定，并列出还需要什么信息；不要编造表名、字段或执行结果。\n" +
            "5. 使用中文，结构清晰，可用简短标题和列表。";

        public static void AnalyzeMySql(DatabaseTabHost host, DatabaseConnectionProfile profile,
            string sql, DataTable result, string error, DbTableRef table, string schema,
            List<LogClue> clues)
        {
            if (host == null) return;
            if (!TryBegin()) return;

            try
            {
                AiOcrSettings settings = host.LoadAiSettings == null ? null : host.LoadAiSettings();
                if (settings == null || !settings.IsConfigured)
                {
                    _running = false;
                    DialogResult answer = MessageBox.Show(host.Owner,
                        "还没有配置 AI。是否现在打开 AI 设置？", "AI 分析",
                        MessageBoxButtons.YesNo, MessageBoxIcon.Information);
                    if (answer == DialogResult.Yes && host.ShowAiSettings != null) host.ShowAiSettings();
                    return;
                }

                string preview = BuildTablePreview(result);
                string userPrompt = BuildMySqlPrompt(profile, sql, error, table, schema, clues, preview);
                DialogResult confirm = MessageBox.Show(host.Owner,
                    "将把以下内容发送给 AI：\r\n" +
                    "· 连接环境（不含密码）\r\n" +
                    "· SQL / 错误信息\r\n" +
                    "· 表结构\r\n" +
                    "· 结果前 " + PreviewRows + " 行（敏感列已打码）\r\n\r\n是否继续？",
                    "AI 分析", MessageBoxButtons.OKCancel, MessageBoxIcon.Information);
                if (confirm != DialogResult.OK)
                {
                    _running = false;
                    return;
                }

                Run(host, settings, userPrompt);
            }
            catch (Exception ex)
            {
                _running = false;
                ReportStartFailure(host, ex);
            }
        }

        public static void AnalyzeRedis(DatabaseTabHost host, DatabaseConnectionProfile profile,
            string command, RedisValueData value, string error, List<LogClue> clues)
        {
            if (host == null) return;
            if (!TryBegin()) return;
            try
            {
                AiOcrSettings settings = host.LoadAiSettings == null ? null : host.LoadAiSettings();
                if (settings == null || !settings.IsConfigured)
                {
                    _running = false;
                    DialogResult answer = MessageBox.Show(host.Owner,
                        "还没有配置 AI。是否现在打开 AI 设置？", "AI 分析",
                        MessageBoxButtons.YesNo, MessageBoxIcon.Information);
                    if (answer == DialogResult.Yes && host.ShowAiSettings != null) host.ShowAiSettings();
                    return;
                }

                string userPrompt = BuildRedisPrompt(profile, command, value, error, clues);
                DialogResult confirm = MessageBox.Show(host.Owner,
                    "将把 Redis 命令、Key 类型/TTL 和值预览（敏感 Key 已打码）发送给 AI。\r\n\r\n是否继续？",
                    "AI 分析", MessageBoxButtons.OKCancel, MessageBoxIcon.Information);
                if (confirm != DialogResult.OK)
                {
                    _running = false;
                    return;
                }
                Run(host, settings, userPrompt);
            }
            catch (Exception ex)
            {
                _running = false;
                ReportStartFailure(host, ex);
            }
        }

        private static void ReportStartFailure(DatabaseTabHost host, Exception ex)
        {
            string message = ex == null ? "未知错误" : ex.Message;
            if (host != null)
            {
                host.Status("AI 分析启动失败：" + message, AppTheme.ErrorColor);
                MessageBox.Show(host.Owner, message, "AI 分析失败",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private static bool TryBegin()
        {
            lock (RunningGate)
            {
                if (_running) return false;
                _running = true;
                return true;
            }
        }

        private static void Run(DatabaseTabHost host, AiOcrSettings settings, string userPrompt)
        {
            Control owner = host.Owner as Control;
            if (owner == null)
            {
                _running = false;
                return;
            }
            host.Status("AI 分析中…", AppTheme.PrimaryColor);
            string text = "";
            string error = "";
            AsyncUi.Run(owner, delegate
            {
                AiOcrRunner.TryChat(settings, SystemPrompt, userPrompt, out text, out error);
            },
            delegate(Exception ex)
            {
                _running = false;
                if (ex != null && error.Length == 0) error = ex.Message;
                if (error.Length > 0)
                {
                    host.Status("AI 分析失败：" + error, AppTheme.ErrorColor);
                    MessageBox.Show(host.Owner, error, "AI 分析失败",
                        MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return;
                }
                host.Status("AI 分析完成。", AppTheme.OkColor);
                using (DatabaseAiResultForm dialog = new DatabaseAiResultForm(text))
                {
                    dialog.ShowDialog(host.Owner);
                }
            });
        }

        private static string BuildMySqlPrompt(DatabaseConnectionProfile profile, string sql,
            string error, DbTableRef table, string schema, List<LogClue> clues, string preview)
        {
            StringBuilder sb = new StringBuilder();
            sb.Append("【数据库】MySQL\r\n");
            if (profile != null)
            {
                sb.Append("【连接】").Append(profile.Name).Append(" · ").Append(profile.Environment)
                  .Append(" · ").Append(profile.Endpoint).Append("\r\n");
                if (!string.IsNullOrEmpty(profile.Database))
                    sb.Append("【默认库】").Append(profile.Database).Append("\r\n");
            }
            if (table != null)
                sb.Append("【目标表】").Append(table.FullName).Append(table.IsView ? "（视图）" : "").Append("\r\n");
            if (!string.IsNullOrEmpty(sql))
                sb.Append("\r\n【SQL】\r\n").Append(Limit(MaskSql(sql), 8000)).Append("\r\n");
            if (!string.IsNullOrEmpty(error))
                sb.Append("\r\n【错误】\r\n").Append(Limit(error, 4000)).Append("\r\n");
            if (!string.IsNullOrEmpty(schema))
                sb.Append("\r\n【表结构】\r\n").Append(Limit(schema, 12000)).Append("\r\n");
            AppendClues(sb, clues);
            sb.Append("\r\n【结果预览】\r\n").Append(preview).Append("\r\n");
            sb.Append("\r\n请判断问题最可能出在哪里，并给出下一步只读排查 SQL。");
            return sb.ToString();
        }

        private static string BuildRedisPrompt(DatabaseConnectionProfile profile, string command,
            RedisValueData value, string error, List<LogClue> clues)
        {
            StringBuilder sb = new StringBuilder();
            sb.Append("【数据库】Redis\r\n");
            if (profile != null)
            {
                sb.Append("【连接】").Append(profile.Name).Append(" · ").Append(profile.Environment)
                  .Append(" · ").Append(profile.Endpoint).Append("\r\n");
                sb.Append("【DB】").Append(profile.RedisDatabase).Append("\r\n");
            }
            if (!string.IsNullOrEmpty(command))
                sb.Append("\r\n【命令】\r\n").Append(Limit(MaskSql(command), 4000)).Append("\r\n");
            if (!string.IsNullOrEmpty(error))
                sb.Append("\r\n【错误】\r\n").Append(Limit(error, 4000)).Append("\r\n");
            if (value != null)
            {
                sb.Append("\r\n【Key】").Append(MaskKeyText(value.Key)).Append("\r\n");
                sb.Append("【类型】").Append(value.Type).Append("  【TTL】")
                  .Append(value.Ttl < 0 ? "无过期" : value.Ttl + " 秒").Append("\r\n");
                sb.Append("【预览】\r\n").Append(BuildRedisPreview(value)).Append("\r\n");
            }
            AppendClues(sb, clues);
            sb.Append("\r\n请判断最可能的问题，并给出只读的排查命令（SCAN/TYPE/TTL/GET/HGETALL 等）。");
            return sb.ToString();
        }

        private static void AppendClues(StringBuilder sb, List<LogClue> clues)
        {
            if (clues == null || clues.Count == 0) return;
            sb.Append("\r\n【日志线索】\r\n");
            for (int i = 0; i < clues.Count && i < 6; i++)
            {
                LogClue clue = clues[i];
                if (clue == null) continue;
                string value = Sensitive.MaskValue(clue.Label, clue.Value);
                sb.Append("- ").Append(clue.Label).Append("：").Append(Limit(value, 800)).Append("\r\n");
            }
        }

        private static string BuildTablePreview(DataTable table)
        {
            if (table == null) return "（没有结果集）";
            if (table.Rows.Count == 0) return "（结果集 0 行）";
            StringBuilder sb = new StringBuilder();
            int columns = Math.Min(table.Columns.Count, PreviewColumns);
            sb.Append("共 ").Append(table.Rows.Count).Append(" 行；仅展示前 ")
              .Append(Math.Min(table.Rows.Count, PreviewRows)).Append(" 行");
            if (table.Columns.Count > columns) sb.Append("、前 ").Append(columns).Append(" 列");
            sb.Append("。\r\n");
            for (int c = 0; c < columns; c++)
            {
                if (c > 0) sb.Append('\t');
                sb.Append(table.Columns[c].ColumnName);
            }
            sb.Append("\r\n");
            int rows = Math.Min(table.Rows.Count, PreviewRows);
            for (int r = 0; r < rows; r++)
            {
                for (int c = 0; c < columns; c++)
                {
                    if (c > 0) sb.Append('\t');
                    string column = table.Columns[c].ColumnName;
                    string text = DataGridExport.CellText(table.Rows[r][c]);
                    if (Sensitive.IsSensitiveKey(column)) text = Sensitive.MaskValue(column, text);
                    sb.Append(Limit(OneLine(text), PreviewCellChars));
                }
                sb.Append("\r\n");
            }
            return sb.ToString();
        }

        private static string BuildRedisPreview(RedisValueData value)
        {
            StringBuilder sb = new StringBuilder();
            if (!string.IsNullOrEmpty(value.Scalar))
            {
                string scalar = value.Scalar;
                if (scalar.Length > 4000) scalar = scalar.Substring(0, 4000) + "…";
                if (Sensitive.IsSensitiveKey(value.Key)) scalar = Sensitive.MaskValue(value.Key, scalar);
                sb.Append(Limit(OneLine(scalar), 4000)).Append("\r\n");
            }
            int count = Math.Min(value.Entries.Count, 30);
            for (int i = 0; i < count; i++)
            {
                RedisEntry entry = value.Entries[i];
                string line;
                if (!string.IsNullOrEmpty(entry.Field))
                    line = entry.Score.Length > 0
                        ? entry.Score + "  " + entry.Field + " => " + entry.Value
                        : entry.Field + " => " + entry.Value;
                else
                    line = entry.Index + "  " + entry.Value;
                if (Sensitive.IsSensitiveKey(entry.Field) || Sensitive.IsSensitiveKey(value.Key))
                    line = Sensitive.MaskValue(entry.Field.Length > 0 ? entry.Field : value.Key, line);
                sb.Append(Limit(OneLine(line), 500)).Append("\r\n");
            }
            if (value.Entries.Count > count) sb.Append("…（还有 ").Append(value.Entries.Count - count).Append(" 项）\r\n");
            if (value.Truncated) sb.Append("（值已按长度截断）\r\n");
            return sb.ToString();
        }

        private static string MaskKeyText(string key)
        {
            if (string.IsNullOrEmpty(key)) return "";
            return Sensitive.IsSensitiveKey(key) ? Sensitive.MaskValue(key, key) : key;
        }

        private static string MaskSql(string sql)
        {
            if (string.IsNullOrEmpty(sql)) return "";
            string masked = SensitiveLiteral.Replace(sql, delegate(Match match)
            {
                return match.Groups[1].Value + match.Groups[2].Value + "'***(" +
                    match.Groups[3].Value.Length + ")'";
            });
            masked = LongLiteral.Replace(masked, delegate(Match match)
            {
                return "'***(" + match.Groups[1].Value.Length + ")'";
            });
            return masked;
        }

        private static string OneLine(string text)
        {
            return (text ?? "").Replace("\r", " ").Replace("\n", " ").Replace("\t", " ");
        }

        private static string Limit(string text, int max)
        {
            if (string.IsNullOrEmpty(text)) return "";
            if (text.Length <= max) return text;
            return text.Substring(0, max) + "…";
        }
    }

    internal sealed class DatabaseAiResultForm : Form
    {
        private readonly TextBox _text;

        public DatabaseAiResultForm(string text)
        {
            Text = "AI 分析结果";
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(820, 620);
            MinimumSize = new Size(560, 380);
            BackColor = Color.White;
            Font = AppTheme.UiFont(9F);

            TableLayoutPanel root = new TableLayoutPanel();
            root.Dock = DockStyle.Fill;
            root.ColumnCount = 1;
            root.RowCount = 2;
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
            root.Padding = new Padding(12);
            Controls.Add(root);

            _text = new TextBox();
            _text.Multiline = true;
            _text.ReadOnly = true;
            _text.Dock = DockStyle.Fill;
            _text.ScrollBars = ScrollBars.Both;
            _text.WordWrap = true;
            _text.Font = AppTheme.UiFont(9.5F);
            _text.BackColor = Color.FromArgb(250, 250, 251);
            _text.Text = text ?? "";
            root.Controls.Add(_text, 0, 0);

            FlowLayoutPanel buttons = new FlowLayoutPanel();
            buttons.Dock = DockStyle.Fill;
            buttons.FlowDirection = FlowDirection.RightToLeft;
            buttons.WrapContents = false;
            buttons.Padding = new Padding(0, 8, 0, 0);
            Button close = AppTheme.MakeButton("关闭", true, 80);
            close.DialogResult = DialogResult.OK;
            Button copy = AppTheme.MakeButton("复制结果", false, 92);
            copy.Click += delegate
            {
                try
                {
                    Clipboard.SetText(_text.Text);
                    copy.Text = "已复制";
                }
                catch (Exception ex)
                {
                    MessageBox.Show(this, "复制失败：" + ex.Message, "复制",
                        MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            };
            buttons.Controls.Add(close);
            buttons.Controls.Add(copy);
            root.Controls.Add(buttons, 0, 1);

            AcceptButton = close;
            CancelButton = close;
        }
    }
}
