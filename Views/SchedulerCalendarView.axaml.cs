using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using EveConsole.ViewModels;

namespace EveConsole.Views;

public partial class SchedulerCalendarView : UserControl
{
    /// <summary>
    /// Keeps the calendar honest while it is on screen: runs that have passed go, and the day
    /// rolls over. ⚠️ Only while attached, so a closed tab costs nothing.
    /// </summary>
    private readonly DispatcherTimer _clock = new() { Interval = TimeSpan.FromMinutes(1) };

    public SchedulerCalendarView()
    {
        InitializeComponent();

        _clock.Tick += (_, _) => (DataContext as SchedulerCalendarViewModel)?.Rebuild();

        // How many chips fit in a day depends on how tall the grid is.
        MonthGrid.SizeChanged += (_, e) =>
            (DataContext as SchedulerCalendarViewModel)?.SetMonthHeight(e.NewSize.Height);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        (DataContext as SchedulerCalendarViewModel)?.Rebuild();
        _clock.Start();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _clock.Stop();
    }
}
