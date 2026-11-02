using System;
using System.Collections.Generic;
using System.Data;
using System.Diagnostics;
using System.Text;
using MySqlConnector;

namespace Curl2Url
{
    // MySqlConnector 封装：连接、元数据、查询、表数据保存。
    // 所有方法都是同步的，由 UI 层放到后台线程执行；同一时刻只允许一个命令。
    internal sealed class MySqlSession : IDatabaseConnection
    {
        private const int MaxCellChars = 262144;
        private const int MaxCellBytes = 65536;

        private readonly DatabaseConnectionProfile _profile;
        private readonly string _connectionString;
        private readonly object _gate = new object();
        private MySqlConnection _connection;
        private MySqlCommand _current;
        private bool _closed;

        private MySqlSession(DatabaseConnectionProfile profile, string connectionString)
        {
            _profile = profile;
            _connectionString = connectionString;
        }

        public string Kind
        {
            get { return DatabaseKinds.MySql; }
        }

        public string Endpoint
        {
            get { return _profile.Endpoint; }
        }

        public string ServerVersion
        {
            get
            {
                try
                {
                    lock (_gate)
                    {
                        EnsureOpen();
                        return _connection.ServerVersion;
                    }
                }
                catch (Exception)
                {
                    return "";
                }
            }
        }

        public bool AllowWrites
        {
            get { return _profile.AllowWrites; }
        }

        public static MySqlSession Open(DatabaseConnectionProfile profile, out string error)
        {
            error = "";
            MySqlSession session = null;
            try
            {
                string connectionString = BuildConnectionString(profile);
                session = new MySqlSession(profile, connectionString);
                lock (session._gate)
                {
                    session.EnsureOpen();
                }
                return session;
            }
            catch (Exception ex)
            {
                error = Friendly(ex, "连接 MySQL 失败");
                if (session != null) session.Dispose();
                return null;
            }
        }

        private static string BuildConnectionString(DatabaseConnectionProfile profile)
        {
            MySqlConnectionStringBuilder builder = new MySqlConnectionStringBuilder();
            builder.Server = (profile.Host ?? "").Trim();
            builder.Port = (uint)Math.Max(1, Math.Min(65535, profile.Port));
            builder.UserID = profile.User ?? "";
            builder.Password = profile.Password ?? "";
            builder.Database = profile.Database ?? "";
            builder.CharacterSet = "utf8mb4";
            builder.ConnectionTimeout = 8;
            builder.DefaultCommandTimeout = 30;
            builder.Pooling = false;
            builder.AllowUserVariables = true;
            builder.AllowLoadLocalInfile = false;
            builder.AllowPublicKeyRetrieval = true;
            builder.SslMode = ParseSslMode(profile.SslMode);
            return builder.ConnectionString;
        }

        private static MySqlSslMode ParseSslMode(string value)
        {
            string text = (value ?? "").Trim();
            if (string.Equals(text, "Disabled", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(text, "None", StringComparison.OrdinalIgnoreCase)) return MySqlSslMode.None;
            if (string.Equals(text, "Required", StringComparison.OrdinalIgnoreCase)) return MySqlSslMode.Required;
            if (string.Equals(text, "VerifyCA", StringComparison.OrdinalIgnoreCase)) return MySqlSslMode.VerifyCA;
            if (string.Equals(text, "VerifyFull", StringComparison.OrdinalIgnoreCase)) return MySqlSslMode.VerifyFull;
            return MySqlSslMode.Preferred;
        }

        private void EnsureOpen()
        {
            if (_closed) throw new ObjectDisposedException("MySqlSession");
            if (_connection == null)
            {
                _connection = new MySqlConnection(_connectionString);
                _connection.Open();
                if (_profile.AllowWrites)
                {
                    TryExecuteSession("SET SESSION TRANSACTION READ WRITE");
                }
                else
                {
                    TryExecuteSession("SET SESSION TRANSACTION READ ONLY");
                    TryExecuteSession("SET SESSION MAX_EXECUTION_TIME=30000");
                }
            }
            else if (_connection.State != ConnectionState.Open)
            {
                _connection.Open();
                if (!_profile.AllowWrites) TryExecuteSession("SET SESSION TRANSACTION READ ONLY");
            }
        }

        private void TryExecuteSession(string sql)
        {
            try
            {
                using (MySqlCommand cmd = _connection.CreateCommand())
                {
                    cmd.CommandText = sql;
                    cmd.CommandTimeout = 5;
                    cmd.ExecuteNonQuery();
                }
            }
            catch (Exception)
            {
                // 老版本 / 代理库可能不支持会话变量，SQL 白名单仍然是第一道闸
            }
        }

        public void Close()
        {
            lock (_gate)
            {
                _closed = true;
                _current = null;
                if (_connection != null)
                {
                    try { _connection.Close(); }
                    catch (Exception) { }
                    try { _connection.Dispose(); }
                    catch (Exception) { }
                    _connection = null;
                }
            }
        }

        public void Dispose()
        {
            Close();
        }

        public void CancelCurrent()
        {
            MySqlCommand command = _current;
            if (command == null) return;
            try
            {
                command.Cancel();
            }
            catch (Exception)
            {
                // 取消失败时让 UI 标记为已取消；下一次查询会按连接状态重连
            }
        }

        public DbQueryResult Query(string sql, int maxRows, int timeoutSeconds, out string error)
        {
            return Query(sql, null, maxRows, timeoutSeconds, out error);
        }

        public DbQueryResult Query(string sql, Dictionary<string, object> parameters,
            int maxRows, int timeoutSeconds, out string error)
        {
            error = "";
            DbQueryResult result = new DbQueryResult();
            Stopwatch watch = Stopwatch.StartNew();
            lock (_gate)
            {
                MySqlCommand command = null;
                try
                {
                    EnsureOpen();
                    command = _connection.CreateCommand();
                    command.CommandText = sql;
                    command.CommandTimeout = Math.Max(1, timeoutSeconds);
                    if (parameters != null)
                    {
                        foreach (KeyValuePair<string, object> pair in parameters)
                        {
                            command.Parameters.AddWithValue(pair.Key, pair.Value ?? DBNull.Value);
                        }
                    }
                    _current = command;

                    int affectedRows = -1;
                    using (MySqlDataReader reader = command.ExecuteReader(CommandBehavior.SingleResult))
                    {
                        if (reader.FieldCount > 0)
                        {
                            result.HasResultSet = true;
                            BuildResultTable(reader, maxRows, result);
                        }
                        else
                        {
                            result.HasResultSet = false;
                        }
                        affectedRows = reader.RecordsAffected;
                    }
                    result.AffectedRows = affectedRows;
                    if (!result.HasResultSet)
                    {
                        result.Message = "执行成功，影响 " +
                            (result.AffectedRows < 0 ? 0 : result.AffectedRows) + " 行。";
                    }
                    return result;
                }
                catch (Exception ex)
                {
                    error = Friendly(ex, "执行 SQL 失败");
                    return result;
                }
                finally
                {
                    result.ElapsedMs = watch.ElapsedMilliseconds;
                    _current = null;
                    if (command != null)
                    {
                        try { command.Dispose(); }
                        catch (Exception) { }
                    }
                }
            }
        }

        private void BuildResultTable(MySqlDataReader reader, int maxRows, DbQueryResult result)
        {
            DataTable table = new DataTable();
            int fieldCount = reader.FieldCount;
            for (int i = 0; i < fieldCount; i++)
            {
                string name = MakeUniqueColumnName(table, reader.GetName(i), i);
                table.Columns.Add(name, typeof(object));
                DbColumnInfo column = new DbColumnInfo();
                column.Name = name;
                column.DataType = SafeDataType(reader, i);
                column.ColumnType = column.DataType;
                result.Columns.Add(column);
            }

            int count = 0;
            while (reader.Read())
            {
                if (maxRows > 0 && count >= maxRows)
                {
                    result.Truncated = true;
                    break;
                }
                object[] values = new object[fieldCount];
                for (int i = 0; i < fieldCount; i++)
                {
                    values[i] = ReadCell(reader, i);
                }
                table.Rows.Add(values);
                count++;
            }
            result.Table = table;
        }

        private static string SafeDataType(MySqlDataReader reader, int index)
        {
            try { return reader.GetDataTypeName(index); }
            catch (Exception) { return ""; }
        }

        private static string MakeUniqueColumnName(DataTable table, string name, int index)
        {
            string baseName = string.IsNullOrEmpty(name) ? "column_" + (index + 1) : name;
            string candidate = baseName;
            int suffix = 2;
            while (table.Columns.Contains(candidate))
            {
                candidate = baseName + "_" + suffix;
                suffix++;
            }
            return candidate;
        }

        private static object ReadCell(MySqlDataReader reader, int index)
        {
            if (reader.IsDBNull(index)) return DBNull.Value;
            Type type = reader.GetFieldType(index);
            if (type == typeof(byte[]))
            {
                long total = reader.GetBytes(index, 0, null, 0, 0);
                int take = (int)Math.Min(total, MaxCellBytes);
                byte[] preview = new byte[take];
                if (take > 0) reader.GetBytes(index, 0, preview, 0, take);
                return new DbBinaryValue(preview, total);
            }
            if (type == typeof(string))
            {
                long total = reader.GetChars(index, 0, null, 0, 0);
                if (total > MaxCellChars)
                {
                    char[] preview = new char[MaxCellChars];
                    reader.GetChars(index, 0, preview, 0, preview.Length);
                    return new DbTruncatedText(new string(preview), total);
                }
                return reader.GetString(index);
            }
            try
            {
                return reader.GetValue(index);
            }
            catch (Exception)
            {
                return Convert.ToString(reader.GetValue(index));
            }
        }

        public List<string> GetDatabases(out string error)
        {
            List<string> names = new List<string>();
            DbQueryResult result = Query("SHOW DATABASES", 500, 10, out error);
            if (error.Length > 0) return names;
            for (int i = 0; i < result.Table.Rows.Count; i++)
            {
                string name = Convert.ToString(result.Table.Rows[i][0]);
                if (!string.IsNullOrEmpty(name)) names.Add(name);
            }
            names.Sort(StringComparer.OrdinalIgnoreCase);
            return names;
        }

        public List<DbTableRef> GetTables(string database, out string error)
        {
            List<DbTableRef> tables = new List<DbTableRef>();
            Dictionary<string, object> parameters = new Dictionary<string, object>();
            parameters["@schema"] = database;
            DbQueryResult result = Query(
                "SELECT TABLE_NAME, TABLE_TYPE FROM information_schema.TABLES " +
                "WHERE TABLE_SCHEMA=@schema ORDER BY TABLE_NAME",
                parameters, 5000, 20, out error);
            if (error.Length > 0) return tables;
            for (int i = 0; i < result.Table.Rows.Count; i++)
            {
                DbTableRef table = new DbTableRef();
                table.Database = database;
                table.Table = Convert.ToString(result.Table.Rows[i][0]);
                table.IsView = Convert.ToString(result.Table.Rows[i][1])
                    .IndexOf("VIEW", StringComparison.OrdinalIgnoreCase) >= 0;
                tables.Add(table);
            }
            return tables;
        }

        public List<DbColumnInfo> GetColumns(string database, string tableName, out string error)
        {
            List<DbColumnInfo> columns = new List<DbColumnInfo>();
            Dictionary<string, object> parameters = new Dictionary<string, object>();
            parameters["@schema"] = database;
            parameters["@table"] = tableName;
            DbQueryResult result = Query(
                "SELECT COLUMN_NAME, DATA_TYPE, COLUMN_TYPE, IS_NULLABLE, COLUMN_KEY, EXTRA, " +
                "COLUMN_DEFAULT, ORDINAL_POSITION, COLUMN_COMMENT FROM information_schema.COLUMNS " +
                "WHERE TABLE_SCHEMA=@schema AND TABLE_NAME=@table ORDER BY ORDINAL_POSITION",
                parameters, 2000, 20, out error);
            if (error.Length > 0) return columns;
            for (int i = 0; i < result.Table.Rows.Count; i++)
            {
                DataRow row = result.Table.Rows[i];
                DbColumnInfo column = new DbColumnInfo();
                column.Name = Convert.ToString(row[0]);
                column.DataType = Convert.ToString(row[1]);
                column.ColumnType = Convert.ToString(row[2]);
                column.Nullable = string.Equals(Convert.ToString(row[3]), "YES", StringComparison.OrdinalIgnoreCase);
                column.Key = Convert.ToString(row[4]);
                column.Extra = Convert.ToString(row[5]);
                column.DefaultValue = row[6] == DBNull.Value ? "" : Convert.ToString(row[6]);
                try { column.Ordinal = Convert.ToInt32(row[7]); }
                catch (Exception) { column.Ordinal = i + 1; }
                column.Comment = row[8] == DBNull.Value ? "" : Convert.ToString(row[8]);
                columns.Add(column);
            }
            return columns;
        }

        public string GetCreateTable(DbTableRef table, out string error)
        {
            error = "";
            lock (_gate)
            {
                try
                {
                    EnsureOpen();
                    using (MySqlCommand command = _connection.CreateCommand())
                    {
                        command.CommandText = "SHOW CREATE TABLE " + Qualified(table);
                        command.CommandTimeout = 20;
                        using (MySqlDataReader reader = command.ExecuteReader(CommandBehavior.SingleResult))
                        {
                            if (reader.Read() && reader.FieldCount >= 2)
                            {
                                return reader.GetString(1);
                            }
                            return "";
                        }
                    }
                }
                catch (Exception ex)
                {
                    error = Friendly(ex, "读取建表语句失败");
                    return "";
                }
            }
        }

        public DbQueryResult FindTables(string pattern, int limit, out string error)
        {
            int safeLimit = Math.Max(1, Math.Min(1000, limit));
            Dictionary<string, object> parameters = new Dictionary<string, object>();
            parameters["@pattern"] = "%" + (pattern ?? "").Replace("%", "\\%").Replace("_", "\\_") + "%";
            return Query(
                "SELECT TABLE_SCHEMA, TABLE_NAME, TABLE_TYPE FROM information_schema.TABLES " +
                "WHERE TABLE_NAME LIKE @pattern ESCAPE '\\\\' ORDER BY TABLE_SCHEMA, TABLE_NAME LIMIT " +
                safeLimit, parameters, safeLimit, 20, out error);
        }

        public DbTableData LoadTable(DbTableRef table, int maxRows, int timeoutSeconds, out string error)
        {
            error = "";
            DbTableData data = new DbTableData();
            data.Table = table;
            List<DbColumnInfo> columns = GetColumns(table.Database, table.Table, out error);
            if (error.Length > 0) return data;
            data.Columns = columns;
            for (int i = 0; i < columns.Count; i++)
            {
                if (columns[i].IsPrimaryKey) data.PrimaryKeys.Add(columns[i].Name);
            }

            int safeRows = Math.Max(1, maxRows);
            DbQueryResult result = Query(
                "SELECT * FROM " + Qualified(table) + " LIMIT " + (safeRows + 1),
                safeRows, timeoutSeconds, out error);
            if (error.Length > 0) return data;
            data.Data = result.Table;
            data.Truncated = result.Truncated;
            data.Message = "读取 " + result.Table.Rows.Count + " 行，耗时 " + result.ElapsedMs + " ms";
            data.Editable = _profile.AllowWrites && !table.IsView && data.PrimaryKeys.Count > 0 &&
                !HasBinaryPrimaryKey(columns);
            return data;
        }

        private static bool HasBinaryPrimaryKey(List<DbColumnInfo> columns)
        {
            for (int i = 0; i < columns.Count; i++)
            {
                if (columns[i].IsPrimaryKey && columns[i].IsBinary) return true;
            }
            return false;
        }

        public DbSaveResult SaveTable(DbTableRef table, DataTable changes,
            List<DbColumnInfo> columns, out string error)
        {
            error = "";
            DbSaveResult result = new DbSaveResult();
            lock (_gate)
            {
                MySqlTransaction transaction = null;
                try
                {
                    EnsureOpen();
                    transaction = _connection.BeginTransaction();
                    int affected = 0;
                    if (changes != null)
                    {
                        for (int i = 0; i < changes.Rows.Count; i++)
                        {
                            DataRow row = changes.Rows[i];
                            if (row.RowState == DataRowState.Added)
                            {
                                affected += ExecuteInsert(table, columns, row, transaction);
                            }
                            else if (row.RowState == DataRowState.Modified)
                            {
                                affected += ExecuteUpdate(table, columns, row, transaction);
                            }
                            else if (row.RowState == DataRowState.Deleted)
                            {
                                affected += ExecuteDelete(table, columns, row, transaction);
                            }
                        }
                    }
                    transaction.Commit();
                    result.Success = true;
                    result.Affected = affected;
                    return result;
                }
                catch (Exception ex)
                {
                    if (transaction != null)
                    {
                        try { transaction.Rollback(); }
                        catch (Exception) { }
                    }
                    error = Friendly(ex, "保存表数据失败（已回滚）");
                    result.Error = error;
                    return result;
                }
                finally
                {
                    if (transaction != null)
                    {
                        try { transaction.Dispose(); }
                        catch (Exception) { }
                    }
                }
            }
        }

        private int ExecuteInsert(DbTableRef table, List<DbColumnInfo> columns, DataRow row,
            MySqlTransaction transaction)
        {
            List<string> names = new List<string>();
            List<object> values = new List<object>();
            for (int i = 0; i < columns.Count; i++)
            {
                DbColumnInfo column = columns[i];
                if (column.IsAutoIncrement || column.IsGenerated) continue;
                object value = row[column.Name];
                if (value == DBNull.Value) continue;
                names.Add(QuoteIdentifier(column.Name));
                values.Add(CleanValue(value, column.Name));
            }

            using (MySqlCommand command = _connection.CreateCommand())
            {
                command.Transaction = transaction;
                string columnSql = names.Count == 0 ? "()" : "(" + string.Join(", ", names.ToArray()) + ")";
                string valueSql = names.Count == 0 ? "()" : "(" + ParameterList(values.Count) + ")";
                command.CommandText = "INSERT INTO " + Qualified(table) + " " + columnSql + " VALUES " + valueSql;
                for (int i = 0; i < values.Count; i++)
                {
                    command.Parameters.AddWithValue("@p" + i, values[i]);
                }
                return command.ExecuteNonQuery();
            }
        }

        private int ExecuteUpdate(DbTableRef table, List<DbColumnInfo> columns, DataRow row,
            MySqlTransaction transaction)
        {
            List<string> sets = new List<string>();
            List<object> values = new List<object>();
            for (int i = 0; i < columns.Count; i++)
            {
                DbColumnInfo column = columns[i];
                if (column.IsGenerated) continue;
                object current = row[column.Name];
                object original = row[column.Name, DataRowVersion.Original];
                if (ValuesEqual(current, original)) continue;
                sets.Add(QuoteIdentifier(column.Name) + "=@p" + values.Count);
                values.Add(CleanValue(current, column.Name));
            }
            if (sets.Count == 0) return 0;

            List<string> where = new List<string>();
            for (int i = 0; i < columns.Count; i++)
            {
                DbColumnInfo column = columns[i];
                if (!column.IsPrimaryKey) continue;
                object original = row[column.Name, DataRowVersion.Original];
                if (original == null || original == DBNull.Value)
                {
                    throw new InvalidOperationException("主键 " + column.Name + " 为空，无法定位要更新的行。");
                }
                where.Add(QuoteIdentifier(column.Name) + "=@k" + where.Count);
                values.Add(CleanValue(original, column.Name));
            }
            if (where.Count == 0) throw new InvalidOperationException("表没有主键，不能安全更新。");

            using (MySqlCommand command = _connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = "UPDATE " + Qualified(table) + " SET " +
                    string.Join(", ", sets.ToArray()) + " WHERE " + string.Join(" AND ", where.ToArray());
                int p = 0;
                for (int i = 0; i < sets.Count; i++) command.Parameters.AddWithValue("@p" + i, values[p++]);
                for (int i = 0; i < where.Count; i++) command.Parameters.AddWithValue("@k" + i, values[p++]);
                return command.ExecuteNonQuery();
            }
        }

        private int ExecuteDelete(DbTableRef table, List<DbColumnInfo> columns, DataRow row,
            MySqlTransaction transaction)
        {
            List<string> where = new List<string>();
            List<object> values = new List<object>();
            for (int i = 0; i < columns.Count; i++)
            {
                DbColumnInfo column = columns[i];
                if (!column.IsPrimaryKey) continue;
                object original = row[column.Name, DataRowVersion.Original];
                if (original == null || original == DBNull.Value)
                {
                    throw new InvalidOperationException("主键 " + column.Name + " 为空，无法删除。");
                }
                where.Add(QuoteIdentifier(column.Name) + "=@k" + where.Count);
                values.Add(CleanValue(original, column.Name));
            }
            if (where.Count == 0) throw new InvalidOperationException("表没有主键，不能安全删除。");

            using (MySqlCommand command = _connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = "DELETE FROM " + Qualified(table) + " WHERE " +
                    string.Join(" AND ", where.ToArray());
                for (int i = 0; i < values.Count; i++)
                {
                    command.Parameters.AddWithValue("@k" + i, values[i]);
                }
                return command.ExecuteNonQuery();
            }
        }

        private static object CleanValue(object value, string column)
        {
            if (value == null || value == DBNull.Value) return DBNull.Value;
            if (value is DbBinaryValue || value is DbTruncatedText || value is byte[])
            {
                throw new InvalidOperationException("字段 " + column + " 是二进制或超大字段，不能通过表格保存。");
            }
            return value;
        }

        private static bool ValuesEqual(object left, object right)
        {
            if (left == null || left == DBNull.Value)
            {
                return right == null || right == DBNull.Value;
            }
            if (right == null || right == DBNull.Value) return false;
            return left.Equals(right);
        }

        private static string ParameterList(int count)
        {
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < count; i++)
            {
                if (i > 0) sb.Append(", ");
                sb.Append("@p").Append(i);
            }
            return sb.ToString();
        }

        private static string Qualified(DbTableRef table)
        {
            return QuoteIdentifier(table.Database) + "." + QuoteIdentifier(table.Table);
        }

        public static string QuoteIdentifier(string identifier)
        {
            string value = identifier ?? "";
            value = value.Replace("`", "``").Replace("\0", "");
            return "`" + value + "`";
        }

        private static string Friendly(Exception ex, string prefix)
        {
            MySqlException mysql = ex as MySqlException;
            if (mysql != null)
            {
                if (mysql.Number == 1317) return "查询已取消。";
                if (mysql.Number == 3024) return "查询超时（超过最大执行时间）。";
                string message = mysql.Message ?? "";
                if (message.IndexOf("timeout", StringComparison.OrdinalIgnoreCase) >= 0)
                    return prefix + "：查询超时。";
                return prefix + "：" + message;
            }
            return prefix + "：" + ex.Message;
        }
    }

    internal sealed class MySqlProvider : IDatabaseProvider
    {
        public string Kind
        {
            get { return DatabaseKinds.MySql; }
        }

        public string DisplayName
        {
            get { return "MySQL"; }
        }

        public int DefaultPort
        {
            get { return 3306; }
        }

        public bool SupportsWrites
        {
            get { return true; }
        }

        public IDatabaseConnection Open(DatabaseConnectionProfile profile, out string error)
        {
            return MySqlSession.Open(profile, out error);
        }

        public IDatabaseWorkspace CreateWorkspace(DatabaseTabHost host,
            DatabaseConnectionProfile profile, IDatabaseConnection connection)
        {
            return new MySqlWorkspace(host, profile, (MySqlSession)connection);
        }
    }
}
