using System.ComponentModel;
using System.Drawing.Drawing2D;
using DevinCleaner.Engine;

namespace DevinCleaner;

// Owner-drawn chat transcript: user bubbles on the right, Devin on the left.
// Only blocks inside the viewport are painted, so long sessions stay responsive.
internal sealed class ChatView : Control
{
    private const int DisplayLimit = 6000;
    private const TextFormatFlags Wrap = TextFormatFlags.WordBreak | TextFormatFlags.TextBoxControl | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding | TextFormatFlags.ExpandTabs;
    private static readonly Font BodyFont = new("Microsoft YaHei UI", 9.5f);
    private static readonly Font NoteFont = new("Microsoft YaHei UI", 8.5f);
    private static readonly Font ThinkingFont = new("Microsoft YaHei UI", 8.5f, FontStyle.Italic);
    private static readonly Font CaptionFont = new("Microsoft YaHei UI", 8f);

    private enum Kind { User, Assistant, Thinking, Tools, System }

    private sealed class Block(Kind kind, int index, ChatMessage message)
    {
        public Kind Kind { get; } = kind;
        public int Index { get; } = index;
        public ChatMessage Message { get; } = message;
        public string Caption = "", Text = "";
        public bool Truncated;
        public Rectangle CaptionRect, Bubble, TextRect;
    }

    private readonly VScrollBar bar = new() { Dock = DockStyle.Right, SmallChange = 40 };
    private readonly List<Block> blocks = [];
    private readonly HashSet<(int, Kind)> expanded = [];
    private IReadOnlyList<ChatMessage> messages = [];
    private bool showThinking, showSystem;
    private int contentHeight;

    public event Action<string, string>? OpenFull;

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public string Placeholder { get; set { field = value; Invalidate(); } } = "正在读取对话…";

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool ShowThinking { get => showThinking; set { showThinking = value; Rebuild(); } }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool ShowSystem { get => showSystem; set { showSystem = value; Rebuild(); } }

    public int TurnCount => blocks.Count(b => b.Kind is Kind.User or Kind.Assistant);

    public ChatView()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
        BackColor = DesktopTheme.Canvas;
        TabStop = true;
        AccessibleName = "对话内容";
        Controls.Add(bar);
        bar.ValueChanged += (_, _) => Invalidate();
    }

    public void SetMessages(IReadOnlyList<ChatMessage> value)
    {
        messages = value;
        expanded.Clear();
        bar.Value = 0;
        Rebuild();
    }

    public string Transcript() => string.Join("\r\n\r\n", blocks.Select(b => (b.Caption.Length > 0 ? $"【{b.Caption}】\r\n" : "") + Body(b))).Replace("\r\n", "\n").Replace("\n", "\r\n");

    private static string Speaker(Kind kind) => kind switch { Kind.User => "你", Kind.System => "系统", _ => "Devin" };

    private void Rebuild()
    {
        blocks.Clear();
        for (var i = 0; i < messages.Count; i++)
        {
            var m = messages[i];
            switch (m.Role)
            {
                case ChatRole.User: blocks.Add(new Block(Kind.User, i, m)); break;
                case ChatRole.System when showSystem: blocks.Add(new Block(Kind.System, i, m)); break;
                case ChatRole.Tool: blocks.Add(new Block(Kind.Tools, i, m)); break;
                case ChatRole.Assistant:
                    if (showThinking && m.Thinking is not null) blocks.Add(new Block(Kind.Thinking, i, m));
                    if (!string.IsNullOrWhiteSpace(m.Text)) blocks.Add(new Block(Kind.Assistant, i, m));
                    if (m.ToolCalls.Count > 0) blocks.Add(new Block(Kind.Tools, i, m));
                    break;
            }
        }
        string? last = null;
        foreach (var b in blocks)
        {
            var who = Speaker(b.Kind);
            b.Caption = who == last ? "" : $"{who} · {b.Message.CreatedAt.ToLocalTime():yyyy-MM-dd HH:mm}";
            last = who;
        }
        Relayout();
    }

    private string Body(Block b)
    {
        var open = expanded.Contains((b.Index, b.Kind));
        var m = b.Message;
        switch (b.Kind)
        {
            case Kind.Thinking:
                return open ? "▾ 思考过程\n\n" + m.Thinking : "▸ 思考过程（点击展开）";
            case Kind.Tools when m.Role == ChatRole.Tool:
                return open ? "▾ 工具结果\n\n" + m.Text : "▸ 工具结果（点击展开）";
            case Kind.Tools:
                var names = string.Join(", ", m.ToolCalls.Select(c => c.Name));
                var head = $"调用工具 · {names}";
                if (!open) return "▸ " + head;
                return "▾ " + head + "\n\n" + string.Join("\n\n", m.ToolCalls.Select(c =>
                    $"■ {c.Name}\n参数：{c.Arguments}\n结果：{(c.Result is null ? "（没有记录结果）" : c.Result.Trim())}"));
            default:
                return m.Text.Trim();
        }
    }

    private static Font FontOf(Kind kind) => kind switch { Kind.User or Kind.Assistant => BodyFont, Kind.Thinking => ThinkingFont, _ => NoteFont };

    private static (Color Fill, Color Ink, Color Border) Colors(Kind kind) => kind switch
    {
        Kind.User => (DesktopTheme.Ink, Color.White, Color.Empty),
        Kind.Assistant => (Color.White, DesktopTheme.Ink, DesktopTheme.Border),
        Kind.Thinking => (Color.FromArgb(244, 239, 252), Color.FromArgb(112, 84, 158), Color.Empty),
        Kind.Tools => (Color.FromArgb(239, 239, 234), DesktopTheme.Accent, Color.Empty),
        _ => (Color.FromArgb(253, 246, 230), Color.FromArgb(152, 94, 19), Color.Empty),
    };

    private void Relayout()
    {
        var width = ClientSize.Width - bar.Width;
        if (width <= 0 || !IsHandleCreated) return;
        var s = DeviceDpi / 96f;
        int pad = (int)(20 * s), gap = (int)(14 * s), captionHeight = CaptionFont.Height + (int)(4 * s);
        var bubbleMax = Math.Max((int)(160 * s), (int)((width - 2 * pad) * 0.78));
        var y = pad;
        using var g = CreateGraphics();
        foreach (var b in blocks)
        {
            var full = Body(b);
            b.Truncated = full.Length > DisplayLimit;
            b.Text = b.Truncated ? full[..DisplayLimit] + "\n\n…（内容较长，双击查看全文）" : full.Length == 0 ? " " : full;
            var inner = (int)((b.Kind is Kind.User or Kind.Assistant ? 12 : 8) * s);
            var max = b.Kind == Kind.System ? width - 2 * pad : bubbleMax;
            var size = TextRenderer.MeasureText(g, b.Text, FontOf(b.Kind), new Size(max - 2 * inner, 1_000_000), Wrap);
            var w = Math.Min(max, size.Width + 2 * inner + 2);
            var h = size.Height + 2 * inner;
            b.CaptionRect = b.Caption.Length == 0 ? Rectangle.Empty : new Rectangle(pad, y, width - 2 * pad, captionHeight);
            y += b.CaptionRect.Height;
            var x = b.Kind == Kind.User ? width - pad - w : b.Kind == Kind.System ? (width - w) / 2 : pad;
            b.Bubble = new Rectangle(x, y, w, h);
            b.TextRect = Rectangle.Inflate(b.Bubble, -inner, -inner);
            y += h + (b.Kind is Kind.Thinking or Kind.Tools ? gap / 2 : gap);
        }
        contentHeight = y + pad;
        bar.LargeChange = Math.Max(1, ClientSize.Height);
        bar.Maximum = Math.Max(0, contentHeight - 1);
        bar.Enabled = contentHeight > ClientSize.Height;
        bar.Value = Math.Clamp(bar.Value, 0, MaxScroll);
        Invalidate();
    }

    private int MaxScroll => Math.Max(0, contentHeight - ClientSize.Height);

    private void ScrollBy(int delta) => bar.Value = Math.Clamp(bar.Value + delta, 0, MaxScroll);

    protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); Relayout(); }
    protected override void OnResize(EventArgs e) { base.OnResize(e); Relayout(); }
    protected override void OnMouseWheel(MouseEventArgs e) { base.OnMouseWheel(e); ScrollBy(-e.Delta * DeviceDpi / 96); }
    protected override bool IsInputKey(Keys keyData) => keyData is Keys.Up or Keys.Down or Keys.PageUp or Keys.PageDown or Keys.Home or Keys.End || base.IsInputKey(keyData);

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        switch (e.KeyCode)
        {
            case Keys.Up: ScrollBy(-bar.SmallChange); break;
            case Keys.Down: ScrollBy(bar.SmallChange); break;
            case Keys.PageUp: ScrollBy(-ClientSize.Height * 9 / 10); break;
            case Keys.PageDown: ScrollBy(ClientSize.Height * 9 / 10); break;
            case Keys.Home: bar.Value = 0; break;
            case Keys.End: bar.Value = MaxScroll; break;
            default: return;
        }
        e.Handled = true;
    }

    private Block? Hit(Point p)
    {
        var at = new Point(p.X, p.Y + bar.Value);
        return blocks.FirstOrDefault(b => b.Bubble.Contains(at));
    }

    private static bool Collapsible(Block b) => b.Kind is Kind.Thinking or Kind.Tools;

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        Cursor = Hit(e.Location) is { } b && Collapsible(b) ? Cursors.Hand : Cursors.Default;
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        Focus();
        if (Hit(e.Location) is not { } b) return;
        if (e.Button == MouseButtons.Left && e.Clicks == 1 && Collapsible(b))
        {
            if (!expanded.Add((b.Index, b.Kind))) expanded.Remove((b.Index, b.Kind));
            Relayout();
        }
        else if (e.Button == MouseButtons.Left && e.Clicks == 2 && !Collapsible(b)) OpenFull?.Invoke(b.Caption.Length > 0 ? b.Caption : Speaker(b.Kind), Body(b));
        else if (e.Button == MouseButtons.Right) ShowMenu(b, e.Location);
    }

    private void ShowMenu(Block b, Point at)
    {
        var menu = new ContextMenuStrip { Font = NoteFont };
        menu.Items.Add("复制这条", null, (_, _) => Copy(Body(b)));
        menu.Items.Add("在窗口中查看全文", null, (_, _) => OpenFull?.Invoke(Speaker(b.Kind), Body(b)));
        menu.Items.Add("复制全部对话", null, (_, _) => Copy(Transcript()));
        menu.Closed += (_, _) => BeginInvoke(menu.Dispose);
        menu.Show(this, at);
    }

    internal static void Copy(string text)
    {
        try { if (text.Length > 0) Clipboard.SetText(text.Replace("\r\n", "\n").Replace("\n", "\r\n")); }
        catch (System.Runtime.InteropServices.ExternalException) { MessageBox.Show("剪贴板暂时被其他程序占用，请稍后重试。", "无法复制", MessageBoxButtons.OK, MessageBoxIcon.Information); }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(BackColor);
        var view = new Rectangle(0, 0, ClientSize.Width - bar.Width, ClientSize.Height);
        if (blocks.Count == 0)
        {
            TextRenderer.DrawText(g, Placeholder, BodyFont, view, DesktopTheme.Muted, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix);
            return;
        }
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var offset = bar.Value;
        var radius = 10 * DeviceDpi / 96f;
        Rectangle Shift(Rectangle r) => new(r.X, r.Y - offset, r.Width, r.Height);
        foreach (var b in blocks)
        {
            var top = b.CaptionRect.IsEmpty ? b.Bubble.Top : b.CaptionRect.Top;
            if (top - offset > view.Bottom) break;
            if (b.Bubble.Bottom - offset < 0) continue;
            if (!b.CaptionRect.IsEmpty)
                TextRenderer.DrawText(g, b.Caption, CaptionFont, Shift(b.CaptionRect), DesktopTheme.Muted,
                    (b.Kind == Kind.User ? TextFormatFlags.Right : b.Kind == Kind.System ? TextFormatFlags.HorizontalCenter : TextFormatFlags.Left) | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
            var (fill, ink, border) = Colors(b.Kind);
            using var path = DesktopTheme.RoundedRect(Shift(b.Bubble), b.Kind is Kind.User or Kind.Assistant ? radius : radius * 0.7f);
            using (var brush = new SolidBrush(fill)) g.FillPath(brush, path);
            if (border != Color.Empty) { using var pen = new Pen(border); g.DrawPath(pen, path); }
            TextRenderer.DrawText(g, b.Text, FontOf(b.Kind), Shift(b.TextRect), ink, Wrap);
        }
    }
}
