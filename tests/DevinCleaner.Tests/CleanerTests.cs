using DevinCleaner.Engine;
using Microsoft.Data.Sqlite;

namespace DevinCleaner.Tests;

public sealed class CleanerTests
{
    [Fact]
    public void FixtureSchemaMatchesTheVerifiedFingerprint()
    {
        using var fixture = new Fixture();
        using var db = fixture.Open();
        Assert.Equal(Catalog.SchemaHash, Db.SchemaHash(db));
    }

    [Fact]
    public void SnapshotListsSessionsWithStatistics()
    {
        using var fixture = new Fixture();
        var snapshot = fixture.Cleaner.Snapshot();
        Assert.True(snapshot.Supported);
        Assert.Equal(Fixture.Ids.Reverse(), snapshot.Sessions.Select(s => s.Id));
        Assert.All(snapshot.Sessions, s => { Assert.Equal(3, s.Messages); Assert.True(s.Bytes > 0); Assert.True(s.CanDelete); });
        Assert.True(snapshot.Sessions.Single(s => s.Id == "gamma-session").Hidden);
        Assert.Equal(["orphan-one", "orphan-two"], snapshot.OrphanLocks);
    }

    [Fact]
    public void DeletesOnlySelectedSessionsAndKeepsAVerifiedBackup()
    {
        using var fixture = new Fixture();
        var plan = fixture.Cleaner.PlanCleanup(new Selection([Fixture.Ids[0]]));
        Assert.Equal(1 + 3 + 1 + 1 + 1, plan.DatabaseRows);

        var result = fixture.Cleaner.Execute(plan.Id);

        Assert.Equal(1, result.DeletedSessions);
        Assert.Equal(0, fixture.Total(Fixture.Ids[0]));
        Assert.Equal(7, fixture.Total(Fixture.Ids[1]));
        Assert.Equal(7, fixture.Total(Fixture.Ids[2]));
        Assert.False(File.Exists(fixture.LockPath(Fixture.Ids[0])));
        Assert.True(File.Exists(fixture.LockPath("orphan-one")));
        var backup = Assert.Single(fixture.Cleaner.Backups());
        Assert.Equal(result.BackupPath, backup.Path);
        Assert.Null(backup.Problem);
        Assert.Equal(Fixture.Ids[0], Assert.Single(backup.Sessions).Id);
        using var db = fixture.Open();
        Assert.Equal("ok", Db.Scalar(db, "PRAGMA integrity_check"));
    }

    [Fact]
    public void RunningSessionsAreProtectedEvenWhenDevinIsOpen()
    {
        using var fixture = new Fixture();
        fixture.Running = true;
        using (fixture.HoldLock(Fixture.Ids[1]))
        {
            var snapshot = fixture.Cleaner.Snapshot();
            var running = snapshot.Sessions.Single(s => s.Id == Fixture.Ids[1]);
            Assert.True(running.Running);
            Assert.False(running.CanDelete);
            Assert.Throws<CleanerException>(() => fixture.Cleaner.PlanCleanup(new Selection([Fixture.Ids[1]])));

            var plan = fixture.Cleaner.PlanCleanup(new Selection([Fixture.Ids[0]]));
            fixture.Cleaner.Execute(plan.Id);
            Assert.Equal(0, fixture.Total(Fixture.Ids[0]));
            Assert.Equal(7, fixture.Total(Fixture.Ids[1]));
        }
    }

    [Fact]
    public void SessionStartedAfterPreviewIsNotDeleted()
    {
        using var fixture = new Fixture();
        var plan = fixture.Cleaner.PlanCleanup(new Selection([Fixture.Ids[0]]));
        using (fixture.HoldLock(Fixture.Ids[0]))
            Assert.Throws<CleanerException>(() => fixture.Cleaner.Execute(plan.Id));
        Assert.Equal(7, fixture.Total(Fixture.Ids[0]));
        Assert.Empty(fixture.Cleaner.Backups());
    }

    [Fact]
    public void NewMessagesAfterPreviewInvalidateThePlan()
    {
        using var fixture = new Fixture();
        var plan = fixture.Cleaner.PlanCleanup(new Selection([Fixture.Ids[0]]));
        using (var db = fixture.Open())
            Fixture.Exec(db, "INSERT INTO message_nodes (session_id,node_id,chat_message,created_at) VALUES ($p0,99,'late',2)", Fixture.Ids[0]);
        var error = Assert.Throws<CleanerException>(() => fixture.Cleaner.Execute(plan.Id));
        Assert.Contains("预览后", error.Message);
        Assert.Equal(8, fixture.Total(Fixture.Ids[0]));
        Assert.Empty(fixture.Cleaner.Backups());
    }

    [Fact]
    public void FailureBeforeCommitLeavesDataAndRemovesBackup()
    {
        using var fixture = new Fixture();
        fixture.Cleaner.Hooks.BeforeCommit = () => throw new IOException("Synthetic failure");
        var plan = fixture.Cleaner.PlanCleanup(new Selection([Fixture.Ids[0], Fixture.Ids[1]]));
        var error = Assert.Throws<CleanerException>(() => fixture.Cleaner.Execute(plan.Id));
        Assert.Contains("未删除任何会话", error.Message);
        Assert.Equal(7, fixture.Total(Fixture.Ids[0]));
        Assert.Equal(7, fixture.Total(Fixture.Ids[1]));
        Assert.True(File.Exists(fixture.LockPath(Fixture.Ids[0])));
        Assert.Empty(Directory.Exists(fixture.BackupRoot) ? Directory.GetFiles(fixture.BackupRoot) : []);
    }

    [Fact]
    public void PlansExpireAndCannotBeReused()
    {
        using var fixture = new Fixture();
        var plan = fixture.Cleaner.PlanCleanup(new Selection([Fixture.Ids[0]]));
        fixture.Cleaner.Execute(plan.Id);
        Assert.Throws<CleanerException>(() => fixture.Cleaner.Execute(plan.Id));
        Assert.Throws<CleanerException>(() => fixture.Cleaner.Execute("unknown"));
    }

    [Fact]
    public void UnknownSchemaOnlyAllowsBrowsing()
    {
        using var fixture = new Fixture();
        using (var db = fixture.Open()) Fixture.Exec(db, "ALTER TABLE sessions ADD COLUMN future_column TEXT");
        var snapshot = fixture.Cleaner.Snapshot();
        Assert.False(snapshot.Supported);
        Assert.NotEmpty(snapshot.Warnings);
        Assert.All(snapshot.Sessions, s => Assert.False(s.CanDelete));
        Assert.Throws<CleanerException>(() => fixture.Cleaner.PlanCleanup(new Selection([Fixture.Ids[0]])));
    }

    [Fact]
    public void OrphanLockCleanupSkipsHeldAndKnownLocks()
    {
        using var fixture = new Fixture();
        using (fixture.HoldLock("orphan-two"))
        {
            var plan = fixture.Cleaner.PlanCleanup(new Selection([], CleanOrphanLocks: true));
            Assert.Equal(["orphan-one"], plan.LockFiles);
            var result = fixture.Cleaner.Execute(plan.Id);
            Assert.Equal(1, result.RemovedLocks);
        }
        Assert.False(File.Exists(fixture.LockPath("orphan-one")));
        Assert.True(File.Exists(fixture.LockPath("orphan-two")));
        Assert.All(Fixture.Ids, id => Assert.True(File.Exists(fixture.LockPath(id))));
        Assert.Null(fixture.Cleaner.Backups().FirstOrDefault());
    }

    [Fact]
    public void CompactShrinksTheDatabaseAfterDeletion()
    {
        using var fixture = new Fixture();
        using (var db = fixture.Open())
            for (var n = 10; n < 400; n++)
                Fixture.Exec(db, "INSERT INTO message_nodes (session_id,node_id,chat_message,created_at) VALUES ($p0,$p1,$p2,1)", Fixture.Ids[0], n, new string('x', 4000));
        var plan = fixture.Cleaner.PlanCleanup(new Selection([Fixture.Ids[0]], Compact: true));
        var result = fixture.Cleaner.Execute(plan.Id);
        Assert.Null(result.CompactError);
        Assert.True(result.BytesAfter < result.BytesBefore / 2, $"{result.BytesBefore} -> {result.BytesAfter}");
        Assert.Equal(7, fixture.Total(Fixture.Ids[1]));
    }

    [Fact]
    public void RestoreBringsBackDeletedSessionsExactly()
    {
        using var fixture = new Fixture();
        string Dump(string id)
        {
            using var db = fixture.Open();
            return string.Join("|", Fixture.SessionTables.SelectMany(t => Db.Rows(db, $"SELECT * FROM {t} WHERE {(t == "sessions" ? "id" : "session_id")}=$p0 ORDER BY 1", [id]))
                .Select(r => string.Join(",", r.Select(v => v?.ToString()))));
        }
        var before = Dump(Fixture.Ids[0]);
        var plan = fixture.Cleaner.PlanCleanup(new Selection([Fixture.Ids[0], Fixture.Ids[1]]));
        var result = fixture.Cleaner.Execute(plan.Id);

        var restored = fixture.Cleaner.Restore(result.BackupPath!, [Fixture.Ids[0]]);

        Assert.Equal(1, restored.Restored);
        Assert.Equal(before, Dump(Fixture.Ids[0]));
        Assert.Equal(0, fixture.Total(Fixture.Ids[1]));
        var again = fixture.Cleaner.Restore(result.BackupPath!, [Fixture.Ids[0]]);
        Assert.Equal(0, again.Restored);
        Assert.Equal([Fixture.Ids[0]], again.Skipped);
        Assert.NotNull(fixture.Cleaner.Backups().Single().Sessions.Single(s => s.Id == Fixture.Ids[0]).BlockedReason);
    }

    [Fact]
    public void RestoreRejectsFilesOutsideTheBackupDirectory()
    {
        using var fixture = new Fixture();
        var outside = Path.Combine(fixture.Base, "sessions-20260101-000000-abcdef12.db");
        File.Copy(Path.Combine(fixture.DataRoot, "sessions.db"), outside);
        Assert.Throws<CleanerException>(() => fixture.Cleaner.Restore(outside, [Fixture.Ids[0]]));
        Assert.Throws<CleanerException>(() => fixture.Cleaner.DeleteBackup(outside));
        Assert.True(File.Exists(outside));
    }

    [Fact]
    public void BackupDirectoryCannotOverlapDataDirectory()
    {
        using var fixture = new Fixture();
        Assert.Throws<CleanerException>(() => new Cleaner(fixture.DataRoot, Path.Combine(fixture.DataRoot, "backups")));
    }

    [Fact]
    public void ReadingDoesNotModifyTheDatabase()
    {
        using var fixture = new Fixture();
        SqliteConnection.ClearAllPools();
        var file = Path.Combine(fixture.DataRoot, "sessions.db");
        using (var db = fixture.Open()) Fixture.Exec(db, "PRAGMA wal_checkpoint(TRUNCATE)");
        var before = File.ReadAllBytes(file);
        fixture.Cleaner.Snapshot();
        fixture.Cleaner.PlanCleanup(new Selection([Fixture.Ids[0]]));
        Assert.Equal(before, File.ReadAllBytes(file));
        Assert.Equal(7, fixture.Total(Fixture.Ids[0]));
    }
}
