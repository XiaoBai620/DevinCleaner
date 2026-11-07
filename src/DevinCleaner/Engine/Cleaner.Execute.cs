using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace DevinCleaner.Engine;

public sealed partial class Cleaner
{
    public ExecuteResult Execute(string planId) => Exclusive(() =>
    {
        PlanState? state;
        lock (gate) plans.TryGetValue(planId, out state);
        if (state is null || state.Public.ExpiresAt < DateTimeOffset.Now) throw new CleanerException("清理预览无效或已过期，请重新预览。");
        var plan = state.Public;
        var ids = plan.Selection.SessionIds;
        var held = new List<SessionLocks.Held>();
        string? backup = null;
        var before = DatabaseBytes();
        try
        {
            // Holding each session lock stops Devin from resuming the session while it is removed.
            foreach (var id in ids) held.Add(SessionLocks.Acquire(LockFile(id)));
            if (ids.Count > 0)
            {
                backup = WriteBackup(state);
                Delete(state);
                foreach (var h in held) h.RemoveOnRelease = true;
            }
        }
        catch (Exception e) when (backup is not null)
        {
            TryDelete(backup);
            throw new CleanerException($"{e.Message} 未删除任何会话，临时备份已移除。", e);
        }
        finally
        {
            foreach (var h in held) h.Dispose();
        }
        lock (gate) plans.Remove(planId);

        var removed = plan.LockFiles.Count == 0 ? 0 : RemoveOrphanLocks(plan.LockFiles);
        string? compactError = null;
        if (plan.Selection.Compact)
        {
            try { Compact(); }
            catch (SqliteException e) { compactError = $"压缩未完成（Devin 可能正在使用数据库，关闭 Devin 后可单独重试）：{e.Message}"; }
        }
        return new ExecuteResult(backup, ids.Count, removed, before, DatabaseBytes(), compactError);
    });

    private void AssertUnchanged(SqliteConnection db, PlanState state)
    {
        if (!SchemaSupported(db)) throw new CleanerException("数据库结构在预览后发生变化，请重新预览。");
        foreach (var (id, stamp) in state.Stamps)
            if (Stamp(db, id) != stamp) throw new CleanerException($"会话 {id} 在预览后有新的变化，请刷新后重新预览。");
    }

    [GeneratedRegex(@"^CREATE TABLE\s+")]
    private static partial Regex CreateTable();

    // The backup is committed and verified before the source is touched.
    private string WriteBackup(PlanState state)
    {
        Directory.CreateDirectory(BackupRoot);
        var file = Path.Combine(BackupRoot, $"sessions-{DateTimeOffset.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("N")[..8]}.db");
        try
        {
            // The source connection is opened without CREATE, which ATTACH inherits; create the file separately.
            using (var created = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = file, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false }.ToString()))
            {
                created.Open();
                Db.Exec(created, "PRAGMA user_version=1");
            }
            using var db = Db.Open(DatabasePath, writable: true);
            AssertUnchanged(db, state);
            Db.Exec(db, "ATTACH DATABASE $p0 AS bak", [file]);
            var ids = state.Public.Selection.SessionIds.Cast<object>().ToList();
            var inList = Db.In(ids.Count);
            using (var transaction = db.BeginTransaction())
            {
                foreach (var table in Catalog.BackupTables)
                {
                    var sql = Db.TableSql(db, "main", table) ?? throw new CleanerException($"数据库缺少表 {table}。");
                    Db.Exec(db, CreateTable().Replace(sql, "CREATE TABLE bak.", 1));
                    var where = table == Catalog.MigrationTable ? "" : $" WHERE {(table == Catalog.SessionTable ? "id" : "session_id")} IN ({inList})";
                    Db.Exec(db, $"INSERT INTO bak.{table} SELECT * FROM main.{table}{where}", table == Catalog.MigrationTable ? null : ids);
                }
                transaction.Commit();
            }
            foreach (var (id, stamp) in state.Stamps)
                foreach (var table in Catalog.ChildTables.Append(Catalog.SessionTable))
                {
                    var column = table == Catalog.SessionTable ? "id" : "session_id";
                    if (Db.Long(db, $"SELECT count(*) FROM bak.{table} WHERE {column}=$p0", [id]) != RowsOf(stamp, table))
                        throw new CleanerException($"备份校验失败：{id}/{table}");
                }
            if (Db.Scalar(db, "PRAGMA bak.quick_check") as string != "ok") throw new CleanerException("备份文件一致性校验失败。");
            Db.Exec(db, "DETACH DATABASE bak");
            Hooks.AfterBackup?.Invoke();
            return file;
        }
        catch
        {
            TryDelete(file);
            throw;
        }
    }

    private void Delete(PlanState state)
    {
        var ids = state.Public.Selection.SessionIds.Cast<object>().ToList();
        var inList = Db.In(ids.Count);
        using var db = Db.Open(DatabasePath, writable: true);
        using var transaction = db.BeginTransaction(deferred: false);
        AssertUnchanged(db, state);
        foreach (var table in Catalog.ChildTables)
            Db.Exec(db, $"DELETE FROM {table} WHERE session_id IN ({inList})", ids);
        Db.Exec(db, $"DELETE FROM {Catalog.SessionTable} WHERE id IN ({inList})", ids);
        foreach (var table in Catalog.ChildTables.Append(Catalog.SessionTable))
            if (Db.Long(db, $"SELECT count(*) FROM {table} WHERE {(table == Catalog.SessionTable ? "id" : "session_id")} IN ({inList})", ids) != 0)
                throw new CleanerException($"删除结果校验失败：{table}");
        Hooks.BeforeCommit?.Invoke();
        transaction.Commit();
    }

    private int RemoveOrphanLocks(IReadOnlyList<string> ids)
    {
        using var db = OpenRead();
        var removed = 0;
        foreach (var id in ids)
        {
            var file = LockFile(id);
            if (!File.Exists(file) || Db.Scalar(db, "SELECT 1 FROM sessions WHERE id=$p0", [id]) is not null || SessionLocks.IsHeld(file)) continue;
            try { File.Delete(file); removed++; }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
        return removed;
    }

    private void Compact()
    {
        using var db = Db.Open(DatabasePath, writable: true);
        Db.Exec(db, "VACUUM");
        Db.Exec(db, "PRAGMA wal_checkpoint(TRUNCATE)");
    }

    private static void TryDelete(string file)
    {
        SqliteConnection.ClearAllPools();
        foreach (var suffix in new[] { "", "-journal", "-wal", "-shm" })
            try { File.Delete(file + suffix); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
