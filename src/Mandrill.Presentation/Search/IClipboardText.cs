namespace Mandrill.Presentation.Search;

/// <summary>Puts text on the clipboard (search "Copy path"); the App implements it (WP-2.2b). UI thread.</summary>
public interface IClipboardText
{
    /// <summary>Replaces the clipboard content with <paramref name="text"/>.</summary>
    /// <param name="text">Text to copy.</param>
    void SetText(string text);
}
