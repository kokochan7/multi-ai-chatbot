using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;

namespace MultiAIChatbot.Services;

/// <summary>
/// Catches errors that would otherwise close the app, writes them to
/// %LOCALAPPDATA%\MultiAIChatbot\crash.log, and keeps the app running when it can.
/// </summary>
public static class CrashLog
{
    public static string FilePath { get; } = Path.Combine(AppState.Folder, "crash.log");

    private static bool _showingMessage;

    public static void Install(Application app)
    {
        // Errors on the UI thread: log, tell the user once, and keep going.
        app.DispatcherUnhandledException += OnDispatcherException;

        // Errors on background threads can't be recovered, but at least get logged.
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Write("Unhandled background error (app closing)", e.ExceptionObject as Exception);

        // Forgotten async tasks that failed.
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Write("Unobserved task error", e.Exception);
            e.SetObserved();
        };
    }

    private static void OnDispatcherException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Write("Unhandled error", e.Exception);
        e.Handled = true;

        if (_showingMessage) return;
        _showingMessage = true;
        try
        {
            MessageBox.Show(
                $"Something went wrong, but the app kept running.\n\n{e.Exception.Message}\n\n" +
                $"Details were saved to:\n{FilePath}",
                "Multi-AI Chatbot", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            _showingMessage = false;
        }
    }

    public static void Write(string what, Exception? ex)
    {
        try
        {
            Directory.CreateDirectory(AppState.Folder);
            // Keep the file small: start over once it passes 1 MB.
            if (File.Exists(FilePath) && new FileInfo(FilePath).Length > 1_000_000) File.Delete(FilePath);
            File.AppendAllText(FilePath,
                $"==== {DateTime.Now:yyyy-MM-dd HH:mm:ss} · version {Updater.CurrentVersionText} · {what}{Environment.NewLine}" +
                $"{ex}{Environment.NewLine}{Environment.NewLine}");
        }
        catch
        {
            // Logging must never cause a second crash.
        }
    }
}
