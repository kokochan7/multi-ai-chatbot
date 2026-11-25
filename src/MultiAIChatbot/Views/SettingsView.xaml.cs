using System.Windows;
using System.Windows.Controls;
using MultiAIChatbot.Services;

namespace MultiAIChatbot.Views;

public partial class SettingsView : UserControl
{
    public sealed record ColumnRow(string Key, string Name, bool Shown);

    private bool _loading;

    public SettingsView()
    {
        InitializeComponent();
        DataFolderText.Text = "Your website logins, history and settings are stored only on this PC, in " +
                              AppState.Folder;
        Refresh();
    }

    private void Refresh()
    {
        _loading = true;
        var s = AppState.Settings;
        ColumnList.ItemsSource = s.ColumnOrder
            .Select(k => new ColumnRow(k, AiSites.DisplayName(k), !s.HiddenSites.Contains(k)))
            .ToList();
        SaveAnswersBox.IsChecked = s.SaveAnswers;
        _loading = false;
    }

    private void Shown_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox { Tag: string key } box) return;
        var hidden = AppState.Settings.HiddenSites;
        hidden.Remove(key);
        if (box.IsChecked != true) hidden.Add(key);

        if (hidden.Count >= AiSites.AllKeys.Length)
        {
            hidden.Remove(key);   // Keep at least one column.
            StatusText.Text = "At least one AI has to stay visible.";
        }
        else
        {
            StatusText.Text = "";
        }
        AppState.SaveSettings();
        Refresh();
    }

    private void MoveUp_Click(object sender, RoutedEventArgs e) => Move(sender, -1);
    private void MoveDown_Click(object sender, RoutedEventArgs e) => Move(sender, +1);

    private void Move(object sender, int delta)
    {
        if (sender is not Button { Tag: string key }) return;
        var order = AppState.Settings.ColumnOrder;
        var i = order.IndexOf(key);
        var j = i + delta;
        if (i < 0 || j < 0 || j >= order.Count) return;
        (order[i], order[j]) = (order[j], order[i]);
        AppState.SaveSettings();
        Refresh();
    }

    private void SaveAnswers_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        AppState.Settings.SaveAnswers = SaveAnswersBox.IsChecked == true;
        AppState.SaveSettings();
    }

    private void ClearHistory_Click(object sender, RoutedEventArgs e)
    {
        var answer = MessageBox.Show(Window.GetWindow(this)!, "Delete all saved questions and answers?",
            "Clear history", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (answer != MessageBoxResult.Yes) return;
        AppState.ClearHistory();
        StatusText.Text = "History cleared.";
    }

    private async void LogOut_Click(object sender, RoutedEventArgs e)
    {
        var answer = MessageBox.Show(Window.GetWindow(this)!,
            "Log out of ChatGPT, Claude and Gemini? You'll need to sign in to each again.",
            "Log out of all sites", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (answer != MessageBoxResult.Yes) return;

        try
        {
            await AppState.LogOutAllAsync();
            StatusText.Text = "Logged out of all sites.";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Couldn't log out: {ex.Message}";
        }
    }
}
