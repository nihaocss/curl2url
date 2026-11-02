using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text;
using System.Threading;

namespace Curl2Url
{
    internal enum RedisReplyKind
    {
        Simple,
        Error,
        Integer,
        Bulk,
        Array,
        Nil
    }

    internal sealed class RedisReply
    {
        public RedisReplyKind Kind;
        public string Text = "";
        public byte[] Bytes;
        public long Integer;
        public List<RedisReply> Items = new List<RedisReply>();

        public bool IsError
        {
            get { return Kind == RedisReplyKind.Error; }
        }

        public string AsText()
        {
            if (Kind == RedisReplyKind.Integer) return Integer.ToString(CultureInfo.InvariantCulture);
            if (Kind == RedisReplyKind.Nil) return "";
            return Text ?? "";
        }

        public string Format(int maxChars)
        {
            StringBuilder sb = new StringBuilder();
            AppendFormat(sb, this, 0, maxChars);
            if (sb.Length > maxChars)
            {
                return sb.ToString(0, maxChars) + "\r\n…（输出已截断）";
            }
            return sb.ToString();
        }

        private static void AppendFormat(StringBuilder sb, RedisReply reply, int depth, int maxChars)
        {
            if (sb.Length >= maxChars || reply == null || depth > 24) return;
            switch (reply.Kind)
            {
                case RedisReplyKind.Simple:
                    Append(sb, reply.Text, maxChars);
                    break;
                case RedisReplyKind.Error:
                    Append(sb, "(error) " + reply.Text, maxChars);
                    break;
                case RedisReplyKind.Integer:
                    Append(sb, "(integer) " + reply.Integer, maxChars);
                    break;
                case RedisReplyKind.Nil:
                    Append(sb, "(nil)", maxChars);
                    break;
                case RedisReplyKind.Bulk:
                    Append(sb, reply.Text, maxChars);
                    break;
                case RedisReplyKind.Array:
                    for (int i = 0; i < reply.Items.Count; i++)
                    {
                        if (sb.Length >= maxChars) break;
                        sb.Append(new string(' ', depth * 2));
                        if (reply.Items[i] != null && reply.Items[i].Kind == RedisReplyKind.Array)
                        {
                            sb.Append(i + 1).Append(") ");
                            AppendFormat(sb, reply.Items[i], depth + 1, maxChars);
                        }
                        else
                        {
                            sb.Append(i + 1).Append(") ");
                            AppendFormat(sb, reply.Items[i], depth + 1, maxChars);
                            sb.Append("\r\n");
                        }
                    }
                    break;
            }
        }

        private static void Append(StringBuilder sb, string text, int maxChars)
        {
            if (string.IsNullOrEmpty(text)) return;
            int remaining = maxChars - sb.Length;
            if (remaining <= 0) return;
            if (text.Length <= remaining) sb.Append(text);
            else sb.Append(text, 0, remaining);
        }
    }

    internal static class RedisProtocol
    {
        public const int MaxBulkBytes = 32 * 1024 * 1024;
        public const int MaxLineBytes = 1024 * 1024;

        public static void WriteCommand(Stream stream, IList<string> parts)
        {
            using (MemoryStream buffer = new MemoryStream())
            {
                WriteAscii(buffer, "*" + parts.Count + "\r\n");
                for (int i = 0; i < parts.Count; i++)
                {
                    byte[] data = Encoding.UTF8.GetBytes(parts[i] ?? "");
                    WriteAscii(buffer, "$" + data.Length + "\r\n");
                    buffer.Write(data, 0, data.Length);
                    buffer.WriteByte((byte)'\r');
                    buffer.WriteByte((byte)'\n');
                }
                byte[] payload = buffer.ToArray();
                stream.Write(payload, 0, payload.Length);
                stream.Flush();
            }
        }

        private static void WriteAscii(Stream stream, string text)
        {
            byte[] bytes = Encoding.ASCII.GetBytes(text);
            stream.Write(bytes, 0, bytes.Length);
        }
    }

    internal sealed class RedisNodeConnection : IDisposable
    {
        public readonly string Host;
        public readonly int Port;
        private readonly bool _tls;
        private readonly bool _allowInvalidCert;
        private readonly object _io = new object();
        private TcpClient _tcp;
        private Stream _stream;
        private byte[] _buffer;
        private int _offset;
        private int _count;
        private bool _closed;

        public RedisNodeConnection(string host, int port, bool tls, bool allowInvalidCert)
        {
            Host = host;
            Port = port;
            _tls = tls;
            _allowInvalidCert = allowInvalidCert;
        }

        public string Address
        {
            get { return RedisClient.FormatAddress(Host, Port); }
        }

        public void Connect()
        {
            if (_stream != null) return;
            TcpClient tcp = new TcpClient();
            tcp.NoDelay = true;
            tcp.ReceiveTimeout = 15000;
            tcp.SendTimeout = 10000;
            try
            {
                IAsyncResult ar = tcp.BeginConnect(Host, Port, null, null);
                if (!ar.AsyncWaitHandle.WaitOne(8000))
                {
                    tcp.Close();
                    throw new TimeoutException("连接超时（8 秒）。");
                }
                tcp.EndConnect(ar);
                Stream stream = tcp.GetStream();
                if (_tls)
                {
                    SslStream ssl = new SslStream(stream, false, delegate(
                        object sender, System.Security.Cryptography.X509Certificates.X509Certificate certificate,
                        System.Security.Cryptography.X509Certificates.X509Chain chain, SslPolicyErrors errors)
                    {
                        return _allowInvalidCert || errors == SslPolicyErrors.None;
                    });
                    ssl.AuthenticateAsClient(Host);
                    stream = ssl;
                }
                _tcp = tcp;
                _stream = stream;
                _buffer = new byte[8192];
                _offset = 0;
                _count = 0;
            }
            catch (Exception)
            {
                try { tcp.Close(); }
                catch (Exception) { }
                throw;
            }
        }

        public RedisReply Send(IList<string> parts)
        {
            lock (_io)
            {
                EnsureConnected();
                RedisProtocol.WriteCommand(_stream, parts);
                return ReadReply(0);
            }
        }

        public List<RedisReply> SendPipeline(List<IList<string>> commands)
        {
            lock (_io)
            {
                EnsureConnected();
                for (int i = 0; i < commands.Count; i++)
                {
                    RedisProtocol.WriteCommand(_stream, commands[i]);
                }
                List<RedisReply> replies = new List<RedisReply>(commands.Count);
                for (int i = 0; i < commands.Count; i++)
                {
                    replies.Add(ReadReply(0));
                }
                return replies;
            }
        }

        private void EnsureConnected()
        {
            if (_closed) throw new ObjectDisposedException("RedisNodeConnection");
            if (_stream == null) Connect();
        }

        private RedisReply ReadReply(int depth)
        {
            if (depth > 32) throw new IOException("Redis 回复嵌套过深。");
            int type = ReadByte();
            if (type < 0) throw new IOException("Redis 连接已关闭。");
            switch ((char)type)
            {
                case '+':
                    return MakeSimple(RedisReplyKind.Simple, ReadLine());
                case '-':
                    return MakeSimple(RedisReplyKind.Error, ReadLine());
                case ':':
                    {
                        RedisReply reply = new RedisReply();
                        reply.Kind = RedisReplyKind.Integer;
                        long value;
                        long.TryParse(ReadLine(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
                        reply.Integer = value;
                        reply.Text = value.ToString(CultureInfo.InvariantCulture);
                        return reply;
                    }
                case '$':
                    {
                        int length;
                        if (!int.TryParse(ReadLine(), NumberStyles.Integer, CultureInfo.InvariantCulture, out length))
                            throw new IOException("Redis bulk 长度格式错误。");
                        if (length < 0)
                        {
                            RedisReply nil = new RedisReply();
                            nil.Kind = RedisReplyKind.Nil;
                            return nil;
                        }
                        if (length > RedisProtocol.MaxBulkBytes)
                            throw new IOException("单个 Redis 回复超过 " +
                                (RedisProtocol.MaxBulkBytes / 1024 / 1024) + " MB，已拒绝。");
                        byte[] data = ReadBytes(length);
                        RedisReply reply = new RedisReply();
                        reply.Kind = RedisReplyKind.Bulk;
                        reply.Bytes = data;
                        reply.Text = DecodeText(data);
                        return reply;
                    }
                case '*':
                    {
                        int count;
                        if (!int.TryParse(ReadLine(), NumberStyles.Integer, CultureInfo.InvariantCulture, out count))
                            throw new IOException("Redis 数组长度格式错误。");
                        RedisReply reply = new RedisReply();
                        reply.Kind = RedisReplyKind.Array;
                        if (count < 0)
                        {
                            reply.Kind = RedisReplyKind.Nil;
                            return reply;
                        }
                        for (int i = 0; i < count; i++)
                        {
                            reply.Items.Add(ReadReply(depth + 1));
                        }
                        return reply;
                    }
                default:
                    throw new IOException("未知 Redis 回复类型：" + (char)type);
            }
        }

        private static RedisReply MakeSimple(RedisReplyKind kind, string text)
        {
            RedisReply reply = new RedisReply();
            reply.Kind = kind;
            reply.Text = text;
            return reply;
        }

        private int ReadByte()
        {
            if (_offset >= _count)
            {
                if (_stream == null) return -1;
                _count = _stream.Read(_buffer, 0, _buffer.Length);
                _offset = 0;
                if (_count <= 0)
                {
                    _count = 0;
                    return -1;
                }
            }
            return _buffer[_offset++];
        }

        private string ReadLine()
        {
            MemoryStream line = new MemoryStream();
            while (true)
            {
                int b = ReadByte();
                if (b < 0) throw new IOException("Redis 连接已关闭。");
                if (b == '\r')
                {
                    int next = ReadByte();
                    if (next < 0) throw new IOException("Redis 连接已关闭。");
                    if (next != '\n') throw new IOException("Redis 协议格式错误：缺少 LF。");
                    break;
                }
                line.WriteByte((byte)b);
                if (line.Length > RedisProtocol.MaxLineBytes)
                    throw new IOException("Redis 单行回复过长。");
            }
            return Encoding.UTF8.GetString(line.ToArray());
        }

        private byte[] ReadBytes(int length)
        {
            byte[] data = new byte[length];
            int offset = 0;
            while (offset < length)
            {
                if (_offset >= _count)
                {
                    _count = _stream.Read(_buffer, 0, _buffer.Length);
                    _offset = 0;
                    if (_count <= 0) throw new IOException("Redis 连接已关闭。");
                }
                int take = Math.Min(length - offset, _count - _offset);
                Array.Copy(_buffer, _offset, data, offset, take);
                _offset += take;
                offset += take;
            }
            int cr = ReadByte();
            int lf = ReadByte();
            if (cr != '\r' || lf != '\n') throw new IOException("Redis bulk 回复格式错误。");
            return data;
        }

        public static string DecodeText(byte[] data)
        {
            if (data == null) return "";
            if (data.Length == 0) return "";
            if (LooksBinary(data))
            {
                int preview = Math.Min(data.Length, 48);
                StringBuilder sb = new StringBuilder();
                sb.Append("<binary ").Append(data.Length).Append(" bytes> ");
                for (int i = 0; i < preview; i++) sb.Append(data[i].ToString("x2"));
                if (preview < data.Length) sb.Append("…");
                return sb.ToString();
            }
            return Encoding.UTF8.GetString(data);
        }

        private static bool LooksBinary(byte[] data)
        {
            for (int i = 0; i < data.Length; i++)
            {
                byte b = data[i];
                if (b == 0) return true;
                if (b < 0x09) return true;
            }
            try
            {
                UTF8Encoding strict = new UTF8Encoding(false, true);
                strict.GetString(data);
                return false;
            }
            catch (Exception)
            {
                return true;
            }
        }

        public void Dispose()
        {
            lock (_io)
            {
                _closed = true;
                try { if (_stream != null) _stream.Dispose(); }
                catch (Exception) { }
                try { if (_tcp != null) _tcp.Close(); }
                catch (Exception) { }
                _stream = null;
                _tcp = null;
                _buffer = null;
            }
        }
    }

    internal sealed class RedisScanState
    {
        public List<string> Nodes = new List<string>();
        public Dictionary<string, string> Cursors = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> Completed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        public bool Done;
    }

    internal sealed class RedisClient : IDatabaseConnection
    {
        private const int MaxEntries = 200;
        private const int MaxStringPreview = 65536;
        private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false);

        private readonly DatabaseConnectionProfile _profile;
        private readonly object _sync = new object();
        private readonly Dictionary<string, RedisNodeConnection> _nodes =
            new Dictionary<string, RedisNodeConnection>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<int, string> _slotOwners = new Dictionary<int, string>();
        private readonly List<string> _masters = new List<string>();

        private bool _isCluster;
        private bool _closed;
        private string _version = "";
        private string _clusterState = "";
        private string _defaultNode = "";
        private long _dbSize = -1;

        private RedisClient(DatabaseConnectionProfile profile)
        {
            _profile = profile;
        }

        public string Kind
        {
            get { return DatabaseKinds.Redis; }
        }

        public string Endpoint
        {
            get { return _profile.Endpoint; }
        }

        public string ServerVersion
        {
            get { return _version; }
        }

        public bool AllowWrites
        {
            get { return _profile.AllowWrites; }
        }

        public static RedisClient Open(DatabaseConnectionProfile profile, out string error)
        {
            error = "";
            RedisClient client = new RedisClient(profile);
            try
            {
                client.Connect();
                return client;
            }
            catch (Exception ex)
            {
                error = Friendly(ex, "连接 Redis 失败");
                client.Dispose();
                return null;
            }
        }

        private void Connect()
        {
            string seedAddress = FormatAddress(_profile.Host, _profile.Port);
            RedisNodeConnection seed = new RedisNodeConnection(_profile.Host, _profile.Port,
                _profile.RedisTls, _profile.AllowInvalidCert);
            seed.Connect();
            try
            {
                Authenticate(seed);
                RedisReply ping = seed.Send(new string[] { "PING" });
                if (ping.IsError) throw new InvalidOperationException("PING 失败：" + ping.Text);

                RedisReply clusterInfo = seed.Send(new string[] { "CLUSTER", "INFO" });
                bool cluster = false;
                if (!clusterInfo.IsError && clusterInfo.Text != null &&
                    clusterInfo.Text.IndexOf("cluster_enabled:1", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    cluster = true;
                }

                if (cluster)
                {
                    if (_profile.RedisDatabase != 0)
                        throw new InvalidOperationException("Redis Cluster 只支持 DB 0，请把 Redis DB 改为 0。");
                    RedisReply slots = seed.Send(new string[] { "CLUSTER", "SLOTS" });
                    if (slots.IsError)
                        throw new InvalidOperationException("读取 CLUSTER SLOTS 失败：" + slots.Text);
                    ParseSlotMap(slots);
                    if (_masters.Count == 0)
                        throw new InvalidOperationException("CLUSTER SLOTS 没有返回主节点。");
                    _isCluster = true;
                    _defaultNode = _masters[0];
                    _clusterState = ParseInfoField(clusterInfo.Text, "cluster_state");
                    _nodes[seedAddress] = seed;
                }
                else
                {
                    _isCluster = false;
                    SelectDatabase(seed, _profile.RedisDatabase);
                    _masters.Add(seedAddress);
                    _defaultNode = seedAddress;
                    _nodes[seedAddress] = seed;
                }

                RedisReply info = seed.Send(new string[] { "INFO", "server" });
                if (!info.IsError) _version = ParseInfoField(info.Text, "redis_version");
                if (string.IsNullOrEmpty(_version))
                {
                    RedisReply fallback = seed.Send(new string[] { "INFO" });
                    if (!fallback.IsError) _version = ParseInfoField(fallback.Text, "redis_version");
                }

                LoadDbSize();
            }
            catch (Exception)
            {
                seed.Dispose();
                _nodes.Remove(seedAddress);
                throw;
            }
        }

        private void Authenticate(RedisNodeConnection node)
        {
            if (string.IsNullOrEmpty(_profile.Password)) return;
            RedisReply reply;
            if (!string.IsNullOrEmpty(_profile.User))
                reply = node.Send(new string[] { "AUTH", _profile.User, _profile.Password });
            else
                reply = node.Send(new string[] { "AUTH", _profile.Password });
            if (reply.IsError)
                throw new InvalidOperationException("Redis 认证失败：" + reply.Text);
        }

        private static void SelectDatabase(RedisNodeConnection node, int database)
        {
            if (database <= 0) return;
            RedisReply reply = node.Send(new string[] { "SELECT", database.ToString(CultureInfo.InvariantCulture) });
            if (reply.IsError)
                throw new InvalidOperationException("SELECT " + database + " 失败：" + reply.Text);
        }

        private void ParseSlotMap(RedisReply slots)
        {
            if (slots.Kind != RedisReplyKind.Array) return;
            for (int i = 0; i < slots.Items.Count; i++)
            {
                RedisReply range = slots.Items[i];
                if (range == null || range.Kind != RedisReplyKind.Array || range.Items.Count < 3) continue;
                int start = (int)range.Items[0].Integer;
                int end = (int)range.Items[1].Integer;
                RedisReply master = range.Items[2];
                if (master == null || master.Kind != RedisReplyKind.Array || master.Items.Count < 2) continue;
                string host = master.Items[0] == null ? "" : master.Items[0].Text;
                int port = master.Items[1] == null ? 0 : (int)master.Items[1].Integer;
                if (string.IsNullOrEmpty(host)) host = _profile.Host;
                if (port <= 0) continue;
                string address = FormatAddress(host, port);
                if (!_masters.Contains(address)) _masters.Add(address);
                for (int slot = start; slot <= end && slot < 16384; slot++)
                {
                    _slotOwners[slot] = address;
                }
            }
        }

        private void LoadDbSize()
        {
            long total = 0;
            bool any = false;
            for (int i = 0; i < _masters.Count; i++)
            {
                try
                {
                    RedisNodeConnection node = EnsureNode(_masters[i]);
                    RedisReply reply = node.Send(new string[] { "DBSIZE" });
                    if (!reply.IsError)
                    {
                        total += reply.Integer;
                        any = true;
                    }
                }
                catch (Exception)
                {
                    // 统计失败不影响连接本身
                }
            }
            _dbSize = any ? total : -1;
        }

        public RedisServerSummary GetSummary()
        {
            RedisServerSummary summary = new RedisServerSummary();
            summary.IsCluster = _isCluster;
            summary.Version = _version;
            summary.ClusterState = _clusterState;
            summary.DbSize = _dbSize;
            for (int i = 0; i < _masters.Count; i++) summary.Masters.Add(_masters[i]);
            return summary;
        }

        public RedisScanState CreateScanState()
        {
            RedisScanState state = new RedisScanState();
            if (_isCluster)
            {
                for (int i = 0; i < _masters.Count; i++) state.Nodes.Add(_masters[i]);
            }
            else
            {
                state.Nodes.Add(_defaultNode);
            }
            for (int i = 0; i < state.Nodes.Count; i++) state.Cursors[state.Nodes[i]] = "0";
            return state;
        }

        public RedisScanPage ScanPage(RedisScanState state, string pattern, int count, out string error)
        {
            error = "";
            RedisScanPage page = new RedisScanPage();
            if (state == null) return page;
            lock (_sync)
            {
                try
                {
                    if (state.Done)
                    {
                        page.Done = true;
                        return page;
                    }

                    int nodeCount = Math.Max(1, state.Nodes.Count);
                    int perNode = Math.Max(100, count / nodeCount);
                    List<RedisKeyInfo> raw = new List<RedisKeyInfo>();
                    HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal);
                    int scanned = 0;

                    for (int i = 0; i < state.Nodes.Count; i++)
                    {
                        string nodeAddress = state.Nodes[i];
                        if (state.Completed.Contains(nodeAddress)) continue;
                        string cursor;
                        if (!state.Cursors.TryGetValue(nodeAddress, out cursor)) cursor = "0";

                        List<string> parts = new List<string>();
                        parts.Add("SCAN");
                        parts.Add(cursor);
                        if (!string.IsNullOrEmpty(pattern))
                        {
                            parts.Add("MATCH");
                            parts.Add(pattern);
                        }
                        parts.Add("COUNT");
                        parts.Add(perNode.ToString(CultureInfo.InvariantCulture));

                        RedisReply reply = ExecuteOnNode(nodeAddress, parts);
                        if (reply == null || reply.IsError)
                        {
                            error = reply == null ? "SCAN 没有返回结果。" : reply.Text;
                            return page;
                        }
                        if (reply.Kind != RedisReplyKind.Array || reply.Items.Count < 2)
                        {
                            error = "SCAN 返回格式不正确。";
                            return page;
                        }
                        string next = reply.Items[0].Text;
                        state.Cursors[nodeAddress] = next;
                        if (next == "0") state.Completed.Add(nodeAddress);
                        scanned++;

                        RedisReply keys = reply.Items[1];
                        if (keys != null && keys.Kind == RedisReplyKind.Array)
                        {
                            for (int k = 0; k < keys.Items.Count; k++)
                            {
                                RedisKeyInfo info = new RedisKeyInfo();
                                info.Key = keys.Items[k].Text;
                                if (string.IsNullOrEmpty(info.Key) || !seen.Add(info.Key)) continue;
                                info.Node = nodeAddress;
                                raw.Add(info);
                            }
                        }
                    }

                    state.Done = state.Completed.Count >= state.Nodes.Count;

                    FillTypesAndTtl(raw);
                    page.Keys = raw;
                    page.Done = state.Done;
                    page.ScannedNodes = scanned;
                    return page;
                }
                catch (Exception ex)
                {
                    error = Friendly(ex, "SCAN 失败");
                    return page;
                }
            }
        }

        private void FillTypesAndTtl(List<RedisKeyInfo> keys)
        {
            if (keys.Count == 0) return;
            Dictionary<string, List<int>> groups = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < keys.Count; i++)
            {
                string owner = OwnerOf(keys[i].Key);
                keys[i].Node = owner;
                List<int> list;
                if (!groups.TryGetValue(owner, out list))
                {
                    list = new List<int>();
                    groups[owner] = list;
                }
                list.Add(i);
            }

            foreach (KeyValuePair<string, List<int>> group in groups)
            {
                List<IList<string>> commands = new List<IList<string>>();
                for (int i = 0; i < group.Value.Count; i++)
                {
                    string key = keys[group.Value[i]].Key;
                    commands.Add(new string[] { "TYPE", key });
                    commands.Add(new string[] { "TTL", key });
                }
                try
                {
                    RedisNodeConnection node = EnsureNode(group.Key);
                    List<RedisReply> replies = node.SendPipeline(commands);
                    for (int i = 0; i < group.Value.Count; i++)
                    {
                        RedisKeyInfo info = keys[group.Value[i]];
                        RedisReply typeReply = i * 2 < replies.Count ? replies[i * 2] : null;
                        RedisReply ttlReply = i * 2 + 1 < replies.Count ? replies[i * 2 + 1] : null;
                        info.Type = typeReply == null || typeReply.IsError ? "?" : typeReply.Text;
                        info.Ttl = ttlReply == null || ttlReply.Kind != RedisReplyKind.Integer ? -2 : ttlReply.Integer;
                    }
                }
                catch (Exception)
                {
                    for (int i = 0; i < group.Value.Count; i++)
                    {
                        keys[group.Value[i]].Type = "?";
                        keys[group.Value[i]].Ttl = -2;
                    }
                }
            }

            keys.Sort(delegate(RedisKeyInfo a, RedisKeyInfo b)
            {
                return string.Compare(a.Key, b.Key, StringComparison.Ordinal);
            });
        }

        public RedisValueData GetValue(string key, out string error)
        {
            error = "";
            RedisValueData value = new RedisValueData();
            value.Key = key ?? "";
            lock (_sync)
            {
                try
                {
                    RedisReply typeReply = Execute(new string[] { "TYPE", key });
                    if (typeReply == null || typeReply.IsError)
                    {
                        error = typeReply == null ? "读取 TYPE 失败。" : typeReply.Text;
                        return value;
                    }
                    string type = typeReply.Text;
                    value.Type = type;
                    if (type == "none")
                    {
                        error = "Key 不存在或已过期。";
                        return value;
                    }

                    RedisReply ttlReply = Execute(new string[] { "TTL", key });
                    value.Ttl = ttlReply != null && ttlReply.Kind == RedisReplyKind.Integer ? ttlReply.Integer : -2;

                    if (type == "string")
                    {
                        RedisReply lenReply = Execute(new string[] { "STRLEN", key });
                        value.Length = lenReply != null && lenReply.Kind == RedisReplyKind.Integer ? lenReply.Integer : -1;
                        RedisReply get = Execute(new string[] { "GETRANGE", key, "0", (MaxStringPreview - 1).ToString(CultureInfo.InvariantCulture) });
                        if (get == null || get.IsError)
                        {
                            error = get == null ? "读取值失败。" : get.Text;
                            return value;
                        }
                        value.Scalar = get.Text;
                        value.Truncated = value.Length > MaxStringPreview;
                    }
                    else if (type == "hash")
                    {
                        RedisReply lenReply = Execute(new string[] { "HLEN", key });
                        value.Length = lenReply != null && lenReply.Kind == RedisReplyKind.Integer ? lenReply.Integer : -1;
                        RedisReply scan = Execute(new string[] { "HSCAN", key, "0", "COUNT", MaxEntries.ToString(CultureInfo.InvariantCulture) });
                        ParseHashOrSet(scan, value, true);
                    }
                    else if (type == "list")
                    {
                        RedisReply lenReply = Execute(new string[] { "LLEN", key });
                        value.Length = lenReply != null && lenReply.Kind == RedisReplyKind.Integer ? lenReply.Integer : -1;
                        RedisReply range = Execute(new string[] { "LRANGE", key, "0", (MaxEntries - 1).ToString(CultureInfo.InvariantCulture) });
                        if (range != null && range.Kind == RedisReplyKind.Array)
                        {
                            for (int i = 0; i < range.Items.Count; i++)
                            {
                                RedisEntry entry = new RedisEntry();
                                entry.Index = i;
                                entry.Value = range.Items[i].Text;
                                value.Entries.Add(entry);
                            }
                        }
                        value.Truncated = value.Length > value.Entries.Count;
                    }
                    else if (type == "set")
                    {
                        RedisReply lenReply = Execute(new string[] { "SCARD", key });
                        value.Length = lenReply != null && lenReply.Kind == RedisReplyKind.Integer ? lenReply.Integer : -1;
                        RedisReply scan = Execute(new string[] { "SSCAN", key, "0", "COUNT", MaxEntries.ToString(CultureInfo.InvariantCulture) });
                        ParseHashOrSet(scan, value, false);
                    }
                    else if (type == "zset")
                    {
                        RedisReply lenReply = Execute(new string[] { "ZCARD", key });
                        value.Length = lenReply != null && lenReply.Kind == RedisReplyKind.Integer ? lenReply.Integer : -1;
                        RedisReply range = Execute(new string[] { "ZRANGE", key, "0", (MaxEntries - 1).ToString(CultureInfo.InvariantCulture), "WITHSCORES" });
                        if (range != null && range.Kind == RedisReplyKind.Array)
                        {
                            for (int i = 0; i + 1 < range.Items.Count; i += 2)
                            {
                                RedisEntry entry = new RedisEntry();
                                entry.Index = i / 2;
                                entry.Value = range.Items[i].Text;
                                entry.Score = range.Items[i + 1].Text;
                                value.Entries.Add(entry);
                            }
                        }
                        value.Truncated = value.Length > value.Entries.Count;
                    }
                    else if (type == "stream")
                    {
                        RedisReply lenReply = Execute(new string[] { "XLEN", key });
                        value.Length = lenReply != null && lenReply.Kind == RedisReplyKind.Integer ? lenReply.Integer : -1;
                        RedisReply range = Execute(new string[] { "XRANGE", key, "-", "+", "COUNT", "100" });
                        if (range != null && range.Kind == RedisReplyKind.Array)
                        {
                            for (int i = 0; i < range.Items.Count; i++)
                            {
                                RedisReply item = range.Items[i];
                                if (item == null || item.Kind != RedisReplyKind.Array || item.Items.Count < 2) continue;
                                RedisEntry entry = new RedisEntry();
                                entry.Index = i;
                                entry.Field = item.Items[0].Text;
                                entry.Value = FormatStreamFields(item.Items[1]);
                                value.Entries.Add(entry);
                            }
                        }
                        value.Truncated = value.Length > value.Entries.Count;
                    }
                    else
                    {
                        error = "暂不支持的 Redis 类型：" + type;
                        return value;
                    }

                    if (error.Length == 0 && value.Length > value.Entries.Count &&
                        (type == "hash" || type == "set"))
                    {
                        value.Truncated = true;
                    }
                    return value;
                }
                catch (Exception ex)
                {
                    error = Friendly(ex, "读取 Redis 值失败");
                    return value;
                }
            }
        }

        private static string FormatStreamFields(RedisReply fields)
        {
            if (fields == null || fields.Kind != RedisReplyKind.Array) return "";
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i + 1 < fields.Items.Count; i += 2)
            {
                if (sb.Length > 0) sb.Append("  ");
                sb.Append(fields.Items[i].Text).Append('=').Append(fields.Items[i + 1].Text);
            }
            return sb.ToString();
        }

        private static void ParseHashOrSet(RedisReply reply, RedisValueData value, bool isHash)
        {
            if (reply == null || reply.Kind != RedisReplyKind.Array || reply.Items.Count < 2) return;
            RedisReply items = reply.Items[1];
            if (items == null || items.Kind != RedisReplyKind.Array) return;
            if (isHash)
            {
                for (int i = 0; i + 1 < items.Items.Count; i += 2)
                {
                    RedisEntry entry = new RedisEntry();
                    entry.Index = i / 2;
                    entry.Field = items.Items[i].Text;
                    entry.Value = items.Items[i + 1].Text;
                    value.Entries.Add(entry);
                }
            }
            else
            {
                for (int i = 0; i < items.Items.Count; i++)
                {
                    RedisEntry entry = new RedisEntry();
                    entry.Index = i;
                    entry.Value = items.Items[i].Text;
                    value.Entries.Add(entry);
                }
            }
        }

        public string RunCommand(string[] parts, out string error)
        {
            error = "";
            try
            {
                RedisReply reply = Execute(parts);
                if (reply == null) return "";
                return reply.Format(20000);
            }
            catch (Exception ex)
            {
                error = Friendly(ex, "命令执行失败");
                return "";
            }
        }

        public bool SetString(string key, string value, out string error)
        {
            return CheckReply(Execute(new string[] { "SET", key, value }), "SET", out error);
        }

        public bool DeleteKey(string key, out string error)
        {
            return CheckReply(Execute(new string[] { "DEL", key }), "DEL", out error);
        }

        public bool RenameKey(string oldKey, string newKey, out string error)
        {
            return CheckReply(Execute(new string[] { "RENAME", oldKey, newKey }), "RENAME", out error);
        }

        public bool SetTtl(string key, long seconds, out string error)
        {
            RedisReply reply;
            if (seconds < 0)
                reply = Execute(new string[] { "PERSIST", key });
            else
                reply = Execute(new string[] { "EXPIRE", key, seconds.ToString(CultureInfo.InvariantCulture) });
            if (!CheckReply(reply, seconds < 0 ? "PERSIST" : "EXPIRE", out error)) return false;
            return true;
        }

        private static bool CheckReply(RedisReply reply, string command, out string error)
        {
            error = "";
            if (reply == null)
            {
                error = command + " 没有返回结果。";
                return false;
            }
            if (reply.IsError)
            {
                error = reply.Text;
                return false;
            }
            return true;
        }

        // ---------------- 命令路由 ----------------

        private RedisReply Execute(string[] parts)
        {
            lock (_sync)
            {
                string target = DetermineTarget(parts);
                RedisReply reply = null;
                for (int attempt = 0; attempt < 4; attempt++)
                {
                    reply = ExecuteOnNode(target, parts);
                    if (reply == null || !reply.IsError) return reply;
                    string text = reply.Text ?? "";
                    if (text.StartsWith("MOVED ", StringComparison.Ordinal))
                    {
                        RedirectInfo info;
                        if (!TryParseRedirect(text, out info)) return reply;
                        _slotOwners[info.Slot] = info.Address;
                        if (!_masters.Contains(info.Address)) _masters.Add(info.Address);
                        target = info.Address;
                        continue;
                    }
                    if (text.StartsWith("ASK ", StringComparison.Ordinal))
                    {
                        RedirectInfo info;
                        if (!TryParseRedirect(text, out info)) return reply;
                        try
                        {
                            ExecuteOnNode(info.Address, new string[] { "ASKING" });
                        }
                        catch (Exception)
                        {
                            return reply;
                        }
                        target = info.Address;
                        continue;
                    }
                    return reply;
                }
                return reply;
            }
        }

        private string DetermineTarget(string[] parts)
        {
            if (!_isCluster) return _defaultNode;
            string command = parts.Length > 0 ? (parts[0] ?? "").ToUpperInvariant() : "";
            if (parts.Length < 2 || IsNoKeyCommand(command)) return _defaultNode;
            int slot = ComputeSlot(parts[1]);
            string owner;
            if (_slotOwners.TryGetValue(slot, out owner)) return owner;
            return _defaultNode;
        }

        private static bool IsNoKeyCommand(string command)
        {
            return command == "PING" || command == "INFO" || command == "ECHO" || command == "TIME" ||
                   command == "DBSIZE" || command == "RANDOMKEY" || command == "SCAN" ||
                   command == "KEYS" || command == "CLUSTER" || command == "COMMAND" ||
                   command == "LASTSAVE" || command == "LATENCY" || command == "SLOWLOG";
        }

        private RedisReply ExecuteOnNode(string address, IList<string> parts)
        {
            RedisNodeConnection node = EnsureNode(address);
            return node.Send(parts);
        }

        private RedisNodeConnection EnsureNode(string address)
        {
            if (_closed) throw new ObjectDisposedException("RedisClient");
            RedisNodeConnection node;
            if (_nodes.TryGetValue(address, out node))
            {
                node.Connect();
                return node;
            }
            string host;
            int port;
            if (!TrySplitAddress(address, out host, out port))
                throw new InvalidOperationException("Redis 节点地址无效：" + address);
            node = new RedisNodeConnection(host, port, _profile.RedisTls, _profile.AllowInvalidCert);
            node.Connect();
            try
            {
                Authenticate(node);
                if (!_isCluster) SelectDatabase(node, _profile.RedisDatabase);
            }
            catch (Exception)
            {
                node.Dispose();
                throw;
            }
            _nodes[address] = node;
            return node;
        }

        private string OwnerOf(string key)
        {
            if (!_isCluster) return _defaultNode;
            string owner;
            if (_slotOwners.TryGetValue(ComputeSlot(key), out owner)) return owner;
            return _defaultNode;
        }

        private sealed class RedirectInfo
        {
            public int Slot;
            public string Address;
        }

        private static bool TryParseRedirect(string text, out RedirectInfo info)
        {
            info = null;
            string[] parts = text.Split(' ');
            if (parts.Length < 3) return false;
            int slot;
            if (!int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out slot)) return false;
            string host;
            int port;
            if (!TrySplitAddress(parts[2], out host, out port)) return false;
            info = new RedirectInfo();
            info.Slot = slot;
            info.Address = FormatAddress(host, port);
            return true;
        }

        public static int ComputeSlot(string key)
        {
            if (string.IsNullOrEmpty(key)) return 0;
            string hashKey = key;
            int open = key.IndexOf('{');
            if (open >= 0)
            {
                int close = key.IndexOf('}', open + 1);
                if (close > open + 1) hashKey = key.Substring(open + 1, close - open - 1);
            }
            byte[] bytes = Utf8.GetBytes(hashKey);
            int crc = 0;
            for (int i = 0; i < bytes.Length; i++)
            {
                crc ^= bytes[i] << 8;
                for (int bit = 0; bit < 8; bit++)
                {
                    if ((crc & 0x8000) != 0) crc = ((crc << 1) ^ 0x1021) & 0xFFFF;
                    else crc = (crc << 1) & 0xFFFF;
                }
            }
            return crc % 16384;
        }

        public static string FormatAddress(string host, int port)
        {
            string value = string.IsNullOrEmpty(host) ? "127.0.0.1" : host;
            if (value.IndexOf(':') >= 0 && !value.StartsWith("[")) value = "[" + value + "]";
            return value + ":" + port.ToString(CultureInfo.InvariantCulture);
        }

        private static bool TrySplitAddress(string address, out string host, out int port)
        {
            host = "";
            port = 0;
            if (string.IsNullOrEmpty(address)) return false;
            int colon;
            if (address.StartsWith("["))
            {
                int close = address.IndexOf(']');
                if (close < 0) return false;
                host = address.Substring(1, close - 1);
                colon = address.IndexOf(':', close + 1);
                if (colon < 0) return false;
            }
            else
            {
                colon = address.LastIndexOf(':');
                if (colon <= 0) return false;
                host = address.Substring(0, colon);
            }
            return int.TryParse(address.Substring(colon + 1), NumberStyles.Integer,
                CultureInfo.InvariantCulture, out port) && port > 0 && port <= 65535;
        }

        private static string ParseInfoField(string info, string field)
        {
            if (string.IsNullOrEmpty(info)) return "";
            string[] lines = info.Replace("\r\n", "\n").Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i].Trim();
                if (line.StartsWith(field + ":", StringComparison.OrdinalIgnoreCase))
                    return line.Substring(field.Length + 1).Trim();
            }
            return "";
        }

        public void Close()
        {
            lock (_sync)
            {
                _closed = true;
                foreach (KeyValuePair<string, RedisNodeConnection> pair in _nodes)
                {
                    try { pair.Value.Dispose(); }
                    catch (Exception) { }
                }
                _nodes.Clear();
            }
        }

        public void Dispose()
        {
            Close();
        }

        private static string Friendly(Exception ex, string prefix)
        {
            TimeoutException timeout = ex as TimeoutException;
            if (timeout != null) return prefix + "：连接超时。";
            SocketException socket = ex as SocketException;
            if (socket != null) return prefix + "：" + socket.Message;
            AuthenticationException auth = ex as AuthenticationException;
            if (auth != null) return prefix + "：TLS 认证失败（证书不受信任，或需要勾选允许无效证书）。";
            return prefix + "：" + ex.Message;
        }
    }
}
