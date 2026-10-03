using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Headless;
using Avalonia.LogicalTree;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Avalonia.Collections;
using EveConsole.ViewModels;
using EveConsole.Views;

// ─────────────────────────────────────────────────────────────────────────────
//  Row detail check
//
//  Does a detail popup stay with the row that is actually showing its item?
//
//  A DataGrid row that leaves the screen does not leave the tree: a scrolled-out row is recycled
//  in place, a Reset recycles every row without a word, and the focused row is parked, clipped
//  to nothing and still bound. A Popup anchored to any of them stays open at the old position
//  while the item's new row opens another — the worklist showed the same manifest twice, under
//  the row and where the row used to be, every time the clicked row moved. RowDetailPopups is
//  what keeps that from happening; this drives the real DataGrid through each way a row can go
//  stale, with and without it, and fails if the fix stops holding.
// ─────────────────────────────────────────────────────────────────────────────
//
// A DataGrid with a per-row Popup bound to IsExpanded, driven the way the worklist is driven,
// flat and grouped, with and without RowDetailPopups:
//   focused  — the expanded row was clicked (the grid's focused row), then the list is filtered
//              so the item moves: the grid parks the old row, still bound.
//   recycled — an expanded row that was NOT focused is filtered away: the grid recycles its row
//              in place, still bound.
//   returns  — the same item filtered back in: its popup must come back.
//   scrolled — an expanded row scrolled off the screen, then back.
//   reopen   — the clicked row's popup closed again, the list filtered so its item moves, and
//              the + pressed again: it must open, and stay open. The view clears the flag when a
//              popup closes while the tool is on screen, so a popup the reconciler shuts on a
//              live row loses its flag too — the + then "does nothing" until a refresh.

AppBuilder.Configure<RigApp>().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = true }).SetupWithoutStarting();

var failures = 0;
foreach (var count in new[] { 40, 60 })
foreach (var grouped in new[] { false, true })
foreach (var withFix in new[] { false, true })
{
    var r = Scenario.Run(withFix, grouped, count);
    var label = $"{count} rows {(grouped ? "grouped" : "flat   ")} {(withFix ? "with fix   " : "without fix")}";
    var ok = r.Focused == 1 && r.Recycled == 0 && r.Returns == 1 && r.ScrolledOut == 0 && r.ScrolledBack == 1 && r.Reopened == 1;
    Console.WriteLine($"{label}: focused-row filter {r.Focused} (want 1) · filtered away {r.Recycled} (want 0) · back {r.Returns} (want 1) · scrolled off {r.ScrolledOut} (want 0) · scrolled back {r.ScrolledBack} (want 1) · reopened after a filter {r.Reopened} (want 1)  {(withFix ? (ok ? "ok" : "FAIL") : (ok ? "(no fault here)" : "fault reproduced"))}");
    if (withFix && !ok) failures++;
}
Console.WriteLine(failures == 0 ? "RowDetailPopups: all cases pass" : "RowDetailPopups: FAILED");
return failures;

sealed class RigApp : Application
{
    public override void Initialize()
    {
        Styles.Add(new Avalonia.Themes.Fluent.FluentTheme());
        Styles.Add(new StyleInclude(new Uri("avares://Avalonia.Controls.DataGrid")) { Source = new Uri("avares://Avalonia.Controls.DataGrid/Themes/Fluent.xaml") });
    }
}

sealed class Row : IExpandableRow, INotifyPropertyChanged
{
    public Row(int n) { ExpandKey = $"row:{n}"; Name = $"Item {n}"; Group = n % 3 == 0 ? "Buy" : "Job"; }
    public string ExpandKey { get; }
    public string Name { get; }
    public string Group { get; }
    private bool _isExpanded;
    public bool IsExpanded { get => _isExpanded; set { if (_isExpanded == value) return; _isExpanded = value; PropertyChanged?.Invoke(this, new(nameof(IsExpanded))); } }
    public event PropertyChangedEventHandler? PropertyChanged;
}

static class Scenario
{
    public static (int Focused, int Recycled, int Returns, int ScrolledOut, int ScrolledBack, int Reopened) Run(bool withFix, bool grouped, int count = 60)
    {
        var items = Enumerable.Range(0, count).Select(n => new Row(n)).ToList();
        var rows  = new BulkObservableCollection<Row>();
        rows.ResetTo(items);
        var view  = new DataGridCollectionView(rows);
        if (grouped) view.GroupDescriptions.Add(new DataGridPathGroupDescription(nameof(Row.Group)));

        var details = new RowDetailPopups();
        var grid = new DataGrid { ItemsSource = view, AutoGenerateColumns = false, IsReadOnly = true, Height = 400, Width = 600 };
        grid.Columns.Add(new DataGridTemplateColumn
        {
            Header = "Task", Width = new DataGridLength(400),
            CellTemplate = new FuncDataTemplate<Row>((_, _) =>
            {
                var panel = new StackPanel();
                var text  = new TextBlock();
                text.Bind(TextBlock.TextProperty, new Binding(nameof(Row.Name)));
                var popup = new Popup { Placement = PlacementMode.BottomEdgeAlignedLeft, Child = new Border { Width = 200, Height = 80 } };
                popup.Bind(Popup.IsOpenProperty, new Binding(nameof(Row.IsExpanded)));
                // As the worklist does: a popup closed while the view is on screen clears the flag.
                popup.Closed += (_, _) =>
                {
                    if (details.Shutting) return;
                    if (popup.DataContext is Row { } r && popup.GetVisualRoot() is not null
                        && ((ILogical)popup).IsAttachedToLogicalTree) r.IsExpanded = false;
                };
                panel.Children.Add(text);
                panel.Children.Add(popup);
                return panel;
            }),
        });

        if (withFix)
        {
            grid.LoadingRow   += (_, e) => details.RowLoaded(grid, e.Row);
            grid.UnloadingRow += (_, e) => details.RowUnloaded(e.Row);
        }

        var window = new Window { Width = 700, Height = 500, Content = grid };
        if (withFix) window.LayoutUpdated += (_, _) => details.Reconcile();
        window.Show();
        Pump();

        // Displayed at the top in both layouts: multiples of three lead the grouped view.
        Row a = items[6], b = items[9], c = items[3], far = items[count - 3], farB = items[count - 6], farC = items[count - 9];

        // 1. Click row A — focus lands in it, making it the grid's focused row — expand, filter.
        RowFor(grid, a).Focus();
        grid.SelectedItem = a;
        Pump();
        a.IsExpanded = true;
        Pump();
        rows.ResetTo([a, b]);
        Pump();
        var focused = OpenPopups(window);

        // 2. Fresh list; expand C without focusing it, then filter it out altogether.
        a.IsExpanded = false;
        rows.ResetTo(items);
        Pump();
        c.IsExpanded = true;
        Pump();
        rows.ResetTo([far, farB, farC]);
        Pump();
        var recycled = OpenPopups(window);

        // 3. And back in.
        rows.ResetTo([c, far]);
        Pump();
        var returns = OpenPopups(window);

        // 4. Full list again with C expanded near the top; scroll to the bottom and back.
        rows.ResetTo(items);
        Pump();
        grid.ScrollIntoView(items[count - 1], null);
        Pump();
        var scrolledOut = OpenPopups(window);
        grid.ScrollIntoView(items[0], null);
        Pump();
        var scrolledBack = OpenPopups(window);

        // 5. Click A's + (focus in its row), close it with the + again, filter so A moves, and
        //    press its + once more.
        c.IsExpanded = false;
        rows.ResetTo(items);
        Pump();
        RowFor(grid, a).Focus();
        grid.SelectedItem = a;
        Pump();
        a.IsExpanded = true;
        Pump();
        a.IsExpanded = false;
        Pump();
        rows.ResetTo([b, c, a]);
        Pump();
        a.IsExpanded = true;
        Pump();
        var reopened = a.IsExpanded ? OpenPopups(window) : 0;

        window.Close();
        Pump();
        return (focused, recycled, returns, scrolledOut, scrolledBack, reopened);
    }

    static DataGridRow RowFor(DataGrid grid, Row item)
        => grid.GetVisualDescendants().OfType<DataGridRow>().First(r => ReferenceEquals(r.DataContext, item));

    static int OpenPopups(Window w) => w.GetVisualDescendants().OfType<Popup>().Count(p => p.IsOpen);

    static void Pump()
    {
        for (var i = 0; i < 5; i++)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        }
    }
}
