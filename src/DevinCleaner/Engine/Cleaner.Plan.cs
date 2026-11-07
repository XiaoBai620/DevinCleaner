namespace DevinCleaner.Engine;

public sealed partial class Cleaner
{
    public Plan PlanCleanup(Selection selection)
    {
        var ids = selection.SessionIds.Distinct(StringComparer.Ordinal).ToList();
        if (ids.Count == 0 && !selection.CleanOrphanLocks) throw new CleanerException("没有选择任何清理内容。");
        var snapshot = Snapshot();
        if (ids.Count > 0 && !snapshot.Supported) throw new CleanerException("数据库结构未验证，不能删除会话。");
        var byId = snapshot.Sessions.ToDictionary(s => s.Id, StringComparer.Ordinal);
        var sessions = new List<SessionInfo>();
        foreach (var id in ids)
        {
            if (!byId.TryGetValue(id, out var session)) throw new CleanerException($"会话 {id} 已不存在，请刷新后重新选择。");
            if (!session.CanDelete) throw new CleanerException($"{session.Title}：{session.BlockedReason}");
            sessions.Add(session);
        }

        var stamps = new Dictionary<string, SessionStamp>(StringComparer.Ordinal);
        using (var db = OpenRead())
            foreach (var id in ids) stamps[id] = Stamp(db, id);
        var tables = ids.Count == 0 ? [] : Catalog.ChildTables.Append(Catalog.SessionTable)
            .Select(t => new PlanTable(t, stamps.Values.Sum(s => RowsOf(s, t)))).ToList();
        var warnings = new List<string>();
        if (ids.Count > 0) warnings.Add($"删除前会把所选会话的全部记录备份到 {BackupRoot}，可在「备份与恢复」中恢复。");
        warnings.Add("Devin 的 summaries 目录中的历史摘要文件无法可靠对应到会话，本工具不修改它们。");

        var plan = new Plan(Guid.NewGuid().ToString("N"), DateTimeOffset.Now + Catalog.PlanLifetime, selection with { SessionIds = ids }, sessions,
            tables, selection.CleanOrphanLocks ? snapshot.OrphanLocks : [], warnings);
        lock (gate)
        {
            foreach (var expired in plans.Where(p => p.Value.Public.ExpiresAt < DateTimeOffset.Now).Select(p => p.Key).ToList()) plans.Remove(expired);
            plans[plan.Id] = new PlanState(plan, stamps);
        }
        return plan;
    }

    public static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1) { value /= 1024; unit++; }
        return unit == 0 ? $"{bytes} B" : $"{value:0.#} {units[unit]}";
    }
}
