using Microsoft.Data.Sqlite;

namespace DevinCleaner.Engine;

public sealed partial class Cleaner
{
    private readonly Func<ProcessState> processCheck;
    private readonly Dictionary<string, PlanState> plans = [];
    private readonly Lock gate = new();
    private bool busy;
    internal TestHooks Hooks { get; } = new();

    public string DataRoot { get; }
    public string BackupRoot { get; }
    public string DatabasePath => Path.Combine(DataRoot, Catalog.Database);
    public string LockRoot => Path.Combine(DataRoot, Catalog.LockDirectory);

    public Cleaner(string dataRoot, string backupRoot, Func<ProcessState>? processCheck = null)
    {
        DataRoot = Path.GetFullPath(dataRoot);
        BackupRoot = Path.GetFullPath(backupRoot);
        if (Inside(DataRoot, BackupRoot) || Inside(BackupRoot, DataRoot)) throw new CleanerException("备份目录和 Devin 数据目录不能互相包含。");
        if (!File.Exists(DatabasePath)) throw new CleanerException($"找不到 Devin 会话数据库：{DatabasePath}");
        this.processCheck = processCheck ?? DevinProcesses.Detect;
    }

    public static string DefaultDataRoot() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "devin", "cli");

    public static string DefaultBackupRoot() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DevinCleaner", "Backups");

    public ProcessState CheckProcesses() => processCheck();

    internal static bool Inside(string root, string target)
    {
        var rel = Path.GetRelativePath(root, target);
        return rel == "." || (rel != ".." && !rel.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) && !Path.IsPathRooted(rel));
    }

    internal string LockFile(string id) =>
        Ids.Valid(id) ? Path.Combine(LockRoot, id + ".lock") : throw new CleanerException($"会话 ID 无效：{id}");

    public bool IsRunning(string id) => SessionLocks.IsHeld(LockFile(id));

    // Readers may run concurrently with each other; writes take the gate exclusively.
    private T Exclusive<T>(Func<T> operation)
    {
        lock (gate)
        {
            if (busy) throw new CleanerException("另一项操作正在进行，请稍候。");
            busy = true;
        }
        try { return operation(); }
        finally { lock (gate) busy = false; }
    }

    // Read connections still open read-write so a WAL database without -shm can be read;
    // query_only blocks every write statement on them.
    private SqliteConnection OpenRead()
    {
        var db = Db.Open(DatabasePath, writable: true);
        Db.Exec(db, "PRAGMA query_only=ON");
        return db;
    }

    private static bool SchemaSupported(SqliteConnection db) => Db.SchemaHash(db) == Catalog.SchemaHash;

    public Snapshot Snapshot()
    {
        Hooks.DuringRead?.Invoke();
        var warnings = new List<string>();
        using var db = OpenRead();
        var supported = SchemaSupported(db);
        if (!supported) warnings.Add("sessions.db 的结构与已验证版本不同（Devin 可能已更新），只允许浏览，不能删除。");
        var stats = Db.Rows(db, "SELECT session_id, count(*), coalesce(sum(length(CAST(chat_message AS BLOB))),0) FROM message_nodes GROUP BY session_id")
            .ToDictionary(r => (string)r[0]!, r => ((long)r[1]!, (long)r[2]!));
        var sessions = new List<SessionInfo>();
        foreach (var r in Db.Rows(db, "SELECT id,title,working_directory,model,created_at,last_activity_at,hidden FROM sessions ORDER BY last_activity_at DESC"))
        {
            var id = (string)r[0]!;
            var valid = Ids.Valid(id);
            var running = valid && IsRunning(id);
            var (messages, bytes) = stats.GetValueOrDefault(id);
            var reason = !supported ? "数据库结构未验证，只允许浏览。"
                : !valid ? "会话 ID 含有无法识别的字符，暂不支持清理。"
                : running ? "此会话正在 Devin 中运行，关闭后才能清理。" : null;
            sessions.Add(new SessionInfo(id, r[1] as string is { Length: > 0 } title ? title : "未命名会话", (string)r[2]!, (string)r[3]!,
                DateTimeOffset.FromUnixTimeSeconds((long)r[4]!), DateTimeOffset.FromUnixTimeSeconds((long)r[5]!), (long)r[6]! != 0,
                messages, bytes, running, reason));
        }
        var orphans = OrphanLocks(sessions.Select(s => s.Id));
        return new Snapshot(sessions, supported, orphans, DatabaseBytes(), warnings);
    }

    internal long DatabaseBytes() =>
        Catalog.SqliteSuffixes.Select(s => new FileInfo(DatabasePath + s)).Where(f => f.Exists).Sum(f => f.Length);

    private List<string> OrphanLocks(IEnumerable<string> sessionIds)
    {
        if (!Directory.Exists(LockRoot)) return [];
        var known = sessionIds.ToHashSet(StringComparer.Ordinal);
        return new DirectoryInfo(LockRoot).EnumerateFiles("*.lock")
            .Where(f => !f.Attributes.HasFlag(FileAttributes.ReparsePoint))
            .Select(f => Path.GetFileNameWithoutExtension(f.Name))
            .Where(id => Ids.Valid(id) && !known.Contains(id) && !IsRunning(id))
            .Order(StringComparer.Ordinal).ToList();
    }

    internal static SessionStamp Stamp(SqliteConnection db, string id)
    {
        object[] args = [id];
        var last = Db.Scalar(db, "SELECT last_activity_at FROM sessions WHERE id=$p0", args) as long?
            ?? throw new CleanerException($"会话 {id} 已不存在，请刷新后重新选择。");
        var messages = Db.Rows(db, "SELECT count(*), coalesce(max(row_id),0) FROM message_nodes WHERE session_id=$p0", args)[0];
        return new SessionStamp(last, (long)messages[0]!, (long)messages[1]!,
            Db.Long(db, "SELECT count(*) FROM tool_call_state WHERE session_id=$p0", args),
            Db.Long(db, "SELECT count(*) FROM prompt_history WHERE session_id=$p0", args),
            Db.Long(db, "SELECT count(*) FROM rendered_commits WHERE session_id=$p0", args));
    }

    internal static long RowsOf(SessionStamp stamp, string table) => table switch
    {
        Catalog.SessionTable => 1,
        "message_nodes" => stamp.Messages,
        "tool_call_state" => stamp.ToolCalls,
        "prompt_history" => stamp.Prompts,
        "rendered_commits" => stamp.Commits,
        _ => throw new ArgumentOutOfRangeException(nameof(table)),
    };
}
