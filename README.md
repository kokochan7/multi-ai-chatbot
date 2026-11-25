<p align="center"><img src="docs/logo.png" width="96" alt="Multi-AI Chatbot logo"></p>

# Multi-AI Chatbot

A lightweight Windows app: type one prompt and see ChatGPT, Claude and Gemini answer side by side in columns, so you can pick the best answer. Uses your own website logins.

## Download

Every change merged to `main` is built by GitHub and published as a release.

1. Open the [latest release](../../releases/latest).
2. Download `MultiAIChatbot.exe`.
3. Double-click it. No install is needed.

After that you don't need to download it again: the app checks for a new version when it starts, and **Settings → Updates → Check for updates** installs the newest build and restarts the app. Keep the .exe in a folder you can write to (like Documents or Desktop), so it can replace itself.

The app needs the Microsoft Edge WebView2 Runtime, which comes with Windows 10 and 11.

## First run

Log in to ChatGPT, Claude and Gemini once, inside their columns. Your logins are saved only on your PC, in `%LOCALAPPDATA%\MultiAIChatbot\BrowserData`, so you stay logged in next time.

## Asking all three

Type in the box at the bottom and press **Enter** (Shift+Enter adds a new line). The app types your question into ChatGPT, Claude and Gemini and presses Send on each. Each column shows a short status ("Sent", or what went wrong). The **+** button starts a new chat on all three sites.

Drag a column by its header bar to move it, and drag the gaps between columns (or the edge of the left menu) to resize them. The app remembers the order and sizes. Your most recent questions are listed in the left menu; click one to see all its answers.

If a site changes its page and sending stops working there, the fix is usually a one-line update to `src/MultiAIChatbot/Services/AiSites.cs`.

## History and Settings

- **History** saves every question, plus each AI's answer once it finishes, on your PC. You can search, copy an answer, delete one entry, or use **Ask again**.
- **Settings** lets you pick a light or dark theme (or follow Windows), show or hide each AI, change the column order, turn answer saving on or off, clear history, log out of all sites, and update the app.

Everything is stored in `%LOCALAPPDATA%\MultiAIChatbot` and never leaves your PC.

## Build it yourself

Install the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) on Windows, then run:

```
dotnet run --project src/MultiAIChatbot
```
