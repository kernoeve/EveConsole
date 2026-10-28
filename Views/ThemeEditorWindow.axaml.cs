using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Interactivity;
using EveConsole.Localization;
using EveConsole.ViewModels;

namespace EveConsole.Views;

/// <summary>The theme editor. One at a time: asked for again, the open one comes forward.</summary>
public partial class ThemeEditorWindow : Window
{
    private static ThemeEditorWindow? _open;

    private readonly ThemeEditorViewModel _vm;

    public ThemeEditorWindow()
    {
        InitializeComponent();

        _vm = new ThemeEditorViewModel { Confirm = ConfirmAsync };
        _vm.PreviewChanged += (colours, light) => Preview.Show(colours, light);
        DataContext = _vm;

        // The first theme was loaded before anything was listening.
        Preview.Show(_vm.Colours, _vm.IsLight);
    }

    /// <summary>
    /// Opens the editor over <paramref name="owner"/>, or over whichever window is active — the
    /// Settings window, when it is asked for from there.
    /// </summary>
    public static void Open(Window? owner = null)
    {
        if (_open is not null) { _open.Activate(); return; }

        owner ??= (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?
            .Windows.FirstOrDefault(w => w.IsActive);

        _open = new ThemeEditorWindow();
        if (owner is not null) _open.Show(owner); else _open.Show();
    }

    protected override async void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        _open = null;

        // Whatever was changed in the last moment before closing is saved, not lost to the pause.
        await _vm.FlushAsync();
        _vm.Detach();
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close();

    private async Task<bool> ConfirmAsync(string message) =>
        await new ConfirmDialog(message, title: SettingsText.ThemeEdTitle).ShowDialog<bool>(this);
}
