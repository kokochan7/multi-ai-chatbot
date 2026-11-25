using System.IO;
using System.Reflection;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;

namespace MultiAIChatbot.Services;

public enum SendResult { Sent, NoChatBox, SendButtonNotFound, FilesNeedSignIn, FilesNotAttached, Error }

/// <summary>
/// Types a prompt into a site's chat box and presses Send, using small scripts run inside the page.
/// </summary>
public static class PromptSender
{
    private static readonly string InsertScript = LoadScript("insert-prompt.js");
    private static readonly string ClickScript = LoadScript("click-send.js");
    private static readonly string ReadScript = LoadScript("read-answer.js");
    private static readonly string AttachScript = LoadScript("attach-files.js");
    private static readonly string AttachCheckScript = LoadScript("attach-check.js");
    private static readonly string UploadFolder = Path.Combine(Path.GetTempPath(), "MultiAIChatbot", "Uploads");

    private static readonly JsonSerializerOptions CamelCase = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public static async Task<SendResult> SendAsync(CoreWebView2 core, SiteSelectors site, string prompt,
        IReadOnlyList<Attachment>? files = null)
    {
        try
        {
            var config = JsonSerializer.Serialize(site, CamelCase);
            var hasFiles = files is { Count: > 0 };

            if (hasFiles)
            {
                if (site.TrustedFileDrop)
                {
                    var outcome = await AttachWithRealDropAsync(core, site, config, files!);
                    if (outcome is SendResult.FilesNeedSignIn or SendResult.FilesNotAttached) return outcome;
                }
                else
                {
                    await RunAsync(core, AttachScript.Replace("__CONFIG__", config).Replace("__FILES__", FilesJson(files!)));
                }
                // Give the site a moment to start the upload before typing.
                await Task.Delay(800);
            }

            var insertJson = await RunAsync(core, InsertScript
                .Replace("__CONFIG__", config)
                .Replace("__TEXT__", JsonSerializer.Serialize(prompt)));
            using (var doc = JsonDocument.Parse(insertJson))
            {
                if (!doc.RootElement.GetProperty("ok").GetBoolean())
                    return SendResult.NoChatBox;
            }

            // The Send button usually turns on a moment after text appears.
            var click = ClickScript.Replace("__CONFIG__", config);
            // With files attached, Send stays off until the upload finishes, so wait longer.
            var attempts = hasFiles ? 200 : 20;
            for (var attempt = 0; attempt < attempts; attempt++)
            {
                await Task.Delay(150);
                var result = await RunAsync(core, click.Replace("__USE_ENTER__", "false"));
                if (result == "clicked") return SendResult.Sent;
            }

            // Last resort: press Enter in the chat box.
            await RunAsync(core, click.Replace("__USE_ENTER__", "true"));
            return SendResult.SendButtonNotFound;
        }
        catch
        {
            return SendResult.Error;
        }
    }

    private static string FilesJson(IReadOnlyList<Attachment> files) =>
        JsonSerializer.Serialize(files.Select(f => new { name = f.Name, type = f.MimeType, data = f.Base64 }));

    private sealed record AttachCheck(int X, int Y, int Attached, bool SignedOut);

    private static async Task<AttachCheck> CheckAttachAsync(CoreWebView2 core, string config)
    {
        var json = await RunAsync(core, AttachCheckScript.Replace("__CONFIG__", config));
        using var doc = JsonDocument.Parse(json);
        var r = doc.RootElement;
        return new AttachCheck(r.GetProperty("x").GetInt32(), r.GetProperty("y").GetInt32(),
            r.GetProperty("attached").GetInt32(), r.GetProperty("signedOut").GetBoolean());
    }

    /// <summary>
    /// Attaches files in ways sites can't tell from a person's own actions, using the browser's
    /// DevTools input (script-made paste events are ignored by Gemini):
    /// 1. a real drop on the chat box, 2. filling the page's file picker, 3. the script way.
    /// After each it waits for a preview of the file. If none shows up it returns FilesNeedSignIn
    /// (the site shows a "Sign in" button) or FilesNotAttached (pictures that never appeared),
    /// so the question isn't sent without the file.
    /// </summary>
    private static async Task<SendResult> AttachWithRealDropAsync(CoreWebView2 core, SiteSelectors site, string config,
        IReadOnlyList<Attachment> files)
    {
        var before = await CheckAttachAsync(core, config);

        async Task<bool> PreviewAppearedAsync(int tries)
        {
            for (var i = 0; i < tries; i++)
            {
                await Task.Delay(200);
                try
                {
                    if ((await CheckAttachAsync(core, config)).Attached > before.Attached) return true;
                }
                catch { /* the page may be busy */ }
            }
            return false;
        }

        List<string> paths;
        try { paths = WriteUploadFiles(files); }
        catch { paths = new List<string>(); }

        // 1. A real drop on the chat box.
        if (before.X >= 0 && paths.Count > 0)
        {
            try
            {
                foreach (var type in new[] { "dragEnter", "dragOver", "drop" })
                {
                    var args = JsonSerializer.Serialize(new
                    {
                        type,
                        x = before.X,
                        y = before.Y,
                        data = new { items = Array.Empty<object>(), files = paths, dragOperationsMask = 1 },
                    });
                    await core.CallDevToolsProtocolMethodAsync("Input.dispatchDragEvent", args);
                    await Task.Delay(type == "drop" ? 0 : 150);   // let the page show its drop zone
                }
                if (await PreviewAppearedAsync(25)) return SendResult.Sent;
            }
            catch { /* older WebView2 runtimes may not support it */ }
        }

        // 2. Fill the page's own file picker, as if the user had chosen the files in it.
        if (paths.Count > 0 && await SetFileInputAsync(core, paths, files) && await PreviewAppearedAsync(20))
            return SendResult.Sent;

        if (before.SignedOut) return SendResult.FilesNeedSignIn;

        // 3. The script way.
        try
        {
            await RunAsync(core, AttachScript.Replace("__CONFIG__", config).Replace("__FILES__", FilesJson(files)));
            if (await PreviewAppearedAsync(15)) return SendResult.Sent;
        }
        catch { }

        // Pictures always show a preview, so none means it didn't work. Other files may
        // look different, so send anyway.
        return files.All(f => f.IsImage) ? SendResult.FilesNotAttached : SendResult.Sent;
    }

    private const string MarkFileInputScript = @"(() => {
  const files = __FILES__;
  const ok = (input) => {
    const accept = (input.accept || '').trim().toLowerCase();
    if (!accept) return true;
    const list = accept.split(',').map(a => a.trim());
    return files.every(f => list.some(a => a === f.type || (a.endsWith('/*') && f.type.startsWith(a.slice(0, -1))) ||
      (a.startsWith('.') && f.name.toLowerCase().endsWith(a))));
  };
  document.querySelectorAll('input[data-mac-upload]').forEach(i => i.removeAttribute('data-mac-upload'));
  const input = Array.from(document.querySelectorAll('input[type=""file""]'))
    .find(i => !i.disabled && (i.multiple || files.length === 1) && ok(i));
  if (!input) return 'none';
  input.setAttribute('data-mac-upload', '1');
  return 'ok';
})();";

    private static async Task<bool> SetFileInputAsync(CoreWebView2 core, List<string> paths, IReadOnlyList<Attachment> files)
    {
        try
        {
            var meta = JsonSerializer.Serialize(files.Select(f => new { name = f.Name, type = f.MimeType.ToLowerInvariant() }));
            if (await RunAsync(core, MarkFileInputScript.Replace("__FILES__", meta)) != "ok") return false;

            using var docJson = JsonDocument.Parse(await core.CallDevToolsProtocolMethodAsync("DOM.getDocument", "{\"depth\":0}"));
            var rootId = docJson.RootElement.GetProperty("root").GetProperty("nodeId").GetInt32();
            using var found = JsonDocument.Parse(await core.CallDevToolsProtocolMethodAsync("DOM.querySelector",
                JsonSerializer.Serialize(new { nodeId = rootId, selector = "input[data-mac-upload]" })));
            var nodeId = found.RootElement.GetProperty("nodeId").GetInt32();
            if (nodeId == 0) return false;
            await core.CallDevToolsProtocolMethodAsync("DOM.setFileInputFiles",
                JsonSerializer.Serialize(new { files = paths, nodeId }));
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Writes the files to a temporary folder so the browser can read them like a real drop.</summary>
    private static List<string> WriteUploadFiles(IReadOnlyList<Attachment> files)
    {
        // Remove folders from earlier sends; the sites have long finished reading them.
        try
        {
            if (Directory.Exists(UploadFolder))
                foreach (var old in Directory.GetDirectories(UploadFolder))
                    if (Directory.GetCreationTimeUtc(old) < DateTime.UtcNow.AddHours(-1))
                        Directory.Delete(old, recursive: true);
        }
        catch { /* leftovers are harmless */ }

        var dir = Path.Combine(UploadFolder, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var paths = new List<string>();
        foreach (var file in files)
        {
            var name = file.Name;
            foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
            var path = Path.Combine(dir, name);
            for (var n = 2; File.Exists(path); n++)
                path = Path.Combine(dir, $"{Path.GetFileNameWithoutExtension(name)} ({n}){Path.GetExtension(name)}");
            File.WriteAllBytes(path, Convert.FromBase64String(file.Base64));
            paths.Add(path);
        }
        return paths;
    }

    /// <summary>How many answers are on the page right now (call before sending).</summary>
    public static async Task<int> CountAnswersAsync(CoreWebView2 core, SiteSelectors site)
    {
        try
        {
            return (await ReadAsync(core, site)).Count;
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>
    /// Waits for a new answer to appear and finish (its text stops changing),
    /// then returns its text. Returns "" if nothing arrives within the time limit.
    /// <paramref name="onProgress"/> gets the answer so far each time it grows, so it can be shown live.
    /// </summary>
    public static async Task<string> WaitForAnswerAsync(CoreWebView2 core, SiteSelectors site, int countBefore,
        Action<string>? onProgress = null, TimeSpan? timeout = null)
    {
        var started = DateTime.UtcNow;
        var logged = false;
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromMinutes(4));
        var lastText = "";
        var lastInfo = "";
        var stableSince = DateTime.UtcNow;

        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(1000);
            AnswerSnapshot snap;
            try
            {
                snap = await ReadAsync(core, site);
            }
            catch
            {
                continue;   // The page may be navigating.
            }

            lastInfo = snap.Info;
            if (snap.Count <= countBefore || snap.Text.Length == 0)
            {
                // Nothing to read after a minute: note what the page looks like (once) while still waiting.
                if (!logged && lastText.Length == 0 && DateTime.UtcNow - started > TimeSpan.FromMinutes(1))
                {
                    logged = true;
                    LogMissedAnswer(countBefore, lastInfo);
                }
                continue;
            }

            if (snap.Text != lastText)
            {
                lastText = snap.Text;
                stableSince = DateTime.UtcNow;
                try { onProgress?.Invoke(lastText); } catch { /* showing progress must not stop the wait */ }
            }
            else if (DateTime.UtcNow - stableSince > TimeSpan.FromSeconds(snap.Streaming ? 20 : 3))
            {
                // Done: the text stopped changing. If the page still looks busy (a leftover
                // Stop button), a longer pause counts as finished too.
                return lastText;
            }
        }
        if (lastText.Length == 0 && !logged) LogMissedAnswer(countBefore, lastInfo);
        return lastText;
    }

    /// <summary>
    /// Notes what the page looked like when no answer could be read, in capture-log.txt next to
    /// the history, so a site's new layout can be fixed from it. Keeps only the newest entries.
    /// </summary>
    private static void LogMissedAnswer(int countBefore, string info)
    {
        try
        {
            var file = Path.Combine(AppState.Folder, "capture-log.txt");
            var lines = File.Exists(file) ? File.ReadAllLines(file).TakeLast(40).ToList() : new List<string>();
            lines.Add($"{DateTime.Now:yyyy-MM-dd HH:mm:ss} no answer read (answers before: {countBefore}) {info}");
            Directory.CreateDirectory(AppState.Folder);
            File.WriteAllLines(file, lines);
        }
        catch
        {
            // Logging must never get in the way of the chat.
        }
    }

    private sealed record AnswerSnapshot(int Count, string Text, bool Streaming, string Info);

    /// <summary>
    /// Reads one answer already on the page (for History's Refresh). <paramref name="index"/> counts
    /// questions in the chat from 0; it's only trusted when the page has one answer per question.
    /// Returns "" when the page doesn't match the chat.
    /// </summary>
    public static async Task<string> ReadAnswerAtAsync(CoreWebView2 core, SiteSelectors site, int index, int questions)
    {
        var all = await ReadAsync(core, site);
        if (all.Count == 0) return "";
        if (all.Count == questions) return (await ReadAsync(core, site, index)).Text;
        // Counts differ (extra steps on the page): only the newest question can be matched safely.
        return index == questions - 1 ? all.Text : "";
    }

    private static async Task<AnswerSnapshot> ReadAsync(CoreWebView2 core, SiteSelectors site, int index = -1)
    {
        var json = await RunAsync(core, ReadScript
            .Replace("__CONFIG__", JsonSerializer.Serialize(site, CamelCase))
            .Replace("__INDEX__", index.ToString()));
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        return new AnswerSnapshot(
            root.GetProperty("count").GetInt32(),
            root.GetProperty("text").GetString() ?? "",
            root.GetProperty("streaming").GetBoolean(),
            root.TryGetProperty("info", out var info) ? info.GetString() ?? "" : "");
    }

    /// <summary>Runs a script and returns its result as a plain string.</summary>
    private static async Task<string> RunAsync(CoreWebView2 core, string script)
    {
        var raw = await core.ExecuteScriptAsync(script);     // JSON-encoded result
        return JsonSerializer.Deserialize<string>(raw) ?? "";
    }

    private static string LoadScript(string fileName)
    {
        var asm = Assembly.GetExecutingAssembly();
        var name = asm.GetManifestResourceNames().First(n => n.EndsWith(fileName, StringComparison.OrdinalIgnoreCase));
        using var stream = asm.GetManifestResourceStream(name)!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
