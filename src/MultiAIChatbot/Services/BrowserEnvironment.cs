using System.IO;
using Microsoft.Web.WebView2.Core;

namespace MultiAIChatbot.Services;

/// <summary>
/// The app's WebView2 browser. Its data lives in %LOCALAPPDATA%\MultiAIChatbot\BrowserData,
/// so logins survive closing and reopening the app. Each site uses its own profile inside it
/// (see <see cref="ProfileNameFor"/>), so their logins are kept apart.
/// </summary>
public static class BrowserEnvironment
{
    public static string DataFolder { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "MultiAIChatbot", "BrowserData");

    private static Task<CoreWebView2Environment>? _environment;

    /// <summary>The browser profile for one site ("chatgpt" → "site-chatgpt").</summary>
    public static string ProfileNameFor(string siteKey) =>
        "site-" + new string(siteKey.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());

    public static Task<CoreWebView2Environment> GetAsync()
    {
        return _environment ??= CreateAsync();
    }

    /// <summary>
    /// Forgets a browser environment whose process crashed, so the next GetAsync starts a new one.
    /// Columns share one environment, so only the first column to recover resets it.
    /// </summary>
    public static void Reset(CoreWebView2Environment dead)
    {
        if (_environment is { IsCompletedSuccessfully: true } task && ReferenceEquals(task.Result, dead))
            _environment = null;
    }

    private static Task<CoreWebView2Environment> CreateAsync()
    {
        Directory.CreateDirectory(DataFolder);
        var options = new CoreWebView2EnvironmentOptions
        {
            // Hides the "controlled by automation" signal some sign-in pages check for.
            AdditionalBrowserArguments = "--disable-blink-features=AutomationControlled"
        };
        return CoreWebView2Environment.CreateAsync(browserExecutableFolder: null, userDataFolder: DataFolder, options: options);
    }
}
