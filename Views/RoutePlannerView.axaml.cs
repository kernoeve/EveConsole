using Avalonia.Controls;
using Avalonia.VisualTree;
using EveConsole.Localization;
using EveConsole.Services;
using EveConsole.ViewModels;

namespace EveConsole.Views;

public partial class RoutePlannerView : UserControl
{
    public RoutePlannerView() => InitializeComponent();

    /// <summary>
    /// A right-click on a step of the route: put its system on the avoid list, or take it off —
    /// whichever it is not. Built when asked, so the words match the list as it is now.
    /// </summary>
    private void OnStepContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (e.Source is not Control source) return;
        var row = source.GetSelfAndVisualAncestors().OfType<DataGridRow>().FirstOrDefault();
        if (row?.DataContext is not RouteStepRowVm step) return;

        var avoided = RouteAvoidList.Contains(step.Step.SystemId);
        var item = new MenuItem
        {
            Header = string.Format(avoided ? MapText.AvoidRemove : MapText.AvoidAdd, step.Label),
        };
        item.Click += (_, _) => RouteAvoidList.Toggle(step.Step.SystemId);
        new ContextMenu { ItemsSource = new[] { item } }.Open(row);
        e.Handled = true;
    }
}
