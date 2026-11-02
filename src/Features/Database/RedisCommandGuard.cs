using System;
using System.Collections.Generic;
using System.Text;

namespace Curl2Url
{
    // Redis 命令白名单 / 黑名单。只读连接只允许读命令；写连接仍然禁用会拖垮实例或绕过审计的命令。
    internal static class RedisCommandGuard
    {
        private static readonly HashSet<string> ReadOnly = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "PING", "ECHO", "INFO", "TIME", "DBSIZE", "RANDOMKEY",
            "GET", "MGET", "STRLEN", "GETRANGE", "EXISTS", "TYPE", "TTL", "PTTL",
            "EXPIRETIME", "PEXPIRETIME",
            "HGET", "HMGET", "HGETALL", "HKEYS", "HVALS", "HLEN", "HEXISTS", "HSCAN", "HRANDFIELD",
            "LRANGE", "LLEN", "LINDEX", "LPOS",
            "SCARD", "SMEMBERS", "SISMEMBER", "SMISMEMBER", "SRANDMEMBER", "SSCAN",
            "SINTER", "SUNION", "SDIFF", "SINTERCARD",
            "ZRANGE", "ZRANGEBYSCORE", "ZREVRANGE", "ZREVRANGEBYSCORE", "ZCARD", "ZSCORE",
            "ZMSCORE", "ZCOUNT", "ZRANK", "ZREVRANK", "ZSCAN", "ZRANDMEMBER", "ZDIFF",
            "ZINTER", "ZUNION", "ZINTERCARD", "ZLEXCOUNT",
            "XLEN", "XRANGE", "XREVRANGE", "XPENDING", "XINFO",
            "MEMORY", "OBJECT", "LCS", "SORT_RO",
            "BITCOUNT", "BITPOS", "GETBIT", "BITFIELD_RO",
            "PFCOUNT", "GEOPOS", "GEODIST", "GEOHASH", "GEOSEARCH"
        };

        private static readonly HashSet<string> AlwaysBlocked = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            // 清库 / 服务端管理 / 配置
            "FLUSHALL", "FLUSHDB", "CONFIG", "SHUTDOWN", "DEBUG", "MODULE", "ACL", "CLIENT",
            "SAVE", "BGSAVE", "BGREWRITEAOF", "REPLICAOF", "SLAVEOF", "MIGRATE",
            // 脚本 / 函数
            "EVAL", "EVALSHA", "EVAL_RO", "EVALSHA_RO", "FCALL", "FCALL_RO", "SCRIPT", "FUNCTION",
            // 会阻塞连接或持续推送
            "MONITOR", "SUBSCRIBE", "PSUBSCRIBE", "SSUBSCRIBE", "PSYNC", "SYNC",
            "BLPOP", "BRPOP", "BLMOVE", "BRPOPLPUSH", "BZPOPMIN", "BZPOPMAX", "BZMPOP", "BLMPOP",
            "XREAD", "XREADGROUP", "WAIT",
            // 连接状态 / 事务 / 切库 / 拓扑
            "AUTH", "HELLO", "QUIT", "RESET", "SELECT", "SWAPDB", "CLUSTER",
            "MULTI", "EXEC", "DISCARD", "WATCH", "UNWATCH",
            // 大键扫描 / 覆盖式恢复
            "KEYS", "SCAN", "RESTORE"
        };

        private static readonly HashSet<string> Destructive = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "DEL", "UNLINK", "RENAME", "RENAMENX", "COPY", "RESTORE",
            "EXPIRE", "PEXPIRE", "EXPIREAT", "PEXPIREAT", "PERSIST", "GETDEL", "GETEX",
            "SET", "SETNX", "SETEX", "PSETEX", "MSET", "MSETNX", "GETSET", "SETRANGE",
            "LSET", "LTRIM", "LREM", "LMPOP",
            "SREM", "SPOP", "SMOVE", "SINTERSTORE", "SUNIONSTORE", "SDIFFSTORE",
            "HDEL", "HSET", "HSETNX", "HMSET", "HINCRBY", "HINCRBYFLOAT",
            "ZADD", "ZINCRBY", "ZREM", "ZREMRANGEBYRANK", "ZREMRANGEBYSCORE", "ZREMRANGEBYLEX",
            "ZPOPMIN", "ZPOPMAX", "ZMPOP", "ZDIFFSTORE", "ZINTERSTORE", "ZUNIONSTORE",
            "XADD", "XDEL", "XTRIM", "XGROUP", "XSETID", "XCLAIM", "XAUTOCLAIM",
            "PFMERGE", "BITOP", "SETBIT", "GEOSEARCHSTORE",
            "LINSERT", "SORT"
        };

        public static bool Validate(IList<string> parts, bool allowWrites, out string command, out string error)
        {
            command = "";
            error = "";
            if (parts == null || parts.Count == 0)
            {
                error = "请输入 Redis 命令。";
                return false;
            }
            command = (parts[0] ?? "").Trim().ToUpperInvariant();
            if (command.Length == 0)
            {
                error = "命令不能为空。";
                return false;
            }
            if (AlwaysBlocked.Contains(command))
            {
                error = "出于安全考虑，命令 " + command + " 已在工具中禁用。";
                return false;
            }
            if (!allowWrites)
            {
                if (!ReadOnly.Contains(command))
                {
                    error = "当前连接是只读模式，只允许只读命令。需要写入请编辑连接并显式开启写权限。";
                    return false;
                }
                return true;
            }
            return true;
        }

        public static bool IsReadOnlyCommand(string command)
        {
            return !string.IsNullOrEmpty(command) && ReadOnly.Contains(command);
        }

        public static bool IsDestructive(string command)
        {
            return !string.IsNullOrEmpty(command) && Destructive.Contains(command);
        }

        public static bool IsAlwaysBlocked(string command)
        {
            return !string.IsNullOrEmpty(command) && AlwaysBlocked.Contains(command);
        }

        public static string EscapePattern(string value)
        {
            if (string.IsNullOrEmpty(value)) return "*";
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < value.Length; i++)
            {
                char c = value[i];
                if (c == '\\' || c == '*' || c == '?' || c == '[' || c == ']')
                    sb.Append('\\');
                sb.Append(c);
            }
            return sb.ToString();
        }
    }

    internal static class RedisCommandLine
    {
        // 解析一条 redis-cli 风格命令：支持单引号、双引号、反斜杠转义。
        public static string[] Parse(string line, out string error)
        {
            error = "";
            if (line == null) line = "";
            if (line.IndexOf('\r') >= 0 || line.IndexOf('\n') >= 0)
            {
                error = "一次只能执行一条 Redis 命令。";
                return null;
            }
            List<string> parts = new List<string>();
            StringBuilder current = new StringBuilder();
            bool started = false;
            char quote = '\0';
            for (int i = 0; i < line.Length; i++)
            {
                char c = line[i];
                if (quote == '\0' && char.IsWhiteSpace(c))
                {
                    if (started || current.Length > 0)
                    {
                        parts.Add(current.ToString());
                        current.Length = 0;
                        started = false;
                    }
                    continue;
                }
                if (c == '\\' && quote != '\'')
                {
                    if (i + 1 >= line.Length)
                    {
                        error = "命令末尾的反斜杠没有转义对象。";
                        return null;
                    }
                    i++;
                    char escaped = line[i];
                    if (escaped == 'n') current.Append('\n');
                    else if (escaped == 'r') current.Append('\r');
                    else if (escaped == 't') current.Append('\t');
                    else current.Append(escaped);
                    started = true;
                    continue;
                }
                if (quote != '\0')
                {
                    if (c == quote)
                    {
                        quote = '\0';
                        started = true;
                    }
                    else
                    {
                        current.Append(c);
                        started = true;
                    }
                    continue;
                }
                if (c == '\'' || c == '"')
                {
                    quote = c;
                    started = true;
                    continue;
                }
                current.Append(c);
                started = true;
            }
            if (quote != '\0')
            {
                error = "引号没有闭合。";
                return null;
            }
            if (started || current.Length > 0)
            {
                parts.Add(current.ToString());
            }
            if (parts.Count == 0)
            {
                error = "请输入 Redis 命令。";
                return null;
            }
            return parts.ToArray();
        }

        public static string Join(IList<string> parts)
        {
            if (parts == null) return "";
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < parts.Count; i++)
            {
                if (i > 0) sb.Append(' ');
                string part = parts[i] ?? "";
                if (part.Length == 0 || part.IndexOf(' ') >= 0 || part.IndexOf('\t') >= 0 ||
                    part.IndexOf('"') >= 0 || part.IndexOf('\'') >= 0)
                {
                    sb.Append('"').Append(part.Replace("\\", "\\\\").Replace("\"", "\\\"")).Append('"');
                }
                else
                {
                    sb.Append(part);
                }
            }
            return sb.ToString();
        }
    }
}
