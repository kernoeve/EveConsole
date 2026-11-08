using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using EveConsole.Localization;
using EveConsole.ViewModels;

namespace EveConsole.Views;

/// <summary>
/// The conversation with the AI companion, as on the right of the main window: the same one, so
/// what is said here shows there too. While the companion is turned off, it says so.
/// </summary>
public sealed class PanelAgentControl : Panel
{
    public PanelAgentControl(MainWindowViewModel main)
    {
        this[!BackgroundProperty] = new DynamicResourceExtension("SurfaceBaseBrush");

        Children.Add(new AgentPanel
        {
            DataContext = main.AgentVm,
            ShowsClose = false,
            FocusesInput = false,
            [!IsVisibleProperty] = new Binding(nameof(AgentPanelViewModel.IsAgentEnabled)) { Source = main.AgentVm },
        });
        Children.Add(new TextBlock
        {
            Text = ClientLayoutsText.AgentOff,
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(10),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            [!IsVisibleProperty] = new Binding("!" + nameof(AgentPanelViewModel.IsAgentEnabled)) { Source = main.AgentVm },
            [!TextBlock.ForegroundProperty] = new DynamicResourceExtension("TextFaintBrush"),
        });

        // Clicked into, it can be typed in, as a Tool Container's tool can.
        AddHandler(PointerPressedEvent, (_, _) => (TopLevel.GetTopLevel(this) as ClientPanelWindow)?.TakeFocus(),
            RoutingStrategies.Tunnel, handledEventsToo: true);
    }
}
