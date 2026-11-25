using System.IO;

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
