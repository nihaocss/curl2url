using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;

namespace Curl2Url
{
    // 命令行 AI 查询回归：用真实表结构 + 真实模型生成 SQL，再按只读闸门执行。
    // 用法：curl2url.exe --aitest [连接名或ID] [数据库] [问题文件] [报告文件]
    // 问题文件每行 "类型[|期望表|问题]"，类型取值 answer（必须生成可执行 SQL）、
    // refuse（必须拒绝生成）、any（两种都算通过）。期望表里 & 分隔的每一组都必须命中，
    // 组内逗号表示任选其一，用来卡住“能跑但查错表”的查询。
    internal static class DatabaseAiTest
    {
        public static int Run(string[] args) { return Run(args, false); }

        public static int Run(string[] args, bool resume)
        {
            // 报告文件名以结果为准：--aitest 会自动改名为 *.last-pass.txt / *.last-fail.txt，
            // 这样看文件名就知道最近一次是过是挂，不会被旧报告误导
            string profileKey = args.Length > 1 ? args[1] : "";
            string database = args.Length > 2 ? args[2] : "";
            string questionsPath = args.Length > 3 ? args[3] : "aitest-questions.txt";
            string rawReportPath = args.Length > 4 ? args[4] : "aitest-report.txt";
            string reportPath = rawReportPath;
            if (reportPath.EndsWith(".txt", StringComparison.OrdinalIgnoreCase))
                reportPath = reportPath.Substring(0, reportPath.Length - 4);
            string resumePath = reportPath + ".resume";
            int startIndex = 0;

            if (!File.Exists(questionsPath))
            {
                WriteLine("找不到问题文件：" + questionsPath);
                return 2;
            }

            if (resume && !File.Exists(resumePath))
            {
                WriteLine("没有续跑文件：" + resumePath + "，请先运行一次 --aitest。");
                return 2;
            }

            AiOcrSettings settings = AiOcrSettings.Load();
            if (settings == null || !settings.IsConfigured)
            {
                WriteLine("AI 没有配置，无法运行 AI 查询测试。");
                return 2;
            }

            DatabaseConnectionStore store = new DatabaseConnectionStore();
            string storeError;
            if (!store.Load(out storeError))
            {
                WriteLine("读取连接配置失败：" + storeError);
                return 2;
            }

            DatabaseConnectionProfile profile = FindProfile(store, profileKey);
            if (profile == null)
            {
                WriteLine("找不到连接：" + (profileKey.Length == 0 ? "（没有上次使用的连接）" : profileKey));
                return 2;
            }
            if (database.Length == 0) database = profile.Option("lastDatabase", "welfare_coupon");
            if (database.Length == 0)
            {
                WriteLine("没有指定数据库。");
                return 2;
            }

            string connectError;
            MySqlSession session = MySqlSession.Open(profile, out connectError);
            if (session == null)
            {
                WriteLine("连接失败：" + connectError);
                return 2;
            }
            // 和界面一样先切到目标库，否则 AI 生成的 SQL 没带库名会报 No database selected
            string useError;
            session.Query("USE " + MySqlSession.QuoteIdentifier(database), 0, 15, out useError);
            if (useError.Length > 0)
            {
                WriteLine("切换数据库失败：" + useError);
                session.Dispose();
                return 2;
            }

            List<string> questionLines = ReadQuestions(questionsPath);
            StringBuilder report = new StringBuilder();
            int passed = 0;
            int failed = 0;
            bool stopped = false;
            int resumeNext = startIndex;
            string summary;
            string outputPath;

            if (resume)
            {
                // 断点续跑：之前的结果原样保留，从上次停下的题继续
                Dictionary<string, string> saved = ReadResume(resumePath);
                string savedDatabase;
                if (saved.TryGetValue("database", out savedDatabase) && savedDatabase.Length > 0 &&
                    !string.Equals(savedDatabase, database, StringComparison.OrdinalIgnoreCase))
                {
                    WriteLine("续跑文件的数据库（" + savedDatabase + "）和本次参数（" + database + "）不一致，已放弃续跑。");
                    session.Dispose();
                    DeleteResume(resumePath);
                    return 2;
                }
                string savedNext;
                if (saved.TryGetValue("next", out savedNext)) int.TryParse(savedNext, out startIndex);
                if (startIndex < 0) startIndex = 0;
                if (startIndex > questionLines.Count) startIndex = questionLines.Count;
                string previousReport = reportPath + ".last-fail.txt";
                if (File.Exists(previousReport))
                {
                    try { report.Append(File.ReadAllText(previousReport, Encoding.UTF8)); }
                    catch (Exception) { }
                }
                WriteLine("续跑：从第 " + (startIndex + 1) + " 题开始。");
            }

            report.Append("连接：").Append(profile.Name).Append("（").Append(profile.Endpoint).Append("）\r\n");
            report.Append("数据库：").Append(database).Append("；问题数：").Append(questionLines.Count).Append("\r\n\r\n");
            WriteLine("连接：" + profile.Name + " / " + database + "，共 " + questionLines.Count + " 个问题");

            try
            {
                for (int i = startIndex; i < questionLines.Count; i++)
                {
                    string line = questionLines[i];
                    string kind = "answer";
                    string expected = "";
                    string question = line;
                    string[] parts = line.Split(new char[] { '|' }, 3);
                    if (parts.Length >= 2)
                    {
                        kind = parts[0].Trim().ToLowerInvariant();
                        if (parts.Length == 3)
                        {
                            expected = parts[1].Trim();
                            question = parts[2].Trim();
                        }
                        else
                        {
                            question = parts[1].Trim();
                        }
                    }
                    if (question.Length == 0) continue;

                    string result;
                    bool fatal;
                    bool ok = RunOne(session, settings, profile, database, kind, expected, question,
                        out result, out fatal);
                    if (ok) passed++;

                    string entry = "[" + (ok ? "PASS" : "FAIL") + "] (" + kind + ") " + question + "\r\n    " + result + "\r\n";
                    report.Append(entry);
                    WriteLine("[" + (i + 1) + "/" + questionLines.Count + "] " + (ok ? "PASS" : "FAIL") + "  " + question);
                    if (!ok) WriteLine("    " + result.Replace("\r\n", "\r\n    "));

                    if (fatal)
                    {
                        // 额度用完 / 鉴权失败时后面的题必然全部失败，没必要继续刷请求；
                        // 把断点记下来，额度恢复后用 --aitest-resume 接着跑
                        WriteLine("AI 配置有问题，本行之后的问题未执行：" + result);
                        for (int k = i + 1; k < questionLines.Count; k++)
                            report.Append("[SKIP] ").Append(questionLines[k]).Append("\r\n    未执行：AI 请求失败\r\n");
                        WriteResume(resumePath, profileKey, database, reportPath, i + 1);
                        WriteLine("已记录断点：额度恢复后可运行 --aitest-resume 从第 " + (i + 2) + " 题继续。");
                        stopped = true;
                        break;
                    }

                    resumeNext = i + 1;
                    Thread.Sleep(800);
                }
            }
            finally
            {
                try { session.Dispose(); }
                catch (Exception) { }
            }

            summary = "通过 " + passed + "，失败 " + failed + "，共 " + (passed + failed) + "。";
            report.Append("\r\n").Append(summary).Append("\r\n");
            outputPath = reportPath + (failed == 0 ? ".last-pass.txt" : ".last-fail.txt");
            try { File.WriteAllText(outputPath, report.ToString(), new UTF8Encoding(true)); }
            catch (Exception ex) { WriteLine("写报告失败：" + ex.Message); }

            // 整轮跑完才删断点；中途因额度停下的保留，等下次续跑
            if (!stopped)
            {
                DeleteResume(resumePath);
                if (failed > 0) WriteResume(resumePath, profileKey, database, reportPath, resumeNext);
            }

            WriteLine(summary);
            WriteLine("报告：" + Path.GetFullPath(outputPath));
            return failed == 0 ? 0 : 1;
        }

        private static bool RunOne(MySqlSession session, AiOcrSettings settings,
            DatabaseConnectionProfile profile, string database, string kind, string expected,
            string question, out string result, out bool fatal)
        {
            result = "";
            fatal = false;
            string schemaError;
            string schema = DatabaseAiQuery.BuildSchema(session, database, question, null, out schemaError);
            if (schemaError.Length > 0)
            {
                result = "读取表结构失败：" + schemaError;
                return false;
            }

            string prompt = DatabaseAiQuery.BuildUserPrompt(profile, database,
                session.ServerVersion, schema, question);
            string answer = "";
            string apiError = "";
            AiOcrRunner.TryChat(settings, DatabaseAiQuery.SystemPrompt, prompt, out answer, out apiError);
            if (apiError.Length > 0)
            {
                Thread.Sleep(1500);
                AiOcrRunner.TryChat(settings, DatabaseAiQuery.SystemPrompt, prompt, out answer, out apiError);
            }
            if (apiError.Length > 0)
            {
                result = "AI 请求失败：" + apiError;
                fatal = apiError.StartsWith("FATAL:", StringComparison.Ordinal);
                return false;
            }

            string note;
            string sql = DatabaseAiQuery.ExtractSql(answer, out note);
            if (sql.Length == 0 && kind != "refuse" && note.StartsWith("--", StringComparison.Ordinal))
            {
                // 和界面一致：模型误判“没有相关表”时提醒一次再试
                string retryPrompt = prompt + "\n\n【提醒】当前库的表结构已经给出，请再仔细检查已有表名、字段和注释；" +
                    "只有确实没有任何可用的表时才用 -- 拒绝。请只输出一条只读 SQL。";
                string retryAnswer = "";
                string retryError = "";
                AiOcrRunner.TryChat(settings, DatabaseAiQuery.SystemPrompt, retryPrompt,
                    out retryAnswer, out retryError);
                if (retryError.Length == 0)
                {
                    answer = retryAnswer;
                    sql = DatabaseAiQuery.ExtractSql(answer, out note);
                }
            }
            if (sql.Length == 0)
            {
                result = "AI 没有生成 SQL：" + OneLine(note.Length > 0 ? note : OneLine(answer));
                return kind != "answer";
            }
            if (kind == "refuse")
            {
                result = "预期拒绝生成，但返回了 SQL：" + OneLine(sql);
                return false;
            }

            string problem;
            if (!DatabaseAiQuery.ValidateGenerated(sql, out problem))
            {
                string retryPrompt = prompt + "\n\n【上一次生成的问题】" + problem +
                    "。请重新生成一条 SQL，严格按照上述规则：列名、表名用反引号，字符串只作为条件值。";
                string retryAnswer = "";
                string retryError = "";
                AiOcrRunner.TryChat(settings, DatabaseAiQuery.SystemPrompt, retryPrompt,
                    out retryAnswer, out retryError);
                if (retryError.Length > 0)
                {
                    result = "第一次生成的 SQL 不合规（" + problem + "），重试失败：" + retryError;
                    return false;
                }
                sql = DatabaseAiQuery.ExtractSql(retryAnswer, out note);
                if (sql.Length == 0)
                {
                    result = "第一次生成的 SQL 不合规（" + problem + "），重试没有给出 SQL：" + OneLine(note);
                    return false;
                }
                if (!DatabaseAiQuery.ValidateGenerated(sql, out problem))
                {
                    result = "重试后的 SQL 仍不合规：" + problem + "；SQL：" + OneLine(sql);
                    return false;
                }
            }

            string guardError;
            if (!SqlGuard.ValidateReadOnly(sql, out guardError))
            {
                result = "生成的不是只读 SQL（" + guardError + "）：" + OneLine(sql);
                return false;
            }

            // 期望表：& 分隔的每一组都必须命中；组内逗号分隔表示任选其一。
            // 用来防止模型拿日志表 / 备份表 / 空表凑出一个能跑但没意义的查询。
            // 漏表属于可以靠提示纠正的错误，和界面一样给一次重试机会。
            string missing = FindMissingTable(sql, expected);
            if (missing.Length > 0)
            {
                string retryPrompt = prompt + "\n\n【上一次生成的 SQL】" + OneLine(sql) +
                    "\n【问题】这条 SQL 漏查了「" + missing + "」；这类问题需要覆盖所有同类主表。" +
                    "请参照规则 5.7 重新生成一条 SQL，只输出 SQL 本体。";
                string retryAnswer = "";
                string retryError = "";
                AiOcrRunner.TryChat(settings, DatabaseAiQuery.SystemPrompt, retryPrompt,
                    out retryAnswer, out retryError);
                if (retryError.Length == 0)
                {
                    string retrySql = DatabaseAiQuery.ExtractSql(retryAnswer, out note);
                    if (retrySql.Length > 0 && DatabaseAiQuery.ValidateGenerated(retrySql, out problem) &&
                        SqlGuard.ValidateReadOnly(retrySql, out guardError))
                    {
                        sql = retrySql;
                        missing = FindMissingTable(sql, expected);
                    }
                }
                if (missing.Length > 0)
                {
                    result = "SQL 没有使用期望的表（" + missing + "）；SQL：" + OneLine(sql);
                    return false;
                }
            }

            string execError;
            DbQueryResult queryResult = session.Query(sql, 100, 60, out execError);
            if (execError.Length > 0)
            {
                result = "执行失败：" + OneLine(execError) + "；SQL：" + OneLine(sql);
                return false;
            }

            int rows = queryResult.HasResultSet ? queryResult.Table.Rows.Count : 0;
            result = "返回 " + rows + " 行 · " + queryResult.ElapsedMs + " ms；SQL：" + OneLine(sql);
            return true;
        }

        // 返回第一个没有命中的期望表组（原文），全部命中时返回空字符串
        private static string FindMissingTable(string sql, string expected)
        {
            if (expected.Length == 0) return "";
            string[] groups = expected.Split('&');
            for (int g = 0; g < groups.Length; g++)
            {
                string[] names = groups[g].Split(',');
                bool groupHit = false;
                for (int i = 0; i < names.Length; i++)
                {
                    string name = names[i].Trim();
                    if (name.Length == 0) continue;
                    if (sql.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        groupHit = true;
                        break;
                    }
                }
                if (!groupHit) return groups[g].Trim();
            }
            return "";
        }

        // 给单题复测用：--aitest 把续跑信息（题号 / 剩余题 / 已用报告）写进 .resume 文件，
        // --aitest-resume 从断点继续，不用整轮重跑烧额度。全部跑完后自动删除该文件。
        private static void WriteResume(string resumePath, string profileKey, string database,
            string reportPath, int nextIndex)
        {
            try
            {
                StringBuilder sb = new StringBuilder();
                sb.Append("profile=").Append(profileKey).Append("\r\n");
                sb.Append("database=").Append(database).Append("\r\n");
                sb.Append("report=").Append(reportPath).Append("\r\n");
                sb.Append("next=").Append(nextIndex).Append("\r\n");
                File.WriteAllText(resumePath, sb.ToString(), new UTF8Encoding(true));
            }
            catch (Exception ex)
            {
                WriteLine("写续跑文件失败：" + ex.Message);
            }
        }

        private static void DeleteResume(string resumePath)
        {
            try { if (File.Exists(resumePath)) File.Delete(resumePath); }
            catch (Exception) { }
        }

        private static Dictionary<string, string> ReadResume(string resumePath)
        {
            Dictionary<string, string> values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            string[] lines = File.ReadAllLines(resumePath, Encoding.UTF8);
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i].Trim().TrimStart('﻿');
                int eq = line.IndexOf('=');
                if (eq <= 0) continue;
                values[line.Substring(0, eq).Trim()] = line.Substring(eq + 1).Trim();
            }
            return values;
        }

        private static DatabaseConnectionProfile FindProfile(DatabaseConnectionStore store, string key)
        {
            if (key.Length == 0) return store.Find(store.LastProfileId);
            DatabaseConnectionProfile byId = store.Find(key);
            if (byId != null) return byId;
            for (int i = 0; i < store.Profiles.Count; i++)
            {
                DatabaseConnectionProfile profile = store.Profiles[i];
                if (profile.Name != null && profile.Name.IndexOf(key, StringComparison.OrdinalIgnoreCase) >= 0)
                    return profile;
            }
            return null;
        }

        private static List<string> ReadQuestions(string path)
        {
            List<string> lines = new List<string>();
            string[] raw = File.ReadAllLines(path, Encoding.UTF8);
            for (int i = 0; i < raw.Length; i++)
            {
                string line = raw[i].Trim().TrimStart('﻿');
                if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal)) continue;
                lines.Add(line);
            }
            return lines;
        }

        private static string OneLine(string text)
        {
            if (text == null) return "";
            string value = text.Replace("\r", " ").Replace("\n", " ").Replace("\t", " ").Trim();
            while (value.IndexOf("  ", StringComparison.Ordinal) >= 0)
                value = value.Replace("  ", " ");
            if (value.Length > 600) value = value.Substring(0, 600) + "…";
            return value;
        }

        private static void WriteLine(string text)
        {
            try
            {
                Console.WriteLine(text);
                Console.Out.Flush();
            }
            catch (Exception) { }
        }
    }
}
