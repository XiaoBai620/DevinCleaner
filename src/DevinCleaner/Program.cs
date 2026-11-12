using DevinCleaner.Engine;

namespace DevinCleaner;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        if (UpdateInstaller.TryRun(args)) return;
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => MessageBox.Show(e.Exception.Message, "Devin 对话清理台", MessageBoxButtons.OK, MessageBoxIcon.Error);
        Cleaner cleaner;
        try { cleaner = new Cleaner(Cleaner.DefaultDataRoot(), Cleaner.DefaultBackupRoot()); }
        catch (CleanerException e)
        {
            MessageBox.Show(e.Message, "Devin 对话清理台", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }
        using var form = new MainForm(cleaner);
        form.Shown += (_, _) => UpdateInstaller.ConfirmStartup();
        Application.Run(form);
    }
}
