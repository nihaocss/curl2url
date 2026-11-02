using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;

namespace Curl2Url
{
    // 连接配置落在 %APPDATA%\curl2url\db-connections.json；密码字段用 Windows DPAPI 加密。
    internal sealed class DatabaseConnectionStore
    {
        private readonly List<DatabaseConnectionProfile> _profiles = new List<DatabaseConnectionProfile>();
        private string _lastProfileId = "";

        public static string ConfigPath
        {
            get
            {
                string custom = Environment.GetEnvironmentVariable("CURL2URL_DB_CONFIG");
                if (!string.IsNullOrWhiteSpace(custom)) return custom;
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "curl2url", "db-connections.json");
            }
        }

        public List<DatabaseConnectionProfile> Profiles
        {
            get { return _profiles; }
        }

        public string LastProfileId
        {
            get { return _lastProfileId; }
        }

        public bool Load(out string error)
        {
            error = "";
            _profiles.Clear();
            _lastProfileId = "";

            try
            {
                string path = ConfigPath;
                if (!File.Exists(path)) return true;

                string json = File.ReadAllText(path, Encoding.UTF8);
                string lastId;
                if (!Deserialize(json, _profiles, out lastId, out error)) return false;
                _lastProfileId = lastId;
                return true;
            }
            catch (Exception ex)
            {
                error = "读取数据库连接配置失败：" + ex.Message;
                return false;
            }
        }

        public bool Save(out string error)
        {
            error = "";
            try
            {
                string path = ConfigPath;
                string directory = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

                string json = Serialize(_profiles, _lastProfileId);
                string temp = path + ".tmp";
                File.WriteAllText(temp, json, new UTF8Encoding(false));
                if (File.Exists(path))
                {
                    try
                    {
                        File.Replace(temp, path, null);
                    }
                    catch (Exception)
                    {
                        // 某些文件系统不支持 Replace，退回直接覆盖
                        File.Copy(temp, path, true);
                        File.Delete(temp);
                    }
                }
                else
                {
                    File.Move(temp, path);
                }
                return true;
            }
            catch (Exception ex)
            {
                error = "保存数据库连接配置失败：" + ex.Message;
                return false;
            }
        }

        public DatabaseConnectionProfile Find(string id)
        {
            if (string.IsNullOrEmpty(id)) return null;
            for (int i = 0; i < _profiles.Count; i++)
            {
                if (_profiles[i].Id == id) return _profiles[i];
            }
            return null;
        }

        public void AddOrUpdate(DatabaseConnectionProfile profile)
        {
            if (profile == null) return;
            for (int i = 0; i < _profiles.Count; i++)
            {
                if (_profiles[i].Id == profile.Id)
                {
                    _profiles[i] = profile;
                    return;
                }
            }
            _profiles.Add(profile);
        }

        public void Remove(string id)
        {
            for (int i = _profiles.Count - 1; i >= 0; i--)
            {
                if (_profiles[i].Id == id) _profiles.RemoveAt(i);
            }
            if (_lastProfileId == id) _lastProfileId = "";
        }

        public void SetLast(string id)
        {
            _lastProfileId = id ?? "";
        }

        public static string Serialize(List<DatabaseConnectionProfile> profiles, string lastProfileId)
        {
            JavaScriptSerializer serializer = new JavaScriptSerializer();
            serializer.MaxJsonLength = 8 * 1024 * 1024;

            Dictionary<string, object> root = new Dictionary<string, object>();
            root["version"] = 1;
            root["lastProfileId"] = lastProfileId ?? "";
            List<object> items = new List<object>();
            if (profiles != null)
            {
                for (int i = 0; i < profiles.Count; i++)
                {
                    DatabaseConnectionProfile p = profiles[i];
                    if (p == null) continue;
                    Dictionary<string, object> item = new Dictionary<string, object>();
                    item["id"] = p.Id;
                    item["name"] = p.Name;
                    item["kind"] = p.Kind;
                    item["host"] = p.Host;
                    item["port"] = p.Port;
                    item["user"] = p.User;
                    item["password"] = Dpapi.Protect(p.Password);
                    item["database"] = p.Database;
                    item["redisDatabase"] = p.RedisDatabase;
                    item["sslMode"] = p.SslMode;
                    item["redisTls"] = p.RedisTls;
                    item["allowInvalidCert"] = p.AllowInvalidCert;
                    item["environment"] = p.Environment;
                    item["allowWrites"] = p.AllowWrites;
                    item["lastUsedUtc"] = p.LastUsedUtc.ToUniversalTime().ToString("o");
                    item["options"] = p.Options;
                    items.Add(item);
                }
            }
            root["connections"] = items.ToArray();
            return serializer.Serialize(root);
        }

        public static bool Deserialize(string json, List<DatabaseConnectionProfile> target,
            out string lastProfileId, out string error)
        {
            lastProfileId = "";
            error = "";
            try
            {
                JavaScriptSerializer serializer = new JavaScriptSerializer();
                serializer.MaxJsonLength = 8 * 1024 * 1024;
                Dictionary<string, object> root = serializer.Deserialize<Dictionary<string, object>>(json);
                if (root == null) return true;

                lastProfileId = GetString(root, "lastProfileId");
                object listObject;
                if (!root.TryGetValue("connections", out listObject)) return true;

                // JavaScriptSerializer 的泛型反序列化会把 JSON 数组解析成 ArrayList，
                // 这里不能只按 object[] 处理，否则已保存的连接会被静默丢弃。
                System.Collections.IEnumerable items = listObject as System.Collections.IEnumerable;
                if (items == null) return true;

                foreach (object entry in items)
                {
                    Dictionary<string, object> item = entry as Dictionary<string, object>;
                    if (item == null) continue;

                    DatabaseConnectionProfile p = new DatabaseConnectionProfile();
                    p.Id = GetString(item, "id");
                    if (p.Id.Length == 0) p.Id = Guid.NewGuid().ToString("N");
                    p.Name = GetString(item, "name");
                    p.Kind = GetString(item, "kind");
                    if (p.Kind.Length == 0) p.Kind = DatabaseKinds.MySql;
                    p.Host = GetString(item, "host");
                    p.Port = GetInt(item, "port", p.Kind == DatabaseKinds.Redis ? 6379 : 3306);
                    p.User = GetString(item, "user");
                    p.Password = Dpapi.Unprotect(GetString(item, "password"));
                    p.Database = GetString(item, "database");
                    p.RedisDatabase = GetInt(item, "redisDatabase", 0);
                    p.SslMode = GetString(item, "sslMode");
                    if (p.SslMode.Length == 0) p.SslMode = "Preferred";
                    p.RedisTls = GetBool(item, "redisTls", false);
                    p.AllowInvalidCert = GetBool(item, "allowInvalidCert", false);
                    p.Environment = GetString(item, "environment");
                    if (p.Environment.Length == 0) p.Environment = DatabaseEnvironment.Local;
                    p.AllowWrites = GetBool(item, "allowWrites", false);
                    p.LastUsedUtc = ParseDate(GetString(item, "lastUsedUtc"));

                    object optionsObject;
                    if (item.TryGetValue("options", out optionsObject))
                    {
                        Dictionary<string, object> options = optionsObject as Dictionary<string, object>;
                        if (options != null)
                        {
                            foreach (KeyValuePair<string, object> pair in options)
                            {
                                p.Options[pair.Key] = pair.Value == null ? "" : Convert.ToString(pair.Value);
                            }
                        }
                    }
                    target.Add(p);
                }
                return true;
            }
            catch (Exception ex)
            {
                error = "数据库连接配置格式不正确：" + ex.Message;
                return false;
            }
        }

        private static string GetString(Dictionary<string, object> data, string key)
        {
            object value;
            if (data == null || !data.TryGetValue(key, out value) || value == null) return "";
            return Convert.ToString(value).Trim();
        }

        private static int GetInt(Dictionary<string, object> data, string key, int fallback)
        {
            object value;
            if (data == null || !data.TryGetValue(key, out value) || value == null) return fallback;
            try { return Convert.ToInt32(value); }
            catch (Exception) { return fallback; }
        }

        private static bool GetBool(Dictionary<string, object> data, string key, bool fallback)
        {
            object value;
            if (data == null || !data.TryGetValue(key, out value) || value == null) return fallback;
            try { return Convert.ToBoolean(value); }
            catch (Exception) { return fallback; }
        }

        private static DateTime ParseDate(string text)
        {
            DateTime value;
            if (DateTime.TryParse(text, null,
                System.Globalization.DateTimeStyles.RoundtripKind, out value))
            {
                return value.ToUniversalTime();
            }
            return DateTime.MinValue;
        }
    }
}
