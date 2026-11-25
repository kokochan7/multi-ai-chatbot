using System.IO;
using System.Reflection;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;

namespace MultiAIChatbot.Services;

public enum SendResult { Sent, NoChatBox, SendButtonNotFound, FilesNeedSignIn, Error }

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
                    if (outcome == SendResult.FilesNeedSignIn) return outcome;
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
    /// Drops the files on the chat box the way a real mouse drop from File Explorer would,
    /// using the browser's own DevTools input. Sites can't tell this from a person's drop,
    /// unlike script-made paste events, which Gemini ignores.
    /// Returns FilesNeedSignIn when nothing got attached and the site shows a "Sign in" button.
    /// </summary>
    private static async Task<SendResult> AttachWithRealDropAsync(CoreWebView2 core, SiteSelectors site, string config,
        IReadOnlyList<Attachment> files)
    {
        var before = await CheckAttachAsync(core, config);
        var dropped = false;
        if (before.X >= 0)
        {
            try
            {
                var paths = WriteUploadFiles(files);
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
                dropped = true;
            }
            catch
            {
                // Older WebView2 runtimes may not support it; the script way below still runs.
            }
        }

        // Wait for a preview of the file to show up in the chat box.
        for (var i = 0; i < 25; i++)
        {
            await Task.Delay(200);
            if ((await CheckAttachAsync(core, config)).Attached > before.Attached) return SendResult.Sent;
        }

        if (before.SignedOut) return SendResult.FilesNeedSignIn;

        // Not sure it worked (the preview may look different): try the script way as well.
        if (!dropped)
            await RunAsync(core, AttachScript.Replace("__CONFIG__", config).Replace("__FILES__", FilesJson(files)));
        return SendResult.Sent;
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
    /// </summary>
    public static async Task<string> WaitForAnswerAsync(CoreWebView2 core, SiteSelectors site, int countBefore,
        TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromMinutes(4));
        var lastText = "";
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

            if (snap.Count <= countBefore || snap.Text.Length == 0) continue;

            if (snap.Text != lastText)
            {
                lastText = snap.Text;
                stableSince = DateTime.UtcNow;
            }
            else if (!snap.Streaming && DateTime.UtcNow - stableSince > TimeSpan.FromSeconds(3))
            {
                return lastText;
            }
        }
        return lastText;
    }

    private sealed record AnswerSnapshot(int Count, string Text, bool Streaming);

    private static async Task<AnswerSnapshot> ReadAsync(CoreWebView2 core, SiteSelectors site)
    {
        var json = await RunAsync(core, ReadScript.Replace("__CONFIG__", JsonSerializer.Serialize(site, CamelCase)));
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        return new AnswerSnapshot(
            root.GetProperty("count").GetInt32(),
            root.GetProperty("text").GetString() ?? "",
            root.GetProperty("streaming").GetBoolean());
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
