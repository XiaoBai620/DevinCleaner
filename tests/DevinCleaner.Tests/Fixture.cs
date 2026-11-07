using System.Text.Json;
using DevinCleaner.Engine;
using Microsoft.Data.Sqlite;

namespace DevinCleaner.Tests;

// Synthetic data only. Never point this fixture at a real Devin data directory.
public sealed class Fixture : IDisposable
{
    public static readonly string[] Ids = ["alpha-session", "beta-session", "gamma-session"];
    private const string Prefix = "devin-cleaner-test-";

    private static readonly List<SchemaObject> Schema =
        JsonSerializer.Deserialize<List<SchemaObject>>(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixture-schema.json")), new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;

    public string Base { get; }
    public string DataRoot { get; }
    public string BackupRoot { get; }
    public bool Running { get; set; }
    public Cleaner Cleaner { get; }

    public Fixture()
    {
        Base = Directory.CreateTempSubdirectory(Prefix).FullName;
        DataRoot = Path.Combine(Base, "synthetic-devin");
        BackupRoot = Path.Combine(Base, "backups");
        try
        {
            Populate();
            Cleaner = new Cleaner(DataRoot, BackupRoot, () => new ProcessState(Running, Running ? ["devin (42)"] : []));
        }
        catch { Dispose(); throw; }
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        var full = Path.GetFullPath(Base);
        Assert.Equal(Path.GetFullPath(Path.GetTempPath()).TrimEnd('\\'), Path.GetDirectoryName(full));
        Assert.StartsWith(Prefix, Path.GetFileName(full));
        if (Directory.Exists(full)) Directory.Delete(full, true);
    }

    private void Populate()
    {
        Directory.CreateDirectory(Path.Combine(DataRoot, "session_locks"));
        using var db = Open();
        Exec(db, "PRAGMA journal_mode=WAL");
        foreach (var type in new[] { "table", "index" })
            foreach (var o in Schema.Where(x => x.Type == type)) Exec(db, o.Sql);
        for (var v = 1; v <= 16; v++) Exec(db, "INSERT INTO refinery_schema_history VALUES ($p0,$p1,'2026-01-01','0')", v, $"migration_{v}");
        for (var i = 0; i < Ids.Length; i++)
        {
            Exec(db, "INSERT INTO sessions (id,working_directory,backend_type,model,agent_mode,created_at,last_activity_at,title,hidden) VALUES ($p0,$p1,'local','synthetic-model','normal',$p2,$p3,$p4,$p5)",
                Ids[i], $@"C:\synthetic\project-{i}", 1780000000L + i, 1780001000L + i, $"Synthetic chat {i + 1}", i == 2 ? 1 : 0);
            for (var n = 0; n < 3; n++)
                Exec(db, "INSERT INTO message_nodes (session_id,node_id,parent_node_id,chat_message,created_at) VALUES ($p0,$p1,$p2,$p3,1)", Ids[i], n, n == 0 ? null : n - 1, $"{{\"text\":\"invented message {n}\"}}");
            Exec(db, "INSERT INTO tool_call_state VALUES ($p0,'call-1','{}','{}')", Ids[i]);
            Exec(db, "INSERT INTO prompt_history (content,timestamp,session_id) VALUES ('invented prompt',1,$p0)", Ids[i]);
            Exec(db, "INSERT INTO rendered_commits (session_id,sequence_number,rendered_html,created_at) VALUES ($p0,1,'<p>invented</p>',1)", Ids[i]);
            File.WriteAllText(LockPath(Ids[i]), "1\n");
        }
        Exec(db, "INSERT INTO app_state VALUES ('schema_compat_version','0')");
        File.WriteAllText(LockPath("orphan-one"), "999999\n");
        File.WriteAllText(LockPath("orphan-two"), "999999\n");
    }

    public string LockPath(string id) => Path.Combine(DataRoot, "session_locks", id + ".lock");

    // Mirrors Devin: an active session keeps its lock file open.
    public FileStream HoldLock(string id) => new(LockPath(id), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read);

    public SqliteConnection Open()
    {
        var db = new SqliteConnection($"Data Source={Path.Combine(DataRoot, "sessions.db")};Pooling=False");
        db.Open();
        return db;
    }

    public static void Exec(SqliteConnection db, string sql, params object?[] args)
    {
        using var command = db.CreateCommand();
        command.CommandText = sql;
        for (var i = 0; i < args.Length; i++) command.Parameters.AddWithValue("$p" + i, args[i] ?? DBNull.Value);
        command.ExecuteNonQuery();
    }

    public long Count(string table, string id)
    {
        using var db = Open();
        using var command = db.CreateCommand();
        command.CommandText = $"SELECT count(*) FROM {table} WHERE {(table == "sessions" ? "id" : "session_id")}=$p0";
        command.Parameters.AddWithValue("$p0", id);
        return (long)command.ExecuteScalar()!;
    }

    public static readonly string[] SessionTables = ["sessions", "message_nodes", "tool_call_state", "prompt_history", "rendered_commits"];

    public long Total(string id) => SessionTables.Sum(t => Count(t, id));

    private sealed record SchemaObject(string Name, string Type, string Sql);
}
