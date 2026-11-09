using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using ChestTracker.Core.Diagnostics;
using ChestTracker.Core.Updates;

namespace ChestTracker.App.Services;

/// <summary>Outcome of the last installation attempt, written by the updater and shown once by the next start.</summary>
public sealed record UpdateResult(bool Success, string From, string To, string Message, DateTimeOffset At);

/// <summary>
/// GitHub update check, download and handover to the updater. Everything update-related lives in
/// %LOCALAPPDATA%\FortniteChestTracker-Update (staging, updater copy, backup, log), separate from the user's data in
/// %LOCALAPPDATA%\FortniteChestTracker, which an update never touches.
/// </summary>
public sealed class UpdateService : IDisposable
{
    private readonly HttpClient _http;

    public UpdateService()
    {
        CurrentVersion = Current();
        _http = UpdateChecker.CreateHttpClient(CurrentVersion.ToString());
        // Test hook: a local stand-in for the GitHub API (release assets must still carry the project's signature).
        var api = Environment.GetEnvironmentVariable("CHESTTRACKER_UPDATE_API");
        ApiBase = Uri.TryCreate(api, UriKind.Absolute, out var u) ? u : null;
    }

    public SemVersion CurrentVersion { get; }

    public Uri? ApiBase { get; }

    public static string Root => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FortniteChestTracker-Update");

    public static string ResultFile => Path.Combine(Root, "last-update.json");

    public static string LogFile => Path.Combine(Root, "update.log");

    /// <summary>A version that was installed but did not start; it is not offered again automatically.</summary>
    public static string SkipFile => Path.Combine(Root, "skip-version.txt");

    public static bool IsSkipped(SemVersion version)
    {
        try
        {
            return File.Exists(SkipFile) && SemVersion.TryParse(File.ReadAllText(SkipFile).Trim(), out var skip) && skip.CompareTo(version) == 0;
        }
        catch (IOException)
        {
            return false;
        }
    }

    /// <summary>The folder the running app was started from (also for a single-file build).</summary>
    public static string InstallDirectory => Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory;

    public static SemVersion Current()
    {
        var info = typeof(UpdateService).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        return SemVersion.TryParse(info, out var v) ? v
            : SemVersion.TryParse(typeof(UpdateService).Assembly.GetName().Version?.ToString(), out var a) ? a
            : new SemVersion(0, 0, 0);
    }

    /// <summary>Never throws; GitHub problems only mean "no update this time".</summary>
    public async Task<UpdateCheckResult> CheckAsync(CancellationToken ct = default)
    {
        var result = await new UpdateChecker(_http, ApiBase).CheckAsync(CurrentVersion, ct);
        if (result is { Status: UpdateCheckStatus.Available, Release: { } r } && IsSkipped(r.Version))
        {
            Log.Info($"Update {r.Version} is not offered again: it did not start on this PC (download it manually to retry)");
            return new UpdateCheckResult(UpdateCheckStatus.UpToDate);
        }

        Log.Info(result.Status switch
        {
            UpdateCheckStatus.Available => $"Update available: {result.Release!.Version} (installed {CurrentVersion})",
            UpdateCheckStatus.UpToDate => $"Up to date ({CurrentVersion})",
            _ => $"Update check skipped: {result.Error}",
        });
        return result;
    }

    /// <summary>
    /// Downloads and verifies the package, copies the current executable to the update folder and starts it in
    /// updater mode. The caller must then exit the app; the updater waits for that before touching any file.
    /// </summary>
    public async Task DownloadAndStartUpdaterAsync(ReleaseInfo release, IProgress<double> progress, CancellationToken ct)
    {
        var staging = Path.Combine(Root, "staging");
        var appDir = await new UpdatePackage(_http).DownloadAndVerifyAsync(release, staging, progress, ct);
        Log.Info($"Update {release.Version} downloaded and verified (signature and SHA-256)");

        var updaterDir = Path.Combine(Root, "updater");
        Directory.CreateDirectory(updaterDir);
        var updater = Path.Combine(updaterDir, "FortniteChestTracker-Updater.exe");
        File.Copy(Environment.ProcessPath!, updater, overwrite: true);

        var psi = new ProcessStartInfo(updater) { UseShellExecute = false, WorkingDirectory = updaterDir };
        foreach (var arg in new[]
                 {
                     "--apply-update", "--source", appDir, "--target", InstallDirectory, "--pid", Environment.ProcessId.ToString(),
                     "--from", CurrentVersion.ToString(), "--to", release.Version.ToString(),
                 })
        {
            psi.ArgumentList.Add(arg);
        }

        Process.Start(psi);
        Log.Info($"Updater started; exiting to install {release.Version}");
    }

    /// <summary>Result of the last update attempt, shown once.</summary>
    public static UpdateResult? TakeResult()
    {
        try
        {
            if (!File.Exists(ResultFile)) return null;
            var result = JsonSerializer.Deserialize<UpdateResult>(File.ReadAllText(ResultFile));
            File.Delete(ResultFile);
            return result;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public void Dispose() => _http.Dispose();
}

/// <summary>
/// The updater process: a copy of the old executable started with --apply-update. It waits for the app to exit,
/// swaps the application folder (with backup), starts the new version and waits until it reports a successful start.
/// Any failure restores the previous version and starts it again.
/// </summary>
public static class UpdaterMode
{
    public const string StartedMarkerArg = "--after-update";

    public static int Run(string[] args)
    {
        string Arg(string name) => args.SkipWhile(a => a != name).Skip(1).FirstOrDefault() ?? throw new ArgumentException($"missing {name}");
        Directory.CreateDirectory(UpdateService.Root);
        void Write(string message)
        {
            try
            {
                File.AppendAllText(UpdateService.LogFile, $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff} {message}{Environment.NewLine}");
            }
            catch (IOException)
            {
            }
        }

        string source, target, from, to;
        int pid;
        try
        {
            source = Arg("--source");
            target = Arg("--target");
            from = Arg("--from");
            to = Arg("--to");
            pid = int.Parse(Arg("--pid"));
        }
        catch (Exception ex)
        {
            Write("Invalid updater arguments: " + ex.Message);
            return 2;
        }

        var backup = Path.Combine(UpdateService.Root, "backup");
        var marker = Path.Combine(UpdateService.Root, $"started-{to}.ok");
        var exe = Path.Combine(target, UpdatePackage.ExecutableName);
        Write($"Updating {from} -> {to} in {target}");

        void Finish(bool ok, string message)
        {
            Write(message);
            try
            {
                File.WriteAllText(UpdateService.ResultFile, JsonSerializer.Serialize(new UpdateResult(ok, from, to, message, DateTimeOffset.Now)));
            }
            catch (IOException)
            {
            }
        }

        // 1. The app must be gone before any file is touched.
        try
        {
            using var app = Process.GetProcessById(pid);
            if (!app.WaitForExit(60_000))
            {
                Finish(false, "The app did not exit within 60 s; update cancelled, nothing was changed.");
                return 1;
            }
        }
        catch (ArgumentException)
        {
            // already exited
        }

        // 2. Swap the application folder (rollback inside on failure).
        try
        {
            UpdateInstaller.Apply(source, target, backup, Write);
        }
        catch (Exception ex)
        {
            Finish(false, $"Installing {to} failed ({ex.Message}); version {from} was restored.");
            TryStart(exe, null, Write);
            return 1;
        }

        // 3. Start the new version and wait until it says it started properly.
        File.Delete(marker);
        Process? started = TryStart(exe, marker, Write);
        var deadline = DateTime.UtcNow.AddSeconds(60);
        while (started is not null && DateTime.UtcNow < deadline && !File.Exists(marker) && !started.HasExited)
        {
            Thread.Sleep(250);
        }

        if (started is not null && File.Exists(marker))
        {
            Finish(true, $"Updated to {to}.");
            TryDelete(backup);
            TryDelete(Path.Combine(UpdateService.Root, "staging"));
            TryDeleteFile(marker);
            return 0;
        }

        // 4. The new version did not start: put the old one back.
        try
        {
            if (started is { HasExited: false })
            {
                started.Kill(entireProcessTree: true);
                started.WaitForExit(10_000);
            }

            UpdateInstaller.Rollback(target, backup, Write);
            Finish(false, $"Version {to} did not start correctly; version {from} was restored.");
            try
            {
                File.WriteAllText(UpdateService.SkipFile, to);
            }
            catch (IOException)
            {
            }
        }
        catch (Exception ex)
        {
            Finish(false, $"Version {to} did not start and restoring {from} failed: {ex.Message}. The previous files are in {backup}.");
            return 1;
        }

        TryStart(exe, null, Write);
        return 1;
    }

    private static Process? TryStart(string exe, string? marker, Action<string> log)
    {
        try
        {
            var psi = new ProcessStartInfo(exe) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(exe)! };
            if (marker is not null)
            {
                psi.ArgumentList.Add(StartedMarkerArg);
                psi.ArgumentList.Add(marker);
            }

            return Process.Start(psi);
        }
        catch (Exception ex)
        {
            log($"Could not start {exe}: {ex.Message}");
            return null;
        }
    }

    private static void TryDelete(string dir)
    {
        try
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static void TryDeleteFile(string file)
    {
        try
        {
            File.Delete(file);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
