using System.Diagnostics;

namespace DevinCleaner.Engine;

public static class DevinProcesses
{
    // Covers both the desktop app (Devin.exe) and the CLI (devin.exe); the lookup is case-insensitive.
    public static ProcessState Detect()
    {
        var found = new List<string>();
        foreach (var process in Process.GetProcessesByName("devin"))
            using (process) found.Add($"{process.ProcessName} ({process.Id})");
        return new ProcessState(found.Count > 0, found);
    }
}
