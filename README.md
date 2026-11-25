# Multi-AI Chatbot

A lightweight Windows app: type one prompt and see ChatGPT, Claude and Gemini answer side by side in columns, so you can pick the best answer. Uses your own website logins.

## Download

Every change is built automatically by GitHub. To get the latest build:

1. Open the [Actions tab](../../actions/workflows/build.yml) and click the newest green run.
2. Under **Artifacts**, download **MultiAIChatbot-win-x64** and unzip it.
3. Double-click `MultiAIChatbot.exe`. No install is needed.

The app needs the Microsoft Edge WebView2 Runtime, which comes with Windows 10 and 11.

## First run

Log in to ChatGPT, Claude and Gemini once, inside their columns. Your logins are saved only on your PC, in `%LOCALAPPDATA%\MultiAIChatbot\BrowserData`, so you stay logged in next time.

## Asking all three

Type in the box at the bottom and press **Enter** (Shift+Enter adds a new line). The app types your question into ChatGPT, Claude and Gemini and presses Send on each. Each column shows a short status ("Sent", or what went wrong). The **+** button starts a new chat on all three sites.

If a site changes its page and sending stops working there, the fix is usually a one-line update to `src/MultiAIChatbot/Services/AiSites.cs`.

## History and Settings

- **History** saves every question, plus each AI's answer once it finishes, on your PC. You can search, copy an answer, delete one entry, or use **Ask again**.
- **Settings** lets you show or hide each AI, change the column order, turn answer saving on or off, clear history, and log out of all sites.

Everything is stored in `%LOCALAPPDATA%\MultiAIChatbot` and never leaves your PC.

## Build it yourself

Install the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) on Windows, then run:

```
dotnet run --project src/MultiAIChatbot
```
