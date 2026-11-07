using System.ComponentModel;
using System.Drawing.Drawing2D;
using System.Text;
using DevinCleaner.Engine;

namespace DevinCleaner;

// Owner-drawn chat transcript: user bubbles on the right, Devin on the left.
// Text is laid out per character so it can be selected by dragging; only blocks
// inside the viewport are painted, so long sessions stay responsive.
internal sealed class ChatView : Control
{
    private const int DisplayLimit = 6000;
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
        public string Caption = "";
        public TextLayout Layout = null!;
        public Rectangle CaptionRect, Bubble;
        public Point Origin;
    }

    private readonly record struct Caret(int Block, int Offset) : IComparable<Caret>
    {
        public int CompareTo(Caret other) => Block != other.Block ? Block.CompareTo(other.Block) : Offset.CompareTo(other.Offset);
    }

    private readonly VScrollBar bar = new() { Dock = DockStyle.Right, SmallChange = 40 };
    private readonly System.Windows.Forms.Timer dragScroll = new() { Interval = 40 };
    private readonly TextMeasurer measure = new();
    private readonly List<Block> blocks = [];
    private readonly HashSet<(int, Kind)> expanded = [];
    private IReadOnlyList<ChatMessage> messages = [];
    private bool showThinking, showSystem, dragging, moved;
    private Caret anchor, focus;
    private Point downAt;
    private int contentHeight;

    public event Action<string, string>? OpenFull;

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public string Placeholder { get; set { field = value; Invalidate(); } } = "正在读取对话…";

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool ShowThinking { get => showThinking; set { showThinking = value; Rebuild(); } }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool ShowSystem { get => showSystem; set { showSystem = value; Rebuild(); } }

    public int TurnCount => blocks.Count(b => b.Kind is Kind.User or Kind.Assistant);

    public string SelectedText
    {
        get
        {
            var (from, to) = Ordered();
            if (from == to || blocks.Count == 0) return "";
            var text = new StringBuilder();
            for (var i = from.Block; i <= to.Block; i++)
            {
                var plain = blocks[i].Layout.Plain;
                var start = i == from.Block ? from.Offset : 0;
                var end = i == to.Block ? to.Offset : plain.Length;
                if (text.Length > 0) text.Append("\n\n");
                text.Append(plain, start, end - start);
            }
            return text.ToString();
        }
    }

    public ChatView()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
        BackColor = DesktopTheme.Canvas;
        TabStop = true;
        AccessibleName = "对话内容，可拖动选择文字，Ctrl+C 复制";
        Controls.Add(bar);
        bar.ValueChanged += (_, _) => Invalidate();
        dragScroll.Tick += (_, _) => DragTick();
    }

    public void SetMessages(IReadOnlyList<ChatMessage> value)
    {
        messages = value;
        expanded.Clear();
        bar.Value = 0;
        Rebuild();
    }

    public string Transcript() => string.Join("\n\n", blocks.Select(b => (b.Caption.Length > 0 ? $"【{b.Caption}】\n" : "") + Body(b)));

    internal static string Crlf(string s) => s.Replace("\r\n", "\n").Replace("\n", "\r\n");

    private static string Speaker(Kind kind) => kind switch { Kind.User => "你", Kind.System => "系统", _ => "Devin" };

    private void Rebuild()
    {
        blocks.Clear();
        anchor = focus = default;
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
                var head = $"调用工具 · {string.Join(", ", m.ToolCalls.Select(c => c.Name))}";
                if (!open) return "▸ " + head;
                return "▾ " + head + "\n\n" + string.Join("\n\n", m.ToolCalls.Select(c =>
                    $"■ {c.Name}\n参数：{c.Arguments}\n结果：{(c.Result is null ? "（没有记录结果）" : c.Result.Trim())}"));
            default:
                return m.Text.Trim();
        }
    }

    private static (Color Fill, Color Ink, Color Border, Color Highlight) Colors(Kind kind) => kind switch
    {
        Kind.User => (DesktopTheme.Ink, Color.White, Color.Empty, Color.FromArgb(96, 110, 140)),
        Kind.Assistant => (Color.White, DesktopTheme.Ink, DesktopTheme.Border, Color.FromArgb(198, 216, 246)),
        Kind.Thinking => (Color.FromArgb(244, 239, 252), Color.FromArgb(112, 84, 158), Color.Empty, Color.FromArgb(214, 200, 240)),
        Kind.Tools => (Color.FromArgb(239, 239, 234), DesktopTheme.Accent, Color.Empty, Color.FromArgb(198, 216, 246)),
        _ => (Color.FromArgb(253, 246, 230), Color.FromArgb(152, 94, 19), Color.Empty, Color.FromArgb(246, 222, 170)),
    };

    private static List<Paragraph> Paragraphs(Block b, string text)
    {
        var ink = Colors(b.Kind).Ink;
        return b.Kind switch
        {
            Kind.Assistant => Markdown.Parse(text, new TextStyle(BodyFont, ink)),
            Kind.User => Markdown.Plain(text, new TextStyle(BodyFont, ink)),
            Kind.Thinking => Markdown.Plain(text, new TextStyle(ThinkingFont, ink)),
            _ => Markdown.Plain(text, new TextStyle(NoteFont, ink)),
        };
    }

    private void Relayout()
    {
        var width = ClientSize.Width - bar.Width;
        if (width <= 0 || !IsHandleCreated) return;
        var s = DeviceDpi / 96f;
        int pad = (int)(20 * s), gap = (int)(14 * s), captionHeight = CaptionFont.Height + (int)(4 * s);
        var bubbleMax = Math.Max((int)(160 * s), (int)((width - 2 * pad) * 0.78));
        var y = pad;
        using var g = CreateGraphics();
        measure.Graphics = g;
        foreach (var b in blocks)
        {
            var full = Body(b);
            var text = full.Length > DisplayLimit ? full[..DisplayLimit] + "\n\n…（内容较长，右键「查看全文」）" : full.Length == 0 ? " " : full;
            var inner = (int)((b.Kind is Kind.User or Kind.Assistant ? 12 : 8) * s);
            var max = b.Kind == Kind.System ? width - 2 * pad : bubbleMax;
            b.Layout = TextLayout.Build(Paragraphs(b, text), max - 2 * inner, BodyFont.Height / 2, measure);
            var w = b.Layout.Width + 2 * inner;
            var h = b.Layout.Height + 2 * inner;
            b.CaptionRect = b.Caption.Length == 0 ? Rectangle.Empty : new Rectangle(pad, y, width - 2 * pad, captionHeight);
            y += b.CaptionRect.Height;
            var x = b.Kind == Kind.User ? width - pad - w : b.Kind == Kind.System ? (width - w) / 2 : pad;
            b.Bubble = new Rectangle(x, y, w, h);
            b.Origin = new Point(x + inner, y + inner);
            y += h + (b.Kind is Kind.Thinking or Kind.Tools ? gap / 2 : gap);
        }
        measure.Graphics = null;
        contentHeight = y + pad;
        bar.LargeChange = Math.Max(1, ClientSize.Height);
        bar.Maximum = Math.Max(0, contentHeight - 1);
        bar.Enabled = contentHeight > ClientSize.Height;
        bar.Value = Math.Clamp(bar.Value, 0, MaxScroll);
        Invalidate();
    }

    private int MaxScroll => Math.Max(0, contentHeight - ClientSize.Height);

    private void ScrollBy(int delta) => bar.Value = Math.Clamp(bar.Value + delta, 0, MaxScroll);

    private (Caret From, Caret To) Ordered() => anchor.CompareTo(focus) <= 0 ? (anchor, focus) : (focus, anchor);

    private bool HasSelection => anchor != focus;

    // Nearest caret to a viewport point; points between bubbles snap to the closest block edge.
    private Caret CaretAt(Point p)
    {
        var at = new Point(p.X, p.Y + bar.Value);
        for (var i = 0; i < blocks.Count; i++)
        {
            var b = blocks[i];
            if (at.Y < b.Bubble.Top) return new Caret(i, 0);
            if (at.Y < b.Bubble.Bottom)
            {
                if (at.X < b.Bubble.Left && b.Kind != Kind.User) return new Caret(i, 0);
                return new Caret(i, b.Layout.OffsetAt(new Point(at.X - b.Origin.X, at.Y - b.Origin.Y)));
            }
        }
        return new Caret(blocks.Count - 1, blocks[^1].Layout.Plain.Length);
    }

    private Block? Hit(Point p)
    {
        var at = new Point(p.X, p.Y + bar.Value);
        return blocks.FirstOrDefault(b => b.Bubble.Contains(at));
    }

    private static bool Collapsible(Block b) => b.Kind is Kind.Thinking or Kind.Tools;

    // Collapsed blocks toggle anywhere; expanded ones only on their header line, so the body stays selectable.
    private bool OnToggle(Block b, Point p) =>
        Collapsible(b) && (!expanded.Contains((b.Index, b.Kind)) || p.Y + bar.Value - b.Origin.Y < b.Layout.Lines[0].Height);

    protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); Relayout(); }
    protected override void OnResize(EventArgs e) { base.OnResize(e); Relayout(); }
    protected override void OnDpiChangedAfterParent(EventArgs e) { base.OnDpiChangedAfterParent(e); measure.Clear(); Relayout(); }
    protected override void OnMouseWheel(MouseEventArgs e) { base.OnMouseWheel(e); ScrollBy(-e.Delta * DeviceDpi / 96); }
    protected override bool IsInputKey(Keys keyData) => (keyData & Keys.KeyCode) is Keys.Up or Keys.Down or Keys.PageUp or Keys.PageDown or Keys.Home or Keys.End || base.IsInputKey(keyData);

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        switch (e.KeyCode)
        {
            case Keys.C when e.Control: Copy(SelectedText); break;
            case Keys.A when e.Control && blocks.Count > 0: anchor = new Caret(0, 0); focus = new Caret(blocks.Count - 1, blocks[^1].Layout.Plain.Length); Invalidate(); break;
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

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        Focus();
        if (blocks.Count == 0) return;
        if (e.Button == MouseButtons.Right)
        {
            if (Hit(e.Location) is { } hit) ShowMenu(hit, e.Location);
            return;
        }
        if (e.Button != MouseButtons.Left) return;
        if (e.Clicks == 2 && Hit(e.Location) is { } b && !OnToggle(b, e.Location))
        {
            var i = blocks.IndexOf(b);
            anchor = new Caret(i, 0);
            focus = new Caret(i, b.Layout.Plain.Length);
            Invalidate();
            return;
        }
        var caret = CaretAt(e.Location);
        if ((ModifierKeys & Keys.Shift) == 0) anchor = caret;
        focus = caret;
        dragging = true;
        moved = false;
        downAt = e.Location;
        Capture = true;
        Invalidate();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (dragging)
        {
            if (Math.Abs(e.X - downAt.X) + Math.Abs(e.Y - downAt.Y) > SystemInformation.DragSize.Width) moved = true;
            focus = CaretAt(e.Location);
            dragScroll.Enabled = e.Y < 0 || e.Y > ClientSize.Height;
            Invalidate();
            return;
        }
        Cursor = Hit(e.Location) is { } b ? OnToggle(b, e.Location) ? Cursors.Hand : Cursors.IBeam : Cursors.Default;
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (!dragging) return;
        dragging = false;
        dragScroll.Enabled = false;
        Capture = false;
        if (moved || e.Button != MouseButtons.Left || Hit(e.Location) is not { } b || !OnToggle(b, e.Location)) return;
        if (!expanded.Add((b.Index, b.Kind))) expanded.Remove((b.Index, b.Kind));
        Relayout();
    }

    private void DragTick()
    {
        var p = PointToClient(MousePosition);
        ScrollBy(p.Y < 0 ? p.Y : p.Y - ClientSize.Height);
        focus = CaretAt(p);
        Invalidate();
    }

    private void ShowMenu(Block b, Point at)
    {
        var menu = new ContextMenuStrip { Font = NoteFont };
        var selection = SelectedText;
        menu.Items.Add("复制选中文字", null, (_, _) => Copy(selection)).Enabled = selection.Length > 0;
        menu.Items.Add("复制这条（原文）", null, (_, _) => Copy(Body(b)));
        menu.Items.Add("查看全文", null, (_, _) => OpenFull?.Invoke(b.Caption.Length > 0 ? b.Caption : Speaker(b.Kind), Body(b)));
        menu.Items.Add("复制全部对话", null, (_, _) => Copy(Transcript()));
        menu.Closed += (_, _) => BeginInvoke(menu.Dispose);
        menu.Show(this, at);
    }

    internal static void Copy(string text)
    {
        try { if (text.Length > 0) Clipboard.SetText(Crlf(text)); }
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
        var offset = bar.Value;
        var s = DeviceDpi / 96f;
        var (from, to) = Ordered();
        for (var i = 0; i < blocks.Count; i++)
        {
            var b = blocks[i];
            var top = b.CaptionRect.IsEmpty ? b.Bubble.Top : b.CaptionRect.Top;
            if (top - offset > view.Bottom) break;
            if (b.Bubble.Bottom - offset < 0) continue;
            if (!b.CaptionRect.IsEmpty)
                TextRenderer.DrawText(g, b.Caption, CaptionFont, new Rectangle(b.CaptionRect.X, b.CaptionRect.Y - offset, b.CaptionRect.Width, b.CaptionRect.Height), DesktopTheme.Muted,
                    (b.Kind == Kind.User ? TextFormatFlags.Right : b.Kind == Kind.System ? TextFormatFlags.HorizontalCenter : TextFormatFlags.Left) | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
            var (fill, _, border, highlight) = Colors(b.Kind);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var bubble = new Rectangle(b.Bubble.X, b.Bubble.Y - offset, b.Bubble.Width, b.Bubble.Height);
            using (var path = DesktopTheme.RoundedRect(bubble, (b.Kind is Kind.User or Kind.Assistant ? 10 : 7) * s))
            {
                using (var brush = new SolidBrush(fill)) g.FillPath(brush, path);
                if (border != Color.Empty) { using var pen = new Pen(border); g.DrawPath(pen, path); }
            }
            g.SmoothingMode = SmoothingMode.None;
            var selStart = i < from.Block || i > to.Block ? -1 : i == from.Block ? from.Offset : 0;
            var selEnd = i == to.Block ? to.Offset : int.MaxValue;
            PaintText(g, b, new Point(b.Origin.X, b.Origin.Y - offset), view, highlight, selStart, selEnd, s);
        }
    }

    private static void PaintText(Graphics g, Block b, Point origin, Rectangle view, Color highlight, int selStart, int selEnd, float s)
    {
        using var codeFill = new SolidBrush(b.Kind == Kind.User ? Color.FromArgb(70, 70, 68) : Color.FromArgb(244, 244, 241));
        using var selFill = new SolidBrush(highlight);
        var layout = b.Layout;
        foreach (var line in layout.Lines)
        {
            var y = origin.Y + line.Y;
            if (y > view.Bottom) break;
            if (y + line.Height < 0) continue;
            if (line.Code) g.FillRectangle(codeFill, origin.X - (int)(4 * s), y, layout.Width + (int)(8 * s), line.Height);
            if (line.Rule) { using var pen = new Pen(DesktopTheme.Border); g.DrawLine(pen, origin.X, y + line.Height / 2, origin.X + layout.Width, y + line.Height / 2); }
            if (selStart >= 0 && selStart <= line.End && selEnd > line.Start)
            {
                var a = Math.Max(selStart, line.Start);
                var z = Math.Min(selEnd, line.End);
                var x1 = line.XOf(a);
                var x2 = selEnd > line.End ? Math.Max(line.XOf(z) + (int)(4 * s), x1 + (int)(4 * s)) : line.XOf(z);
                if (x2 > x1) g.FillRectangle(selFill, origin.X + x1, y, x2 - x1, line.Height);
            }
            foreach (var seg in line.Segs)
            {
                var font = seg.Style.Font;
                var segY = y + line.Height - font.Height;
                if (seg.Style.Code && !line.Code) g.FillRectangle(codeFill, origin.X + seg.X - 1, segY, seg.Right - seg.X + 2, font.Height);
                TextRenderer.DrawText(g, seg.Text, font, new Point(origin.X + seg.X, segY), seg.Style.Code && b.Kind == Kind.User ? Color.White : seg.Style.Color, TextMeasurer.Flags);
            }
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) dragScroll.Dispose();
        base.Dispose(disposing);
    }
}
