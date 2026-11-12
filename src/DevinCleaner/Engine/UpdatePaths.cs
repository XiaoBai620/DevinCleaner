using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace DevinCleaner.Engine;

internal static class SafePaths
{
    public static string Normal(string value)
    {
        var unprefixed = value.StartsWith(@"\\?\UNC\", StringComparison.Ordinal) ? @"\\" + value[8..]
            : value.StartsWith(@"\\?\", StringComparison.Ordinal) ? value[4..] : value;
        return Path.GetFullPath(unprefixed);
    }

    public static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    public static bool Inside(string root, string target)
    {
        var rel = Path.GetRelativePath(root, target);
        return rel == "." || (rel != ".." && !rel.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) && !Path.IsPathRooted(rel));
    }
}

internal static class FileLinks
{
    // .NET has no hard-link count API; a second link would let a delete or rewrite reach data outside the root.
    public static uint Count(string file)
    {
        using var handle = File.OpenHandle(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (!GetFileInformationByHandle(handle, out var info)) throw new Win32Exception(Marshal.GetLastWin32Error());
        return info.NumberOfLinks;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileInformation
    {
        public uint FileAttributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime, LastAccessTime, LastWriteTime;
        public uint VolumeSerialNumber, FileSizeHigh, FileSizeLow, NumberOfLinks, FileIndexHigh, FileIndexLow;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle file, out FileInformation information);
}
