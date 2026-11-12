using System.Diagnostics;
using System.Drawing;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace DevinCleaner.Tests;

public sealed class SearchFocusTests
{
    private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;

    [Fact]
    public void BlankSurfacesDismissSearchWithoutChangingItsText() => WithWindow(async form =>
    {
        var search = Field<TextBox>(form, "search");
        var field = Assert.IsType<SearchField>(search.Parent);
        search.Text = "Synthetic";
        foreach (var surface in new Control[] { form.Controls[0], Field<Label>(form, "processTitle"), form })
        {
            Assert.True(search.Focus());
            Assert.True(field.ContainsFocus);
            Assert.Equal(DesktopTheme.Accent, field.BorderColor);
            MouseDown(surface);
            await NextMessage();
            Assert.False(search.Focused);
            Assert.False(field.ContainsFocus);
            Assert.Equal(DesktopTheme.Border, field.BorderColor);
            Assert.Equal("Synthetic", search.Text);
        }
    });

    [Fact]
    public void SearchSurfaceAndClearButtonKeepEditingWhileOtherControlsWork() => WithWindow(async form =>
    {
        var search = Field<TextBox>(form, "search");
        var field = Assert.IsType<SearchField>(search.Parent);
        search.Text = "Synthetic";
        Assert.True(search.Focus());
        MouseDown(field);
        MouseDown(search);
        await NextMessage();
        Assert.True(search.Focused);
        Assert.Equal("Synthetic", search.Text);

        Field<Button>(form, "clearSearch").PerformClick();
        Assert.True(search.Focused);
        Assert.Empty(search.Text);

        var selectAll = Field<Button>(form, "selectAll");
        MouseDown(selectAll);
        selectAll.PerformClick();
        await NextMessage();
        Assert.False(field.ContainsFocus);
        Assert.Equal(Fixture.Ids.Length, Field<HashSet<string>>(form, "selected").Count);

        Assert.True(search.Focus());
        var filter = Field<ComboBox>(form, "filter");
        MouseDown(filter);
        await NextMessage();
        Assert.True(filter.Focused);
        filter.DroppedDown = false;

        Assert.True(search.Focus());
        var list = Field<DataGridView>(form, "list");
        MouseDown(list);
        await NextMessage();
        Assert.True(list.ContainsFocus);
    });

    [Fact]
    public void KeyboardNavigationStillLeavesAndReturnsToSearch() => WithWindow(async form =>
    {
        var search = Field<TextBox>(form, "search");
        search.Text = "Synthetic";
        Assert.True(search.Focus());
        var tab = Message.Create(search.Handle, 0x0100, (nint)Keys.Tab, 0);
        Assert.True(search.PreProcessMessage(ref tab));
        await NextMessage();
        Assert.False(search.Focused);

        typeof(Control).GetMethod("OnKeyDown", PrivateInstance)!.Invoke(form, [new KeyEventArgs(Keys.Control | Keys.F)]);
        Assert.True(search.Focused);
        Assert.Equal(search.TextLength, search.SelectionLength);
    });

    private static void WithWindow(Func<MainForm, Task> action)
    {
        ExceptionDispatchInfo? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                using var fixture = new Fixture();
                using var form = new MainForm(fixture.Cleaner, new NoUpdates())
                {
                    ShowInTaskbar = false,
                    StartPosition = FormStartPosition.Manual,
                    Location = new Point(-20000, -20000),
                };
                form.Shown += async (_, _) =>
                {
                    try
                    {
                        var watch = Stopwatch.StartNew();
                        while (!Field<Button>(form, "refresh").Enabled && watch.Elapsed < TimeSpan.FromSeconds(10))
                            await Task.Delay(10);
                        Assert.True(Field<Button>(form, "refresh").Enabled);
                        await action(form);
                    }
                    catch (Exception error) { failure = ExceptionDispatchInfo.Capture(error); }
                    finally { form.Close(); }
                };
                Application.Run(form);
            }
            catch (Exception error) { failure = ExceptionDispatchInfo.Capture(error); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(120)), "The isolated focus test did not finish within two minutes.");
        failure?.Throw();
    }

    private static T Field<T>(MainForm form, string name) =>
        (T)typeof(MainForm).GetField(name, PrivateInstance)!.GetValue(form)!;

    private static Task NextMessage() => Task.Delay(10);

    private static void MouseDown(Control control)
    {
        // Target this test window directly; never move the user's pointer or send global input.
        SendMessage(control.Handle, 0x0201, (nint)1, (nint)(4 | (4 << 16)));
        // End the press without relying on desktop cursor hit-testing for an offscreen window.
        SendMessage(control.Handle, 0x001F, 0, 0);
    }

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern nint SendMessage(nint window, int message, nint wParam, nint lParam);

    private sealed class NoUpdates : IUpdateChecker
    {
        public Task<AvailableUpdate?> CheckAsync(CancellationToken cancellationToken) => Task.FromResult<AvailableUpdate?>(null);
    }
}
