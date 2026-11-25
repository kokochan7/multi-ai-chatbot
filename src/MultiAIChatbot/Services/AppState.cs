using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MultiAIChatbot.Services;

/// <summary>User preferences, saved to settings.json.</summary>
public sealed class AppSettings
{
    /// <summary>Column order, left to right, by site key.</summary>
    public List<string> ColumnOrder { get; set; } = new(AiSites.AllKeys);
    public List<string> HiddenSites { get; set; } = new();
    public bool SaveAnswers { get; set; } = true;
}

/// <summary>One question and the three answers to it.</summary>
public sealed class HistoryEntry : INotifyPropertyChanged
{
    private string _chatGpt = "", _claude = "", _gemini = "";

    public Guid Id { get; set; } = Guid.NewGuid();
    public DateTime Time { get; set; } = DateTime.Now;
    public string Prompt { get; set; } = "";

    public string ChatGpt { get => _chatGpt; set => Set(ref _chatGpt, value); }
    public string Claude { get => _claude; set => Set(ref _claude, value); }
    public string Gemini { get => _gemini; set => Set(ref _gemini, value); }

    [JsonIgnore]
    public string TimeText => Time.ToString("MMM d, h:mm tt");

    public void SetAnswer(string siteKey, string text)
    {
        switch (siteKey)
        {
            case "chatgpt": ChatGpt = text; break;
            case "claude": Claude = text; break;
            case "gemini": Gemini = text; break;
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Set(ref string field, string value, [CallerMemberName] string? name = null)
    {
        if (field == value) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}

/// <summary>
/// App-wide state: settings and history, stored as JSON files in
/// %LOCALAPPDATA%\MultiAIChatbot. Nothing leaves this PC.
/// </summary>
public static class AppState
{
    public static string Folder { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MultiAIChatbot");

    private static string SettingsFile => Path.Combine(Folder, "settings.json");
    private static string HistoryFile => Path.Combine(Folder, "history.json");

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static AppSettings Settings { get; private set; } = Load<AppSettings>(SettingsFile) ?? new AppSettings();

    /// <summary>Newest first.</summary>
    public static ObservableCollection<HistoryEntry> History { get; } =
        new(Load<List<HistoryEntry>>(HistoryFile) ?? new List<HistoryEntry>());

    /// <summary>Raised after settings change, so the chat page can rearrange its columns.</summary>
    public static event Action? SettingsChanged;

    /// <summary>Raised when the user asks to log out of every site.</summary>
    public static event Func<Task>? LogOutRequested;

    static AppState()
    {
        NormalizeSettings();
    }

    public static void SaveSettings()
    {
        NormalizeSettings();
        Save(SettingsFile, Settings);
        SettingsChanged?.Invoke();
    }

    public static HistoryEntry AddHistory(string prompt)
    {
        var entry = new HistoryEntry { Prompt = prompt };
        History.Insert(0, entry);
        SaveHistory();
        return entry;
    }

    public static void SaveHistory() => Save(HistoryFile, History.ToList());

    public static void DeleteHistory(HistoryEntry entry)
    {
        History.Remove(entry);
        SaveHistory();
    }

    public static void ClearHistory()
    {
        History.Clear();
        SaveHistory();
    }

    public static async Task LogOutAllAsync()
    {
        if (LogOutRequested != null) await LogOutRequested.Invoke();
    }

    /// <summary>Makes sure every site appears exactly once in the column order.</summary>
    private static void NormalizeSettings()
    {
        var order = Settings.ColumnOrder.Where(AiSites.AllKeys.Contains).Distinct().ToList();
        order.AddRange(AiSites.AllKeys.Where(k => !order.Contains(k)));
        Settings.ColumnOrder = order;
        Settings.HiddenSites = Settings.HiddenSites.Where(AiSites.AllKeys.Contains).Distinct().ToList();
    }

    private static T? Load<T>(string path) where T : class
    {
        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize<T>(File.ReadAllText(path)) : null;
        }
        catch
        {
            return null;   // A damaged file shouldn't stop the app from starting.
        }
    }

    private static void Save<T>(string path, T value)
    {
        try
        {
            Directory.CreateDirectory(Folder);
            var temp = path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(value, Json));
            File.Move(temp, path, overwrite: true);
        }
        catch
        {
            // Saving is best-effort; never crash the app over it.
        }
    }
}
