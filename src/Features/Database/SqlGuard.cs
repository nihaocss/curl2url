using System;
using System.Text;
using System.Text.RegularExpressions;

namespace Curl2Url
{
    // 数据库只读模式的第一道闸：只允许明确的只读语句。
    // 第二道闸是 MySQL 会话级 SET SESSION TRANSACTION READ ONLY。
    internal static class SqlGuard
    {
        private static readonly Regex OutfilePattern = new Regex(
            @"\bINTO\s+(OUTFILE|DUMPFILE)\b", RegexOptions.IgnoreCase);

        private static readonly Regex LockingSelectPattern = new Regex(
            @"\b(FOR\s+UPDATE|FOR\s+SHARE|LOCK\s+IN\s+SHARE\s+MODE)\b", RegexOptions.IgnoreCase);

        private static readonly Regex WriteKeywordPattern = new Regex(
            @"\b(INSERT|UPDATE|DELETE|REPLACE|MERGE|CALL|LOAD)\b", RegexOptions.IgnoreCase);

        private static readonly Regex WherePattern = new Regex(@"\bWHERE\b", RegexOptions.IgnoreCase);

        private static readonly string[] BlockedAdminPrefixes =
        {
            "SET GLOBAL", "SET PERSIST", "SET PERSIST_ONLY", "RESET PERSIST",
            "SET PASSWORD", "SHUTDOWN", "KILL",
            "SET SQL_LOG_BIN", "SET SESSION SQL_LOG_BIN", "SET @@SESSION.SQL_LOG_BIN", "SET @@GLOBAL",
            "GRANT ", "REVOKE ", "CREATE USER", "ALTER USER", "DROP USER", "RENAME USER",
            "INSTALL PLUGIN", "UNINSTALL PLUGIN", "RESET MASTER",
            "PURGE BINARY LOGS", "CHANGE MASTER", "CHANGE REPLICATION",
            "START REPLICA", "STOP REPLICA", "START SLAVE", "STOP SLAVE",
            "LOAD DATA"
        };

        public static string StripTrailingSemicolon(string sql)
        {
            if (sql == null) return "";
            string text = sql.Trim();
            while (text.EndsWith(";", StringComparison.Ordinal))
            {
                text = text.Substring(0, text.Length - 1).TrimEnd();
            }
            return text;
        }

        public static bool Validate(string sql, bool allowWrites, out string error)
        {
            error = "";
            string statement;
            if (!Normalize(sql, out statement, out error)) return false;
            if (statement.Length == 0)
            {
                error = "SQL 为空。";
                return false;
            }

            string normalized = StripCommentsAndStrings(statement);
            string first = FirstWord(normalized);
            if (first.Length == 0)
            {
                error = "没有识别到 SQL 关键字。";
                return false;
            }

            if (OutfilePattern.IsMatch(normalized))
            {
                error = "不允许在客户端执行 INTO OUTFILE / DUMPFILE。";
                return false;
            }

            if (!allowWrites)
            {
                bool ok = first == "SELECT" || first == "SHOW" || first == "DESCRIBE" ||
                          first == "DESC" || first == "EXPLAIN" || first == "USE";
                if (first == "WITH")
                {
                    ok = !WriteKeywordPattern.IsMatch(normalized);
                }
                if (!ok)
                {
                    error = "当前连接是只读模式，只允许 SELECT / SHOW / DESCRIBE / EXPLAIN / WITH(SELECT) / USE。";
                    return false;
                }
                if (LockingSelectPattern.IsMatch(normalized))
                {
                    error = "只读模式不允许 FOR UPDATE / LOCK IN SHARE MODE。";
                    return false;
                }
            }
            else
            {
                string upper = UpperPrefix(normalized);
                for (int i = 0; i < BlockedAdminPrefixes.Length; i++)
                {
                    if (upper.StartsWith(BlockedAdminPrefixes[i], StringComparison.Ordinal))
                    {
                        error = "为了安全，数据库工具不执行服务器管理语句：" + BlockedAdminPrefixes[i].Trim();
                        return false;
                    }
                }
            }

            return true;
        }

        public static bool ValidateReadOnly(string sql, out string error)
        {
            return Validate(sql, false, out error);
        }

        public static bool IsDestructive(string sql)
        {
            string statement;
            string error;
            if (!Normalize(sql, out statement, out error)) return true;
            string normalized = StripCommentsAndStrings(statement);
            string first = FirstWord(normalized);
            if (first == "DROP" || first == "TRUNCATE" || first == "ALTER" ||
                first == "RENAME" || first == "REPLACE" || first == "CALL")
            {
                return true;
            }
            if (first == "DELETE" || first == "UPDATE")
            {
                return !WherePattern.IsMatch(normalized);
            }
            return false;
        }

        public static bool IsReadOnlyStatement(string sql)
        {
            string statement;
            string error;
            if (!Normalize(sql, out statement, out error)) return false;
            string normalized = StripCommentsAndStrings(statement);
            string first = FirstWord(normalized);
            if (first == "WITH") return !WriteKeywordPattern.IsMatch(normalized);
            return first == "SELECT" || first == "SHOW" || first == "DESCRIBE" ||
                   first == "DESC" || first == "EXPLAIN" || first == "USE";
        }

        // 返回去掉末尾分号、用于真正执行的语句；内部有第二条语句时直接拒绝。
        public static bool Normalize(string sql, out string statement, out string error)
        {
            statement = "";
            error = "";
            if (sql == null || sql.Trim().Length == 0)
            {
                error = "SQL 为空。";
                return false;
            }

            string text = sql.Trim();
            bool inSingle = false;
            bool inDouble = false;
            bool inBacktick = false;
            bool inLineComment = false;
            bool inBlockComment = false;
            int firstSemicolon = -1;
            int semicolonCount = 0;

            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                char next = i + 1 < text.Length ? text[i + 1] : '\0';

                if (inLineComment)
                {
                    if (c == '\n' || c == '\r') inLineComment = false;
                    continue;
                }
                if (inBlockComment)
                {
                    if (c == '*' && next == '/')
                    {
                        inBlockComment = false;
                        i++;
                    }
                    continue;
                }
                if (inSingle)
                {
                    if (c == '\\' && next != '\0') { i++; continue; }
                    if (c == '\'')
                    {
                        if (next == '\'') { i++; continue; }
                        inSingle = false;
                    }
                    continue;
                }
                if (inDouble)
                {
                    if (c == '\\' && next != '\0') { i++; continue; }
                    if (c == '"')
                    {
                        if (next == '"') { i++; continue; }
                        inDouble = false;
                    }
                    continue;
                }
                if (inBacktick)
                {
                    if (c == '`')
                    {
                        if (next == '`') { i++; continue; }
                        inBacktick = false;
                    }
                    continue;
                }

                if (c == '\'') { inSingle = true; continue; }
                if (c == '"') { inDouble = true; continue; }
                if (c == '`') { inBacktick = true; continue; }
                if (c == '#') { inLineComment = true; continue; }
                if (c == '-' && next == '-') { inLineComment = true; i++; continue; }
                if (c == '/' && next == '*') { inBlockComment = true; i++; continue; }
                if (c == ';')
                {
                    if (firstSemicolon < 0) firstSemicolon = i;
                    semicolonCount++;
                }
            }

            if (semicolonCount > 1)
            {
                error = "一次只能执行一条 SQL；检测到多个分号。";
                return false;
            }
            if (firstSemicolon >= 0)
            {
                string tail = text.Substring(firstSemicolon + 1);
                if (TailIsOnlyWhitespaceOrComments(tail))
                {
                    text = text.Substring(0, firstSemicolon).TrimEnd();
                }
                else
                {
                    error = "一次只能执行一条 SQL；检测到分号后还有内容。";
                    return false;
                }
            }

            if (text.Length == 0)
            {
                error = "SQL 为空。";
                return false;
            }
            statement = text;
            return true;
        }

        private static bool TailIsOnlyWhitespaceOrComments(string tail)
        {
            if (string.IsNullOrEmpty(tail)) return true;
            int i = 0;
            while (i < tail.Length)
            {
                char c = tail[i];
                if (char.IsWhiteSpace(c)) { i++; continue; }
                if (c == '#')
                {
                    while (i < tail.Length && tail[i] != '\n' && tail[i] != '\r') i++;
                    continue;
                }
                if (c == '-' && i + 1 < tail.Length && tail[i + 1] == '-')
                {
                    i += 2;
                    while (i < tail.Length && tail[i] != '\n' && tail[i] != '\r') i++;
                    continue;
                }
                if (c == '/' && i + 1 < tail.Length && tail[i + 1] == '*')
                {
                    int end = tail.IndexOf("*/", i + 2, StringComparison.Ordinal);
                    if (end < 0) return false;
                    i = end + 2;
                    continue;
                }
                return false;
            }
            return true;
        }

        // 只用于关键字判断：注释替换成空格，字符串和反引号内容替换成占位符。
        public static string StripCommentsAndStrings(string sql)
        {
            if (string.IsNullOrEmpty(sql)) return "";
            StringBuilder sb = new StringBuilder(sql.Length);
            bool inSingle = false, inDouble = false, inBacktick = false;
            bool inLineComment = false, inBlockComment = false;

            for (int i = 0; i < sql.Length; i++)
            {
                char c = sql[i];
                char next = i + 1 < sql.Length ? sql[i + 1] : '\0';

                if (inLineComment)
                {
                    if (c == '\n' || c == '\r') { inLineComment = false; sb.Append(' '); }
                    else sb.Append(' ');
                    continue;
                }
                if (inBlockComment)
                {
                    if (c == '*' && next == '/') { inBlockComment = false; i++; sb.Append("  "); }
                    else sb.Append(' ');
                    continue;
                }
                if (inSingle)
                {
                    if (c == '\\' && next != '\0') { i++; continue; }
                    if (c == '\'')
                    {
                        if (next == '\'') { i++; continue; }
                        inSingle = false;
                        sb.Append("'?'");
                    }
                    continue;
                }
                if (inDouble)
                {
                    if (c == '\\' && next != '\0') { i++; continue; }
                    if (c == '"')
                    {
                        if (next == '"') { i++; continue; }
                        inDouble = false;
                        sb.Append("'?'");
                    }
                    continue;
                }
                if (inBacktick)
                {
                    if (c == '`')
                    {
                        if (next == '`') { i++; continue; }
                        inBacktick = false;
                        sb.Append("`x`");
                    }
                    continue;
                }

                if (c == '\'') { inSingle = true; continue; }
                if (c == '"') { inDouble = true; continue; }
                if (c == '`') { inBacktick = true; continue; }
                if (c == '#') { inLineComment = true; sb.Append(' '); continue; }
                if (c == '-' && next == '-') { inLineComment = true; i++; sb.Append("  "); continue; }
                if (c == '/' && next == '*') { inBlockComment = true; i++; sb.Append("  "); continue; }

                sb.Append(c);
            }
            return sb.ToString();
        }

        private static string FirstWord(string normalized)
        {
            string trimmed = (normalized ?? "").TrimStart();
            int end = 0;
            while (end < trimmed.Length && (char.IsLetter(trimmed[end]) || trimmed[end] == '_')) end++;
            return trimmed.Substring(0, end).ToUpperInvariant();
        }

        // 统一空白和大小写，避免 SET   GLOBAL / SET\nGLOBAL 这类写法绕过前缀检查。
        private static string UpperPrefix(string normalized)
        {
            string text = (normalized ?? "").TrimStart();
            StringBuilder collapsed = new StringBuilder(text.Length);
            bool pendingSpace = false;
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (char.IsWhiteSpace(c))
                {
                    pendingSpace = collapsed.Length > 0;
                    continue;
                }
                if (pendingSpace) collapsed.Append(' ');
                pendingSpace = false;
                collapsed.Append(char.ToUpperInvariant(c));
            }
            return collapsed.ToString();
        }
    }
}
