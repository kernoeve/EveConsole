using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;

namespace EveConsole.Controls;

/// <summary>
/// A hex colour box with a palette beside it.
///
/// <para><b>Why a palette rather than a colour wheel.</b> Avalonia's full picker is a separate
/// package this app does not reference, and adding one would buy the ability to choose any of
/// sixteen million colours — most of which are unreadable on EVE's dark mail background. A short
/// list that is known to work is more useful here than a wheel that mostly does not, and it costs
/// no dependency.</para>
///
/// <para>The hex box stays editable, so anything the palette lacks can still be pasted in — the
/// palette is a shortcut, not a restriction.</para>
/// </summary>
public partial class ColorField : UserControl
{
    /// <summary>
    /// Colours that stay legible on EVE's mail background, roughly by hue.
    ///
    /// <para>⚠️ Nothing very dark. Mail is drawn on near-black, and a colour picked for a white
    /// page disappears entirely — which is the failure this control exists to prevent, since the
    /// author cannot see the result until a mail has been sent.</para>
    /// </summary>
    private static readonly string[] Palette =
    [
        "#e8e8f0", "#c8c8d8", "#888899",   // neutrals
        "#c85a5a", "#e07b52", "#c8a84b",   // warm
        "#4a9a5a", "#4ac8a8", "#0ff5d6",   // green to cyan
        "#5599aa", "#5b9bd5", "#8a7bd8",   // blue
        "#c86ab0", "#e0508c", "#ffffff",   // pink, white
    ];

    public ColorField()
    {
        InitializeComponent();

        foreach (var hex in Palette) Swatches.Children.Add(Swatch(hex));

        ClearButton.Click += (_, _) => { Value = ""; CloseFlyout(); };

        HexBox.TextChanged += (_, _) =>
        {
            if (_settingText) return;
            SetCurrentValue(ValueProperty, HexBox.Text ?? "");
            ShowPreview(HexBox.Text);
        };
    }

    /// <summary>⚠️ Guards the two-way loop: pushing a picked colour into the box raises
    /// TextChanged, which would write it back and fight the binding mid-update.</summary>
    private bool _settingText;

    public static readonly StyledProperty<string> ValueProperty =
        AvaloniaProperty.Register<ColorField, string>(
            nameof(Value), defaultValue: "",
            defaultBindingMode: Avalonia.Data.BindingMode.TwoWay);

    /// <summary>The hex the field holds. Empty means no colour.</summary>
    public string Value
    {
        get => GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property != ValueProperty) return;

        var v = change.GetNewValue<string>() ?? "";
        if ((HexBox.Text ?? "") == v) { ShowPreview(v); return; }

        _settingText = true;
        try { HexBox.Text = v; }
        finally { _settingText = false; }
        ShowPreview(v);
    }

    private Control Swatch(string hex)
    {
        var button = new Button
        {
            Width = 26, Height = 20, Margin = new Thickness(0, 0, 4, 4),
            Padding = new Thickness(0), CornerRadius = new CornerRadius(2),
            Background = Brush(hex) ?? Brushes.Transparent,
            BorderBrush = new SolidColorBrush(Color.Parse("#3a3a4a")),
            BorderThickness = new Thickness(1),
            Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand),
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        ToolTip.SetTip(button, hex);
        button.Click += (_, _) => { Value = hex; CloseFlyout(); };
        return button;
    }

    /// <summary>The flyout does not close on its own when a button inside it is pressed, and a
    /// palette that stays open after a choice looks like the choice did not register.</summary>
    private void CloseFlyout() => PickButton.Flyout?.Hide();

    private void ShowPreview(string? hex) =>
        Preview.Background = Brush(hex) ?? Brushes.Transparent;

    /// <summary>A brush for a hex value, or null when it is empty or not a colour — an
    /// in-progress "#c8a" while someone is still typing is not an error worth showing.</summary>
    private static IBrush? Brush(string? hex)
    {
        var s = (hex ?? "").Trim();
        if (s.Length == 0) return null;
        if (!s.StartsWith('#')) s = "#" + s;
        try { return new SolidColorBrush(Color.Parse(s)); }
        catch { return null; }
    }
}
