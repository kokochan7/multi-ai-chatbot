using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace MultiAIChatbot.Services;

/// <summary>A file the user dropped or picked, to be attached on every AI site.</summary>
public sealed record Attachment(string Name, string MimeType, string Base64, long Size)
{
    /// <summary>Largest total size sent at once. Bigger files are better uploaded on each site directly.</summary>
    public const long MaxTotalBytes = 25 * 1024 * 1024;

    public string SizeText => Size switch
    {
        < 1024 => $"{Size} B",
        < 1024 * 1024 => $"{Size / 1024.0:0} KB",
        _ => $"{Size / (1024.0 * 1024):0.0} MB",
    };

    public bool IsImage => MimeType.StartsWith("image/", StringComparison.OrdinalIgnoreCase);

    private ImageSource? _thumbnail, _preview;
    private bool _thumbnailTried, _previewTried;

    /// <summary>A small picture of an image file for its chip in the chat box, or null for other files.</summary>
    public ImageSource? Thumbnail
    {
        get
        {
            if (!_thumbnailTried) { _thumbnailTried = true; _thumbnail = Decode(96); }
            return _thumbnail;
        }
    }

    /// <summary>A bigger picture, shown when hovering over the thumbnail.</summary>
    public ImageSource? Preview
    {
        get
        {
            if (!_previewTried) { _previewTried = true; _preview = Decode(0); }
            return _preview;
        }
    }

    public bool HasThumbnail => Thumbnail != null;

    private ImageSource? Decode(int maxPixels)
    {
        if (!IsImage) return null;
        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.StreamSource = new MemoryStream(Convert.FromBase64String(Base64));
            if (maxPixels > 0) image.DecodePixelHeight = maxPixels;
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch
        {
            return null;   // a format Windows can't show (HEIC without its codec, SVG...)
        }
    }

    public static Attachment FromFile(string path)
    {
        var bytes = File.ReadAllBytes(path);
        return new Attachment(Path.GetFileName(path), MimeFor(path), Convert.ToBase64String(bytes), bytes.LongLength);
    }

    private static string MimeFor(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".gif" => "image/gif",
        ".webp" => "image/webp",
        ".bmp" => "image/bmp",
        ".heic" => "image/heic",
        ".pdf" => "application/pdf",
        ".txt" or ".log" => "text/plain",
        ".md" => "text/markdown",
        ".csv" => "text/csv",
        ".json" => "application/json",
        ".html" or ".htm" => "text/html",
        ".xml" => "application/xml",
        ".doc" => "application/msword",
        ".docx" => "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        ".xls" => "application/vnd.ms-excel",
        ".xlsx" => "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
        ".ppt" => "application/vnd.ms-powerpoint",
        ".pptx" => "application/vnd.openxmlformats-officedocument.presentationml.presentation",
        ".py" => "text/x-python",
        ".js" => "text/javascript",
        ".cs" => "text/plain",
        _ => "application/octet-stream",
    };
}
