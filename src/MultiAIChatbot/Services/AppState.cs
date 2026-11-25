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
    public bool CheckForUpdatesOnStartup { get; set; } = true;
    /// <summary>"System" (follow Windows), "Light" or "Dark".</summary>
    public string Theme { get; set; } = "System";
    /// <summary>Width of the left menu in pixels (drag its edge to change).</summary>
    public double SidebarWidth { get; set; } = 232;
    /// <summary>Relative width of each AI column, by site key (drag the gaps to change).</summary>
    public Dictionary<string, double> ColumnWidths { get; set; } = new();
}

/// <summary>One question and the three answers to it.</summary>
public sealed class HistoryEntry : INotifyPropertyChanged
{
    private string _chatGpt = "", _claude = "", _gemini = "";

    public Guid Id { get; set; } = Guid.NewGuid();
    /// <summary>Which chat this question belongs to. Follow-up questions share it until New Chat.</summary>
    public Guid ConversationId { get; set; }
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
/// One chat: the first question and every follow-up asked before New Chat was pressed.
/// Shown as a single item in the left menu's Recent list, titled with its first question.
/// </summary>
public sealed class Conversation : INotifyPropertyChanged
{
    public Guid Id { get; init; }

    /// <summary>Oldest first.</summary>
    public ObservableCollection<HistoryEntry> Entries { get; } = new();

    public string Title => Entries.Count > 0 ? Entries[0].Prompt : "";
    public DateTime Time => Entries.Count > 0 ? Entries[^1].Time : DateTime.MinValue;
    public string TimeText => Time.ToString("MMM d, h:mm tt");
    public string CountText => Entries.Count == 1 ? "1 question" : $"{Entries.Count} questions";

    public Conversation()
    {
        Entries.CollectionChanged += (_, _) =>
        {
            foreach (var name in new[] { nameof(Title), nameof(Time), nameof(TimeText), nameof(CountText) })
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        };
    }

    public event PropertyChangedEventHandler? PropertyChanged;
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

    /// <summary>The same questions grouped into chats, most recently used chat first.</summary>
    public static ObservableCollection<Conversation> Conversations { get; } = new();

    /// <summary>Raised after settings change, so the chat page can rearrange its columns.</summary>
    public static event Action? SettingsChanged;

    /// <summary>Raised when the user asks to log out of every site.</summary>
    public static event Func<Task>? LogOutRequested;

    static AppState()
    {
        NormalizeSettings();
        BuildConversations();
    }

    /// <summary>Groups the saved questions into chats. Older history has no chat id, so each question becomes its own chat.</summary>
    private static void BuildConversations()
    {
        foreach (var entry in History)
            if (entry.ConversationId == Guid.Empty) entry.ConversationId = entry.Id;

        var chats = History
            .GroupBy(e => e.ConversationId)
            .Select(g =>
            {
                var chat = new Conversation { Id = g.Key };
                foreach (var e in g.OrderBy(e => e.Time)) chat.Entries.Add(e);
                return chat;
            })
            .OrderByDescending(c => c.Time);
        foreach (var chat in chats) Conversations.Add(chat);
    }

    public static void SaveSettings()
    {
        NormalizeSettings();
        Save(SettingsFile, Settings);
        SettingsChanged?.Invoke();
    }

    /// <summary>Saves settings without telling the pages to rebuild (used for panel sizes).</summary>
    public static void SaveSettingsQuietly() => Save(SettingsFile, Settings);

    public static HistoryEntry AddHistory(string prompt, Guid conversationId)
    {
        var entry = new HistoryEntry { Prompt = prompt, ConversationId = conversationId };
        History.Insert(0, entry);

        var chat = Conversations.FirstOrDefault(c => c.Id == conversationId);
        if (chat == null)
        {
            chat = new Conversation { Id = conversationId };
            Conversations.Insert(0, chat);
        }
        else
        {
            var index = Conversations.IndexOf(chat);
            if (index > 0) Conversations.Move(index, 0);
        }
        chat.Entries.Add(entry);

        SaveHistory();
        return entry;
    }

    public static void SaveHistory() => Save(HistoryFile, History.ToList());

    /// <summary>Deletes a whole chat with all its questions and answers.</summary>
    public static void DeleteConversation(Conversation chat)
    {
        foreach (var entry in chat.Entries) History.Remove(entry);
        Conversations.Remove(chat);
        SaveHistory();
    }

    public static void ClearHistory()
    {
        History.Clear();
        Conversations.Clear();
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
        Settings.ColumnWidths ??= new();
        if (double.IsNaN(Settings.SidebarWidth) || Settings.SidebarWidth < 160 || Settings.SidebarWidth > 600)
            Settings.SidebarWidth = 232;
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
