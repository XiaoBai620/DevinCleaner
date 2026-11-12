using System.Drawing;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace DevinCleaner.Tests;

public sealed class FilterComboBoxTests
{
    [Fact]
    public void NativeDropdownShowsEveryFilterAfterHandleRecreation() => OnSta(() =>
    {
        using var form = Create(out var filter);
        form.Show();
        AssertAllItemsVisible(filter);
        filter.DroppedDown = false;
        typeof(Control).GetMethod("RecreateHandle", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(filter, null);
        AssertAllItemsVisible(filter);
        filter.DroppedDown = false;
        form.Close();
    });

    [Fact]
    public void NativeKeyboardSelectionRetainsFilterTextAndEvents() => OnSta(() =>
    {
        using var form = Create(out var filter);
        form.Show();
        filter.Focus();
        var changes = 0;
        filter.SelectedIndexChanged += (_, _) => changes++;
        SendMessage(filter.Handle, 0x0100, (IntPtr)Keys.Down, (IntPtr)1);
        SendMessage(filter.Handle, 0x0101, (IntPtr)Keys.Down, (IntPtr)1);
        Application.DoEvents();
        Assert.Equal(1, filter.SelectedIndex);
        Assert.Equal("可清理", filter.Text);
        Assert.Equal(1, changes);
        AssertAllItemsVisible(filter);
        filter.DroppedDown = false;
        Assert.Equal(1, filter.SelectedIndex);
        form.Close();
    });

    private static Form Create(out FilterComboBox filter)
    {
        var form = new Form
        {
            ShowInTaskbar = false, StartPosition = FormStartPosition.Manual,
            Location = new Point(-20000, -20000), ClientSize = new Size(300, 200),
            AutoScaleMode = AutoScaleMode.Dpi, AutoScaleDimensions = new SizeF(96, 96),
        };
        filter = new FilterComboBox { Location = new Point(12, 12), Width = 240 };
        filter.Items.AddRange(["全部会话", "可清理", "运行中", "已隐藏"]);
        filter.SelectedIndex = 0;
        form.Controls.Add(filter);
        return form;
    }

    private static void AssertAllItemsVisible(FilterComboBox filter)
    {
        filter.DroppedDown = true;
        Application.DoEvents();
        var info = new ComboBoxInfo { Size = Marshal.SizeOf<ComboBoxInfo>() };
        Assert.True(GetComboBoxInfo(filter.Handle, ref info));
        Assert.NotEqual(IntPtr.Zero, info.List);
        Assert.True(GetClientRect(info.List, out var client));
        Assert.NotEqual((IntPtr)(-1), SendMessage(info.List, 0x0198, (IntPtr)(filter.Items.Count - 1), out var lastItem));
        Assert.True(lastItem.Top >= client.Top && lastItem.Bottom <= client.Bottom,
            $"Final filter row {lastItem.Top}..{lastItem.Bottom} is clipped by {client.Top}..{client.Bottom}.");
        Assert.InRange(client.Bottom - client.Top, filter.ItemHeight * filter.Items.Count,
            filter.ItemHeight * filter.Items.Count + 2);
    }

    private static void OnSta(Action action)
    {
        ExceptionDispatchInfo? failure = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception error) { failure = ExceptionDispatchInfo.Capture(error); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "The native filter test did not finish.");
        failure?.Throw();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct ComboBoxInfo
    {
        public int Size;
        public NativeRect Item, Button;
        public int ButtonState;
        public IntPtr Combo, Edit, List;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetComboBoxInfo(IntPtr window, ref ComboBoxInfo info);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetClientRect(IntPtr window, out NativeRect rect);
    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr window, int message, IntPtr wParam, out NativeRect rect);
}
