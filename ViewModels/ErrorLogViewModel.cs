using System.Collections.ObjectModel;
using System.Globalization;
using System.Reactive;
using EveConsole.Data;
using EveConsole.Models;
using EveConsole.Services;
using Microsoft.EntityFrameworkCore;
using ReactiveUI;

namespace EveConsole.ViewModels;

// One row in the Error Log viewer.
public class ErrorLogRowVm
{
    public DateTimeOffset OccurredAt { get; }
    public string TimeText { get; }
    public long   TimeSort { get; }
    public string Source   { get; }
    public string Context  { get; }
    public string Message  { get; }
    public string Inner    { get; }

    /// <summary>Which client wrote the row. Blank on anything logged before the columns existed.</summary>
    public string Client   { get; }

    // Combined message shown in the detail pane.
    public string Detail => Inner.Length > 0 ? $"{Message}\n\nInner: {Inner}" : Message;

    public ErrorLogRowVm(AppErrorEntry e)
    {
        OccurredAt = e.OccurredAt;
        TimeText   = e.OccurredAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
        TimeSort   = e.OccurredAt.UtcTicks;
        Source     = e.Source;
        Context    = e.Context;
        Message    = e.Message;
        Inner      = e.InnerMessage ?? "";

        // ⚠️ Host and kind together, as one column. Either alone is ambiguous: one machine can run
        // a desktop client and the worker at once, and "headless" says nothing about where.
        Client     = e.HostName.Length == 0 ? ""
                   : e.Headless            ? $"{e.HostName} (worker)"
                   :                          e.HostName;
    }
}

// Viewer over the AppErrorLog table, filterable by an occurred-at date range (defaults to the
// last 24 hours). Errors are low-volume, so the whole filtered set is loaded (capped) each time.
public class ErrorLogViewModel : ReactiveObject
{
    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly AppErrorLogger                  _errorLogger;

    public ObservableCollection<ErrorLogRowVm> Rows { get; } = new();

    private string _dateFrom;
    public string DateFrom { get => _dateFrom; set { this.RaiseAndSetIfChanged(ref _dateFrom, value); _ = LoadAsync(); } }

    private string _dateThru = "";
    public string DateThru { get => _dateThru; set { this.RaiseAndSetIfChanged(ref _dateThru, value); _ = LoadAsync(); } }

    private ErrorLogRowVm? _selected;
    public ErrorLogRowVm? Selected { get => _selected; set => this.RaiseAndSetIfChanged(ref _selected, value); }

    private string _statusText = "";
    public string StatusText { get => _statusText; private set => this.RaiseAndSetIfChanged(ref _statusText, value); }

    public ReactiveCommand<Unit, Unit> RefreshCommand { get; }

    private bool _isLoading;

    public ErrorLogViewModel(IDbContextFactory<AppDbContext> dbFactory, AppErrorLogger errorLogger)
    {
        _dbFactory   = dbFactory;
        _errorLogger = errorLogger;
        _dateFrom    = DateTime.Now.AddHours(-24).ToString("yyyy-MM-dd HH:mm");   // last 24 hours

        RefreshCommand = ReactiveCommand.Create(() => { _ = LoadAsync(); });

        // ⚠️ Deliberately NOT loaded here. Constructing this at startup meant opening the Error Log
        // showed a list read when the application launched — which looks current, is not, and is
        // worse than an empty grid because nothing about it says so. MainWindowViewModel.OpenTool
        // calls Reload when the tab is opened.
    }

    /// <summary>
    /// Reads the log. Called when the tool is opened, including when it is closed and opened again.
    ///
    /// <para>Not called when returning to a tab that was already open: the list is then as old as
    /// the moment it was opened, which is what the Refresh button is for, and re-reading five
    /// thousand rows every time somebody passes through the tab is a cost with no reader.</para>
    /// </summary>
    public void Reload() => _ = LoadAsync();

    private async Task LoadAsync()
    {
        if (_isLoading) return;
        _isLoading = true;
        StatusText = "Loading…";
        try
        {
            // DateTimeOffset can't be compared in a LINQ Where against SQLite, so filter in raw SQL
            // with DateTimeOffset parameters — EF converts them to the stored text format.
            var parts = new List<string>();
            var ps    = new List<object>();
            if (TryDate(_dateFrom, out var from))
            { int i = ps.Count; ps.Add(from); parts.Add($"\"OccurredAt\" >= {{{i}}}"); }
            if (TryDate(_dateThru, out var thru))
            { int i = ps.Count; ps.Add(thru); parts.Add($"\"OccurredAt\" < {{{i}}}"); }
            var where = parts.Count > 0 ? "WHERE " + string.Join(" AND ", parts) : "";

            await using var db = await _dbFactory.CreateDbContextAsync();
#pragma warning disable EF1002
            var list = await db.AppErrors.FromSqlRaw(
                    $"SELECT * FROM \"AppErrorLog\" {where} ORDER BY \"OccurredAt\" DESC LIMIT 5000", ps.ToArray())
                .AsNoTracking().ToListAsync();
#pragma warning restore EF1002

            Rows.Clear();
            foreach (var e in list) Rows.Add(new ErrorLogRowVm(e));
            StatusText = list.Count == 0 ? "No errors in range." : $"{list.Count:N0} error(s)";
        }
        catch (Exception ex)
        {
            _errorLogger.Log("ErrorLogViewModel", "Load", ex);
            StatusText = AppErrorLogger.Line("Error loading log", ex);
        }
        finally { _isLoading = false; }
    }

    // Accepts a plain date or date+time, interpreted as local time.
    // ⚠️ Typed as local, compared as UTC. The rows are written with DateTimeOffset.UtcNow, and on
    // SQLite the comparison is between TEXT — "2026-09-11 13:00:00-06:00" against a stored
    // "2026-09-11 19:00:00+00:00" — where the offset is just more characters. A local-offset
    // parameter therefore matched from the wrong hour: the whole local offset's worth of rows
    // either side of the boundary. Sent as UTC it has the stored shape and the text order is the
    // time order. PostgreSQL compares typed values and never minded.
    private static bool TryDate(string s, out DateTimeOffset dt)
    {
        if (!string.IsNullOrWhiteSpace(s) &&
            DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var d))
        { dt = new DateTimeOffset(d).ToUniversalTime(); return true; }
        dt = default; return false;
    }
}
