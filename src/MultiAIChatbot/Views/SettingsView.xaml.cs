using System.Windows.Controls;
using MultiAIChatbot.Services;

namespace MultiAIChatbot.Views;

public partial class SettingsView : UserControl
{
    public SettingsView()
    {
        InitializeComponent();
        DataFolderText.Text = $"Your website logins are stored only on this PC, in {BrowserEnvironment.DataFolder}";
    }
}
