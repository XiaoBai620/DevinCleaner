using DevinCleaner.Engine;
using Microsoft.Data.Sqlite;

namespace DevinCleaner;

internal sealed class BackupDialog : Form
{
    private readonly Cleaner cleaner;
    private readonly ListBox backupList = new() { Dock = DockStyle.Fill, BorderStyle = BorderStyle.None, IntegralHeight = false, Font = new Font("Microsoft YaHei UI", 9f), AccessibleName = "备份列表" };
    private readonly CheckedListBox sessionList = new() { Dock = DockStyle.Fill, BorderStyle = BorderStyle.None, IntegralHeight = false, CheckOnClick = true, Font = new Font("Microsoft YaHei UI", 9f), AccessibleName = "备份中的会话" };
    private readonly Label status = DesktopTheme.Label("", 8.5f);
    private readonly Button restore = DesktopTheme.Button("恢复勾选的会话", primary: true);
    private readonly Button remove = DesktopTheme.Button("删除此备份");
    private readonly Button openFolder = DesktopTheme.Button("打开备份目录");
    private IReadOnlyList<BackupInfo> items = [];

    public bool Changed { get; private set; }

    public BackupDialog(Cleaner cleaner)
    {
        this.cleaner = cleaner;
        AutoScaleDimensions = new SizeF(96, 96);
        AutoScaleMode = AutoScaleMode.Dpi;
        Text = "备份与恢复";
        ClientSize = new Size(900, 560);
        MinimumSize = new Size(760, 460);
        BackColor = DesktopTheme.Canvas;
        Font = new Font("Microsoft YaHei UI", 9f);
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = MaximizeBox = false;
        ShowInTaskbar = false;

        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 3, Padding = new Padding(20, 16, 20, 14) };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 38));
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 62));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 56));
        var left = DesktopTheme.Label("删除前自动生成的备份", 10f, true);
        left.Dock = DockStyle.Fill;
        var right = DesktopTheme.Label("备份中的会话（勾选后恢复）", 10f, true);
        right.Dock = DockStyle.Fill;
        root.Controls.Add(left, 0, 0);
        root.Controls.Add(right, 1, 0);
        root.Controls.Add(Frame(backupList, new Padding(0, 0, 10, 0)), 0, 1);
        root.Controls.Add(Frame(sessionList, Padding.Empty), 1, 1);

        var footer = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 4, RowCount = 1, Padding = new Padding(0, 12, 0, 0), Margin = Padding.Empty };
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (var i = 0; i < 3; i++) footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        status.Dock = DockStyle.Fill;
        status.TextAlign = ContentAlignment.MiddleLeft;
        status.AutoEllipsis = true;
        footer.Controls.Add(status, 0, 0);
        openFolder.Margin = remove.Margin = new Padding(8, 0, 0, 0);
        restore.Margin = new Padding(8, 0, 0, 0);
        footer.Controls.Add(openFolder, 1, 0);
        footer.Controls.Add(remove, 2, 0);
        footer.Controls.Add(restore, 3, 0);
        root.Controls.Add(footer, 0, 2);
        root.SetColumnSpan(footer, 2);
        Controls.Add(root);

        backupList.SelectedIndexChanged += (_, _) => RenderSessions();
        sessionList.ItemCheck += (_, _) => BeginInvoke(RenderButtons);
        restore.Click += (_, _) => Restore();
        remove.Click += (_, _) => DeleteBackup();
        openFolder.Click += (_, _) =>
        {
            Directory.CreateDirectory(cleaner.BackupRoot);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(cleaner.BackupRoot) { UseShellExecute = true });
        };
        Shown += (_, _) => Reload();
    }

    private static Control Frame(Control inner, Padding margin)
    {
        var frame = new SurfacePanel { Dock = DockStyle.Fill, Padding = new Padding(10), Margin = margin, CornerRadius = 8 };
        frame.Controls.Add(inner);
        return frame;
    }

    private void Reload(string? keep = null)
    {
        UseWaitCursor = true;
        try { items = cleaner.Backups(); }
        finally { UseWaitCursor = false; }
        backupList.BeginUpdate();
        backupList.Items.Clear();
        foreach (var b in items) backupList.Items.Add($"{b.CreatedAt:yyyy-MM-dd HH:mm} · {b.Sessions.Count} 个会话 · {Cleaner.FormatBytes(b.Bytes)}");
        backupList.EndUpdate();
        var index = items.ToList().FindIndex(b => b.Path == keep);
        if (items.Count > 0) backupList.SelectedIndex = Math.Max(0, index);
        RenderSessions();
    }

    private BackupInfo? Current => backupList.SelectedIndex >= 0 && backupList.SelectedIndex < items.Count ? items[backupList.SelectedIndex] : null;

    private void RenderSessions()
    {
        sessionList.BeginUpdate();
        sessionList.Items.Clear();
        if (Current is { } backup)
            foreach (var s in backup.Sessions)
                sessionList.Items.Add($"{s.Title} · {s.Messages:N0} 条消息 · {s.LastActivityAt.ToLocalTime():yyyy-MM-dd HH:mm}" + (s.BlockedReason is null ? "" : $"（{s.BlockedReason}）"));
        sessionList.EndUpdate();
        RenderButtons();
    }

    private void RenderButtons()
    {
        var backup = Current;
        var restorable = backup?.Sessions.Where((s, i) => s.BlockedReason is null && sessionList.GetItemChecked(i)).Count() ?? 0;
        restore.Enabled = backup?.Problem is null && restorable > 0;
        remove.Enabled = backup is not null;
        status.Text = items.Count == 0 ? $"还没有备份。备份目录：{cleaner.BackupRoot}"
            : backup?.Problem is { } problem ? problem
            : $"已勾选 {restorable} 个可恢复的会话 · 已存在于当前数据库的会话会被跳过";
        status.ForeColor = backup?.Problem is null ? DesktopTheme.Muted : Color.FromArgb(152, 94, 19);
    }

    private void Restore()
    {
        if (Current is not { } backup) return;
        var ids = backup.Sessions.Where((s, i) => s.BlockedReason is null && sessionList.GetItemChecked(i)).Select(s => s.Id).ToList();
        if (MessageBox.Show(this, $"将 {ids.Count} 个会话恢复到 Devin 数据库。\n恢复后需要在 Devin 中重新打开会话列表才能看到。", "确认恢复", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
        try
        {
            var result = cleaner.Restore(backup.Path, ids);
            Changed = true;
            MessageBox.Show(this, $"已恢复 {result.Restored} 个会话" + (result.Skipped.Count > 0 ? $"，跳过 {result.Skipped.Count} 个已存在的会话" : "") + "。", "恢复完成", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception e) when (e is CleanerException or IOException or SqliteException)
        {
            MessageBox.Show(this, e.Message, "恢复未完成", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        Reload(backup.Path);
    }

    private void DeleteBackup()
    {
        if (Current is not { } backup) return;
        if (MessageBox.Show(this, $"永久删除这个备份？其中的 {backup.Sessions.Count} 个会话将无法再恢复。\n\n{backup.Path}", "删除备份", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) != DialogResult.OK) return;
        try { cleaner.DeleteBackup(backup.Path); }
        catch (Exception e) when (e is CleanerException or IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(this, e.Message, "删除未完成", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        Reload();
    }
}
