using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Interactivity;
using EveConsole.Localization;

namespace EveConsole.Views;

/// <summary>
/// A small copy icon to put beside a name: a click copies <see cref="Text"/> and the main window
/// says so ("Copied …"). Two overlapping sheets drawn in the theme's colours, the accent on hover.
/// </summary>
public class CopyTextButton : Button
{
    public static readonly StyledProperty<string?> TextProperty =
        AvaloniaProperty.Register<CopyTextButton, string?>(nameof(Text));

    /// <summary>What a click copies.</summary>
    public string? Text
    {
        get => GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    // Styled as a Button: the theme's template, with the look set here.
    protected override Type StyleKeyOverride => typeof(Button);

    private readonly Rectangle _back, _front;

    public CopyTextButton()
    {
        Classes.Add("copytext");
        Width = Height = 22;
        Padding = new Thickness(0);
        Background = Avalonia.Media.Brushes.Transparent;
        BorderThickness = new Thickness(0);
        Cursor = new Cursor(StandardCursorType.Hand);
        VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center;
        HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Center;
        VerticalContentAlignment = Avalonia.Layout.VerticalAlignment.Center;
        ToolTip.SetTip(this, AssetsText.TipCopyName);

        _back  = Sheet(3, 0);
        _front = Sheet(0, 3);
        _front.Bind(Shape.FillProperty, _front.GetResourceObservable("SurfacePanelBrush"));
        var canvas = new Canvas { Width = 12, Height = 13 };
        canvas.Children.Add(_back);
        canvas.Children.Add(_front);
        Content = canvas;

        PointerEntered += (_, _) => Ink("AccentBrush");
        PointerExited  += (_, _) => Ink("TextMutedBrush");
        Ink("TextMutedBrush");

        // On the Click event rather than OnClick: a click raised by keyboard or code goes through
        // the event, and copies too.
        Click += async (_, _) => await CopyAsync(this, Text);
    }

    private static Rectangle Sheet(double left, double top)
    {
        var sheet = new Rectangle { Width = 9, Height = 10, RadiusX = 1.5, RadiusY = 1.5, StrokeThickness = 1.2 };
        Canvas.SetLeft(sheet, left);
        Canvas.SetTop(sheet, top);
        return sheet;
    }

    private void Ink(string brushKey)
    {
        _back.Bind(Shape.StrokeProperty, _back.GetResourceObservable(brushKey));
        _front.Bind(Shape.StrokeProperty, _front.GetResourceObservable(brushKey));
    }

    /// <summary>
    /// Copies <paramref name="text"/> and says so in the middle of the main window — the same as
    /// the button, for a right-click "Copy Name" beside it.
    /// </summary>
    public static async Task CopyAsync(Visual from, string? text)
    {
        if (string.IsNullOrEmpty(text) || TopLevel.GetTopLevel(from)?.Clipboard is not { } clipboard) return;
        await clipboard.SetTextAsync(text);
        // The main window, even from a tool in a window of its own.
        var main = TopLevel.GetTopLevel(from) as MainWindow
                   ?? (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow as MainWindow;
        main?.ShowToast(string.Format(AssetsText.ToastNameCopied, text));
    }
}
