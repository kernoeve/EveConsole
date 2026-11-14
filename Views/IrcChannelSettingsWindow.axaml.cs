using Avalonia.Controls;
using Avalonia.Interactivity;
using EveConsole.ViewModels;

namespace EveConsole.Views;

/// <summary>An IRC channel's settings and lists, for one of its operators.</summary>
public partial class IrcChannelSettingsWindow : Window
{
    public IrcChannelSettingsWindow() => InitializeComponent();

    /// <summary>Opens one, owned by the window it was asked from.</summary>
    public static IrcChannelSettingsWindow ShowFor(IrcChannelSettingsVm vm, Window host)
    {
        var window = new IrcChannelSettingsWindow { DataContext = vm };
        window.Show(host);
        return window;
    }

    private IrcChannelSettingsVm? Vm => DataContext as IrcChannelSettingsVm;

    /// <summary>A list is read from the server the first time its tab is shown: each is a question the server answers.</summary>
    private void OnTabChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (Vm is not { } vm || Tabs.SelectedItem is not TabItem { Tag: string letter } || letter.Length != 1) return;
        if (vm.Lists.FirstOrDefault(l => l.Letter == letter[0]) is { } list) _ = list.LoadAsync();
    }

    private async void OnSave(object? sender, RoutedEventArgs e)
    {
        if (Vm is { } vm && await vm.SaveAsync()) Close();
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close();

    private void OnAddEntry(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: IrcModeListVm list }) _ = list.AddAsync();
    }

    private void OnRemoveEntry(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: IrcModeListEntryVm entry }) _ = entry.List.RemoveAsync(entry);
    }
}
