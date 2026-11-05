using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using EveConsole.ViewModels;

namespace EveConsole.Views;

/// <summary>
/// Who is talking, on top of everything else, for when the app is behind a game.
///
/// <para>⚠️ Over a game in windowed or borderless mode only: nothing but the game itself draws over exclusive
/// fullscreen, and getting there would mean hooking the game, which is not on offer.</para>
/// </summary>
public partial class MumbleOverlayWindow : Window
{
    public MumbleOverlayWindow()
    {
        InitializeComponent();

        // Where it was left. Saved when it moves, so a crash loses nothing.
        PositionChanged += (_, e) =>
        {
            if (DataContext is MumbleViewModel vm && IsVisible) vm.SaveOverlayPosition(e.Point.X, e.Point.Y);
        };
    }

    private void OnDrag(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) BeginMoveDrag(e);
    }
}

/// <summary>Shows and hides the overlay as its setting says, whichever tool is open.</summary>
public sealed class MumbleOverlayController
{
    private readonly MumbleViewModel _vm;
    private MumbleOverlayWindow? _window;

    public MumbleOverlayController(MumbleViewModel vm)
    {
        _vm = vm;
        _vm.PropertyChanged += OnChanged;
        Apply();
    }

    private void OnChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MumbleViewModel.ShowOverlay)) Apply();
    }

    private void Apply()
    {
        if (_vm.ShowOverlay)
        {
            if (_window is not null) return;

            _window = new MumbleOverlayWindow { DataContext = _vm };
            if (_vm.OverlayPosition is { } at)
            {
                _window.WindowStartupLocation = WindowStartupLocation.Manual;
                _window.Position = new PixelPoint((int)at.X, (int)at.Y);
            }
            _window.Show();
        }
        else
        {
            _window?.Close();
            _window = null;
        }
    }

    /// <summary>The app is closing: it must not keep the process alive behind it.</summary>
    public void Close()
    {
        _window?.Close();
        _window = null;
    }
}
