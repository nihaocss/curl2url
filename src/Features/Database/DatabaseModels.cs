using System;
using System.Collections.Generic;
using System.Data;

namespace Curl2Url
{
    internal static class DatabaseKinds
    {
        public const string MySql = "mysql";
        public const string Redis = "redis";

        public static string DisplayName(string kind)
        {
            if (kind == Redis) return "Redis";
            return "MySQL";
        }
    }

    internal static class DatabaseEnvironment
    {
        public const string Local = "本地";
        public const string Test = "测试";
        public const string Staging = "预发";
        public const string Production = "生产";

        public static readonly string[] All = { Local, Test, Staging, Production };
    }

    // 连接配置是纯数据对象；密码只存在内存里，落盘由 DatabaseConnectionStore 做 DPAPI 加密。
    internal sealed class DatabaseConnectionProfile
    {
        public string Id = Guid.NewGuid().ToString("N");
        public string Name = "";
        public string Kind = DatabaseKinds.MySql;
        public string Host = "127.0.0.1";
        public int Port = 3306;
        public string User = "";
        public string Password = "";
        public string Database = "";
        public int RedisDatabase = 0;
        public string SslMode = "Preferred";
        public bool RedisTls = false;
        public bool AllowInvalidCert = false;
        public string Environment = DatabaseEnvironment.Local;
        public bool AllowWrites = false;
        public DateTime LastUsedUtc = DateTime.MinValue;
        public Dictionary<string, string> Options = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        public DatabaseConnectionProfile Clone()
        {
            DatabaseConnectionProfile copy = new DatabaseConnectionProfile();
            copy.Id = Id;
            copy.Name = Name;
            copy.Kind = Kind;
            copy.Host = Host;
            copy.Port = Port;
            copy.User = User;
            copy.Password = Password;
            copy.Database = Database;
            copy.RedisDatabase = RedisDatabase;
            copy.SslMode = SslMode;
            copy.RedisTls = RedisTls;
            copy.AllowInvalidCert = AllowInvalidCert;
            copy.Environment = Environment;
            copy.AllowWrites = AllowWrites;
            copy.LastUsedUtc = LastUsedUtc;
            copy.Options = new Dictionary<string, string>(Options, StringComparer.OrdinalIgnoreCase);
            return copy;
        }

        public string Endpoint
        {
            get { return Host + ":" + Port.ToString(); }
        }

        public string DisplayText
        {
            get
            {
                string name = string.IsNullOrEmpty(Name) ? "(未命名)" : Name;
                return name + "  ·  " + DatabaseKinds.DisplayName(Kind) + "  ·  " + Endpoint;
            }
        }

        public string Option(string key, string fallback)
        {
            string value;
            if (Options != null && Options.TryGetValue(key, out value) && value != null) return value;
            return fallback;
        }

        public void SetOption(string key, string value)
        {
            if (Options == null) Options = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            Options[key] = value ?? "";
        }
    }

    internal sealed class DbTableRef
    {
        public string Database = "";
        public string Table = "";
        public bool IsView = false;

        public string FullName
        {
            get
            {
                if (string.IsNullOrEmpty(Database)) return Table;
                return Database + "." + Table;
            }
        }
    }

    internal sealed class DbColumnInfo
    {
        public string Name = "";
        public string DataType = "";
        public string ColumnType = "";
        public bool Nullable = true;
        public string Key = "";
        public string Extra = "";
        public string DefaultValue = "";
        public string Comment = "";
        public int Ordinal = 0;

        public bool IsPrimaryKey
        {
            get { return string.Equals(Key, "PRI", StringComparison.OrdinalIgnoreCase); }
        }

        public bool IsAutoIncrement
        {
            get { return Extra != null && Extra.IndexOf("auto_increment", StringComparison.OrdinalIgnoreCase) >= 0; }
        }

        public bool IsGenerated
        {
            get
            {
                return Extra != null &&
                    (Extra.IndexOf("generated", StringComparison.OrdinalIgnoreCase) >= 0 ||
                     Extra.IndexOf("virtual", StringComparison.OrdinalIgnoreCase) >= 0);
            }
        }

        public bool IsBinary
        {
            get
            {
                string t = (DataType ?? "").ToLowerInvariant();
                return t == "binary" || t == "varbinary" || t == "tinyblob" ||
                       t == "blob" || t == "mediumblob" || t == "longblob" ||
                       t == "bit" || t == "geometry";
            }
        }

        public string Summary
        {
            get
            {
                string text = Name + "  " + ColumnType;
                if (!Nullable) text += "  NOT NULL";
                if (IsPrimaryKey) text += "  PK";
                if (IsAutoIncrement) text += "  AUTO_INCREMENT";
                if (IsGenerated) text += "  GENERATED";
                return text;
            }
        }
    }

    // Query 结果统一用 DataTable 承载：查询页只读，表数据页在可写连接上允许编辑。
    internal sealed class DbQueryResult
    {
        public DataTable Table = new DataTable();
        public List<DbColumnInfo> Columns = new List<DbColumnInfo>();
        public bool HasResultSet;
        public int AffectedRows;
        public long ElapsedMs;
        public bool Truncated;
        public string Message = "";
    }

    internal sealed class DbTableData
    {
        public DbTableRef Table;
        public DataTable Data = new DataTable();
        public List<DbColumnInfo> Columns = new List<DbColumnInfo>();
        public List<string> PrimaryKeys = new List<string>();
        public bool Editable;
        public bool Truncated;
        public string Message = "";
    }

    internal sealed class DbSaveResult
    {
        public bool Success;
        public int Affected;
        public string Error = "";
    }

    // 大字段 / 二进制不把完整内容塞进 UI，避免一条 SELECT 把内存打满。
    internal sealed class DbBinaryValue
    {
        public byte[] Preview;
        public long TotalLength;

        public DbBinaryValue(byte[] preview, long totalLength)
        {
            Preview = preview ?? new byte[0];
            TotalLength = totalLength;
        }

        public string HexPreview(int maxBytes)
        {
            int take = Math.Min(Preview.Length, Math.Max(0, maxBytes));
            System.Text.StringBuilder sb = new System.Text.StringBuilder(take * 2);
            for (int i = 0; i < take; i++) sb.Append(Preview[i].ToString("x2"));
            if (take < Preview.Length || TotalLength > Preview.Length) sb.Append("...");
            return sb.ToString();
        }

        public override string ToString()
        {
            return "<BLOB " + TotalLength + " bytes>";
        }
    }

    internal sealed class DbTruncatedText
    {
        public string Preview = "";
        public long TotalLength;

        public DbTruncatedText(string preview, long totalLength)
        {
            Preview = preview ?? "";
            TotalLength = totalLength;
        }

        public override string ToString()
        {
            return Preview + "...(截断，共 " + TotalLength + " 字符)";
        }
    }

    internal sealed class DbTableSearchRow
    {
        public string Database = "";
        public string Table = "";
        public string Type = "";
    }

    internal sealed class RedisKeyInfo
    {
        public string Key = "";
        public string Type = "";
        public long Ttl = -2;
        public string Node = "";
    }

    internal sealed class RedisScanPage
    {
        public List<RedisKeyInfo> Keys = new List<RedisKeyInfo>();
        public bool Done;
        public long ScannedNodes;
    }

    internal sealed class RedisEntry
    {
        public long Index;
        public string Field = "";
        public string Value = "";
        public string Score = "";
    }

    internal sealed class RedisValueData
    {
        public string Key = "";
        public string Type = "";
        public long Ttl = -2;
        public long Length = -1;
        public string Scalar = "";
        public bool Truncated;
        public List<RedisEntry> Entries = new List<RedisEntry>();
    }

    internal sealed class RedisServerSummary
    {
        public bool IsCluster;
        public string Version = "";
        public List<string> Masters = new List<string>();
        public string ClusterState = "";
        public long DbSize = -1;

        public string DisplayText
        {
            get
            {
                string mode = IsCluster ? "Cluster " + Masters.Count + " 主节点" : "单机";
                string version = string.IsNullOrEmpty(Version) ? "Redis" : "Redis " + Version;
                return version + " · " + mode;
            }
        }
    }

    internal sealed class LogClue
    {
        public string Label = "";
        public string Value = "";

        public override string ToString()
        {
            return Label + "：" + Value;
        }
    }
}
