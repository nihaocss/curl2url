using System;
using System.Collections.Generic;
using System.Data;
using System.Text;
using System.Text.RegularExpressions;

namespace Curl2Url
{
    // AI 查询：用户用中文描述需求，模型按当前库的表结构生成一条只读 SQL。
    // 模型输出不一定可信，所以这里只负责“取出 SQL”，执行前仍要过 SqlGuard 的只读闸门。
    internal static class DatabaseAiQuery
    {
        public const string SystemPrompt =
            "你是 MySQL 查询助手。根据用户的中文需求和提供的表结构，生成一条可直接执行的只读 SQL。\n" +
            "规则：\n" +
            "1. 只输出 SQL 本体：不要 markdown 代码块、不要编号、不要解释。\n" +
            "2. 只能是 SELECT / SHOW / DESCRIBE / EXPLAIN / WITH(SELECT)；禁止 INSERT、UPDATE、DELETE、REPLACE、" +
            "任何 DDL、存储过程、INTO OUTFILE、FOR UPDATE。\n" +
            "3. 只能使用提供的表名和字段名；表名、字段名用反引号包裹；不要臆造不存在的表或字段。\n" +
            "4. 单引号字符串只能作为条件里的值（如 WHERE `mobile`='13800000000'），" +
            "绝不能把字符串常量当列名或别名。别名一律用反引号，例如 `card_id` AS `卡券号`、COUNT(*) AS `数量`，" +
            "不要写成 'card_id' AS '卡券号'。\n" +
            "5. 先根据字段名和字段注释推断时间、状态、金额等字段的含义；优先使用注释匹配用户描述的字段。" +
            "如果用户问的是某类业务对象（例如卡券），以表名和注释最匹配的表为准，不要凭空添加不存在的分类列。\n" +
            "5.1 表名后面会标出大致行数（≈N 行）。用户没有明确指定某张表时，优先用数据量大的业务主表，" +
            "不要用日志表、备份表、中间表或空表代替主表；用户明确要查日志 / 记录时除外。\n" +
            "5.2 如果多张表是同一种业务的不同子类型（例如表注释都写着“券生产管理表，多用于电影券 / 储值券”），" +
            "用户只说“卡券 / 卡卷 / 券”而没有限定类型时，必须覆盖所有券主表：清单用 UNION ALL，数量分别统计后相加，不能只算电影券。" +
            "典型例子：问“有多少张卡券”，应写成 " +
            "SELECT (SELECT COUNT(*) FROM `t_card` WHERE …) + (SELECT COUNT(*) FROM `t_deposit` WHERE …)。\n" +
            "5.3 用户说“新增卡券 / 新增券”时，按券主表的创建时间统计，不要用派发日志表；" +
            "只有明确提到“派发 / 发放 / 餐补”时才用日志表。\n" +
            "5.4 用户说“记录 / 日志 / 明细”时，优先用名称或注释里带这些词的表，" +
            "不要用名称相近但含义不同的表（例如“拦截”不是“锁定”）。\n" +
            "5.5 查询某个手机号的订单时，必须包含主订单表（注释为“订单表”、数据量最大的那张，通常是 `t_order_good`），" +
            "其他 API / 渠道 / 补差表只能作为补充；统计订单数量和金额时用订单主表，" +
            "订单的电影信息表、明细表、取票码表不能代替订单主表。" +
            "用户没有限定订单类型（商品 / 电影 / 点餐等）时，一律以主订单表为准。\n" +
            "5.6 按影城 / 影院统计时，用订单表里的影城字段（如 cinema_name / cinema_no）并按券支付金额过滤；" +
            "不要拿券主表的“使用场景”字段（use_place）代替影城。\n" +
            "5.7 必查清单：凡是问卡券 / 卡卷 / 券的，不论问数量、清单、状态、过期、绑定还是按天统计，" +
            "都要同时覆盖“电影券”主表和“储值券”主表，并在 SQL 里真实出现这两张表；只查一张表算答错。" +
            "如果一张表也没有明确对应，先按表注释里带“电影券 / 储值券 / 券”的主表来。" +
            "“只有某个号有 0 张券”这种情况不能成为只查一张表的理由，判断一律用查询结果说话。\n" +
            "5.8 用户问到具体号码（手机号、订单号、快递单号、卡号）的明细时，必须把号码放进 WHERE 条件里查；" +
            "用户说“这个订单”却没有给出订单号时，输出一行 -- 开头的反问，要用户补充订单号，不要返回没有号码条件的全表记录。" +
            "字段注释写“快递信息”不等于快递单号，不能直接当“物流单号”用；能用条件定位时，" +
            "用 `exp_name` 等明确字段，必要时再用 LIKE 在 `express_msg` 里匹配单号。" +
            "拒绝时写清缺什么，例如“-- 缺少订单号，无法查询物流单号，请提供订单号”。\n" +
            "6. 用户给的手机号、单号等按原样使用，不要补位、改位或替换成别的号码；位数看起来不对也照原样查询。\n" +
            "7. 默认加 LIMIT 100，除非用户明确要求更多，或查询是聚合统计（COUNT/SUM/AVG 等）。\n" +
            "8. 聚合结果使用有意义的中文别名。\n" +
            "9. 不要以分号结尾，一次只输出一条语句。\n" +
            "10. 如果现有表结构无法完成，只输出一行以 -- 开头的简短原因，不要编造。" +
            "注意：表结构可能因篇幅只列出相关度最高的一部分，看不到某张表不代表它不存在；" +
            "先仔细检查已列出的表名、字段和注释，再决定是否拒绝。";

        // 写语句也一并识别出来：这样界面能明确提示“不是只读查询”，而不是假装没看懂。
        private static readonly Regex SqlStartPattern = new Regex(
            @"\b(SELECT|SHOW|DESCRIBE|DESC|EXPLAIN|WITH|INSERT|UPDATE|DELETE|REPLACE|CREATE|ALTER|DROP|TRUNCATE|RENAME|CALL|SET|GRANT|REVOKE|LOCK|UNLOCK)\b",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        // 模型偶尔会把列名写成字符串常量（'card_id' AS '卡券号'），SQL 能跑但结果全是字面量。
        // 这里做一次输出检查，命中就让模型重写，而不是把错误查询直接执行。
        private static readonly Regex QuotedAliasPattern = new Regex(
            @"\bAS\s*'", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex QuotedTablePattern = new Regex(
            @"\b(FROM|JOIN)\s+'", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex LiteralIdentifierPattern = new Regex(
            @"\bSELECT\s+'[A-Za-z_][A-Za-z0-9_]*'\s*(,|\bFROM\b|$)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        public static bool ValidateGenerated(string sql, out string problem)
        {
            problem = "";
            if (string.IsNullOrEmpty(sql)) return false;
            if (QuotedAliasPattern.IsMatch(sql))
            {
                problem = "别名不能用单引号，必须用反引号，例如 `card_id` AS `卡券号`";
                return false;
            }
            if (QuotedTablePattern.IsMatch(sql))
            {
                problem = "表名不能用单引号，必须用反引号或直接写表名";
                return false;
            }
            if (LiteralIdentifierPattern.IsMatch(sql))
            {
                problem = "看起来把列名写成了字符串常量；列名必须直接写字段名";
                return false;
            }
            return true;
        }

        private static readonly string[] Starters =
        {
            "SELECT", "SHOW", "DESCRIBE", "DESC", "EXPLAIN", "WITH",
            "INSERT", "UPDATE", "DELETE", "REPLACE", "CREATE", "ALTER", "DROP", "TRUNCATE",
            "RENAME", "CALL", "SET", "GRANT", "REVOKE", "LOCK", "UNLOCK"
        };

        // 从模型输出里取出可执行的 SQL；没有识别到语句时返回空字符串，note 里放原因/说明。
        public static string ExtractSql(string answer, out string note)
        {
            note = "";
            if (string.IsNullOrEmpty(answer)) return "";

            string text = answer.Trim();
            // 常见写法：```sql ... ```，先剥掉围栏
            if (text.StartsWith("```", StringComparison.Ordinal))
            {
                int firstBreak = text.IndexOf('\n');
                if (firstBreak >= 0)
                {
                    int close = text.LastIndexOf("```", StringComparison.Ordinal);
                    if (close > firstBreak)
                    {
                        text = text.Substring(firstBreak + 1, close - firstBreak - 1).Trim();
                    }
                }
            }
            text = Regex.Replace(text, "```[a-zA-Z]*", "").Trim();

            string[] lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            int start = -1;
            for (int i = 0; i < lines.Length; i++)
            {
                if (StartsWithKeyword(lines[i].TrimStart())) { start = i; break; }
            }

            string candidate;
            if (start >= 0)
            {
                candidate = string.Join("\n", lines, start, lines.Length - start).Trim();
            }
            else
            {
                // "SQL: SELECT ..." 这类一行里带前缀的写法
                Match match = SqlStartPattern.Match(text);
                if (!match.Success)
                {
                    string firstLine = lines.Length == 0 ? "" : lines[0].Trim();
                    note = firstLine.Length > 300 ? firstLine.Substring(0, 300) + "…" : firstLine;
                    return "";
                }
                candidate = text.Substring(match.Index).Trim();
            }

            string statement;
            string error;
            if (!SqlGuard.Normalize(candidate, out statement, out error))
            {
                note = error;
                return "";
            }
            return statement;
        }

        // 从中文问题里抽出用于匹配表名 / 表注释 / 字段注释的关键词。
        // 只取 2~3 字的中文词组并过滤虚词（“这个/多少/帮我”），另外给“卡卷→卡券”
        // 这类常见错字补一个变体，否则注释里的“卡券”永远匹配不上。
        public static List<string> ExtractKeywords(string question)
        {
            List<string> keywords = new List<string>();
            if (string.IsNullOrEmpty(question)) return keywords;

            List<string> runs = new List<string>();
            StringBuilder run = new StringBuilder();
            for (int i = 0; i < question.Length; i++)
            {
                char ch = question[i];
                bool isCjk = ch >= 0x4E00 && ch <= 0x9FFF;
                if (isCjk) run.Append(ch);
                else if (run.Length > 0)
                {
                    runs.Add(run.ToString());
                    run.Length = 0;
                }
            }
            if (run.Length > 0) runs.Add(run.ToString());

            // 先 3 字词再 2 字词，长词更能区分表
            for (int pass = 0; pass < 2; pass++)
            {
                int len = pass == 0 ? 3 : 2;
                for (int r = 0; r < runs.Count; r++)
                {
                    string text = runs[r];
                    for (int start = 0; start + len <= text.Length; start++)
                    {
                        string word = text.Substring(start, len);
                        if (HasStopChar(word)) continue;
                        if (keywords.Contains(word)) continue;
                        keywords.Add(word);
                    }
                }
            }

            MatchCollection asciiWords = Regex.Matches(question, @"[A-Za-z_][A-Za-z0-9_]{2,}");
            foreach (Match match in asciiWords)
            {
                string word = match.Value.ToLowerInvariant();
                if (!keywords.Contains(word)) keywords.Add(word);
            }

            for (int i = 0; i < keywords.Count; i++)
            {
                if (keywords[i].IndexOf('卷') < 0) continue;
                string fixedWord = keywords[i].Replace('卷', '券');
                if (!keywords.Contains(fixedWord)) keywords.Add(fixedWord);
            }
            // 业务口语同义词：用户说“锁卡”，表注释里写的是“锁定”
            if (question.IndexOf("锁卡", StringComparison.Ordinal) >= 0 &&
                !keywords.Contains("锁定"))
            {
                keywords.Insert(0, "锁定");
            }

            if (keywords.Count > 16) keywords.RemoveRange(16, keywords.Count - 16);
            return keywords;
        }

        // 统计关键词在文本里出现的次数并按压词长加权；文本和关键词都按小写比较。
        public static int MatchScore(string text, List<string> keywords)
        {
            if (string.IsNullOrEmpty(text) || keywords == null || keywords.Count == 0) return 0;
            text = text.ToLowerInvariant();
            int score = 0;
            for (int i = 0; i < keywords.Count; i++)
            {
                string keyword = keywords[i];
                if (keyword.Length < 2) continue;
                string needle = keyword.ToLowerInvariant();
                int index = 0;
                int hits = 0;
                while (hits < 3)
                {
                    index = text.IndexOf(needle, index, StringComparison.Ordinal);
                    if (index < 0) break;
                    hits++;
                    index += needle.Length;
                }
                score += hits * keyword.Length;
            }
            return score;
        }

        private static bool HasStopChar(string word)
        {
            const string stopChars = "的了是吗呢吧啊我你您他她它这那有没多少张个请帮查下想看要在是和与或把被给对从到里中上间就都还也只很太";
            for (int i = 0; i < word.Length; i++)
            {
                if (stopChars.IndexOf(word[i]) >= 0) return true;
            }
            return false;
        }

        public static string BuildUserPrompt(DatabaseConnectionProfile profile, string database,
            string serverVersion, string schema, string question)
        {
            StringBuilder sb = new StringBuilder();
            if (profile != null)
            {
                sb.Append("【环境】").Append(profile.Name).Append(" · ").Append(profile.Environment).Append("\r\n");
            }
            sb.Append("【当前库】").Append(database);
            if (!string.IsNullOrEmpty(serverVersion)) sb.Append("（服务端 ").Append(serverVersion).Append("）");
            sb.Append("\r\n");
            sb.Append("【表结构】\r\n").Append(schema).Append("\r\n");
            sb.Append("【用户问题】\r\n").Append(question).Append("\r\n\r\n");
            sb.Append("请只输出一条只读 SQL。");
            return sb.ToString();
        }

        // 拼给模型的库表结构：一次 information_schema 查询拿全部列和注释，
        // 按“问题里提到的表 / 当前选中的表”优先排序，并限制总长度。
        // 放在这里而不是窗口类里，命令行 AI 查询测试和界面走的是同一套逻辑。
        public static string BuildSchema(MySqlSession session, string database, string question,
            DbTableRef selected, out string error)
        {
            error = "";
            List<DbTableRef> tables = session.GetTables(database, out error);
            if (error.Length > 0) return "";

            Dictionary<string, object> parameters = new Dictionary<string, object>();
            parameters["@schema"] = database;
            DbQueryResult result = session.Query(
                "SELECT c.TABLE_NAME, c.COLUMN_NAME, c.COLUMN_TYPE, c.COLUMN_KEY, " +
                "c.COLUMN_COMMENT, t.TABLE_COMMENT, t.TABLE_ROWS " +
                "FROM information_schema.COLUMNS c " +
                "JOIN information_schema.TABLES t " +
                "  ON t.TABLE_SCHEMA=c.TABLE_SCHEMA AND t.TABLE_NAME=c.TABLE_NAME " +
                "WHERE c.TABLE_SCHEMA=@schema " +
                "ORDER BY c.TABLE_NAME, c.ORDINAL_POSITION",
                parameters, 20000, 30, out error);
            if (error.Length > 0) return "";

            Dictionary<string, List<string>> tableLines =
                new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, string> tableComments =
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, long> tableRows =
                new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            // 表名 + 表注释 + 所有字段名/注释的小写拼接，用于按问题关键词打分
            Dictionary<string, string> tableTexts =
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            DataTable rows = result.Table;
            for (int i = 0; i < rows.Rows.Count; i++)
            {
                DataRow row = rows.Rows[i];
                string tableName = Convert.ToString(row[0]);
                if (tableName.Length == 0) continue;
                string columnName = Convert.ToString(row[1]);
                string columnType = Convert.ToString(row[2]);
                string key = Convert.ToString(row[3]);
                string columnComment = SafeComment(Convert.ToString(row[4]));
                string tableComment = SafeComment(Convert.ToString(row[5]));

                if (!tableComments.ContainsKey(tableName))
                {
                    tableComments[tableName] = tableComment;
                    long rowsEstimate = 0;
                    long.TryParse(Convert.ToString(row[6]), out rowsEstimate);
                    tableRows[tableName] = rowsEstimate;
                }

                string textEntry = tableName + " " + tableComment + " " + columnName + " " + columnComment;
                string textSoFar;
                tableTexts[tableName] = tableTexts.TryGetValue(tableName, out textSoFar)
                    ? textSoFar + " " + textEntry
                    : textEntry;

                List<string> lines;
                if (!tableLines.TryGetValue(tableName, out lines))
                {
                    lines = new List<string>();
                    tableLines[tableName] = lines;
                }
                if (lines.Count >= 80)
                {
                    if (lines.Count == 80) lines.Add("  …（列太多，已省略其余列）");
                    continue;
                }

                StringBuilder line = new StringBuilder();
                line.Append("  `").Append(columnName).Append("` ").Append(columnType);
                if (string.Equals(key, "PRI", StringComparison.OrdinalIgnoreCase)) line.Append(" PK");
                if (columnComment.Length > 0) line.Append("  -- ").Append(columnComment);
                lines.Add(line.ToString());
            }

            List<string> keywords = ExtractKeywords(question);
            tables.Sort(delegate(DbTableRef a, DbTableRef b)
            {
                int sa = TableScore(a, selected, question, keywords, tableTexts, tableComments);
                int sb = TableScore(b, selected, question, keywords, tableTexts, tableComments);
                if (sa != sb) return sb.CompareTo(sa);
                return string.Compare(a.Table, b.Table, StringComparison.OrdinalIgnoreCase);
            });

            const int maxChars = 18000;
            StringBuilder schema = new StringBuilder();
            schema.Append("库 `").Append(database).Append("`；服务端 ").Append(session.ServerVersion);
            if (keywords.Count > 0)
                schema.Append("；按问题相关度排序，关键词：").Append(string.Join("、", keywords.ToArray()));
            schema.Append("\r\n");
            int omitted = 0;
            for (int i = 0; i < tables.Count; i++)
            {
                DbTableRef table = tables[i];
                List<string> lines;
                if (!tableLines.TryGetValue(table.Table, out lines) || lines.Count == 0)
                {
                    omitted++;
                    continue;
                }

                StringBuilder block = new StringBuilder();
                block.Append("表 `").Append(table.Table).Append("`");
                if (table.IsView) block.Append("（视图）");
                long approximateRows;
                if (tableRows.TryGetValue(table.Table, out approximateRows) && !table.IsView)
                    block.Append(" ≈").Append(approximateRows).Append(" 行");
                string comment;
                if (tableComments.TryGetValue(table.Table, out comment) && comment.Length > 0)
                    block.Append(" ").Append(comment);
                block.Append("\r\n");
                for (int j = 0; j < lines.Count; j++) block.Append(lines[j]).Append("\r\n");

                if (schema.Length + block.Length > maxChars)
                {
                    omitted++;
                    continue;
                }
                schema.Append(block);
            }
            if (omitted > 0)
                schema.Append("（另有 ").Append(omitted).Append(" 张表因篇幅未列出）\r\n");
            return schema.ToString();
        }

        private static int TableScore(DbTableRef table, DbTableRef selected, string question,
            List<string> keywords, Dictionary<string, string> tableTexts,
            Dictionary<string, string> tableComments)
        {
            if (table == null) return 0;
            int score = 0;
            if (selected != null &&
                string.Equals(selected.Table, table.Table, StringComparison.OrdinalIgnoreCase))
            {
                score += 60;
            }
            string name = (table.Table ?? "").ToLowerInvariant();
            string q = (question ?? "").ToLowerInvariant();
            if (name.Length >= 3 && q.IndexOf(name, StringComparison.Ordinal) >= 0) score += 100;
            string[] parts = name.Split(new char[] { '_', '-' }, StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < parts.Length; i++)
            {
                if (parts[i].Length >= 3 && q.IndexOf(parts[i], StringComparison.Ordinal) >= 0) score += 10;
            }

            string text;
            if (!tableTexts.TryGetValue(table.Table ?? "", out text) || string.IsNullOrEmpty(text))
                text = name;
            // 中文问题很少直接命中英文表名，注释（手机号码 / 卡券序列号 …）才是主要线索
            score += MatchScore(text, keywords) * 4;

            string comment;
            if (tableComments.TryGetValue(table.Table ?? "", out comment) && comment.Length > 0)
                score += MatchScore(name + " " + comment.ToLowerInvariant(), keywords) * 6;

            if (LooksLikePhoneQuestion(question) &&
                (text.IndexOf("mobile", StringComparison.Ordinal) >= 0 ||
                 text.IndexOf("phone", StringComparison.Ordinal) >= 0 ||
                 text.IndexOf("tel", StringComparison.Ordinal) >= 0))
            {
                score += 20;
            }

            if ((q.IndexOf('券') >= 0 || q.IndexOf('卡') >= 0) &&
                (text.IndexOf('券') >= 0 || text.IndexOf("coupon", StringComparison.Ordinal) >= 0))
            {
                score += 15;
            }
            return score;
        }

        private static bool LooksLikePhoneQuestion(string question)
        {
            if (string.IsNullOrEmpty(question)) return false;
            if (question.IndexOf("手机", StringComparison.Ordinal) >= 0 ||
                question.IndexOf("电话", StringComparison.Ordinal) >= 0) return true;
            int digits = 0;
            for (int i = 0; i < question.Length; i++)
            {
                char ch = question[i];
                if (ch >= '0' && ch <= '9')
                {
                    digits++;
                    if (digits >= 7) return true;
                }
                else
                {
                    digits = 0;
                }
            }
            return false;
        }

        private static string SafeComment(string value)
        {
            string text = (value ?? "").Replace("\r", " ").Replace("\n", " ").Trim();
            if (text.Length > 120) text = text.Substring(0, 120) + "…";
            return text;
        }

        private static bool StartsWithKeyword(string line)
        {
            if (string.IsNullOrEmpty(line)) return false;
            for (int i = 0; i < Starters.Length; i++)
            {
                string keyword = Starters[i];
                if (line.Length < keyword.Length) continue;
                if (!line.Substring(0, keyword.Length).Equals(keyword, StringComparison.OrdinalIgnoreCase)) continue;
                if (line.Length == keyword.Length) return true;
                char next = line[keyword.Length];
                if (!char.IsLetterOrDigit(next) && next != '_') return true;
            }
            return false;
        }
    }
}
