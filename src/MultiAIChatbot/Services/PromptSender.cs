using System.IO;
using System.Reflection;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;

namespace MultiAIChatbot.Services;

public enum SendResult { Sent, NoChatBox, SendButtonNotFound, Error }

/// <summary>
/// Types a prompt into a site's chat box and presses Send, using small scripts run inside the page.
/// </summary>
public static class PromptSender
{
    private static readonly string InsertScript = LoadScript("insert-prompt.js");
    private static readonly string ClickScript = LoadScript("click-send.js");
    private static readonly string ReadScript = LoadScript("read-answer.js");

    private static readonly JsonSerializerOptions CamelCase = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public static async Task<SendResult> SendAsync(CoreWebView2 core, SiteSelectors site, string prompt)
    {
        try
        {
            var config = JsonSerializer.Serialize(site, CamelCase);

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
            for (var attempt = 0; attempt < 20; attempt++)
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
