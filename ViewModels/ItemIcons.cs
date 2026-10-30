using Avalonia.Media.Imaging;
using Avalonia.Threading;
using EveConsole.Services;

namespace EveConsole.ViewModels;

/// <summary>
/// An item's picture, fetched once and shared.
///
/// <para>⚠️ /bp for a blueprint and /icon for everything else. The image server does not fall back
/// between them: ask for the wrong one and you get nothing rather than a placeholder, which is how
/// a grid of blueprints ends up with no pictures at all.</para>
///
/// <para>Here rather than copied into each row model — five grids draw these now, and the variant
/// rule is the sort of detail that gets remembered in four places and forgotten in the fifth.</para>
/// </summary>
public static class ItemIcons
{
    /// <remarks>⚠️ When the server refuses the one asked for, the other is tried. Not every caller
    /// can tell a blueprint: a contract's line for one is not always marked as one (an original
    /// listed as a plain item), and asking its /icon got a 400 and a blank picture. The refusal is
    /// cached, so the wrong guess costs one request a session.</remarks>
    public static async Task<Bitmap?> GetAsync(int typeId, bool blueprint = false)
    {
        if (typeId <= 0) return null;
        return await EveImageCache.GetAsync(Url(typeId, blueprint))
            ?? await EveImageCache.GetAsync(Url(typeId, !blueprint));
    }

    private static string Url(int typeId, bool blueprint) =>
        $"https://images.evetech.net/types/{typeId}/{(blueprint ? "bp" : "icon")}?size=32";

    /// <summary>
    /// Fetches an icon and hands it back on the UI thread.
    ///
    /// <para>Fire and forget: the cache answers instantly once warm, and a row disposed before the
    /// fetch lands simply sets a property nobody is watching.</para>
    /// </summary>
    public static async Task LoadAsync(int typeId, Action<Bitmap> apply, bool blueprint = false)
    {
        var bmp = await GetAsync(typeId, blueprint);
        if (bmp is not null) Dispatcher.UIThread.Post(() => apply(bmp));
    }

    /// <summary>A reaction formula's picture, from its product's icon: see <see cref="FormulaPicture"/>.
    /// Drawn on the UI thread, where it is handed back.</summary>
    public static async Task LoadFormulaAsync(int productTypeId, Action<Bitmap> apply)
    {
        var bmp = await GetAsync(productTypeId);
        if (bmp is not null) Dispatcher.UIThread.Post(() => apply(FormulaPicture.Compose(bmp, 32)));
    }
}
