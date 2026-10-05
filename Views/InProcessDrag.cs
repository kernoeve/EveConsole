using Avalonia.Input;

namespace EveConsole.Views;

/// <summary>
/// A drag whose payload is one of this app's own objects — a fit's or a map's tab, an item from
/// the fitting finder — rather than something another program could read.
/// </summary>
/// <remarks>
/// <para>⚠️ Avalonia 11.3's <see cref="DataTransfer"/> (which replaces the obsolete DataObject,
/// and is the only drag API in Avalonia 12) carries strings and bytes, not objects: a format is
/// made with <see cref="DataFormat.CreateStringApplicationFormat"/> or its bytes twin, so the view
/// model a tab drag used to put straight into a DataObject no longer has anywhere to go.</para>
///
/// <para>So the drag carries a token, and the object stays here. One drag runs at a time; a token
/// that is not the current one — a drag that has ended, or a drop from another copy of the app
/// that happens to use the same format name — reads as nothing, as an unrelated payload did
/// before.</para>
/// </remarks>
internal static class InProcessDrag
{
    private static string? _token;
    private static object? _payload;

    /// <summary>A format for this app's own drags, named as the old string formats were.</summary>
    public static DataFormat<string> Format(string name) => DataFormat.CreateStringApplicationFormat(name);

    /// <summary>Runs a drag of <paramref name="payload"/> until it is dropped or abandoned.</summary>
    public static async Task<DragDropEffects> RunAsync(
        PointerEventArgs trigger, DataFormat<string> format, object payload, DragDropEffects allowed)
    {
        var token = Guid.NewGuid().ToString("N");
        (_token, _payload) = (token, payload);

        var data = new DataTransfer();
        data.Add(DataTransferItem.Create(format, token));
        try
        {
            return await DragDrop.DoDragDropAsync(trigger, data, allowed);
        }
        finally
        {
            if (_token == token) (_token, _payload) = (null, null);
        }
    }

    /// <summary>The object being dragged, if this drag is one of ours in that format and of that type.</summary>
    public static T? Get<T>(DragEventArgs e, DataFormat<string> format) where T : class =>
        _token is { } current && e.DataTransfer.TryGetValue(format) == current ? _payload as T : null;
}
