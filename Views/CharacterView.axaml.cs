using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Threading;
using Avalonia.VisualTree;
using EveConsole.Models;
using EveConsole.ViewModels;
using ReactiveUI;

namespace EveConsole.Views;

public partial class CharacterView : UserControl
{
    private IDisposable? _vmSub;
    private OverviewViewModel? _vm;

    public CharacterView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        _vmSub?.Dispose();
        if (_vm is not null) _vm.LayoutChanged -= RebuildLayout;

        if (DataContext is not OverviewViewModel vm) { _vm = null; return; }
        _vm = vm;
        _vm.LayoutChanged += RebuildLayout;
        RebuildLayout();

        // LiveCharts doesn't re-render when IsVisible flips false→true without a nudge.
        // Double-post so the invalidate runs after the layout pass triggered by the IsVisible
        // binding, not during it (which would result in a 0×0 chart).
        _vmSub = vm.WhenAnyValue(x => x.HasIncomeData, x => x.HasExpenseData)
            .Subscribe(_ =>
            {
                Dispatcher.UIThread.Post(() =>
                    Dispatcher.UIThread.Post(() =>
                    {
                        IncomeChart?.InvalidateMeasure();
                        IncomeChart?.InvalidateVisual();
                        ExpenseChart?.InvalidateMeasure();
                        ExpenseChart?.InvalidateVisual();
                    }));
            });
    }

    private Border? SectionFor(string key) => key switch
    {
        "ActivitySummary"   => SectionActivitySummary,
        "Alerts"            => SectionAlerts,
        "Notifications"     => SectionNotifications,
        "News"              => SectionNews,
        "PersonalKillmails" => SectionPersonalKillmails,
        "SaleListingBuild"  => SectionSaleListingBuild,
        "SaleListingMarket" => SectionSaleListingMarket,
        "IncomePie"         => SectionIncomePie,
        "ExpensePie"        => SectionExpensePie,
        "IncomeExpense"     => SectionIncomeExpense,
        "StandingProjects"  => SectionStandingProjects,
        "StandingBuyOrders" => SectionStandingBuyOrders,
        "WorklistAll"       => SectionWorklistAll,
        "WorklistBuy"       => SectionWorklistBuy,
        "WorklistHaul"      => SectionWorklistHaul,
        "WorklistJobs"      => SectionWorklistJobs,
        "WorklistNeeds"     => SectionWorklistNeeds,
        _                   => null,
    };

    // Rebuilds the LayoutHost grid from the current layout, moving section controls into it.
    private void RebuildLayout()
    {
        if (_vm is null || LayoutHost is null || SectionStore is null) return;
        var layout = _vm.Layout;

        // Detach every section from wherever it currently lives.
        foreach (var (key, _) in OverviewLayout.KnownSections)
        {
            if (SectionFor(key) is { } b)
            {
                LayoutHost.Children.Remove(b);
                SectionStore.Children.Remove(b);
            }
        }

        int rows = Math.Max(1, layout.Rows);
        int cols = Math.Max(1, layout.Cols);

        LayoutHost.RowDefinitions.Clear();
        LayoutHost.ColumnDefinitions.Clear();
        for (int r = 0; r < rows; r++)
            LayoutHost.RowDefinitions.Add(new RowDefinition(1, GridUnitType.Star));
        for (int c = 0; c < cols; c++)
            LayoutHost.ColumnDefinitions.Add(new ColumnDefinition(1, GridUnitType.Star));

        foreach (var p in layout.Sections)
        {
            if (!p.Enabled) continue;
            if (SectionFor(p.Key) is not { } b) continue;

            int row     = Math.Clamp(p.Row - 1, 0, rows - 1);
            int col     = Math.Clamp(p.Col - 1, 0, cols - 1);
            int rowSpan = Math.Clamp(p.RowSpan, 1, rows - row);
            int colSpan = Math.Clamp(p.ColSpan, 1, cols - col);

            Grid.SetRow(b, row);
            Grid.SetColumn(b, col);
            Grid.SetRowSpan(b, rowSpan);
            Grid.SetColumnSpan(b, colSpan);
            b.Margin = new Thickness(6);
            b.SizeChanged -= OnSectionSizeChanged;
            b.SizeChanged += OnSectionSizeChanged;
            LayoutHost.Children.Add(b);
        }

        // Nudge everything that was just (re)placed to measure again.
        //
        // ⚠️ Reparenting is what makes this necessary. A section is measured wherever it happens
        // to be sitting, and SectionStore is a full-width panel while a layout cell is a fraction
        // of that — so a control that sizes itself from the width it was last given keeps the
        // wrong one. LiveCharts showed this first by measuring to 0×0. The double Post is
        // deliberate: the first gets past the layout pass this method triggers, the second past
        // the one that settles it.
        Dispatcher.UIThread.Post(() =>
            Dispatcher.UIThread.Post(() =>
            {
                IncomeChart?.InvalidateMeasure();
                IncomeChart?.InvalidateVisual();
                ExpenseChart?.InvalidateMeasure();
                ExpenseChart?.InvalidateVisual();

                foreach (var child in LayoutHost.Children)
                    ReMeasureGrids(child);
            }));
    }

    /// <summary>
    /// Re-measures a card's grids whenever the card's own size changes.
    ///
    /// <para>⚠️ Tied to the size actually changing, not done once after placement. The one-shot
    /// version was not enough: the sale listing still came up with its last columns hanging off
    /// the card until the window was resized by hand, which proves the card reaches its final
    /// width AFTER the nudge rather than before it. Rather than guess how many layout passes that
    /// takes, this reacts to the event that says it happened — the same event a manual resize
    /// raises, which is why resizing was the workaround.</para>
    ///
    /// <para>Cheap: a DataGrid whose width did not really change re-measures to the same widths
    /// and draws nothing new.</para>
    /// </summary>
    private void OnSectionSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        if (e.WidthChanged && sender is Layoutable l) ReMeasureGrids(l);
    }

    /// <summary>
    /// Forces a card's grids to work out their column widths again.
    ///
    /// <para>⚠️ Invalidating measure is not enough, which is why two earlier attempts at this
    /// changed nothing. A DataGrid keeps the column widths it computed from the width it was first
    /// measured at, and re-measuring against an unchanged constraint reuses them — so a grid
    /// measured wide and then placed in a narrower card keeps its wide columns, and the last ones
    /// sit outside the card, where ClipToBounds hides them. Dragging the window worked only
    /// because that changes the constraint for real.</para>
    ///
    /// <para>Reassigning <c>Width</c> is what clears a column's computed value. The throwaway
    /// assignment first is deliberate: the property short-circuits on an unchanged value, so
    /// writing the same length straight back would do nothing. Both writes land before the
    /// invalidation, so no layout pass falls between them to draw the interim widths.</para>
    /// </summary>
    private static void ReMeasureGrids(Layoutable section)
    {
        section.InvalidateMeasure();
        foreach (var grid in section.GetVisualDescendants().OfType<DataGrid>())
        {
            foreach (var col in grid.Columns)
            {
                var width = col.Width;
                col.Width = new DataGridLength(1, DataGridLengthUnitType.Auto);
                col.Width = width;
            }

            grid.InvalidateMeasure();
            grid.InvalidateArrange();
        }
    }

    private async void OnCustomizeClick(object? sender, RoutedEventArgs e)
    {
        if (_vm is null) return;
        var dlg = new OverviewCustomizeWindow
        {
            DataContext = new OverviewCustomizeViewModel(_vm.Layout),
        };
        var result = await dlg.ShowDialog<OverviewLayout?>(GetWindow());
        if (result is not null)
            await _vm.ApplyLayoutAsync(result);
    }

    /// <summary>
    /// Double-click a killmail row to open it in the Killmail tool.
    ///
    /// <para>⚠️ Walks up from what was actually tapped rather than reading SelectedItem. The rows
    /// carry links now, and a click landing on one of those buttons does not select the row it
    /// sits in — so the selection would be the previous row, or none.</para>
    /// </summary>
    private void OnKillmailDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (_vm is null) return;

        for (var c = e.Source as Control; c is not null; c = c.Parent as Control)
            if (c.DataContext is Activity24hKillRowVm row)
            {
                _vm.RequestOpenKillmail?.Invoke(row.KillMailId);
                return;
            }
    }

    // The row carries its own navigation, so the button's DataContext is all this needs.
    private void OnOpenProjectItem(object? sender, RoutedEventArgs e)
        => ((sender as Control)?.DataContext as StandingProjectRowVm)?.OpenItem();

    private void OnOpenBuyOrderItem(object? sender, RoutedEventArgs e)
        => ((sender as Control)?.DataContext as StandingBuyOrderRowVm)?.OpenItem();

    // ── Personal killmails ────────────────────────────────────────────────────
    //
    // The same row type Corp Activity renders, so the links are the row's own methods and these
    // are pure dispatch. Six entities plus the system and its region.
    private void OnOpenKillVictim(object? sender, RoutedEventArgs e)         => Kill(sender)?.OpenVictim();
    private void OnOpenKillVictimCorp(object? sender, RoutedEventArgs e)     => Kill(sender)?.OpenVictimCorp();
    private void OnOpenKillVictimAlliance(object? sender, RoutedEventArgs e) => Kill(sender)?.OpenVictimAlliance();
    private void OnOpenKillFb(object? sender, RoutedEventArgs e)             => Kill(sender)?.OpenFb();
    private void OnOpenKillFbCorp(object? sender, RoutedEventArgs e)         => Kill(sender)?.OpenFbCorp();
    private void OnOpenKillFbAlliance(object? sender, RoutedEventArgs e)     => Kill(sender)?.OpenFbAlliance();
    private void OnOpenKillSystem(object? sender, RoutedEventArgs e)         => Kill(sender)?.OpenSystem();
    private void OnOpenKillRegion(object? sender, RoutedEventArgs e)         => Kill(sender)?.OpenRegion();

    private static Activity24hKillRowVm? Kill(object? sender)
        => (sender as Control)?.DataContext as Activity24hKillRowVm;

    private void OnOpenNotifications(object? sender, RoutedEventArgs e)
        => _vm?.OpenToolRequested?.Invoke("notifications");

    private void OnOpenKillmailsTool(object? sender, RoutedEventArgs e)
        => _vm?.OpenToolRequested?.Invoke("killmails");

    private void OnOpenAlertSettings(object? sender, RoutedEventArgs e)
        => _vm?.OpenAlertSettingsRequested?.Invoke();

    private void OnOpenStandingProjects(object? sender, RoutedEventArgs e)
        => _vm?.NavigateToStandingProjects?.Invoke();

    private void OnOpenStandingBuyOrders(object? sender, RoutedEventArgs e)
        => _vm?.NavigateToStandingBuyOrders?.Invoke();

    /// <summary>Expands or collapses a task's manifest, the same gesture the tool uses.</summary>
    private void OnOverviewManifestToggle(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control control) return;
        if (control.FindAncestorOfType<DataGridRow>() is not { } row) return;

        row.AreDetailsVisible = !row.AreDetailsVisible;

        // The glyph lives on the item so it stays correct when the row is recycled.
        if (row.DataContext is WorklistRowVm vm) vm.IsExpanded = row.AreDetailsVisible;
    }

    private void OnOpenManifestItem(object? sender, RoutedEventArgs e)
        => ((sender as Control)?.DataContext as EveConsole.Services.Worklist.WorklistLine)?.OpenItem();

    private void OnOpenNeedItem(object? sender, RoutedEventArgs e)
        => ((sender as Control)?.DataContext as StationNeedRowVm)?.OpenItem();

    private void OnOpenWorklist(object? sender, RoutedEventArgs e)
        => _vm?.OpenToolRequested?.Invoke("worklist");

    // Opens the tool AND selects its Station Needs tab: a link named "Station Needs" that lands on
    // whatever tab was last open has not gone where it said.
    //
    // ⚠️ Posted, not set inline. Opening the tool creates the WorklistView, whose TabControl then
    // binds SelectedIndex two-way and writes its own default (0) back over anything set before it
    // existed. Running at Background priority puts the selection after that first bind.
    private void OnOpenWorklistNeeds(object? sender, RoutedEventArgs e)
    {
        // Requested BEFORE opening, so a view created by the open call picks it up on load; a view
        // that already exists is handled by the second call.
        _vm?.Worklist?.RequestStationNeedsTab();
        _vm?.OpenToolRequested?.Invoke("worklist");
        _vm?.Worklist?.ShowStationNeedsTab();
    }

    /// <summary>
    /// Shows as many Station Needs columns as the panel can actually fit.
    ///
    /// <para>⚠️ Width-driven rather than a fixed set. This section can be a third of a three-column
    /// dashboard or span the whole window, and one column list cannot serve both: wide, it wastes
    /// the space; narrow, it squeezes out the three columns the panel exists for.</para>
    ///
    /// <para>The order is the answer to "what do I drop first". Station, Item and Short are the
    /// question itself and never go. Then the two that give the shortfall context (Total, On hand),
    /// then the four that say where the demand came from, then the two that price and size it —
    /// those last are the most easily read in the tool instead.</para>
    /// </summary>
    private void OnNeedsGridSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        if (sender is not DataGrid grid) return;

        var w = e.NewSize.Width;

        // Thresholds are cumulative: each tier adds roughly the width its columns occupy, so a
        // column appears only once there is room for it rather than the moment it would fit.
        var showContext  = w >= 520;   // Total, On hand
        var showSources  = w >= 860;   // Order jobs, Jobs, Inv levels, Stn levels
        var showValuation = w >= 1080; // Short value, Short volume

        foreach (var c in grid.Columns)
        {
            c.IsVisible = c.Header as string switch
            {
                "Total" or "On hand"                                  => showContext,
                "Order jobs" or "Jobs" or "Inv levels" or "Stn levels" => showSources,
                "Short value" or "Short volume"                        => showValuation,
                _                                                     => true,   // Station, Item, Short
            };
        }
    }

    private Window GetWindow() => (TopLevel.GetTopLevel(this) as Window)!;
}
