using System.Collections.ObjectModel;
using System.Globalization;
using System.Reactive;
using System.Text;
using ReactiveUI;

namespace EveConsole.ViewModels;

/// <summary>
/// One row of an agent-built grid.
///
/// <para>⚠️ Indexed rather than typed, because the shape is not known until the agent decides it.
/// The grid's columns are whatever the agent asked for, so its rows cannot be a class with
/// properties — the DataGrid columns are built at runtime and bind to <c>[0]</c>, <c>[1]</c> and
/// so on against this indexer.</para>
///
/// <para>Out-of-range reads return empty rather than throwing: a ragged row from the agent should
/// leave a blank cell, not take down the tab it was meant to fill.</para>
/// </summary>
public sealed class AgentGridRow(string[] cells)
{
    public string[] Cells { get; } = cells;

    public string this[int index] =>
        index >= 0 && index < Cells.Length ? Cells[index] : "";
}

/// <summary>
/// A grid the agent filled in, shown as its own tab.
///
/// <para>The point of it is what does NOT happen: rows shown here are not in the conversation, so
/// they do not consume the chat history the next turn has to carry, and they are not read out
/// loud. A fifty-row answer becomes a sentence and a tab.</para>
///
/// <para>⚠️ One instance per invocation. Every other tool in the application is a singleton
/// reached from the nav — asking twice returns to the same screen. These are answers, and a new
/// answer must not overwrite the previous one, so each call opens a tab of its own.</para>
/// </summary>
public sealed class AgentGridViewModel : ReactiveObject
{
    public string   Title   { get; }
    public string[] Columns { get; }

    public ObservableCollection<AgentGridRow> Rows { get; } = [];

    /// <summary>What the agent said the grid is, shown above it. Empty when it said nothing.</summary>
    public string Caption { get; }
    public bool   HasCaption => Caption.Length > 0;

    public string RowCountText => Rows.Count == 1 ? "1 row" : $"{Rows.Count:N0} rows";

    private string _statusText = "";
    public string StatusText
    {
        get => _statusText;
        set => this.RaiseAndSetIfChanged(ref _statusText, value);
    }

    /// <summary>
    /// Rows the capsuleer has selected, kept in sync by the view.
    ///
    /// <para>Bound rather than read from the grid on demand so the copy commands can be driven
    /// from the keyboard and the toolbar without either one reaching into the control.</para>
    /// </summary>
    public ObservableCollection<AgentGridRow> Selected { get; } = [];

    public ReactiveCommand<Unit, Unit> CopyCommand    { get; }
    public ReactiveCommand<Unit, Unit> CopyAllCommand { get; }

    /// <summary>Set by the view, which owns the clipboard and the file picker.</summary>
    public Func<string, Task>? CopyToClipboard { get; set; }
    public Func<string, string, Task<string?>>? SaveTextFile { get; set; }

    public AgentGridViewModel(string title, string caption, string[] columns, IEnumerable<string[]> rows)
    {
        Title   = title;
        Caption = caption;
        Columns = columns;
        foreach (var r in rows) Rows.Add(new AgentGridRow(r));

        CopyCommand    = ReactiveCommand.CreateFromTask(() => CopyAsync(selectedOnly: true));
        CopyAllCommand = ReactiveCommand.CreateFromTask(() => CopyAsync(selectedOnly: false));
    }

    /// <summary>
    /// Puts the grid on the clipboard as tab-separated text.
    ///
    /// <para>⚠️ Tabs, not commas, because the destination is a spreadsheet. Excel splits pasted
    /// text on tabs unconditionally; whether it splits on commas depends on the machine's list
    /// separator, so a CSV pasted on a European install lands in a single column. The file that
    /// the save button writes is a real CSV — that path goes through an import, and this one does
    /// not.</para>
    /// </summary>
    private async Task CopyAsync(bool selectedOnly)
    {
        if (CopyToClipboard is null) return;

        var rows = selectedOnly && Selected.Count > 0
            ? Rows.Where(Selected.Contains).ToList()   // grid order, not click order
            : Rows.ToList();

        var sb = new StringBuilder();
        sb.AppendLine(string.Join('\t', Columns.Select(Flatten)));
        foreach (var r in rows)
            sb.AppendLine(string.Join('\t', Columns.Select((_, i) => Flatten(r[i]))));

        await CopyToClipboard(sb.ToString());
        StatusText = $"Copied {rows.Count:N0} row(s) with headers.";
    }

    /// <summary>
    /// A cell as one line, so a value containing a tab or a newline cannot shift every column
    /// after it into the wrong place when pasted.
    /// </summary>
    private static string Flatten(string value) =>
        value.Replace('\t', ' ').Replace("\r\n", " ").Replace('\n', ' ').Replace('\r', ' ');

    public async Task SaveCsvAsync()
    {
        if (SaveTextFile is null) return;

        var sb = new StringBuilder();
        sb.AppendLine(string.Join(',', Columns.Select(Csv)));
        foreach (var r in Rows)
            sb.AppendLine(string.Join(',', Columns.Select((_, i) => Csv(r[i]))));

        var suggested = SafeFileName(Title) + ".csv";
        var saved     = await SaveTextFile(suggested, sb.ToString());
        StatusText    = saved is null ? "" : $"Saved {Rows.Count:N0} row(s) to {saved}";
    }

    /// <summary>
    /// ⚠️ RFC 4180 quoting, not a bare join. A ship name with a comma in it, or a note containing
    /// a quotation mark, silently corrupts every column after it otherwise — and item and
    /// character names in EVE contain both.
    /// </summary>
    private static readonly System.Buffers.SearchValues<char> CsvSpecials =
        System.Buffers.SearchValues.Create(",\"\n\r");

    private static string Csv(string value)
    {
        if (value.Length == 0) return "";
        var needsQuotes = value.AsSpan().IndexOfAny(CsvSpecials) >= 0;
        return needsQuotes ? '"' + value.Replace("\"", "\"\"") + '"' : value;
    }

    /// <summary>The title as a filename, since the agent chose it and may have used anything.</summary>
    internal static string SafeFileName(string title)
    {
        var cleaned = new string(title
            .Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '-' : c)
            .ToArray()).Trim();
        return cleaned.Length == 0 ? "export" : cleaned;
    }
}

/// <summary>
/// A formatted document the agent wrote, shown as its own tab.
///
/// <para>The same reasoning as <see cref="AgentGridViewModel"/>, for prose rather than a table: a
/// report belongs somewhere it can be read, kept and saved, rather than scrolled past in a chat
/// panel and read aloud a paragraph at a time.</para>
/// </summary>
public sealed class AgentDocumentViewModel : ReactiveObject
{
    public string Title    { get; }
    public string Markdown { get; }

    public string SubtitleText { get; }
    public bool   HasSubtitle  => SubtitleText.Length > 0;

    private string _statusText = "";
    public string StatusText
    {
        get => _statusText;
        set => this.RaiseAndSetIfChanged(ref _statusText, value);
    }

    public ReactiveCommand<Unit, Unit> CopyCommand { get; }

    public Func<string, Task>? CopyToClipboard { get; set; }
    public Func<string, string, Task<string?>>? SaveTextFile { get; set; }

    public AgentDocumentViewModel(string title, string markdown)
    {
        Title    = title;
        Markdown = markdown;
        SubtitleText = DateTimeOffset.Now.ToString("d MMMM yyyy, HH:mm", CultureInfo.CurrentCulture);

        CopyCommand = ReactiveCommand.CreateFromTask(async () =>
        {
            if (CopyToClipboard is null) return;
            // The source, not the rendering. What is on screen is one presentation of this text;
            // the markdown is what pastes usefully into anything else.
            await CopyToClipboard(Markdown);
            StatusText = "Copied the document as Markdown.";
        });
    }

    public async Task SaveMarkdownAsync()
    {
        if (SaveTextFile is null) return;
        var saved  = await SaveTextFile(AgentGridViewModel.SafeFileName(Title) + ".md", Markdown);
        StatusText = saved is null ? "" : $"Saved to {saved}";
    }
}
