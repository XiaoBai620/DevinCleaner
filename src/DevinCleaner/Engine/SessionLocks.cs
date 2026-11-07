namespace DevinCleaner.Engine;

// Devin keeps session_locks/<id>.lock open while the session is active, so an
// exclusive open fails with a sharing or lock violation. This does not depend on
// the PID written in the file, which Windows may have reused long ago.
internal static class SessionLocks
{
    private const int SharingViolation = unchecked((int)0x80070020);
    private const int LockViolation = unchecked((int)0x80070021);

    public static bool IsHeld(string file)
    {
        try
        {
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.None);
            return false;
        }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException) { return false; }
        catch (IOException e) when (e.HResult is SharingViolation or LockViolation) { return true; }
        catch (UnauthorizedAccessException) { return true; }
    }

    // Holding the lock exclusively stops Devin from resuming the session while it is deleted.
    public static Held Acquire(string file)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        var existed = File.Exists(file);
        try { return new Held(new FileStream(file, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None), file, !existed); }
        catch (Exception e) when (e is UnauthorizedAccessException || (e is IOException io && io.HResult is SharingViolation or LockViolation))
        {
            throw new CleanerException($"会话 {Path.GetFileNameWithoutExtension(file)} 正在 Devin 中运行，请关闭该会话后重试。", e);
        }
    }

    public sealed class Held(FileStream stream, string file, bool created) : IDisposable
    {
        public bool RemoveOnRelease { get; set; } = created;

        public void Dispose()
        {
            stream.Dispose();
            if (!RemoveOnRelease) return;
            // Devin may already have reopened a lock it recreated; that copy is not ours to remove.
            try { File.Delete(file); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
}
