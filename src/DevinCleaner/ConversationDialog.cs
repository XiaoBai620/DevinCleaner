using DevinCleaner.Engine;
using Microsoft.Data.Sqlite;

namespace DevinCleaner;

internal sealed class ConversationDialog : Form
{
    private readonly ChatView chat = new() { Dock = DockStyle.Fill };
    private readonly Label meta = DesktopTheme.Label("正在读取…", 8.5f);
    private readonly CheckBox thinking = new() { Text = "显示思考过程", AutoSize = true, ForeColor = DesktopTheme.Ink, Enabled = false };
    private readonly CheckBox system = new() { Text = "显示系统消息", AutoSize = true, ForeColor = DesktopTheme.Ink, Enabled = false };
    private readonly Button copyAll = DesktopTheme.Button("复制全部");

    public ConversationDialog(Cleaner cleaner, SessionInfo session)
    {
        AutoScaleDimensions = new SizeF(96, 96);
        AutoScaleMode = AutoScaleMode.Dpi;
        Text = $"对话 · {session.Title}";
        Icon = Icon.ExtractAssociatedIcon(Environment.ProcessPath!);
        Size = new Size(900, 760);
        MinimumSize = new Size(560, 420);
        StartPosition = FormStartPosition.CenterParent;
        BackColor = DesktopTheme.Canvas;
        ForeColor = DesktopTheme.Ink;
        Font = new Font("Microsoft YaHei UI", 9f);
        ShowInTaskbar = false;
        KeyPreview = true;

        var header = new TableLayoutPanel { Dock = DockStyle.Top, Height = 64, ColumnCount = 4, RowCount = 2, Padding = new Padding(20, 10, 16, 6), BackColor = Color.White };
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        header.RowStyles.Add(new RowStyle(SizeType.Percent, 55));
        header.RowStyles.Add(new RowStyle(SizeType.Percent, 45));
        var title = DesktopTheme.Label(session.Title, 11f, true);
        title.Dock = DockStyle.Fill;
        title.AutoEllipsis = true;
        title.TextAlign = ContentAlignment.MiddleLeft;
        meta.Dock = DockStyle.Fill;
        meta.AutoEllipsis = true;
        meta.TextAlign = ContentAlignment.MiddleLeft;
        header.Controls.Add(title, 0, 0);
        header.Controls.Add(meta, 0, 1);
        foreach (var box in new[] { thinking, system })
        {
            box.Anchor = AnchorStyles.Right;
            box.Margin = new Padding(12, 0, 0, 0);
        }
        copyAll.Anchor = AnchorStyles.Right;
        copyAll.Margin = new Padding(12, 0, 0, 0);
        copyAll.Enabled = false;
        header.Controls.Add(thinking, 1, 0);
        header.Controls.Add(system, 2, 0);
        header.Controls.Add(copyAll, 3, 0);
        header.SetRowSpan(thinking, 2);
        header.SetRowSpan(system, 2);
        header.SetRowSpan(copyAll, 2);
        var divider = new Panel { Dock = DockStyle.Top, Height = 1, BackColor = DesktopTheme.Border };

        Controls.Add(chat);
        Controls.Add(divider);
        Controls.Add(header);

        var tip = new ToolTip { AutoPopDelay = 15000 };
        tip.SetToolTip(chat, "拖动选择文字，Ctrl+C 复制 · 双击选中整条 · 点击灰色条展开工具调用 / 思考过程 · 右键更多");
        tip.SetToolTip(copyAll, "按当前显示设置复制整段对话为纯文本");
        thinking.CheckedChanged += (_, _) => { chat.ShowThinking = thinking.Checked; };
        system.CheckedChanged += (_, _) => { chat.ShowSystem = system.Checked; };
        copyAll.Click += (_, _) => ChatView.Copy(chat.Transcript());
        chat.OpenFull += ShowFull;
        KeyDown += (_, e) => { if (e.KeyCode == Keys.Escape) Close(); };
        FormClosed += (_, _) => tip.Dispose();

        Shown += async (_, _) =>
        {
            chat.Focus();
            try
            {
                var messages = await Task.Run(() => cleaner.Conversation(session.Id));
                if (IsDisposed) return;
                chat.SetMessages(messages);
                var users = messages.Count(m => m.Role == ChatRole.User);
                var tools = messages.Where(m => m.Role == ChatRole.Assistant).Sum(m => m.ToolCalls.Count);
                meta.Text = $"{session.WorkingDirectory}  ·  {users} 次提问 · {tools} 次工具调用  ·  只读，不会修改数据";
                if (users == 0 && chat.TurnCount == 0) chat.Placeholder = "这个会话的主线里没有可显示的对话。";
                thinking.Enabled = system.Enabled = copyAll.Enabled = true;
            }
            catch (Exception e) when (e is CleanerException or SqliteException or IOException or System.Text.Json.JsonException)
            {
                if (IsDisposed) return;
                meta.Text = "读取失败";
                chat.Placeholder = $"无法读取对话：{e.Message}";
            }
        };
    }

    private void ShowFull(string title, string content)
    {
        using var dialog = new Form { Text = title, StartPosition = FormStartPosition.CenterParent, Size = new Size(760, 560), MinimumSize = new Size(480, 320), Font = Font, BackColor = DesktopTheme.Canvas, MinimizeBox = false, ShowInTaskbar = false, Padding = new Padding(14) };
        var text = new TextBox { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, WordWrap = true, BackColor = Color.White, ForeColor = DesktopTheme.Ink, Font = new Font("Microsoft YaHei UI", 9.5f), Text = ChatView.Crlf(content) };
        dialog.Controls.Add(text);
        dialog.KeyPreview = true;
        dialog.KeyDown += (_, e) => { if (e.KeyCode == Keys.Escape) dialog.Close(); };
        dialog.Shown += (_, _) => { text.SelectionStart = 0; text.SelectionLength = 0; };
        dialog.ShowDialog(this);
    }
}
