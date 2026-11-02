using System;
using System.Collections.Generic;
using System.Data;
using System.Reflection;
using System.Text;

namespace Curl2Url
{
    // --selftest 里的无网络自检：SQL 闸门、Redis 闸门/CRC、CSV、DPAPI、驱动可加载。
    internal static class DatabaseSelftest
    {
        public static string Run()
        {
            string error = CheckSqlGuard();
            if (error.Length > 0) return error;
            error = CheckRedisGuard();
            if (error.Length > 0) return error;
            error = CheckCrc16();
            if (error.Length > 0) return error;
            error = CheckCsv();
            if (error.Length > 0) return error;
            error = CheckDpapi();
            if (error.Length > 0) return error;
            error = CheckConnectionStore();
            if (error.Length > 0) return error;
            error = CheckAiSqlExtraction();
            if (error.Length > 0) return error;
            error = CheckAiSchemaKeywords();
            if (error.Length > 0) return error;
            error = CheckSqlFormatter();
            if (error.Length > 0) return error;
            error = CheckHarContent();
            if (error.Length > 0) return error;
            error = CheckProviders();
            if (error.Length > 0) return error;
            return "";
        }

        private static string CheckSqlGuard()
        {
            string statement;
            string error;
            if (!SqlGuard.Normalize("SELECT 1;", out statement, out error) || statement != "SELECT 1")
                return "SQL Normalize 去掉末尾分号失败：" + error;
            if (SqlGuard.Normalize("SELECT 1; SELECT 2", out statement, out error))
                return "SQL 多语句没有被拒绝";
            if (!SqlGuard.Normalize("SELECT ';'", out statement, out error))
                return "SQL 字符串内的分号被误判：" + error;
            if (!SqlGuard.Normalize("SELECT 1; -- ok", out statement, out error) || statement != "SELECT 1")
                return "SQL 尾部注释处理失败：" + error;

            if (!SqlGuard.ValidateReadOnly("SELECT * FROM t", out error))
                return "只读 SELECT 被拒绝：" + error;
            if (!SqlGuard.ValidateReadOnly("WITH x AS (SELECT 1) SELECT * FROM x", out error))
                return "只读 CTE 被拒绝：" + error;
            if (SqlGuard.ValidateReadOnly("WITH x AS (SELECT 1) DELETE FROM t", out error))
                return "只读 CTE 内的 DELETE 没有被拒绝";
            if (SqlGuard.ValidateReadOnly("SELECT * FROM t FOR UPDATE", out error))
                return "FOR UPDATE 没有被拒绝";
            if (SqlGuard.ValidateReadOnly("SELECT * FROM t FOR SHARE", out error))
                return "FOR SHARE 没有被拒绝";
            if (SqlGuard.ValidateReadOnly("INSERT INTO t VALUES (1)", out error))
                return "只读模式 INSERT 没有被拒绝";
            if (SqlGuard.ValidateReadOnly("SELECT * FROM t INTO OUTFILE '/tmp/x'", out error))
                return "INTO OUTFILE 没有被拒绝";

            if (SqlGuard.Validate("UPDATE t SET a=1", true, out error))
            {
                if (!SqlGuard.IsDestructive("UPDATE t SET a=1"))
                    return "无 WHERE 的 UPDATE 没有被标记为破坏性";
            }
            else
            {
                return "写模式 UPDATE 被拒绝：" + error;
            }
            if (SqlGuard.Validate("UPDATE t SET a=1 WHERE id=1", true, out error))
            {
                if (SqlGuard.IsDestructive("UPDATE t SET a=1 WHERE id=1"))
                    return "带 WHERE 的 UPDATE 被误判为破坏性";
            }
            else
            {
                return "写模式带 WHERE 的 UPDATE 被拒绝：" + error;
            }
            if (SqlGuard.Validate("SET PASSWORD FOR u = 'x'", true, out error))
                return "SET PASSWORD 没有被禁用";
            if (SqlGuard.Validate("SET\nGLOBAL max_connections=1", true, out error))
                return "换行拆分的 SET GLOBAL 没有被禁用";
            if (SqlGuard.Validate("SET sql_log_bin=0", true, out error))
                return "SET sql_log_bin 没有被禁用";
            if (SqlGuard.Validate("RENAME USER a TO b", true, out error))
                return "RENAME USER 没有被禁用";
            if (!SqlGuard.Validate("DELETE FROM t WHERE id=1", true, out error))
                return "写模式 DELETE 被拒绝：" + error;
            return "";
        }

        private static string CheckRedisGuard()
        {
            string error;
            string[] parts = RedisCommandLine.Parse("SET \"a b\" \"c\\\"d\"", out error);
            if (parts == null || parts.Length != 3 || parts[1] != "a b" || parts[2] != "c\"d")
                return "Redis 命令解析失败：" + (error.Length > 0 ? error : "参数不正确");
            if (RedisCommandLine.Parse("", out error) != null) return "空 Redis 命令没有被拒绝";
            if (RedisCommandLine.Parse("GET\r\nSET x y", out error) != null) return "多行 Redis 命令没有被拒绝";
            if (RedisCommandLine.Parse("GET 'unclosed", out error) != null) return "未闭合引号没有被拒绝";

            string command;
            if (!RedisCommandGuard.Validate(new string[] { "GET", "k" }, false, out command, out error))
                return "只读 GET 被拒绝：" + error;
            if (RedisCommandGuard.Validate(new string[] { "SET", "k", "v" }, false, out command, out error))
                return "只读模式 SET 没有被拒绝";
            if (!RedisCommandGuard.Validate(new string[] { "SET", "k", "v" }, true, out command, out error))
                return "写模式 SET 被拒绝：" + error;
            if (!RedisCommandGuard.IsDestructive("SET")) return "SET 没有被标记为破坏性";
            if (RedisCommandGuard.Validate(new string[] { "FLUSHALL" }, true, out command, out error))
                return "FLUSHALL 没有被禁用";
            if (RedisCommandGuard.Validate(new string[] { "CONFIG", "GET", "*" }, true, out command, out error))
                return "CONFIG 没有被禁用";
            if (RedisCommandGuard.Validate(new string[] { "AUTH", "x" }, true, out command, out error))
                return "AUTH 没有被禁用";
            if (!RedisCommandGuard.Validate(new string[] { "DEL", "k" }, true, out command, out error))
                return "写模式 DEL 被拒绝：" + error;

            string escaped = RedisCommandGuard.EscapePattern("a*b?[c]\\d");
            if (escaped != "a\\*b\\?\\[c\\]\\\\d") return "Redis glob 转义不正确：" + escaped;

            string joined = RedisCommandLine.Join(new string[] { "SET", "a b", "c\"d" });
            string[] round = RedisCommandLine.Parse(joined, out error);
            if (round == null || round.Length != 3 || round[1] != "a b" || round[2] != "c\"d")
                return "Redis 命令拼接/回读失败：" + joined;
            return "";
        }

        private static string CheckCrc16()
        {
            if (RedisClient.ComputeSlot("foo") != 12182)
                return "CRC16 foo 槽位不正确：" + RedisClient.ComputeSlot("foo");
            if (RedisClient.ComputeSlot("bar") != 5061)
                return "CRC16 bar 槽位不正确：" + RedisClient.ComputeSlot("bar");
            if (RedisClient.ComputeSlot("{user1000}.following") != RedisClient.ComputeSlot("user1000"))
                return "CRC16 hash tag 没有生效";
            return "";
        }

        private static string CheckCsv()
        {
            DataTable table = new DataTable();
            table.Columns.Add("id", typeof(int));
            table.Columns.Add("name");
            DataRow row = table.NewRow();
            row[0] = 1;
            row[1] = "a,b";
            table.Rows.Add(row);
            row = table.NewRow();
            row[0] = 2;
            row[1] = "line1\nline2";
            table.Rows.Add(row);
            string csv = DataGridExport.ToCsv(table, true);
            if (csv.IndexOf("id,name", StringComparison.Ordinal) < 0)
                return "CSV 表头不正确";
            if (csv.IndexOf("\"a,b\"", StringComparison.Ordinal) < 0)
                return "CSV 逗号字段没有加引号";
            if (csv.IndexOf("\"line1\nline2\"", StringComparison.Ordinal) < 0)
                return "CSV 换行字段没有加引号";
            return "";
        }

        private static string CheckDpapi()
        {
            string original = "db-selftest-密码-123";
            string protectedText = Dpapi.Protect(original);
            if (string.IsNullOrEmpty(protectedText)) return "DPAPI Protect 返回空";
            if (protectedText == original) return "DPAPI 没有加密";
            if (Dpapi.Unprotect(protectedText) != original) return "DPAPI 解密结果不一致";
            return "";
        }

        // 连接配置必须能完整地序列化再读回（曾因 JavaScriptSerializer 返回 ArrayList 丢过配置）。
        private static string CheckConnectionStore()
        {
            DatabaseConnectionProfile profile = new DatabaseConnectionProfile();
            profile.Name = "store-selftest";
            profile.Host = "127.0.0.1";
            profile.Port = 3307;
            profile.User = "tester";
            profile.Password = "p@ss-中文";
            profile.Environment = DatabaseEnvironment.Test;
            profile.AllowWrites = false;

            List<DatabaseConnectionProfile> source = new List<DatabaseConnectionProfile>();
            source.Add(profile);
            string json = DatabaseConnectionStore.Serialize(source, profile.Id);

            List<DatabaseConnectionProfile> loaded = new List<DatabaseConnectionProfile>();
            string lastId;
            string error;
            if (!DatabaseConnectionStore.Deserialize(json, loaded, out lastId, out error))
                return "连接配置反序列化失败：" + error;
            if (loaded.Count != 1)
                return "连接配置反序列化后数量不正确：" + loaded.Count;
            DatabaseConnectionProfile copy = loaded[0];
            if (copy.Name != profile.Name || copy.Host != profile.Host || copy.Port != profile.Port ||
                copy.User != profile.User || copy.Password != profile.Password ||
                copy.Environment != profile.Environment || copy.AllowWrites)
            {
                return "连接配置反序列化字段不一致";
            }
            if (lastId != profile.Id)
                return "连接配置 lastProfileId 不一致";
            return "";
        }

        // AI 返回的是自然语言/代码块，必须能稳定地取出单条 SQL，且写语句要交给 SqlGuard 拦下。
        private static string CheckAiSqlExtraction()
        {
            string note;
            string sql = DatabaseAiQuery.ExtractSql("```sql\nSELECT * FROM `app` LIMIT 10;\n```", out note);
            if (sql != "SELECT * FROM `app` LIMIT 10")
                return "AI SQL 代码块解析失败：" + sql;

            sql = DatabaseAiQuery.ExtractSql("SQL: SELECT 1", out note);
            if (sql != "SELECT 1") return "AI SQL 行内前缀解析失败：" + sql;

            sql = DatabaseAiQuery.ExtractSql("```\nSELECT COUNT(*) FROM `app`\n```", out note);
            if (sql != "SELECT COUNT(*) FROM `app`")
                return "AI SQL 无语言标记代码块解析失败：" + sql;

            sql = DatabaseAiQuery.ExtractSql("-- 表结构里没有相关字段", out note);
            if (sql.Length != 0) return "AI 拒绝语句没有被识别为无 SQL：" + sql;

            sql = DatabaseAiQuery.ExtractSql("DELETE FROM `app`", out note);
            string error;
            if (sql.Length == 0) return "AI 写语句没有被解析出来";
            if (SqlGuard.ValidateReadOnly(sql, out error))
                return "AI 写语句没有被只读闸门拦下：" + sql;
            return "";
        }

        // 中文关键词要能命中表/字段注释，并且“卡卷”这种错字要能映射到注释里的“卡券”。
        private static string CheckAiSchemaKeywords()
        {
            List<string> keywords = DatabaseAiQuery.ExtractKeywords(
                "帮我查下15219896454这个手机号有多少张卡卷");
            if (!keywords.Contains("手机号")) return "AI 关键词没有提取出“手机号”：" + string.Join("、", keywords.ToArray());
            if (!keywords.Contains("卡券")) return "AI 关键词没有把“卡卷”映射成“卡券”：" + string.Join("、", keywords.ToArray());
            if (keywords.Contains("多少")) return "AI 关键词没有过滤虚词“多少”：" + string.Join("、", keywords.ToArray());

            int cardScore = DatabaseAiQuery.MatchScore(
                "t_card 券生产管理表 card_serial_num 卡券序列号 mobile 手机号码", keywords);
            int orderScore = DatabaseAiQuery.MatchScore(
                "fsl_order 电影订单 mobile 下单手机号", keywords);
            if (cardScore <= orderScore)
                return "AI 表相关度排序错误：卡券表 " + cardScore + " 不高于订单表 " + orderScore;

            string problem;
            if (DatabaseAiQuery.ValidateGenerated(
                "SELECT 'card_id' AS '卡券号', `mobile` FROM `t_card`", out problem))
                return "AI 把字符串当列名的 SQL 没有被识别出来";
            if (!DatabaseAiQuery.ValidateGenerated(
                "SELECT '电影卡' AS `卡券类型`, `card_id` AS `卡券号` FROM `t_card`", out problem))
                return "合法的字面量分类列被误判：" + problem;
            return "";
        }

        // 格式化只动空白和关键字大小写：子句换行、关键字转小写，字符串和标识符原样保留，
        // 并且重复格式化结果不变。
        private static string CheckSqlFormatter()
        {
            string source = "SELECT '卡片' AS `卡券类型`, b FROM t WHERE a=1 AND b='SELECT FROM' ORDER BY a LIMIT 10";
            string formatted = SqlFormatter.Format(source);
            if (!formatted.StartsWith("select\r\n", StringComparison.Ordinal))
                return "SQL 格式化没有把 SELECT 转成小写：" + formatted;
            if (formatted.IndexOf("\r\nfrom ", StringComparison.Ordinal) < 0)
                return "SQL 格式化没有把 FROM 换行：" + formatted;
            if (formatted.IndexOf("\r\nwhere", StringComparison.Ordinal) < 0)
                return "SQL 格式化没有把 WHERE 换行：" + formatted;
            if (formatted.IndexOf("\r\n  and ", StringComparison.Ordinal) < 0)
                return "SQL 格式化没有把 AND 换行：" + formatted;
            if (formatted.IndexOf("'SELECT FROM'", StringComparison.Ordinal) < 0)
                return "SQL 格式化改坏了字符串常量：" + formatted;
            if (formatted.IndexOf("`卡券类型`", StringComparison.Ordinal) < 0)
                return "SQL 格式化改坏了反引号标识符：" + formatted;

            string again = SqlFormatter.Format(formatted);
            if (!string.Equals(formatted, again, StringComparison.Ordinal))
                return "SQL 格式化不是幂等的：\r\n" + formatted + "\r\n---\r\n" + again;

            string statement;
            string error;
            if (!SqlGuard.Normalize(formatted, out statement, out error))
                return "SQL 格式化后无法解析：" + error;
            return "";
        }

        // HAR 响应体：微信开发者工具会把明文标成 base64（解出来是乱码），
        // 二进制图片不能当文本显示，真正的 base64 文本 JSON 仍要能正常解码。
        private static string CheckHarContent()
        {
            Dictionary<string, object> plain = new Dictionary<string, object>();
            plain["mimeType"] = "text/plain";
            plain["encoding"] = "base64";
            plain["text"] = "something wrong with reqeust handler";
            string shown = HarParser.ReadContentForTest(plain);
            if (shown != "something wrong with reqeust handler")
                return "HAR 明文被误标 base64 时显示成了乱码：" + shown;

            Dictionary<string, object> json = new Dictionary<string, object>();
            json["mimeType"] = "application/json";
            json["encoding"] = "base64";
            json["text"] = Convert.ToBase64String(Encoding.UTF8.GetBytes("{\"rc\":200}"));
            shown = HarParser.ReadContentForTest(json);
            if (shown != "{\"rc\":200}")
                return "HAR 里真正 base64 的 JSON 没有正确解码：" + shown;

            Dictionary<string, object> image = new Dictionary<string, object>();
            image["mimeType"] = "image/jpeg";
            image["encoding"] = "base64";
            image["text"] = Convert.ToBase64String(new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46 });
            shown = HarParser.ReadContentForTest(image);
            if (shown.IndexOf("二进制响应") < 0 || shown.IndexOf("图片") < 0)
                return "HAR 里的图片响应没有被识别成二进制：" + shown;
            return "";
        }

        private static string CheckProviders()
        {
            if (DatabaseProviders.Find(DatabaseKinds.MySql) == null) return "MySQL Provider 未注册";
            if (DatabaseProviders.Find(DatabaseKinds.Redis) == null) return "Redis Provider 未注册";

            Type connectionType = Type.GetType("MySqlConnector.MySqlConnection, MySqlConnector");
            if (connectionType == null) return "MySqlConnector 程序集没有加载";

            // System.Memory 在部分代码路径（读大字段 / 宽结果集）会用到 Vector 类型，
            // 漏打包时 SQL 能解析、真正取数据才报“找不到 System.Numerics.Vectors”。这里提前加载一次。
            try
            {
                Assembly.Load("System.Numerics.Vectors, Version=4.1.4.0, Culture=neutral, PublicKeyToken=b03f5f7f11d50a3a");
            }
            catch (Exception ex)
            {
                return "System.Numerics.Vectors 程序集没有加载：" + ex.Message;
            }
            try
            {
                IDisposable instance = Activator.CreateInstance(connectionType) as IDisposable;
                if (instance != null) instance.Dispose();
            }
            catch (Exception ex)
            {
                return "MySqlConnector 类型初始化失败：" + ex.Message;
            }

            DatabaseConnectionProfile profile = new DatabaseConnectionProfile();
            profile.Name = "selftest";
            profile.SetOption("charset", "utf8mb4");
            DatabaseConnectionProfile copy = profile.Clone();
            if (copy.Name != "selftest" || copy.Option("charset", "") != "utf8mb4")
                return "连接配置 Clone 失败";
            return "";
        }
    }
}
