using System;
using System.Collections.Generic;

namespace Curl2Url
{
    // SQL 关键字表：语法高亮和"关键字转小写"共用一份，两边不会走样。
    internal static class SqlKeywords
    {
        public static readonly HashSet<string> Set = new HashSet<string>(
            new string[]
            {
                "SELECT", "FROM", "WHERE", "AND", "OR", "NOT", "XOR", "IN", "IS", "NULL",
                "LIKE", "RLIKE", "REGEXP", "BETWEEN", "EXISTS", "ALL", "ANY", "DISTINCT",
                "GROUP", "BY", "ORDER", "HAVING", "LIMIT", "OFFSET", "ASC", "DESC",
                "JOIN", "INNER", "LEFT", "RIGHT", "OUTER", "CROSS", "NATURAL", "ON", "USING",
                "UNION", "INTERSECT", "EXCEPT", "AS", "WITH", "RECURSIVE",
                "INSERT", "INTO", "VALUES", "VALUE", "UPDATE", "SET", "DELETE", "REPLACE",
                "CREATE", "ALTER", "DROP", "TRUNCATE", "RENAME", "TABLE", "VIEW", "INDEX",
                "DATABASE", "SCHEMA", "TEMPORARY", "IF", "ELSE", "THEN", "CASE", "WHEN", "END",
                "PRIMARY", "KEY", "FOREIGN", "REFERENCES", "UNIQUE", "FULLTEXT", "SPATIAL",
                "CONSTRAINT", "DEFAULT", "AUTO_INCREMENT", "COMMENT", "ENGINE", "CHARSET",
                "COLLATE", "CHARACTER", "ADD", "COLUMN", "MODIFY", "CHANGE",
                // 建表语句里常见的其余关键字，转小写后更好读
                "ROW_FORMAT", "BTREE", "HASH", "DYNAMIC", "COMPACT", "REDUNDANT", "COMPRESSED",
                "CASCADE", "RESTRICT", "ACTION", "CURRENT_TIMESTAMP", "TRUE", "FALSE",
                "BEGIN", "START", "TRANSACTION", "COMMIT", "ROLLBACK", "SAVEPOINT",
                "SHOW", "DESCRIBE", "EXPLAIN", "USE", "GRANT", "REVOKE",
                "COUNT", "SUM", "AVG", "MIN", "MAX", "IFNULL", "COALESCE", "CONCAT",
                "NOW", "DATE", "TIME", "YEAR", "MONTH", "DAY", "INTERVAL", "CAST", "CONVERT",
                "SUBSTRING", "TRIM", "LOWER", "UPPER", "LENGTH", "ROUND",
                "INT", "BIGINT", "SMALLINT", "TINYINT", "MEDIUMINT", "DECIMAL", "NUMERIC",
                "FLOAT", "DOUBLE", "BOOLEAN", "BOOL",
                "VARCHAR", "CHAR", "TEXT", "TINYTEXT", "MEDIUMTEXT", "LONGTEXT",
                "BLOB", "TINYBLOB", "MEDIUMBLOB", "LONGBLOB", "JSON", "DATETIME", "TIMESTAMP",
                "UNSIGNED", "SIGNED", "ZEROFILL"
            },
            StringComparer.OrdinalIgnoreCase);
    }
}
