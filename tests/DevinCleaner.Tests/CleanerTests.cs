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
    public void ConversationFollowsTheMainChain()
    {
        using var fixture = new Fixture();
        var messages = fixture.Cleaner.Conversation(Fixture.Ids[0]);
        Assert.Equal([ChatRole.System, ChatRole.User, ChatRole.Assistant], messages.Select(m => m.Role));
        Assert.Equal("invented question", messages[1].Text);
        Assert.Equal("invented answer", messages[2].Text);
    }

    [Fact]
    public void ConversationSkipsAbandonedBranchesAndPairsToolResults()
    {
        using var fixture = new Fixture();
        var id = Fixture.Ids[0];
        using (var db = fixture.Open())
        {
            void Node(int n, int parent, string json) =>
                Fixture.Exec(db, "INSERT INTO message_nodes (session_id,node_id,parent_node_id,chat_message,created_at) VALUES ($p0,$p1,$p2,$p3,1)", id, n, parent, json);
            Node(3, 1, """{"role":"assistant","content":"abandoned retry"}""");
            Node(4, 2, """{"role":"assistant","content":"","thinking":{"thinking":"  plan it  ","signature":""},"tool_calls":[{"id":"t1","name":"grep","arguments":{"pattern":"x"}}]}""");
            Node(5, 4, $$"""{"role":"tool","tool_call_id":"t1","content":"{{new string('r', Cleaner.ToolResultLimit + 50)}}"}""");
            Node(6, 5, """{"role":"assistant","content":"done"}""");
            Fixture.Exec(db, "UPDATE sessions SET main_chain_id=6 WHERE id=$p0", id);
        }
        var messages = fixture.Cleaner.Conversation(id);
        Assert.DoesNotContain(messages, m => m.Text == "abandoned retry");
        Assert.DoesNotContain(messages, m => m.Role == ChatRole.Tool);
        var call = Assert.Single(messages.Single(m => m.ToolCalls.Count > 0).ToolCalls);
        Assert.Equal("grep", call.Name);
        Assert.Equal("x", call.Summary);
        Assert.Equal("pattern: x", call.Arguments);
        Assert.StartsWith(new string('r', Cleaner.ToolResultLimit), call.Result);
        Assert.Contains("未显示", call.Result);
        Assert.Equal("plan it", messages[3].Thinking);
        Assert.Equal("done", messages[^1].Text);
    }

    [Fact]
    public void ConversationKeepsLongTextImagesAndReadableArguments()
    {
        using var fixture = new Fixture();
        var id = Fixture.Ids[0];
        var longText = string.Concat(Enumerable.Range(0, 3000).Select(i => $"第{i}行。"));
        var png = Convert.ToBase64String([0x89, 0x50, 0x4E, 0x47, 1, 2, 3]);
        using (var db = fixture.Open())
        {
            void Node(int n, int parent, object message) =>
                Fixture.Exec(db, "INSERT INTO message_nodes (session_id,node_id,parent_node_id,chat_message,created_at) VALUES ($p0,$p1,$p2,$p3,1)", id, n, parent, System.Text.Json.JsonSerializer.Serialize(message));
            Node(3, 2, new { role = "user", content = "[Image 1]\n" + longText, images = new[] { new { width = 10, height = 5, base64_data = png, mime_type = "image/png" } } });
            Node(4, 3, new { role = "assistant", content = "", tool_calls = new object[]
            {
                new { id = "w1", name = "write", arguments = new { file_path = @"C:\x\a.txt", content = "line1\nline2" } },
                new { id = "m1", name = "mcp_call_tool", arguments = new { server_name = "ida", tool_name = "decompile", arguments = new { addr = "0x10" } } },
            } });
            Node(5, 4, new { role = "tool", tool_call_id = "w1", content = "ok" });
            Node(6, 5, new { role = "tool", tool_call_id = "m1", content = "[Image 1]", images = new[] { new { width = 1, height = 1, base64_data = png } } });
            Fixture.Exec(db, "UPDATE sessions SET main_chain_id=6 WHERE id=$p0", id);
        }
        var messages = fixture.Cleaner.Conversation(id);
        var user = messages.Single(m => m.Role == ChatRole.User && m.Images.Count > 0);
        Assert.EndsWith(longText, user.Text);
        Assert.Equal((10, 5), (user.Images[0].Width, user.Images[0].Height));
        Assert.Equal(Convert.FromBase64String(png), user.Images[0].Data);
        var calls = messages.Single(m => m.ToolCalls.Count == 2).ToolCalls;
        Assert.Equal(@"C:\x\a.txt", calls[0].Summary);
        Assert.Equal("file_path: C:\\x\\a.txt\ncontent:\nline1\nline2", calls[0].Arguments);
        Assert.Equal("ok", calls[0].Result);
        Assert.Equal("ida · decompile", calls[1].Summary);
        Assert.Contains("\"addr\": \"0x10\"", calls[1].Arguments);
        Assert.Single(calls[1].Images);
    }

    [Fact]
    public void ConversationReadingDoesNotModifyTheDatabase()
    {
        using var fixture = new Fixture();
        SqliteConnection.ClearAllPools();
        var file = Path.Combine(fixture.DataRoot, "sessions.db");
        using (var db = fixture.Open()) Fixture.Exec(db, "PRAGMA wal_checkpoint(TRUNCATE)");
        var before = File.ReadAllBytes(file);
        fixture.Cleaner.Conversation(Fixture.Ids[1]);
        Assert.Equal(before, File.ReadAllBytes(file));
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
