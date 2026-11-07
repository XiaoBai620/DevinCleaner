using System.Drawing;
using System.Windows.Forms;

namespace DevinCleaner.Tests;

public sealed class ChatLayoutTests
{
    private static readonly TextStyle Body = new(new Font("Microsoft YaHei UI", 9.5f), Color.Black);

    [Fact]
    public void MarkdownStripsMarkersButKeepsText()
    {
        var paragraphs = Markdown.Parse("# 标题\n**粗体** 和 `code`\n- 项目\n---\n```cs\nvar x = 1;\n```", Body);
        Assert.Equal(["标题", "粗体 和 code", "• 项目", "", "var x = 1;"], paragraphs.Select(p => string.Concat(p.Runs.Select(r => r.Text))));
        Assert.True(paragraphs[0].Runs[0].Style.Font.Bold);
        Assert.True(paragraphs[1].Runs[0].Style.Font.Bold);
        Assert.False(paragraphs[1].Runs[1].Style.Font.Bold);
        Assert.True(paragraphs[1].Runs[2].Style.Code);
        Assert.True(paragraphs[3].Rule);
        Assert.True(paragraphs[4].Code);
    }

    [Fact]
    public void UnmatchedMarkersStayLiteral()
    {
        var paragraphs = Markdown.Parse("a ** b ` c", Body);
        Assert.Equal("a ** b ` c", string.Concat(paragraphs[0].Runs.Select(r => r.Text)));
    }

    [Fact]
    public void LayoutWrapsWithinWidthAndMapsOffsets()
    {
        using var bitmap = new Bitmap(1, 1);
        using var g = Graphics.FromImage(bitmap);
        var measure = new TextMeasurer { Graphics = g };
        var text = "这是一段用于测试换行的中文文本，mixed with English words that should wrap.";
        var layout = TextLayout.Build(Markdown.Plain(text + "\n第二段", Body), 120, 8, measure);
        Assert.Equal(text + "\n第二段", layout.Plain);
        Assert.True(layout.Lines.Count > 3);
        Assert.All(layout.Lines, l => Assert.True(l.Right <= 120, $"line {l.Start} is {l.Right}px"));
        Assert.Equal(text.Length + 1, layout.Lines[^1].Start);
        Assert.Equal(0, layout.OffsetAt(new Point(-5, 0)));
        Assert.Equal(layout.Plain.Length, layout.OffsetAt(new Point(1000, layout.Height + 50)));
        var line = layout.Lines[1];
        Assert.Equal(line.Start, line.OffsetAt(0));
        Assert.Equal(line.XOf(line.Start + 2), line.Segs[0].X + line.Segs[0].Advance[2]);
    }

    [Fact]
    public void MeasuredWidthMatchesRenderedWidth()
    {
        using var bitmap = new Bitmap(1, 1);
        using var g = Graphics.FromImage(bitmap);
        var measure = new TextMeasurer { Graphics = g };
        const string text = "Devin 回复 ab12 ×→";
        var layout = TextLayout.Build(Markdown.Plain(text, Body), 10_000, 8, measure);
        var rendered = TextRenderer.MeasureText(g, text, Body.Font, Size.Empty, TextMeasurer.Flags).Width;
        Assert.InRange(layout.Width - rendered, -2, 2);
    }
}
