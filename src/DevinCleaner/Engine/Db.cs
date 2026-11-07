using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;

namespace DevinCleaner.Engine;

internal static class Db
{
    public static SqliteConnection Open(string file, bool writable)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = file,
            Mode = writable ? SqliteOpenMode.ReadWrite : SqliteOpenMode.ReadOnly,
            Pooling = false,
            DefaultTimeout = 15,
        }.ToString());
        try
        {
            connection.Open();
            if (!writable) Exec(connection, "PRAGMA query_only=ON");
            return connection;
        }
        catch { connection.Dispose(); throw; }
    }

    public static string SchemaHash(SqliteConnection db, string schema = "main")
    {
        var lines = Rows(db, $"SELECT type,name,sql FROM {schema}.sqlite_schema WHERE type IN ('table','trigger') AND name NOT LIKE 'sqlite_%'")
            .Select(r => (Key: $"{r[0]}:{r[1]}", Line: $"{r[0]}:{r[1]}:{((string)r[2]!).Replace("\r\n", "\n").Trim()}"))
            .OrderBy(x => x.Key, StringComparer.Ordinal)
            .Select(x => x.Line);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', lines))));
    }

    public static string? TableSql(SqliteConnection db, string schema, string table) =>
        Scalar(db, $"SELECT sql FROM {schema}.sqlite_schema WHERE type='table' AND name=$p0", [table]) as string;

    public static int Exec(SqliteConnection db, string sql, IReadOnlyList<object>? args = null)
    {
        using var command = Command(db, sql, args);
        return command.ExecuteNonQuery();
    }

    public static object? Scalar(SqliteConnection db, string sql, IReadOnlyList<object>? args = null)
    {
        using var command = Command(db, sql, args);
        return command.ExecuteScalar();
    }

    public static long Long(SqliteConnection db, string sql, IReadOnlyList<object>? args = null) => (long)Scalar(db, sql, args)!;

    public static List<object?[]> Rows(SqliteConnection db, string sql, IReadOnlyList<object>? args = null)
    {
        using var command = Command(db, sql, args);
        using var reader = command.ExecuteReader();
        var rows = new List<object?[]>();
        while (reader.Read())
        {
            var row = new object?[reader.FieldCount];
            for (var i = 0; i < row.Length; i++) row[i] = reader.IsDBNull(i) ? null : reader.GetValue(i);
            rows.Add(row);
        }
        return rows;
    }

    // Placeholders are named $p0..$pN in the order the arguments are listed.
    public static string In(int count, int offset = 0) => string.Join(',', Enumerable.Range(offset, count).Select(i => "$p" + i));

    private static SqliteCommand Command(SqliteConnection db, string sql, IReadOnlyList<object>? args)
    {
        var command = db.CreateCommand();
        command.CommandText = sql;
        for (var i = 0; i < (args?.Count ?? 0); i++) command.Parameters.AddWithValue("$p" + i, args![i]);
        return command;
    }
}
