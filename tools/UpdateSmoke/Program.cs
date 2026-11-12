using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DevinCleaner;

// Test-only host. It links the production updater but never links or constructs Cleaner.
internal static class Program
{
    private static string results = "";

    [STAThread]
    private static int Main(string[] args)
    {
        results = Environment.GetEnvironmentVariable("DEVINCLEANER_UPDATE_SMOKE_RESULTS") ?? "";
        var root = Path.GetFullPath(results);
        if (!Directory.Exists(root) || !File.Exists(Path.Combine(root, ".smoke-owner"))) return 90;
        using var process = Process.GetCurrentProcess();
        Record($"process-{process.Id}.json", new
        {
            id = process.Id,
            path = Environment.ProcessPath,
            startTicks = process.StartTime.ToUniversalTime().Ticks,
            role = args.FirstOrDefault() ?? "",
        });
        try
        {
            ApplicationConfiguration.Initialize();
            if (UpdateInstaller.TryRun(args)) return 0;
            if (args.Length == 3 && args[0] == "--smoke-start")
            {
                BeginUpdate(args[1], Version.Parse(args[2])).GetAwaiter().GetResult();
                Record("handoff.json", new { committed = true });
                return 0;
            }
            if (args.FirstOrDefault() != "--complete-update") return 91;
            Record("new-started.json", new { version = UpdateChecker.CurrentVersion.ToString(3) });
            if (Environment.GetEnvironmentVariable("DEVINCLEANER_UPDATE_SMOKE_FAIL_STARTUP") == "1") return 23;

            using var form = new Form
            {
                Text = "DevinCleaner isolated update smoke test",
                Width = 420,
                Height = 150,
                ShowInTaskbar = false,
                StartPosition = FormStartPosition.CenterScreen,
            };
            using var timer = new System.Windows.Forms.Timer { Interval = 100 };
            var watch = Stopwatch.StartNew();
            var stage = JsonDocument.Parse(File.ReadAllText(Path.Combine(results, "prepared.json")))
                .RootElement.GetProperty("directory").GetString()!;
            form.Shown += (_, _) =>
            {
                UpdateInstaller.ConfirmStartup();
                Record("shown.json", new { version = UpdateChecker.CurrentVersion.ToString(3) });
                timer.Start();
            };
            timer.Tick += (_, _) =>
            {
                var siblings = Directory.GetFiles(Path.GetDirectoryName(Environment.ProcessPath!)!, ".devincleaner-update-*");
                if (!Directory.Exists(stage) && siblings.Length == 0)
                {
                    Record("complete.json", new { version = UpdateChecker.CurrentVersion.ToString(3), cleanup = true });
                    form.Close();
                }
                else if (watch.Elapsed > TimeSpan.FromSeconds(30))
                {
                    Record("failure.json", new { error = "Startup was shown but update cleanup did not complete." });
                    form.Close();
                }
            };
            Application.Run(form);
            return File.Exists(Path.Combine(results, "complete.json")) ? 0 : 92;
        }
        catch (Exception error)
        {
            Record("failure.json", new { error = error.ToString() });
            return 93;
        }
        finally { Record($"exit-{process.Id}.json", new { exited = true }); }
    }

    private static async Task BeginUpdate(string candidate, Version version)
    {
        var bytes = File.ReadAllBytes(candidate);
        var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
        var url = $"https://github.com/XiaoBai620/DevinCleaner/releases/download/v{version.ToString(3)}/";
        var executable = new Uri(url + "DevinCleaner.exe");
        var checksum = new Uri(url + "SHA256SUMS.txt");
        using var client = new HttpClient(new LocalPackageHandler(executable, checksum, bytes, hash));
        var updater = new AppUpdater(client, AppUpdater.StagingRoot);
        var prepared = await updater.DownloadAsync(new AvailableUpdate(version,
            new Uri($"https://github.com/XiaoBai620/DevinCleaner/releases/tag/v{version.ToString(3)}"),
            "Isolated synthetic smoke test", new UpdatePackage(executable, checksum, bytes.LongLength, hash)),
            null, CancellationToken.None);
        Record("prepared.json", new { directory = prepared.Directory });
        new UpdateInstaller().Start(prepared);
    }

    private static void Record(string name, object value)
    {
        var path = Path.Combine(results, name);
        var temporary = path + ".writing";
        File.WriteAllText(temporary, JsonSerializer.Serialize(value));
        File.Move(temporary, path, overwrite: true);
    }

    private sealed class LocalPackageHandler(Uri executable, Uri checksum, byte[] bytes, string hash) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (request.RequestUri != executable && request.RequestUri != checksum)
                throw new InvalidOperationException("The isolated smoke host never requests network content.");
            var content = request.RequestUri == checksum
                ? Encoding.UTF8.GetBytes(hash + "  DevinCleaner.exe\n") : bytes;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                RequestMessage = request,
                Content = new ByteArrayContent(content),
            });
        }
    }
}
