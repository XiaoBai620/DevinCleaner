using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Windows.Forms;
using DevinCleaner.Engine;

namespace DevinCleaner.Tests;

public sealed class MainFormTests
{
    private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;

    [Fact]
    public void RunningSessionsCannotBeChecked() => OnSta(() =>
    {
        using var fixture = new Fixture();
        using var held = fixture.HoldLock(Fixture.Ids[0]);
        using var form = Create(fixture);
        var grid = Field<DataGridView>(form, "list");
        CheckRow(grid, Fixture.Ids[0]);
        CheckRow(grid, Fixture.Ids[1]);
        Assert.Equal([Fixture.Ids[1]], Field<HashSet<string>>(form, "selected"));
        Assert.True(Field<Button>(form, "preview").Enabled);
    });

    [Fact]
    public void FilterAndSearchNarrowTheList() => OnSta(() =>
    {
        using var fixture = new Fixture();
        using var form = Create(fixture);
        var grid = Field<DataGridView>(form, "list");
        Assert.Equal(3, grid.Rows.Count);
        Field<ComboBox>(form, "filter").SelectedIndex = 3;
        Assert.Equal("gamma-session", ((SessionInfo)Assert.Single(grid.Rows.Cast<DataGridViewRow>()).Tag!).Id);
        Field<ComboBox>(form, "filter").SelectedIndex = 0;
        Field<TextBox>(form, "search").Text = "project-1";
        Assert.Equal("beta-session", ((SessionInfo)Assert.Single(grid.Rows.Cast<DataGridViewRow>()).Tag!).Id);
    });

    [Fact]
    public void MaintenanceOptionsEnablePreviewWithoutSessions() => OnSta(() =>
    {
        using var fixture = new Fixture();
        using var form = Create(fixture);
        Assert.False(Field<Button>(form, "preview").Enabled);
        Field<CheckBox>(form, "cleanLocks").Checked = true;
        Assert.True(Field<Button>(form, "preview").Enabled);
        Assert.Contains("2 个", Field<CheckBox>(form, "cleanLocks").Text);
    });

    private static MainForm Create(Fixture fixture)
    {
        var form = new MainForm(fixture.Cleaner, new NoUpdates());
        typeof(MainForm).GetField("state", PrivateInstance)!.SetValue(form, fixture.Cleaner.Snapshot());
        typeof(MainForm).GetMethod("RenderSessions", PrivateInstance)!.Invoke(form, null);
        return form;
    }

    private static T Field<T>(MainForm form, string name) => (T)typeof(MainForm).GetField(name, PrivateInstance)!.GetValue(form)!;

    private static void CheckRow(DataGridView grid, string id)
    {
        var row = grid.Rows.Cast<DataGridViewRow>().Single(r => ((SessionInfo)r.Tag!).Id == id);
        typeof(DataGridView).GetMethod("OnCellClick", PrivateInstance)!.Invoke(grid, [new DataGridViewCellEventArgs(0, row.Index)]);
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
        Assert.True(thread.Join(TimeSpan.FromSeconds(120)), "The isolated UI test did not finish within two minutes.");
        failure?.Throw();
    }

    private sealed class NoUpdates : IUpdateChecker
    {
        public Task<AvailableUpdate?> CheckAsync(CancellationToken cancellationToken) => Task.FromResult<AvailableUpdate?>(null);
    }
}
