using Avalonia.Controls;
using Avalonia.Interactivity;
using EveConsole.Services.Mumble;
using EveConsole.ViewModels;

namespace EveConsole.Views;

/// <summary>The details of what is selected in the channel tree. Only one at a time, as they all show the same selection.</summary>
public partial class MumbleDetailWindow : Window
{
    private static MumbleDetailWindow? _open;

    public MumbleDetailWindow() => InitializeComponent();

    /// <summary>⚠️ Belongs to the window it was asked from: over a game, one owned by the main window would open behind the game.</summary>
    public static void ShowFor(MumbleViewModel vm, Window host)
    {
        if (_open is { } shown && !ReferenceEquals(shown.Owner, host)) shown.Close();
        if (_open is { } already) { already.Activate(); return; }

        _open = new MumbleDetailWindow { DataContext = vm };
        _open.Closed += (_, _) => _open = null;
        _open.Show(host);
    }

    private async void OnUnban(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: MumbleBan ban } && DataContext is MumbleViewModel vm
            && await new ConfirmDialog(string.Format(System.Globalization.CultureInfo.CurrentCulture, Localization.MumbleText.ConfirmUnban, ban.Name)).ShowDialog<bool>(this))
            await vm.UnbanAsync(ban);
    }
}

/// <summary>A ban's second line, for the list.</summary>
public static class MumbleBanText
{
    public static readonly Avalonia.Data.Converters.IValueConverter Convert =
        new Avalonia.Data.Converters.FuncValueConverter<MumbleBan, string>(b => b is null ? "" : MumbleViewModel.BanText(b));
}
