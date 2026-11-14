using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using EveConsole.ViewModels;

namespace EveConsole.Views;

/// <summary>
/// A Worklist control as one line: the character, and how many tasks of each kind are ready for
/// them. Clicked, the list drops down under it, the same list the control shows in its list mode.
/// </summary>
public sealed class PanelWorklistLine : PanelDropLine
{
    private const double DropWidth = 420, DropHeight = 400;

    private readonly PanelWorklistVm _vm;

    public PanelWorklistLine(PanelWorklistVm vm)
        : base(Line(vm), new PanelWorklistControl { DataContext = vm, ManagesLifetime = false }, DropWidth, DropHeight)
    {
        _vm = vm;
        DataContext = vm;
        Padding = new Thickness(6, 0);
    }

    protected override void Shown() => _vm.Start();

    protected override void Hidden() => _vm.Dispose();

    private static DockPanel Line(PanelWorklistVm vm)
    {
        var name = new TextBlock
        {
            FontSize = 11,
            FontWeight = FontWeight.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            [!TextBlock.TextProperty] = new Binding(nameof(PanelWorklistVm.LineName)),
            [!TextBlock.ForegroundProperty] = new DynamicResourceExtension("TextBrightBrush"),
        };
        var counts = new ItemsControl
        {
            VerticalAlignment = VerticalAlignment.Center,
            ItemsSource = vm.Counts,
            ItemsPanel = new FuncTemplate<Panel?>(() => new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 }),
            ItemTemplate = new FuncDataTemplate<WorklistKindCount>((count, _) => new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 4,
                Children =
                {
                    new TextBlock
                    {
                        Text = count?.Kind, FontSize = 10, VerticalAlignment = VerticalAlignment.Center,
                        [!TextBlock.ForegroundProperty] = new DynamicResourceExtension("TextMutedBrush"),
                    },
                    new TextBlock
                    {
                        Text = count?.Count.ToString(System.Globalization.CultureInfo.CurrentCulture), FontSize = 11,
                        FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center,
                        [!TextBlock.ForegroundProperty] = new DynamicResourceExtension("AccentBrush"),
                    },
                },
            }),
        };
        // Drawn, not a ▾ character: not every font has one.
        var chevron = new Avalonia.Controls.Shapes.Path
        {
            Data = Geometry.Parse("M0,0 L8,0 L4,5 Z"),
            Width = 8, Height = 5, VerticalAlignment = VerticalAlignment.Center,
            [!Avalonia.Controls.Shapes.Shape.FillProperty] = new DynamicResourceExtension("TextMutedBrush"),
        };
        var line = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(name, Dock.Left);
        DockPanel.SetDock(chevron, Dock.Right);
        name.Margin = new Thickness(0, 0, 10, 0);
        chevron.Margin = new Thickness(8, 0, 0, 0);
        // The full tool, for everything this leaves out. A button, so its click doesn't drop the list down.
        var open = PanelLinks.OpenToolButton(vm.OpenWorklistCommand, EveConsole.Localization.ClientLayoutsText.WorklistOpenTool);
        DockPanel.SetDock(open, Dock.Right);
        line.Children.Add(name);
        line.Children.Add(chevron);
        line.Children.Add(open);
        line.Children.Add(counts);
        return line;
    }
}

/// <summary>Small pieces the panel controls share.</summary>
internal static class PanelLinks
{
    /// <summary>The box-and-arrow button that opens a full tool.</summary>
    public static Button OpenToolButton(System.Windows.Input.ICommand command, string tip)
    {
        var open = new Button
        {
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(4, 2),
            MinHeight = 0,
            VerticalAlignment = VerticalAlignment.Center,
            Cursor = new Cursor(StandardCursorType.Hand),
            Command = command,
            Content = new Avalonia.Controls.Shapes.Path
            {
                Data = Geometry.Parse("M7 1h4v4M11 1L5.5 6.5M9 7v4H1V3h4"),
                StrokeThickness = 1.2, Width = 12, Height = 12,
                [!Avalonia.Controls.Shapes.Shape.StrokeProperty] = new DynamicResourceExtension("TextMutedBrush"),
            },
        };
        ToolTip.SetTip(open, tip);
        return open;
    }

    /// <summary>A link-style button, for "Open …" at the foot of a dropped panel.</summary>
    public static Button TextLink(string text, Action click)
    {
        var link = new Button
        {
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(0),
            MinHeight = 0,
            Cursor = new Cursor(StandardCursorType.Hand),
            HorizontalAlignment = HorizontalAlignment.Left,
            Content = new TextBlock
            {
                Text = text, FontSize = 11, TextDecorations = TextDecorations.Underline,
                [!TextBlock.ForegroundProperty] = new DynamicResourceExtension("InfoBrush"),
            },
        };
        link.Click += (_, _) => click();
        return link;
    }
}
