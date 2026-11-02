using System;
using System.Collections.Generic;
using System.Text;

namespace Curl2Url
{
    // 把 AI 生成或手写的一整行 SQL 拆成多行：select / from / where / join / group by …
    // 各占一行，SELECT 列表和 WHERE 条件按缩进展开。
    // 关键字统一转小写（大写单词不熟的用户更好认），字符串、注释和标识符原样保留。
    internal static class SqlFormatter
    {
        private const string MajorKeywords =
            "SELECT,FROM,WHERE,HAVING,LIMIT,UNION,UNION ALL,GROUP BY,ORDER BY,JOIN,ON," +
            "LEFT JOIN,RIGHT JOIN,INNER JOIN,OUTER JOIN,CROSS JOIN,FULL JOIN,STRAIGHT_JOIN";

        private static readonly HashSet<string> MajorSet = BuildSet(MajorKeywords);

        private static readonly HashSet<string> NoSpaceBeforeParen = BuildSet(
            "IN,EXISTS,VALUES,AND,OR,NOT,ON,BY,SELECT,FROM,WHERE,JOIN,HAVING,LIMIT,UNION," +
            "THEN,ELSE,WHEN,LIKE,BETWEEN,IS,AS,CASE,ALL,DISTINCT,USING");

        private sealed class Token
        {
            public string Text;
            public bool IsWord;
            public bool IsComment;
        }

        public static string Format(string sql)
        {
            if (string.IsNullOrEmpty(sql)) return sql ?? "";
            string trimmed = sql.Trim();
            if (trimmed.Length == 0) return "";

            List<Token> tokens = Tokenize(trimmed);
            if (tokens.Count == 0) return "";

            StringBuilder sb = new StringBuilder(trimmed.Length + 64);
            int depth = 0;
            int whereDepth = -1;

            string previous = "";
            bool lineStart = true;

            for (int i = 0; i < tokens.Count; i++)
            {
                Token token = tokens[i];
                if (token.IsComment)
                {
                    if (!lineStart) NewLine(sb, depth * 2);
                    sb.Append(token.Text).Append("\r\n");
                    lineStart = true;
                    previous = "";
                    continue;
                }

                if (token.IsWord)
                {
                    string word = token.Text.ToUpperInvariant();
                    string combined = CombineKeyword(tokens, ref i, word);
                    if (combined.Length > 0) word = combined;

                    // 关键字统一显示成小写（大写单词不熟的用户更好认）；
                    // 普通标识符（表名/字段名）保持原样，避免改坏大小写敏感的对象名。
                    bool isKeyword = combined.Length > 0 || MajorSet.Contains(word) ||
                        SqlKeywords.Set.Contains(word);
                    string display = isKeyword ? word.ToLowerInvariant() : token.Text;

                    if (MajorSet.Contains(word))
                    {
                        if (!(lineStart && sb.Length == 0)) NewLine(sb, depth * 2);
                        sb.Append(display);
                        lineStart = false;
                        previous = display;

                        if (word == "SELECT" || word == "WHERE" || word == "HAVING" || word == "ON")
                        {
                            // 字段列表 / 条件先换行再缩进，长 SQL 更容易看
                            NewLine(sb, depth * 2 + 2);
                            lineStart = true;
                        }

                        if (word == "WHERE" || word == "HAVING") whereDepth = depth;
                        else whereDepth = -1;
                        continue;
                    }

                    if ((word == "AND" || word == "OR") && whereDepth >= 0 && depth == whereDepth)
                    {
                        NewLine(sb, depth * 2 + 2);
                        sb.Append(display);
                        lineStart = false;
                        previous = display;
                        continue;
                    }

                    if (!lineStart && NeedSpace(previous, display)) sb.Append(' ');
                    sb.Append(display);
                    lineStart = false;
                    previous = display;
                    continue;
                }

                string text = token.Text;
                if (text == "(")
                {
                    if (!lineStart && NeedSpace(previous, text)) sb.Append(' ');
                    sb.Append('(');
                    depth++;
                    lineStart = false;
                    previous = text;
                    continue;
                }
                if (text == ")")
                {
                    if (depth > 0) depth--;
                    if (whereDepth > depth) whereDepth = -1;
                    TrimTrailingSpace(sb);
                    sb.Append(')');
                    lineStart = false;
                    previous = text;
                    continue;
                }
                if (text == ",")
                {
                    TrimTrailingSpace(sb);
                    sb.Append(',');
                    lineStart = false;
                    previous = text;
                    // 只有 SELECT 列表里的逗号换行；GROUP BY / ORDER BY 保持一行
                    if (depth == 0 && IsInSelectList(sb))
                    {
                        NewLine(sb, 2);
                        lineStart = true;
                    }
                    else
                    {
                        sb.Append(' ');
                    }
                    continue;
                }
                if (text == ".")
                {
                    TrimTrailingSpace(sb);
                    sb.Append('.');
                    lineStart = false;
                    previous = text;
                    continue;
                }
                if (text == ";")
                {
                    TrimTrailingSpace(sb);
                    sb.Append(';');
                    lineStart = false;
                    previous = text;
                    continue;
                }

                if (!lineStart && NeedSpace(previous, text)) sb.Append(' ');
                sb.Append(text);
                lineStart = false;
                previous = text;
            }

            return sb.ToString().TrimEnd();
        }

        // SELECT 列表判断：当前行（缩进 2 格）属于 SELECT 后展开的字段列表。
        // 不能简单看 depth，因为 SELECT 只有这一处会主动换行到 2 格缩进。
        private static bool IsInSelectList(StringBuilder sb)
        {
            int start = LastLineStart(sb);
            string line = sb.ToString(start, sb.Length - start);
            if (line.StartsWith("  ", StringComparison.Ordinal) &&
                !line.TrimStart().StartsWith("AND", StringComparison.OrdinalIgnoreCase) &&
                !line.TrimStart().StartsWith("OR", StringComparison.OrdinalIgnoreCase))
            {
                // 往回找最近的子句关键字，确认当前在 SELECT 和 FROM 之间
                string before = sb.ToString(0, start);
                int selectAt = LastKeywordIndex(before, "SELECT");
                if (selectAt < 0) return false;
                int fromAt = LastKeywordIndex(before, "FROM");
                return fromAt < selectAt;
            }
            return false;
        }

        private static int LastKeywordIndex(string text, string keyword)
        {
            int index = text.LastIndexOf(keyword, StringComparison.OrdinalIgnoreCase);
            while (index > 0 && (char.IsLetterOrDigit(text[index - 1]) || text[index - 1] == '_'))
            {
                index = text.LastIndexOf(keyword, index - 1, StringComparison.OrdinalIgnoreCase);
            }
            return index;
        }

        private static int LastLineStart(StringBuilder sb)
        {
            for (int i = sb.Length - 1; i >= 0; i--)
            {
                if (sb[i] == '\n') return i + 1;
            }
            return 0;
        }

        private static string CombineKeyword(List<Token> tokens, ref int index, string word)
        {
            if (word == "GROUP" || word == "ORDER")
            {
                if (NextWordIs(tokens, index, "BY"))
                {
                    index++;
                    return word + " BY";
                }
            }
            if (word == "UNION" && NextWordIs(tokens, index, "ALL"))
            {
                index++;
                return "UNION ALL";
            }
            if ((word == "LEFT" || word == "RIGHT" || word == "INNER" || word == "OUTER" ||
                 word == "CROSS" || word == "FULL") && NextWordIs(tokens, index, "JOIN"))
            {
                index++;
                return word + " JOIN";
            }
            return word;
        }

        private static bool NextWordIs(List<Token> tokens, int index, string word)
        {
            if (index + 1 >= tokens.Count) return false;
            Token next = tokens[index + 1];
            return next.IsWord && next.Text.Equals(word, StringComparison.OrdinalIgnoreCase);
        }

        private static bool NeedSpace(string previous, string current)
        {
            if (previous.Length == 0) return false;
            if (current == "," || current == ")" || current == "." || current == ";") return false;
            if (previous == "(" || previous == ".") return false;
            if (current == "(") return NoSpaceBeforeParen.Contains(previous);
            return true;
        }

        private static void NewLine(StringBuilder sb, int indent)
        {
            TrimTrailingSpace(sb);
            if (sb.Length > 0 && sb[sb.Length - 1] != '\n') sb.Append("\r\n");
            for (int i = 0; i < indent; i++) sb.Append(' ');
        }

        private static void TrimTrailingSpace(StringBuilder sb)
        {
            while (sb.Length > 0 && (sb[sb.Length - 1] == ' ' || sb[sb.Length - 1] == '\t')) sb.Length--;
        }

        private static HashSet<string> BuildSet(string csv)
        {
            HashSet<string> set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string[] parts = csv.Split(',');
            for (int i = 0; i < parts.Length; i++)
            {
                string part = parts[i].Trim();
                if (part.Length > 0) set.Add(part);
            }
            return set;
        }

        private static List<Token> Tokenize(string sql)
        {
            List<Token> tokens = new List<Token>();
            int i = 0;
            while (i < sql.Length)
            {
                char ch = sql[i];
                if (char.IsWhiteSpace(ch))
                {
                    i++;
                    continue;
                }

                if (ch == '\'' || ch == '"' || ch == '`')
                {
                    int start = i;
                    char quote = ch;
                    i++;
                    while (i < sql.Length)
                    {
                        if (sql[i] == '\\' && i + 1 < sql.Length)
                        {
                            i += 2;
                            continue;
                        }
                        if (sql[i] == quote)
                        {
                            if (i + 1 < sql.Length && sql[i + 1] == quote)
                            {
                                i += 2;
                                continue;
                            }
                            i++;
                            break;
                        }
                        i++;
                    }
                    tokens.Add(new Token { Text = sql.Substring(start, i - start), IsWord = false });
                    continue;
                }

                if (ch == '-' && i + 1 < sql.Length && sql[i + 1] == '-')
                {
                    int start = i;
                    while (i < sql.Length && sql[i] != '\n') i++;
                    tokens.Add(new Token { Text = sql.Substring(start, i - start).TrimEnd('\r'), IsComment = true });
                    continue;
                }
                if (ch == '#')
                {
                    int start = i;
                    while (i < sql.Length && sql[i] != '\n') i++;
                    tokens.Add(new Token { Text = sql.Substring(start, i - start).TrimEnd('\r'), IsComment = true });
                    continue;
                }
                if (ch == '/' && i + 1 < sql.Length && sql[i + 1] == '*')
                {
                    int start = i;
                    i += 2;
                    while (i + 1 < sql.Length && !(sql[i] == '*' && sql[i + 1] == '/')) i++;
                    if (i + 1 < sql.Length) i += 2;
                    tokens.Add(new Token { Text = sql.Substring(start, i - start), IsComment = true });
                    continue;
                }

                if (char.IsLetterOrDigit(ch) || ch == '_' || ch == '@' || ch == '$')
                {
                    int start = i;
                    while (i < sql.Length &&
                        (char.IsLetterOrDigit(sql[i]) || sql[i] == '_' || sql[i] == '@' ||
                         sql[i] == '$' || sql[i] == '#'))
                    {
                        i++;
                    }
                    tokens.Add(new Token { Text = sql.Substring(start, i - start), IsWord = true });
                    continue;
                }

                string symbol = ch.ToString();
                if (i + 1 < sql.Length)
                {
                    string two = sql.Substring(i, 2);
                    if (two == "<=" || two == ">=" || two == "<>" || two == "!=" ||
                        two == "||" || two == "&&" || two == ":=" || two == "->")
                    {
                        symbol = two;
                        i++;
                    }
                }
                tokens.Add(new Token { Text = symbol, IsWord = false });
                i++;
            }
            return tokens;
        }
    }
}
