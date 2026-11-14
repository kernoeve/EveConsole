using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Platform.Storage;

namespace EveConsole.Views;

/// <summary>
/// Pasting into a chat's message box: files copied in Explorer, or a picture (a screenshot, a browser's Copy image), are
/// sent as files where the conversation takes them. Text pastes as it always did.
/// </summary>
internal static class ChatPaste
{
    /// <param name="canAttach">Whether the conversation open takes files now.</param>
    /// <param name="send">Sends one: its content, name and size.</param>
    public static void Hook(TextBox box, Func<bool> canAttach, Func<Stream, string, long, Task> send)
    {
        var passText = false;
        box.PastingFromClipboard += async (_, e) =>
        {
            // The text paste let through below comes back here: it goes on as usual.
            if (passText) { passText = false; return; }
            if (!canAttach() || TopLevel.GetTopLevel(box)?.Clipboard is not { } clipboard) return;

            // ⚠️ Decided before looking, as the looking waits: the text, if that is all it was, is pasted again below.
            e.Handled = true;
            try
            {
                if (await clipboard.TryGetFilesAsync() is { Length: > 0 } items)
                {
                    foreach (var file in items.OfType<IStorageFile>())
                    {
                        var size = (await file.GetBasicPropertiesAsync()).Size ?? 0;
                        await using var content = await file.OpenReadAsync();
                        await send(content, file.Name, (long)size);
                    }
                    return;
                }

                // ⚠️ Text first when there is any: a spreadsheet's copied cells come with a picture of them too, and
                // are meant as text. A screenshot, or a browser's Copy image, has none.
                if (string.IsNullOrEmpty(await clipboard.TryGetTextAsync()) && await clipboard.TryGetBitmapAsync() is { } bitmap)
                {
                    using var png = new MemoryStream();
                    bitmap.Save(png);
                    png.Position = 0;
                    var name = $"Pasted picture {DateTime.Now:yyyy-MM-dd HHmmss}.png";
                    await send(png, name, png.Length);
                    return;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or NotSupportedException)
            {
                // What could not be read as a file goes as text, where there is any.
            }

            passText = true;
            box.Paste();
        };
    }
}
