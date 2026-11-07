namespace DevinCleaner.Engine;

// Storage layout observed on 2026-11-08 (Devin CLI sessions.db, refinery migration 16).
// This is not a stable Devin API; any schema change disables writes.
internal static class Catalog
{
    public const string Database = "sessions.db";
    public const string LockDirectory = "session_locks";
    public const string SchemaHash = "06e148f7dc7ce3ca80187afe9e4a570108714584a98ffdd26efe5e0c3cf36fa3";

    // Child tables first; this mirrors the delete order embedded in devin.exe.
    public static readonly string[] ChildTables = ["tool_call_state", "rendered_commits", "message_nodes", "prompt_history"];
    public const string SessionTable = "sessions";
    public const string MigrationTable = "refinery_schema_history";
    public static readonly string[] BackupTables = [MigrationTable, SessionTable, .. ChildTables];
    public static readonly string[] SqliteSuffixes = ["", "-wal"];
    public static readonly TimeSpan PlanLifetime = TimeSpan.FromMinutes(15);

    public static string Describe(string table) => table switch
    {
        SessionTable => "会话元数据（标题、工作目录、模型）",
        "message_nodes" => "对话消息",
        "tool_call_state" => "工具调用记录",
        "prompt_history" => "输入历史",
        "rendered_commits" => "渲染缓存",
        _ => table,
    };
}
