using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace MultiAIChatbot.Services;

/// <summary>
/// Finds a picture for an item on the Compare page (a product, place, tool…).
/// Looks the name up on Bing image search and takes the first result's thumbnail; if that
/// finds nothing, tries the matching Wikipedia article's picture. Pictures are kept on this PC
/// so each name is only looked up once. Returns null when nothing is found, and the page
/// shows a placeholder instead.
/// </summary>
public static class ProductImages
{
    private static readonly string CacheFolder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MultiAIChatbot", "image-cache2");
    // The first version picked up Bing's page decorations instead of results; its pictures are dropped.
    private static readonly string OldCacheFolder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MultiAIChatbot", "image-cache");

    private static readonly HttpClient Http = CreateClient();
    private static readonly SemaphoreSlim Gate = new(4);
    private static readonly ConcurrentDictionary<string, Task<ImageSource?>> Memory = new(StringComparer.OrdinalIgnoreCase);

    // Each image result is an <a class="iusc" m="{json}"> link; the JSON holds its thumbnail (turl).
    private static readonly Regex BingResult = new("<a\\b[^>]*\\bclass=\"[^\"]*\\biusc\\b[^\"]*\"[^>]*>", RegexOptions.Compiled);
    private static readonly Regex MAttribute = new("\\sm=\"([^\"]+)\"", RegexOptions.Compiled);

    private static HttpClient CreateClient()
    {
        var client = new HttpClient(new HttpClientHandler { AutomaticDecompression = DecompressionMethods.All })
        {
            Timeout = TimeSpan.FromSeconds(12),
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0 Safari/537.36");
        client.DefaultRequestHeaders.AcceptLanguage.ParseAdd("en-US,en;q=0.8");
        return client;
    }

    /// <summary>The picture for <paramref name="name"/>, or null if none was found.</summary>
    public static Task<ImageSource?> GetAsync(string name)
    {
        var key = name.Trim();
        if (key.Length == 0) return Task.FromResult<ImageSource?>(null);
        return Memory.GetOrAdd(key, k => Task.Run(() => LoadAsync(k)));
    }

    /// <summary>
    /// Deletes the saved pictures for the items the AIs agreed on in these questions,
    /// so deleting a chat also removes the pictures it brought in.
    /// </summary>
    public static void Forget(IEnumerable<HistoryEntry> entries)
    {
        foreach (var entry in entries.ToList())
        {
            List<AgreedItem> items;
            try
            {
                var answers = AppState.Settings.ColumnOrder
                    .Select(k => (Site: k, Answer: entry.AnswerFor(k)))
                    .Where(a => !string.IsNullOrWhiteSpace(a.Answer))
                    .ToList();
                if (answers.Count < 2) continue;
                items = AnswerComparer.Compare(answers);
            }
            catch { continue; }

            foreach (var item in items.Where(i => i.Count >= 2))
            {
                var key = item.Name.Trim();
                Memory.TryRemove(key, out _);
                var file = Path.Combine(CacheFolder, Hash(key));
                TryDelete(file);
                TryDelete(file + ".none");
            }
        }
    }

    /// <summary>Deletes every saved picture (used when all history is cleared).</summary>
    public static void ForgetAll()
    {
        Memory.Clear();
        try { if (Directory.Exists(CacheFolder)) Directory.Delete(CacheFolder, recursive: true); } catch { }
        try { if (Directory.Exists(OldCacheFolder)) Directory.Delete(OldCacheFolder, recursive: true); }
        catch { /* a picture still being written; it's harmless */ }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    private static async Task<ImageSource?> LoadAsync(string name)
    {
        try
        {
            Directory.CreateDirectory(CacheFolder);
            if (Directory.Exists(OldCacheFolder))
                try { Directory.Delete(OldCacheFolder, recursive: true); } catch { }
            var file = Path.Combine(CacheFolder, Hash(name));
            var none = file + ".none";

            if (File.Exists(file)) return Decode(await File.ReadAllBytesAsync(file));
            // A name that found nothing is tried again after a week.
            if (File.Exists(none) && File.GetLastWriteTimeUtc(none) > DateTime.UtcNow.AddDays(-7)) return null;

            await Gate.WaitAsync();
            try
            {
                foreach (var find in new Func<string, Task<string?>>[] { FromBingAsync, FromWikipediaAsync })
                {
                    string? url;
                    try { url = await find(name); } catch { continue; }
                    if (url == null) continue;

                    byte[] bytes;
                    try { bytes = await Http.GetByteArrayAsync(url); } catch { continue; }
                    var image = Decode(bytes);
                    if (image == null) continue;

                    await File.WriteAllBytesAsync(file, bytes);
                    return image;
                }
            }
            finally { Gate.Release(); }

            await File.WriteAllTextAsync(none, "");
            return null;
        }
        catch
        {
            return null;
        }
    }

    private static async Task<string?> FromBingAsync(string name)
    {
        var url = "https://www.bing.com/images/async?q=" + Uri.EscapeDataString(name) +
                  "&first=0&count=5&mmasync=1&adlt=strict";
        var html = await Http.GetStringAsync(url);
        string? thumb = null;
        foreach (Match link in BingResult.Matches(html))
        {
            var m = MAttribute.Match(link.Value);
            if (!m.Success) continue;
            try
            {
                using var json = JsonDocument.Parse(WebUtility.HtmlDecode(m.Groups[1].Value));
                if (json.RootElement.TryGetProperty("turl", out var t) && t.GetString() is { Length: > 0 } u &&
                    u.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                {
                    thumb = u;
                    break;
                }
            }
            catch (JsonException) { }
        }
        if (thumb == null) return null;
        // Ask Bing for a square, centred crop at a size that stays sharp on high-DPI screens.
        return thumb + (thumb.Contains('?') ? "&" : "?") + "w=240&h=240&c=7";
    }

    private static async Task<string?> FromWikipediaAsync(string name)
    {
        var url = "https://en.wikipedia.org/w/api.php?action=query&format=json&generator=search&gsrlimit=1" +
                  "&prop=pageimages&piprop=thumbnail&pithumbsize=240&gsrsearch=" + Uri.EscapeDataString(name);
        using var doc = JsonDocument.Parse(await Http.GetStringAsync(url));
        if (!doc.RootElement.TryGetProperty("query", out var query) ||
            !query.TryGetProperty("pages", out var pages)) return null;
        foreach (var page in pages.EnumerateObject())
            if (page.Value.TryGetProperty("thumbnail", out var thumb) &&
                thumb.TryGetProperty("source", out var source))
                return source.GetString();
        return null;
    }

    private static ImageSource? Decode(byte[] bytes)
    {
        if (bytes.Length < 200) return null;
        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.DecodePixelWidth = 240;
            image.StreamSource = new MemoryStream(bytes);
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch
        {
            return null; // a format WPF can't show (for example WebP without the codec)
        }
    }

    private static string Hash(string name) =>
        Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(name.ToLowerInvariant()))).ToLowerInvariant() + ".img";
}
