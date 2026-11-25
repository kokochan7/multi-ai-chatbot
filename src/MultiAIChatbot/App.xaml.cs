using System.Windows;
using MultiAIChatbot.Services;

namespace MultiAIChatbot;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        CrashLog.Install(this);
        Updater.FinishPendingUpdate(e.Args);
        Theme.Initialize();
        base.OnStartup(e);
    }
}
