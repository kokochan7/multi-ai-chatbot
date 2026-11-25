using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media.Imaging;
using IDataObject = System.Windows.IDataObject;
using IComDataObject = System.Runtime.InteropServices.ComTypes.IDataObject;

namespace MultiAIChatbot.Services;

/// <summary>
/// Turns whatever was dropped or pasted on the chat box into real files. Windows apps hand over
/// pictures in many shapes: a file path (Explorer), a "virtual file" with its bytes (web browsers,
/// Outlook, the Photos app), raw image data (screenshots, "Copy image"), HTML with an &lt;img&gt;,
/// or just the picture's web address. Without this, the text box only sees the address and types it.
/// </summary>
public static class DroppedFiles
{
    /// <summary>What was found. Paths are ready now; Urls still need downloading.</summary>
    public sealed record Found(List<string> Paths, List<string> Urls, string? Text);

    private const string FileGroupDescriptorW = "FileGroupDescriptorW";
    private const string FileContents = "FileContents";
    private const string HtmlFormat = "HTML Format";
    private const int FileDescriptorSize = 592;   // sizeof(FILEDESCRIPTORW)
    private const int FileNameOffset = 72;        // offset of cFileName inside it

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };
    private static readonly string TempRoot = Path.Combine(Path.GetTempPath(), "MultiAIChatbot", "Dropped");
    private static readonly Regex ImgSrc = new("<img[^>]+?src\\s*=\\s*[\"']([^\"']+)[\"']",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>True when the drag carries something that should become an attachment rather than typed text.</summary>
    public static bool HasFileLikeData(IDataObject data, bool fromPaste)
    {
        try
        {
            if (data.GetDataPresent(DataFormats.FileDrop) || data.GetDataPresent(FileGroupDescriptorW)) return true;

            // A copied picture often comes with some text too (Excel cells, Word). Pasting text wins then.
            if (fromPaste && data.GetDataPresent(DataFormats.UnicodeText)) return false;

            if (data.GetDataPresent("PNG") || data.GetDataPresent(DataFormats.Bitmap) || data.GetDataPresent(DataFormats.Dib))
                return true;
            if (data.GetData(HtmlFormat) is string html && ImgSrc.IsMatch(html)) return true;
            if (!fromPaste && LooksLikeFileText(TextOf(data))) return true;
        }
        catch { /* some drag sources fail when asked for a format; treat as plain text */ }
        return false;
    }

    /// <summary>
    /// Reads the drag data now (it's only valid during the drop). Files with bytes are written to a
    /// temporary folder; picture web addresses are returned in Urls for <see cref="DownloadAsync"/>.
    /// </summary>
    public static Found Extract(IDataObject data, bool fromPaste)
    {
        var paths = new List<string>();
        var urls = new List<string>();
        string? text = null;

        // 1. Real files from Explorer or the desktop.
        if (data.GetData(DataFormats.FileDrop) is string[] dropped && dropped.Length > 0)
            return new Found(dropped.ToList(), urls, null);

        // 2. Virtual files (web browsers, Outlook, Photos): the bytes come with the drag.
        TryAdd(() => paths.AddRange(ReadVirtualFiles(data)));
        if (paths.Count > 0) return new Found(paths, urls, null);

        // 3. Raw picture data (screenshots, "Copy image").
        TryAdd(() => { var p = ReadBitmap(data, fromPaste); if (p != null) paths.Add(p); });
        if (paths.Count > 0) return new Found(paths, urls, null);

        // 4. HTML with a picture in it.
        TryAdd(() =>
        {
            if (data.GetData(HtmlFormat) is not string html) return;
            foreach (Match m in ImgSrc.Matches(html))
            {
                var src = System.Net.WebUtility.HtmlDecode(m.Groups[1].Value);
                if (src.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                {
                    var p = SaveDataUrl(src);
                    if (p != null) paths.Add(p);
                }
                else if (Uri.TryCreate(src, UriKind.Absolute, out var u) && (u.Scheme == "http" || u.Scheme == "https"))
                    urls.Add(u.AbsoluteUri);
            }
        });
        if (paths.Count > 0 || urls.Count > 0) return new Found(paths, urls, null);

        // 5. Text that is a picture: a data: URL, a local file, or a web address.
        if (!fromPaste)
        {
            text = TextOf(data);
            var t = text?.Trim();
            if (!string.IsNullOrEmpty(t) && LooksLikeFileText(t))
            {
                if (t.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                {
                    var p = SaveDataUrl(t);
                    if (p != null) paths.Add(p);
                }
                else if (Uri.TryCreate(t, UriKind.Absolute, out var u) && u.IsFile && File.Exists(u.LocalPath))
                    paths.Add(u.LocalPath);
                else if (File.Exists(t))
                    paths.Add(t);
                else if (Uri.TryCreate(t, UriKind.Absolute, out var w) && (w.Scheme == "http" || w.Scheme == "https"))
                    urls.Add(w.AbsoluteUri);
            }
        }
        return new Found(paths, urls, text);
    }

    /// <summary>
    /// Downloads a picture (or PDF) from the web into a temporary file. Returns null when the
    /// address isn't a file, such as a normal web page.
    /// </summary>
    public static async Task<string?> DownloadAsync(string url)
    {
        try
        {
            using var response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
            if (!response.IsSuccessStatusCode) return null;
            var mime = response.Content.Headers.ContentType?.MediaType ?? "";
            if (!mime.StartsWith("image/", StringComparison.OrdinalIgnoreCase) && mime != "application/pdf") return null;
            if (response.Content.Headers.ContentLength > Attachment.MaxTotalBytes) return null;

            var bytes = await response.Content.ReadAsByteArrayAsync();
            var name = Path.GetFileName(new Uri(url).LocalPath);
            if (string.IsNullOrWhiteSpace(name) || !Path.HasExtension(name)) name = "image" + ExtensionFor(mime);
            return Save(name, bytes);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Deletes the temporary copies once they've been read into attachments. Never touches the user's own files.</summary>
    public static void Cleanup(IEnumerable<string> paths)
    {
        foreach (var path in paths)
        {
            try
            {
                var dir = Path.GetDirectoryName(Path.GetFullPath(path));
                if (dir != null && dir.StartsWith(TempRoot, StringComparison.OrdinalIgnoreCase) && Directory.Exists(dir))
                    Directory.Delete(dir, recursive: true);
            }
            catch { /* leftover temp files are harmless */ }
        }
    }

    // ---- Readers ----

    private static void TryAdd(Action read)
    {
        try { read(); } catch { /* fall through to the next format */ }
    }

    private static string? TextOf(IDataObject data)
    {
        foreach (var format in new[] { "UniformResourceLocatorW", DataFormats.UnicodeText, DataFormats.Text })
        {
            try
            {
                if (data.GetData(format) is string s && s.Length > 0) return s.TrimEnd('\0');
                if (data.GetData(format) is MemoryStream ms)
                    return (format.EndsWith('W') ? Encoding.Unicode : Encoding.Default).GetString(ms.ToArray()).TrimEnd('\0');
            }
            catch { }
        }
        return null;
    }

    private static bool LooksLikeFileText(string? text)
    {
        var t = text?.Trim();
        if (string.IsNullOrEmpty(t) || t.Contains('\n')) return false;
        if (t.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase)) return true;
        if (!Uri.TryCreate(t, UriKind.Absolute, out var u)) return false;
        if (u.IsFile) return File.Exists(u.LocalPath);
        return u.Scheme is "http" or "https";
    }

    private static IEnumerable<string> ReadVirtualFiles(IDataObject data)
    {
        if (data.GetData(FileGroupDescriptorW) is not MemoryStream descriptor) yield break;
        var bytes = descriptor.ToArray();
        if (bytes.Length < 4) yield break;
        var count = BitConverter.ToInt32(bytes, 0);

        for (var i = 0; i < count; i++)
        {
            var start = 4 + i * FileDescriptorSize;
            if (start + FileDescriptorSize > bytes.Length) yield break;
            var flags = BitConverter.ToUInt32(bytes, start);
            var attributes = BitConverter.ToUInt32(bytes, start + 36);
            if ((flags & 0x4) != 0 && (attributes & 0x10) != 0) continue;   // a folder

            var name = Encoding.Unicode.GetString(bytes, start + FileNameOffset, 520);
            var end = name.IndexOf('\0');
            name = Path.GetFileName(end >= 0 ? name[..end] : name);
            if (string.IsNullOrWhiteSpace(name)) continue;

            // Dragging a link makes a .url shortcut; that's a link, not a file.
            var ext = Path.GetExtension(name).ToLowerInvariant();
            if (ext is ".url" or ".lnk" or ".website") continue;

            var content = ReadFileContents(data, i);
            if (content == null || content.Length == 0) continue;
            yield return Save(name, content);
        }
    }

    private static byte[]? ReadFileContents(IDataObject data, int index)
    {
        if (data is IComDataObject com)
        {
            var format = new FORMATETC
            {
                cfFormat = (short)DataFormats.GetDataFormat(FileContents).Id,
                dwAspect = DVASPECT.DVASPECT_CONTENT,
                lindex = index,
                ptd = IntPtr.Zero,
                tymed = TYMED.TYMED_ISTREAM | TYMED.TYMED_HGLOBAL,
            };
            try
            {
                com.GetData(ref format, out var medium);
                try { return ReadMedium(medium); }
                finally { ReleaseStgMedium(ref medium); }
            }
            catch when (index > 0) { return null; }
            catch { /* try the plain WPF way below */ }
        }

        // WPF can only fetch the first file this way.
        return index == 0 && data.GetData(FileContents) is MemoryStream ms ? ms.ToArray() : null;
    }

    private static byte[]? ReadMedium(STGMEDIUM medium)
    {
        if (medium.tymed == TYMED.TYMED_ISTREAM && medium.unionmember != IntPtr.Zero)
        {
            var stream = (IStream)Marshal.GetObjectForIUnknown(medium.unionmember);
            var read = Marshal.AllocCoTaskMem(sizeof(int));
            try
            {
                using var output = new MemoryStream();
                var buffer = new byte[81920];
                while (true)
                {
                    stream.Read(buffer, buffer.Length, read);
                    var n = Marshal.ReadInt32(read);
                    if (n <= 0) break;
                    output.Write(buffer, 0, n);
                    if (output.Length > Attachment.MaxTotalBytes) break;   // AddFiles reports it as too big
                }
                return output.ToArray();
            }
            finally
            {
                Marshal.FreeCoTaskMem(read);
                Marshal.ReleaseComObject(stream);
            }
        }

        if (medium.tymed == TYMED.TYMED_HGLOBAL && medium.unionmember != IntPtr.Zero)
        {
            var size = (int)Math.Min((long)(ulong)GlobalSize(medium.unionmember), Attachment.MaxTotalBytes + 1);
            var ptr = GlobalLock(medium.unionmember);
            if (ptr == IntPtr.Zero) return null;
            try
            {
                var bytes = new byte[size];
                Marshal.Copy(ptr, bytes, 0, size);
                return bytes;
            }
            finally { GlobalUnlock(medium.unionmember); }
        }
        return null;
    }

    private static string? ReadBitmap(IDataObject data, bool fromPaste)
    {
        // Prefer the PNG copy: it keeps transparency.
        if (data.GetData("PNG") is MemoryStream png && png.Length > 0) return Save("image.png", png.ToArray());

        BitmapSource? image = null;
        try { image = data.GetData(DataFormats.Bitmap) as BitmapSource; } catch { }
        if (image == null && fromPaste && Clipboard.ContainsImage()) image = Clipboard.GetImage();
        if (image == null) return null;

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        using var ms = new MemoryStream();
        encoder.Save(ms);
        return Save("image.png", ms.ToArray());
    }

    private static string? SaveDataUrl(string url)
    {
        var comma = url.IndexOf(',');
        if (comma < 0) return null;
        var header = url[5..comma];                       // e.g. "image/png;base64"
        if (!header.EndsWith(";base64", StringComparison.OrdinalIgnoreCase)) return null;
        var mime = header[..header.IndexOf(';')];
        var bytes = Convert.FromBase64String(url[(comma + 1)..]);
        return Save("image" + ExtensionFor(mime), bytes);
    }

    private static string Save(string name, byte[] bytes)
    {
        foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        var dir = Path.Combine(TempRoot, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, name);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private static string ExtensionFor(string mime) => mime.ToLowerInvariant() switch
    {
        "image/jpeg" or "image/jpg" => ".jpg",
        "image/gif" => ".gif",
        "image/webp" => ".webp",
        "image/bmp" => ".bmp",
        "image/svg+xml" => ".svg",
        "image/heic" => ".heic",
        "application/pdf" => ".pdf",
        _ => ".png",
    };

    [DllImport("kernel32.dll")] private static extern IntPtr GlobalLock(IntPtr hMem);
    [DllImport("kernel32.dll")] private static extern bool GlobalUnlock(IntPtr hMem);
    [DllImport("kernel32.dll")] private static extern UIntPtr GlobalSize(IntPtr hMem);
    [DllImport("ole32.dll")] private static extern void ReleaseStgMedium(ref STGMEDIUM medium);
}
