namespace MultiAIChatbot.Services;

/// <summary>
/// Where each site's chat box and Send button are. When a site redesigns its page,
/// these selectors are usually the only thing that needs updating.
/// </summary>
public sealed record SiteSelectors(string[] EditorSelectors, string[] SendSelectors, string[] AnswerSelectors);

public static class AiSites
{
    public static readonly SiteSelectors ChatGpt = new(
        EditorSelectors: new[] { "#prompt-textarea", "div.ProseMirror[contenteditable='true']", "textarea" },
        SendSelectors: new[] { "button[data-testid='send-button']", "#composer-submit-button", "button[aria-label*='Send']" },
        AnswerSelectors: new[] { "[data-message-author-role='assistant']" });

    public static readonly SiteSelectors Claude = new(
        EditorSelectors: new[] { "div[contenteditable='true'].ProseMirror", "[data-testid='chat-input'] [contenteditable='true']", "div[contenteditable='true']" },
        SendSelectors: new[] { "button[aria-label='Send message']", "button[aria-label='Send Message']", "button[aria-label*='Send']" },
        AnswerSelectors: new[] { ".font-claude-response", "[data-testid='assistant-message']", ".font-claude-message" });

    public static readonly SiteSelectors Gemini = new(
        EditorSelectors: new[] { "rich-textarea div.ql-editor[contenteditable='true']", "div.ql-editor[contenteditable='true']", "div[contenteditable='true']" },
        SendSelectors: new[] { "button[aria-label='Send message']", "button.send-button", "button[aria-label*='Send']" },
        AnswerSelectors: new[] { "model-response message-content", "message-content", ".model-response-text" });

    public static readonly string[] AllKeys = { "chatgpt", "claude", "gemini" };

    public static string DisplayName(string key) => key switch
    {
        "chatgpt" => "ChatGPT",
        "claude" => "Claude",
        "gemini" => "Gemini",
        _ => key
    };

    public static SiteSelectors? ByKey(string key) => key.ToLowerInvariant() switch
    {
        "chatgpt" => ChatGpt,
        "claude" => Claude,
        "gemini" => Gemini,
        _ => null
    };
}
