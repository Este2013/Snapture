using Microsoft.Toolkit.Uwp.Notifications;

namespace Snapture.App;

/// <summary>
/// Native Action Center toasts (the Snipping-Tool style), including an inline
/// image. Works for an unpackaged WPF app via the toolkit's compat layer.
/// </summary>
internal static class Notifications
{
    /// <summary>
    /// Toast for a saved capture: title, message, an optional inline preview image,
    /// and an "open" argument so a click opens the file.
    /// </summary>
    public static void ShowSaved(string title, string message, string? imagePath, string? openFilePath)
    {
        var builder = new ToastContentBuilder()
            .AddText(title)
            .AddText(message);

        if (imagePath is not null)
            builder.AddInlineImage(new Uri(imagePath));
        if (openFilePath is not null)
            builder.AddArgument("open", openFilePath);

        builder.Show();
    }
}
