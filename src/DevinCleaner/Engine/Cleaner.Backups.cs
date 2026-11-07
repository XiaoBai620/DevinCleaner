using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace DevinCleaner.Engine;

public sealed partial class Cleaner
{
    [GeneratedRegex(@"^sessions-\d{8}-\d{6}-[0-9a-f]{8}\.db\z")]
    private static partial Regex BackupName();

    public IReadOnlyList<BackupInfo> Backups()
    {
        if (!Directory.Exists(BackupRoot)) return [];
        var result = new List<BackupInfo>();
        foreach (var file in new DirectoryInfo(BackupRoot).EnumerateFiles("sessions-*.db").Where(f => BackupName().IsMatch(f.Name)).OrderByDescending(f => f.Name, StringComparer.Ordinal))
        {
            try
            {
                using var db = OpenBackup(file.FullName);
                var sessions = Db.Rows(db, "SELECT s.id,s.title,s.working_directory,s.model,s.created_at,s.last_activity_at,s.hidden,(SELECT count(*) FROM bak.message_nodes m WHERE m.session_id=s.id) FROM bak.sessions s ORDER BY s.last_activity_at DESC")
                    .Select(r => new SessionInfo((string)r[0]!, r[1] as string is { Length: > 0 } t ? t : "未命名会话", (string)r[2]!, (string)r[3]!,
                        DateTimeOffset.FromUnixTimeSeconds((long)r[4]!), DateTimeOffset.FromUnixTimeSeconds((long)r[5]!), (long)r[6]! != 0, (long)r[7]!, 0, false,
                        Db.Scalar(db, "SELECT 1 FROM main.sessions WHERE id=$p0", [r[0]!]) is null ? null : "当前数据库中已有同 ID 的会话"))
                    .ToList();
                result.Add(new BackupInfo(file.FullName, file.CreationTime, file.Length, sessions, CompatibilityProblem(db)));
            }
            catch (Exception e) when (e is SqliteException or CleanerException or IOException)
            {
                result.Add(new BackupInfo(file.FullName, file.CreationTime, file.Length, [], $"无法读取备份：{e.Message}"));
            }
        }
        return result;
    }

    private string CheckBackupPath(string file)
    {
        var full = Path.GetFullPath(file);
        if (!Inside(BackupRoot, full) || !BackupName().IsMatch(Path.GetFileName(full)) || !File.Exists(full)) throw new CleanerException("只能使用本工具备份目录中的备份文件。");
        return full;
    }

    // query_only on the connection also covers the attached backup.
    private SqliteConnection OpenBackup(string file)
    {
        var full = CheckBackupPath(file);
        var db = OpenRead();
        try
        {
            Db.Exec(db, "ATTACH DATABASE $p0 AS bak", [full]);
            return db;
        }
        catch { db.Dispose(); throw; }
    }

    // A backup can only be restored into the exact table layout it was taken from.
    private static string? CompatibilityProblem(SqliteConnection db)
    {
        if (!SchemaSupported(db)) return "当前数据库结构未验证，不能恢复。";
        foreach (var table in Catalog.BackupTables)
            if (Db.TableSql(db, "bak", table) is not { } sql || sql != Db.TableSql(db, "main", table)) return $"备份中的 {table} 表结构与当前数据库不同，不能恢复。";
        var version = "SELECT coalesce(max(version),0) FROM {0}.refinery_schema_history";
        if (Db.Long(db, string.Format(version, "bak")) != Db.Long(db, string.Format(version, "main"))) return "备份来自其他版本的 Devin，不能恢复。";
        return null;
    }

    public RestoreResult Restore(string backupFile, IReadOnlyList<string> sessionIds) => Exclusive(() =>
    {
        var ids = sessionIds.Distinct(StringComparer.Ordinal).ToList();
        if (ids.Count == 0) throw new CleanerException("没有选择要恢复的会话。");
        var full = CheckBackupPath(backupFile);
        var held = new List<SessionLocks.Held>();
        try
        {
            foreach (var id in ids) held.Add(SessionLocks.Acquire(LockFile(id)));
            using var db = Db.Open(DatabasePath, writable: true);
            Db.Exec(db, "ATTACH DATABASE $p0 AS bak", [full]);
            if (CompatibilityProblem(db) is { } problem) throw new CleanerException(problem);
            var skipped = new List<string>();
            var restored = 0;
            using var transaction = db.BeginTransaction(deferred: false);
            foreach (var id in ids)
            {
                object[] args = [id];
                if (Db.Scalar(db, "SELECT 1 FROM bak.sessions WHERE id=$p0", args) is null) throw new CleanerException($"备份中没有会话 {id}。");
                if (Db.Scalar(db, "SELECT 1 FROM main.sessions WHERE id=$p0", args) is not null) { skipped.Add(id); continue; }
                Db.Exec(db, "INSERT INTO main.sessions SELECT * FROM bak.sessions WHERE id=$p0", args);
                foreach (var table in Catalog.ChildTables)
                {
                    var expected = Db.Long(db, $"SELECT count(*) FROM bak.{table} WHERE session_id=$p0", args);
                    Db.Exec(db, $"INSERT INTO main.{table} SELECT * FROM bak.{table} WHERE session_id=$p0", args);
                    if (Db.Long(db, $"SELECT count(*) FROM main.{table} WHERE session_id=$p0", args) != expected) throw new CleanerException($"恢复校验失败：{id}/{table}");
                }
                restored++;
            }
            transaction.Commit();
            return new RestoreResult(restored, skipped);
        }
        finally
        {
            foreach (var h in held) h.Dispose();
        }
    });

    public void DeleteBackup(string backupFile) => Exclusive(() =>
    {
        File.Delete(CheckBackupPath(backupFile));
        return 0;
    });
}
