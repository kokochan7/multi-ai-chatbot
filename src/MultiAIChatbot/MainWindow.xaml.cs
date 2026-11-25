using System.Windows;

namespace MultiAIChatbot;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
    }

    private void Nav_Checked(object sender, RoutedEventArgs e)
    {
        // Checked can fire while the window is still being built.
        if (ChatPage is null || HistoryPage is null || SettingsPage is null) return;

        ChatPage.Visibility = sender == NavChat ? Visibility.Visible : Visibility.Collapsed;
        HistoryPage.Visibility = sender == NavHistory ? Visibility.Visible : Visibility.Collapsed;
        SettingsPage.Visibility = sender == NavSettings ? Visibility.Visible : Visibility.Collapsed;
    }
}
