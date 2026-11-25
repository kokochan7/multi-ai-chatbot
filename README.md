<p align="center"><img src="docs/logo.png" width="96" alt="Multi-AI Chatbot logo"></p>

# Multi-AI Chatbot

A lightweight Windows app: type one prompt and see ChatGPT, Claude and Gemini answer side by side in columns, so you can pick the best answer. Uses your own website logins.

## Download

Every change merged to `main` is built by GitHub and published as a release.

1. Open the [latest release](../../releases/latest).
2. Download `MultiAIChatbot.exe`.
3. Double-click it. No install is needed.

After that you don't need to download it again: the app checks for a new version once when it starts, and **Settings → Updates → Check for updates** installs the newest build and restarts the app. Keep the .exe in a folder you can write to (like Documents or Desktop), so it can replace itself.

The app needs the Microsoft Edge WebView2 Runtime, which comes with Windows 10 and 11.

## First run

Log in to ChatGPT, Claude and Gemini once, inside their columns. Your logins are saved only on your PC, in `%LOCALAPPDATA%\MultiAIChatbot\BrowserData`, so you stay logged in next time.

## Asking all three

Type in the box at the bottom and press **Enter** (Shift+Enter adds a new line). To include files or images, drop them on the box (from File Explorer, a web page, or another app), paste a picture with Ctrl+V, or click the paperclip; they're attached on every site before your question is sent (up to 25 MB in total), and pictures show a small preview in the box (hover for a bigger one). Sites usually need you to be signed in to accept files; if one isn't, its column says so and the question isn't sent there. If the box has grown, the shrink button next to Send puts it back to one line. The app types your question into ChatGPT, Claude and Gemini and presses Send on each. Each column shows a short status ("Sent", or what went wrong). The **New Chat** button at the top of the left menu starts a new chat on all three sites.

Drag a column by its header bar to move it, and drag the gaps between columns (or the edge of the left menu) to resize them. The app remembers the order and sizes, and **Reset view** at the bottom of the left menu puts everything back to the default layout. Your past chats are listed under Recent in the left menu by their first question, with a search box and Clear all; click one to see every question in that chat with all its answers on the History page. Follow-up questions stay in the same chat until you press **New Chat**.

If a site changes its page and sending stops working there, the fix is usually a one-line update to `src/MultiAIChatbot/Services/AiSites.cs`.

## History and Settings

- **History** saves every question, plus each AI's answer once it finishes, on your PC. Pick a chat under **Recent** in the left menu (with search) to see each of its questions with the answers side by side (shown as formatted text with headings, lists, links and code), copy an answer, use **Ask again**, or delete the chat.
- **Settings** lets you pick a light or dark theme (or follow Windows), show or hide each AI, change the column order, turn answer saving on or off, clear history, log out of all sites, and update the app.

Everything is stored in `%LOCALAPPDATA%\MultiAIChatbot` and never leaves your PC.

## Build it yourself

Install the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) on Windows, then run:

```
dotnet run --project src/MultiAIChatbot
```
