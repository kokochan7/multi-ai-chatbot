using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;

namespace MultiAIChatbot.Services;

/// <summary>A newer build published on GitHub Releases.</summary>
public sealed record UpdateInfo(Version Version, string DownloadUrl, string PageUrl);

/// <summary>
/// Checks GitHub Releases for a newer build, downloads it, swaps it in for the
/// running .exe and restarts. Every push to main publishes a release (see
/// .github/workflows/build.yml), so the app always finds the newest build.
/// </summary>
public static class Updater
{
    private const string LatestReleaseApi =
        "https://api.github.com/repos/kokochan7/multi-ai-chatbot/releases/latest";
    private const string AssetName = "MultiAIChatbot.exe";

    /// <summary>Command-line flag the new copy gets, so it waits for the old copy to close.</summary>
    public const string WaitForPidArg = "--after-update";

    private static readonly HttpClient Http = CreateClient();

    public static Version CurrentVersion { get; } =
        Normalize(Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0));

    public static string CurrentVersionText => $"{CurrentVersion.Major}.{CurrentVersion.Minor}.{CurrentVersion.Build}";

    public static string ExePath => Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, AssetName);

    private static string OldExePath => ExePath + ".old";

    /// <summary>Returns the newer release, or null when this copy is up to date.</summary>
    public static async Task<UpdateInfo?> CheckAsync(CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, LatestReleaseApi);
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        using var response = await Http.SendAsync(request, ct);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return null;   // No releases yet.
        response.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        var root = doc.RootElement;

        var tag = root.GetProperty("tag_name").GetString() ?? "";
        if (!Version.TryParse(tag.TrimStart('v', 'V'), out var latest)) return null;
        latest = Normalize(latest);
        if (latest <= CurrentVersion) return null;

        string? url = null;
        foreach (var asset in root.GetProperty("assets").EnumerateArray())
        {
            if (string.Equals(asset.GetProperty("name").GetString(), AssetName, StringComparison.OrdinalIgnoreCase))
            {
                url = asset.GetProperty("browser_download_url").GetString();
                break;
            }
        }
        if (url is null) return null;

        var page = root.TryGetProperty("html_url", out var p) ? p.GetString() ?? "" : "";
        return new UpdateInfo(latest, url, page);
    }

    /// <summary>
    /// Downloads the update next to the running .exe, swaps the files, and starts
    /// the new copy. The caller should shut the app down right after this returns.
    /// </summary>
    public static async Task DownloadAndInstallAsync(UpdateInfo update, IProgress<double>? progress = null,
        CancellationToken ct = default)
    {
        var exe = ExePath;
        var newExe = exe + ".new";

        using (var response = await Http.GetAsync(update.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct))
        {
            response.EnsureSuccessStatusCode();
            var total = response.Content.Headers.ContentLength;
            await using var source = await response.Content.ReadAsStreamAsync(ct);
            await using var target = File.Create(newExe);
            var buffer = new byte[81920];
            long done = 0;
            int read;
            while ((read = await source.ReadAsync(buffer, ct)) > 0)
            {
                await target.WriteAsync(buffer.AsMemory(0, read), ct);
                done += read;
                if (total > 0) progress?.Report((double)done / total.Value);
            }
        }

        // Windows lets a running .exe be renamed (but not overwritten), so move
        // the current one aside and put the new one in its place.
        TryDelete(OldExePath);
        File.Move(exe, OldExePath);
        try
        {
            File.Move(newExe, exe);
        }
        catch
        {
            File.Move(OldExePath, exe);   // Put the old copy back so the app still starts.
            throw;
        }

        Process.Start(new ProcessStartInfo(exe, $"{WaitForPidArg} {Environment.ProcessId}")
        {
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(exe) ?? "",
        });
    }

    /// <summary>
    /// Run at startup. After an update, waits for the old copy to close (it holds
    /// the saved logins open) and removes the old .exe.
    /// </summary>
    public static void FinishPendingUpdate(string[] args)
    {
        var i = Array.IndexOf(args, WaitForPidArg);
        if (i >= 0 && i + 1 < args.Length && int.TryParse(args[i + 1], out var pid))
        {
            try
            {
                using var old = Process.GetProcessById(pid);
                old.WaitForExit(20000);
            }
            catch
            {
                // Already closed.
            }
        }

        for (var attempt = 0; attempt < 10 && File.Exists(OldExePath); attempt++)
        {
            if (TryDelete(OldExePath)) break;
            Thread.Sleep(300);
        }
    }

    private static bool TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static Version Normalize(Version v) => new(v.Major, v.Minor, Math.Max(v.Build, 0));

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("MultiAIChatbot-Updater");
        return client;
    }
}
