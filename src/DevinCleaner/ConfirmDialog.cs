using DevinCleaner.Engine;

namespace DevinCleaner;

internal sealed class ConfirmDialog : Form
{
    private static readonly Color Ink = DesktopTheme.Ink;
    private static readonly Color Muted = DesktopTheme.Muted;
    private static readonly Color Canvas = DesktopTheme.Canvas;
    private static readonly Color Danger = DesktopTheme.Danger;
    private readonly Plan plan;
    private readonly Func<string, bool> isRunning;
    private readonly Button confirm;
    private readonly Label gate = new() { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft };
    private readonly System.Windows.Forms.Timer timer = new() { Interval = 1500 };
    private bool checking;

    internal const string OrphanLocksHelp = "Devin 打开会话时会在 session_locks 里放一个只记录进程号的小文件（几字节），用来标记“正在使用”。会话被删除后这个文件常被遗留，就成了孤立锁文件。删除它们不影响任何对话，只是让目录更干净；正被占用的锁会自动跳过。";

    public ConfirmDialog(Plan plan, Func<string, bool> isRunning)
    {
        this.plan = plan;
        this.isRunning = isRunning;
        AutoScaleDimensions = new SizeF(96, 96);
        AutoScaleMode = AutoScaleMode.Dpi;
        Text = "核对清理范围";
        ShowIcon = false;
        ClientSize = new Size(960, 640);
        MinimumSize = new Size(820, 560);
        BackColor = Canvas;
        ForeColor = Ink;
        Font = new Font("Microsoft YaHei UI", 9f);
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = MaximizeBox = false;
        ShowInTaskbar = false;

        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 5, Padding = new Padding(24, 20, 24, 16), Margin = Padding.Empty };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 80));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 80));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 56));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 72));

        var heading = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Margin = Padding.Empty };
        heading.RowStyles.Add(new RowStyle(SizeType.Absolute, 23));
        heading.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
        heading.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        heading.Controls.Add(new Label { Text = "02  /  核对并执行", AutoSize = true, ForeColor = Danger, Font = new Font(Font.FontFamily, 9f, FontStyle.Bold), Margin = Padding.Empty }, 0, 0);
        heading.Controls.Add(new Label { Text = "确认这次要清理的内容", AutoSize = true, Font = new Font(Font.FontFamily, 19f, FontStyle.Bold), Margin = Padding.Empty }, 0, 1);
        heading.Controls.Add(new Label { Text = "展开左侧明细，右侧可查看并复制完整说明。", AutoSize = true, ForeColor = Muted, Margin = Padding.Empty }, 0, 2);
        root.Controls.Add(heading, 0, 0);

        var metrics = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 1, Margin = new Padding(0, 10, 0, 8) };
        metrics.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        for (var i = 0; i < 3; i++) metrics.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f / 3));
        metrics.Controls.Add(Metric("删除的会话", plan.Sessions.Count), 0, 0);
        metrics.Controls.Add(Metric("涉及数据库记录", plan.DatabaseRows), 1, 0);
        metrics.Controls.Add(Metric("删除的孤立锁文件", plan.LockFiles.Count), 2, 0);
        root.Controls.Add(metrics, 0, 1);

        root.Controls.Add(new Label
        {
            Dock = DockStyle.Fill, Padding = new Padding(12, 9, 12, 9), Margin = new Padding(0, 2, 0, 10),
            BackColor = DesktopTheme.Hover, ForeColor = Ink, TextAlign = ContentAlignment.MiddleLeft,
            Text = plan.Sessions.Count > 0 ? "删除前会先备份所选会话，之后可在「备份与恢复」中找回。正在运行的会话会被拒绝。" : "本次不删除会话，只执行下方列出的维护操作。",
        }, 0, 2);

        var body = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = Padding.Empty };
        body.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 43));
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 57));
        var tree = new TreeView { Dock = DockStyle.Fill, ShowNodeToolTips = true, BackColor = Color.White, ForeColor = Ink, BorderStyle = BorderStyle.None, FullRowSelect = true, HideSelection = false, ItemHeight = 30, Indent = 18, Font = Font, AccessibleName = "清理项目明细" };
        var detail = new TextBox { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, WordWrap = true, ScrollBars = ScrollBars.Vertical, BorderStyle = BorderStyle.None, BackColor = Color.White, ForeColor = Ink, Font = Font, AccessibleName = "所选项目完整说明" };
        var treeFrame = new Panel { Dock = DockStyle.Fill, BackColor = Color.White, Padding = new Padding(10), Margin = new Padding(0, 0, 10, 0) };
        treeFrame.Controls.Add(tree);
        var detailFrame = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, BackColor = Color.White, Padding = new Padding(16), Margin = Padding.Empty };
        detailFrame.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
        detailFrame.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        detailFrame.Controls.Add(new Label { Text = "完整说明", AutoSize = true, Font = new Font(Font, FontStyle.Bold), ForeColor = Muted, Margin = Padding.Empty }, 0, 0);
        detailFrame.Controls.Add(detail, 0, 1);
        body.Controls.Add(treeFrame, 0, 0);
        body.Controls.Add(detailFrame, 1, 0);
        root.Controls.Add(body, 0, 3);

        TreeNode Section(string title, IEnumerable<(string Label, string Detail)> entries, bool expand = false)
        {
            var items = entries.ToList();
            var node = new TreeNode($"{title}  ·  {items.Count}") { Tag = $"{title}\r\n\r\n" + (items.Count == 0 ? "本次没有涉及此类内容。" : string.Join("\r\n\r\n", items.Select(i => i.Detail))) };
            foreach (var item in items) node.Nodes.Add(new TreeNode(item.Label) { Tag = item.Detail, ToolTipText = item.Detail });
            tree.Nodes.Add(node);
            if (expand) node.Expand();
            return node;
        }

        var overview = Section("本次范围",
        [
            ($"删除 {plan.Sessions.Count} 个会话", $"删除 {plan.Sessions.Count} 个会话的元数据、对话消息、工具调用、输入历史和渲染缓存。"),
            (plan.Selection.CleanOrphanLocks ? $"孤立锁文件：删除 {plan.LockFiles.Count} 个" : "孤立锁文件：保留", OrphanLocksHelp),
        ], true);
        Section("删除的会话", plan.Sessions.Select(s => (s.Title, $"{s.Title}\r\n\r\n会话 ID\r\n{s.Id}\r\n\r\n工作目录\r\n{s.WorkingDirectory}\r\n\r\n消息\r\n{s.Messages:N0} 条 · {Cleaner.FormatBytes(s.Bytes)}\r\n\r\n最后活动\r\n{s.LastActivityAt.ToLocalTime():yyyy-MM-dd HH:mm}")));
        Section("数据库变更", plan.Tables.Select(t => ($"{t.Name} · {t.Rows:N0} 条", $"{t.Name}\r\n{Catalog.Describe(t.Name)}\r\n\r\n删除 {t.Rows:N0} 条")));
        Section("孤立锁文件", plan.LockFiles.Select(id => (id + ".lock", $"session_locks\\{id}.lock")));
        Section("说明", plan.Warnings.Select((w, i) => ($"说明 {i + 1}", w)));
        tree.AfterSelect += (_, e) => detail.Text = e.Node?.Tag as string ?? "";
        tree.SelectedNode = overview;

        confirm = ActionButton("确认清理", Danger, Color.White);
        confirm.DialogResult = DialogResult.OK;
        confirm.Enabled = false;
        var cancel = ActionButton("返回调整", Color.White, Ink);
        cancel.DialogResult = DialogResult.Cancel;
        var footer = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 1, Margin = Padding.Empty, Padding = new Padding(0, 14, 0, 0) };
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        footer.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        gate.Text = "正在检查执行条件…";
        gate.Font = new Font(Font.FontFamily, 8.5f);
        footer.Controls.Add(gate, 0, 0);
        footer.Controls.Add(cancel, 1, 0);
        footer.Controls.Add(confirm, 2, 0);
        root.Controls.Add(footer, 0, 4);
        Controls.Add(root);
        AcceptButton = null;
        CancelButton = cancel;
        ActiveControl = cancel;

        timer.Tick += async (_, _) => await UpdateGate();
        Shown += async (_, _) => { await UpdateGate(); if (!IsDisposed) timer.Start(); };
        FormClosed += (_, _) => timer.Stop();
    }

    private static Control Metric(string label, long count)
    {
        var card = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, BackColor = Color.White, Padding = new Padding(14, 8, 14, 8), Margin = new Padding(0, 0, 8, 0) };
        card.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        card.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        card.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        card.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left, ForeColor = Muted }, 0, 0);
        card.Controls.Add(new Label { Text = count.ToString("N0"), AutoSize = true, Anchor = AnchorStyles.Right, ForeColor = Ink, Font = new Font("Segoe UI", 22f, FontStyle.Bold) }, 1, 0);
        return card;
    }

    private static Button ActionButton(string title, Color back, Color fore)
    {
        var button = new Button
        {
            Text = title, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, FlatStyle = FlatStyle.Flat, BackColor = back, ForeColor = fore,
            Font = new Font("Microsoft YaHei UI", 9f, FontStyle.Bold), Padding = new Padding(18, 10, 18, 10),
            Margin = new Padding(10, 0, 0, 0), Cursor = Cursors.Hand, UseVisualStyleBackColor = false,
        };
        button.FlatAppearance.BorderSize = back == Color.White ? 1 : 0;
        button.FlatAppearance.BorderColor = DesktopTheme.Border;
        return button;
    }

    private async Task UpdateGate()
    {
        if (checking || IsDisposed) return;
        checking = true;
        try
        {
            var running = await Task.Run(() => plan.Sessions.Where(s => isRunning(s.Id)).Select(s => s.Title).ToList());
            if (IsDisposed) return;
            var expired = DateTimeOffset.Now >= plan.ExpiresAt;
            confirm.Enabled = !expired && running.Count == 0;
            gate.Text = expired ? "预览已过期，请返回重新预览。"
                : running.Count > 0 ? $"会话「{running[0]}」{(running.Count > 1 ? $"等 {running.Count} 个" : "")}正在运行，关闭后将自动允许确认。"
                : $"可以执行 · 此预览有效至 {plan.ExpiresAt.ToLocalTime():HH:mm}";
            gate.ForeColor = expired || running.Count > 0 ? DesktopTheme.Warning : DesktopTheme.Success;
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            if (IsDisposed) return;
            confirm.Enabled = false;
            gate.Text = "暂时无法确认会话状态，请稍后重试。";
            gate.AccessibleDescription = e.Message;
            gate.ForeColor = Danger;
        }
        finally { checking = false; }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) timer.Dispose();
        base.Dispose(disposing);
    }
}
