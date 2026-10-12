using Avalonia;
using Avalonia.Controls;
using EveConsole.Services;

namespace EveConsole.Views;

/// <summary>
/// An item's picture by its type id, for a grid cell or a list row: set <see cref="TypeId"/> and
/// it fetches the icon itself, so a row model needs no picture property of its own.
///
/// <para>A blueprint has no icon — the image server refuses one — so a refused icon falls back to
/// the blueprint's picture, the copy's or the original's by <see cref="BlueprintPicture"/>. Both
/// come through <see cref="EveImageCache"/>, which keeps them on disk and remembers a refusal for
/// the session.</para>
///
/// <para>⚠️ A recycled row changes its TypeId while a fetch for the old one is still out; the
/// answer is kept only if it is still for the id on show.</para>
/// </summary>
public sealed class TypeIcon : Image
{
    public static readonly StyledProperty<int> TypeIdProperty =
        AvaloniaProperty.Register<TypeIcon, int>(nameof(TypeId));

    /// <summary>"bp" (an original, the default) or "bpc" (a copy), for a blueprint.</summary>
    public static readonly StyledProperty<string> BlueprintPictureProperty =
        AvaloniaProperty.Register<TypeIcon, string>(nameof(BlueprintPicture), "bp");

    public int TypeId
    {
        get => GetValue(TypeIdProperty);
        set => SetValue(TypeIdProperty, value);
    }

    public string BlueprintPicture
    {
        get => GetValue(BlueprintPictureProperty);
        set => SetValue(BlueprintPictureProperty, value);
    }

    protected override Type StyleKeyOverride => typeof(Image);

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == TypeIdProperty || change.Property == BlueprintPictureProperty)
            _ = LoadAsync(TypeId, BlueprintPicture);
    }

    private async Task LoadAsync(int typeId, string blueprint)
    {
        Source = null;
        if (typeId <= 0) return;
        var bmp = await EveImageCache.GetAsync($"https://images.evetech.net/types/{typeId}/icon?size=32")
               ?? await EveImageCache.GetAsync($"https://images.evetech.net/types/{typeId}/{blueprint}?size=32");
        if (typeId == TypeId) Source = bmp;
    }
}
