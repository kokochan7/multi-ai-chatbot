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

## Build it yourself

Install the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) on Windows, then run:

```
dotnet run --project src/MultiAIChatbot
```
