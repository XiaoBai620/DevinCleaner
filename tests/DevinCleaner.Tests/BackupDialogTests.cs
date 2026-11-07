using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Windows.Forms;
using DevinCleaner.Engine;

namespace DevinCleaner.Tests;

public sealed class BackupDialogTests
{
    private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;

    [Fact]
    public void SelectAllChecksOnlyRestorableSessionsAndToggles() => OnSta(() =>
    {
        using var fixture = new Fixture();
        var plan = fixture.Cleaner.PlanCleanup(new Selection([Fixture.Ids[0], Fixture.Ids[1]]));
        var backup = fixture.Cleaner.Execute(plan.Id).BackupPath!;
        fixture.Cleaner.Restore(backup, [Fixture.Ids[1]]);

        using var dialog = new BackupDialog(fixture.Cleaner);
        _ = dialog.Handle;
        typeof(BackupDialog).GetField("items", PrivateInstance)!.SetValue(dialog, fixture.Cleaner.Backups());
        var backups = Field<ListBox>(dialog, "backupList");
        backups.Items.Add("backup");
        backups.SelectedIndex = 0;
        var sessions = Field<CheckedListBox>(dialog, "sessionList");
        var selectAll = Field<Button>(dialog, "selectAll");
        var info = fixture.Cleaner.Backups().Single();
        var restorable = info.Sessions.Select((s, i) => (s, i)).Where(x => x.s.BlockedReason is null).Select(x => x.i).ToList();
        Assert.Equal(2, sessions.Items.Count);
        Assert.Single(restorable);

        Click(selectAll);
        Assert.Equal(restorable, sessions.CheckedIndices.Cast<int>());
        Assert.Equal("取消全选", selectAll.Text);
        Assert.True(Field<Button>(dialog, "restore").Enabled);

        Click(selectAll);
        Assert.Empty(sessions.CheckedIndices.Cast<int>());
        Assert.Equal("全选", selectAll.Text);
        Assert.False(Field<Button>(dialog, "restore").Enabled);
    });

    // The dialog is never shown, so PerformClick would be ignored; raise Click directly.
    private static void Click(Button button) => typeof(Control).GetMethod("OnClick", PrivateInstance)!.Invoke(button, [EventArgs.Empty]);

    private static T Field<T>(BackupDialog dialog, string name) => (T)typeof(BackupDialog).GetField(name, PrivateInstance)!.GetValue(dialog)!;

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
}
