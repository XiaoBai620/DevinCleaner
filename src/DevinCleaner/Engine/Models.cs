using System.Text.RegularExpressions;

namespace DevinCleaner.Engine;

public sealed class CleanerException(string message, Exception? inner = null) : Exception(message, inner);

public sealed record ProcessState(bool Running, IReadOnlyList<string> Processes);

public sealed record SessionInfo(
    string Id, string Title, string WorkingDirectory, string Model, DateTimeOffset CreatedAt, DateTimeOffset LastActivityAt,
    bool Hidden, long Messages, long Bytes, bool Running, string? BlockedReason)
{
    public bool CanDelete => BlockedReason is null;
}

public sealed record Snapshot(
    IReadOnlyList<SessionInfo> Sessions, bool Supported, IReadOnlyList<string> OrphanLocks, long DatabaseBytes, IReadOnlyList<string> Warnings);

public sealed record Selection(IReadOnlyList<string> SessionIds, bool CleanOrphanLocks = false);

public sealed record PlanTable(string Name, long Rows);

public sealed record Plan(
    string Id, DateTimeOffset ExpiresAt, Selection Selection, IReadOnlyList<SessionInfo> Sessions,
    IReadOnlyList<PlanTable> Tables, IReadOnlyList<string> LockFiles, IReadOnlyList<string> Warnings)
{
    public long DatabaseRows => Tables.Sum(t => t.Rows);
}

public sealed record ExecuteResult(string? BackupPath, int DeletedSessions, int RemovedLocks);

public enum ChatRole { User, Assistant, Tool, System }

public sealed record ChatImage(byte[] Data, int Width, int Height);

public sealed record ChatToolCall(string Id, string Name, string Summary, string Arguments, string? Result, IReadOnlyList<ChatImage> Images);

public sealed record ChatMessage(ChatRole Role, string Text, string? Thinking, IReadOnlyList<ChatToolCall> ToolCalls, IReadOnlyList<ChatImage> Images, DateTimeOffset CreatedAt);

public sealed record BackupInfo(string Path, DateTimeOffset CreatedAt, long Bytes, IReadOnlyList<SessionInfo> Sessions, string? Problem);

public sealed record RestoreResult(int Restored, IReadOnlyList<string> Skipped);

internal sealed record SessionStamp(long LastActivity, long Messages, long MaxRow, long ToolCalls, long Prompts, long Commits);

internal sealed record PlanState(Plan Public, Dictionary<string, SessionStamp> Stamps);

// Seams for fault-injection tests; all null in production.
internal sealed class TestHooks
{
    public Action? DuringRead;
    public Action? AfterBackup;
    public Action? BeforeCommit;
}

internal static partial class Ids
{
    [GeneratedRegex(@"^[A-Za-z0-9_-]{1,128}\z")]
    private static partial Regex Pattern();

    public static bool Valid(string? value) => value is not null && Pattern().IsMatch(value);
}
