using Avalonia.Controls;
using EveConsole.ViewModels;

namespace EveConsole.Views;

/// <summary>Live intel on a panel, as a list. See <see cref="PanelIntelVm"/>.</summary>
public partial class PanelIntelControl : UserControl
{
    public PanelIntelControl() => InitializeComponent();

    /// <summary>False when the list drops down from a single line, which starts and stops the view model itself.</summary>
    public bool ManagesLifetime { get; init; } = true;

    protected override void OnAttachedToVisualTree(Avalonia.VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (ManagesLifetime) (DataContext as PanelIntelVm)?.Start();
    }

    protected override void OnDetachedFromVisualTree(Avalonia.VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        if (ManagesLifetime) (DataContext as PanelIntelVm)?.Dispose();
    }
}
