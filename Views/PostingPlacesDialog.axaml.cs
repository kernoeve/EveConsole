using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using EveConsole.ViewModels;

namespace EveConsole.Views;

/// <summary>
/// The places a posting lists whatever is found in. Closes with the places in their order, or
/// null when cancelled. See <see cref="PostingPlacesViewModel"/>.
/// </summary>
public partial class PostingPlacesDialog : Window
{
    public PostingPlacesDialog() => InitializeComponent();

    private PostingPlacesViewModel? Vm => DataContext as PostingPlacesViewModel;

    // A double-click on a step adds it, as the button beside the tree does.
    private void OnTreeDoubleTapped(object? sender, TappedEventArgs e) => Vm?.Add();

    private void OnRemove(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is PlaceChoiceVm c) Vm?.Remove(c);
    }

    private void OnUp(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is PlaceChoiceVm c) Vm?.Move(c, -1);
    }

    private void OnDown(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is PlaceChoiceVm c) Vm?.Move(c, +1);
    }

    private void OnSave(object? sender, RoutedEventArgs e) => Close(Vm?.Result());

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(null);
}
