using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reactive;
using ReactiveUI;

namespace EveConsole.ViewModels;

// One editable post block in the Add/Edit Posting dialog.
public class PostBlockRow : ReactiveObject
{
    // Options live on the row so each ComboBox binds to its own DataContext (a relative
    // $parent[Window] binding is fragile when the item container recycles on reorder).
    public IReadOnlyList<string> PostTypeOptions { get; } = ["Summary", "Detail", "Static"];

    private string _postType;
    public string PostType
    {
        get => _postType;
        set
        {
            // Ignore transient null/empty writes — a recycling ComboBox can momentarily reset
            // SelectedItem to null (before its items are ready) and clobber a valid type otherwise.
            if (string.IsNullOrEmpty(value)) return;
            this.RaiseAndSetIfChanged(ref _postType, value);
            this.RaisePropertyChanged(nameof(IsStatic));
            this.RaisePropertyChanged(nameof(ShowHeaderFooter));
            this.RaisePropertyChanged(nameof(HeaderColorLabel));
        }
    }
    public bool IsStatic => _postType == "Static";
    public bool ShowHeaderFooter => !IsStatic;   // Summary / Detail carry a header + footer

    private string _name;
    public string Name { get => _name; set => this.RaiseAndSetIfChanged(ref _name, value); }

    private string? _staticContent;
    public string? StaticContent { get => _staticContent; set => this.RaiseAndSetIfChanged(ref _staticContent, value); }

    private string _header;
    public string Header { get => _header; set => this.RaiseAndSetIfChanged(ref _header, value); }

    private string _footer;
    public string Footer { get => _footer; set => this.RaiseAndSetIfChanged(ref _footer, value); }

    /// <summary>Colour for the header text — or, on a Static block, for all of its content.
    /// Empty for none, and only EVE mail shows it.</summary>
    private string _headerColor;
    public string HeaderColor { get => _headerColor; set => this.RaiseAndSetIfChanged(ref _headerColor, value); }

    private string _footerColor;
    public string FooterColor { get => _footerColor; set => this.RaiseAndSetIfChanged(ref _footerColor, value); }

    /// <summary>What the colour field beside a Static block is labelled. It colours the content
    /// rather than a header, and calling it "header colour" there would be a lie.</summary>
    public string HeaderColorLabel => IsStatic ? "TEXT COLOUR" : "HEADER COLOUR";

    public PostBlockRow(string postType, string name, string? staticContent, string header, string footer,
                        string headerColor = "", string footerColor = "")
    {
        _postType      = postType;
        _name          = name;
        _staticContent = staticContent;
        _header        = header;
        _footer        = footer;
        _headerColor   = headerColor;
        _footerColor   = footerColor;
    }
}

// Manages the ordered list of post blocks for one posting. The first block is the parent; the
// rest are supporting detail (e.g. Slack thread replies). Backs the grid in the posting dialog.
public class PostsEditorViewModel : ReactiveObject
{
    public ObservableCollection<PostBlockRow> Posts { get; } = [];

    public ReactiveCommand<Unit, Unit>         AddPostCommand    { get; }
    public ReactiveCommand<PostBlockRow, Unit> MoveUpCommand     { get; }
    public ReactiveCommand<PostBlockRow, Unit> MoveDownCommand   { get; }
    public ReactiveCommand<PostBlockRow, Unit> DeletePostCommand { get; }

    public PostsEditorViewModel(IEnumerable<PostBlockDraft> existing)
    {
        foreach (var d in existing)
            Posts.Add(new PostBlockRow(d.PostType, d.Name, d.StaticContent, d.Header, d.Footer,
                                       d.HeaderColor, d.FooterColor));

        // Default a new posting to a single "Detail" block named "Detail".
        if (Posts.Count == 0)
            Posts.Add(new PostBlockRow("Detail", "Detail", null, "", ""));

        AddPostCommand    = ReactiveCommand.Create(() => Posts.Add(new PostBlockRow("Summary", "", null, "", "")));
        MoveUpCommand     = ReactiveCommand.Create<PostBlockRow>(row => Move(row, -1));
        MoveDownCommand   = ReactiveCommand.Create<PostBlockRow>(row => Move(row, +1));
        DeletePostCommand = ReactiveCommand.Create<PostBlockRow>(row => Posts.Remove(row));
    }

    private void Move(PostBlockRow row, int delta)
    {
        int i = Posts.IndexOf(row);
        int j = i + delta;
        if (i < 0 || j < 0 || j >= Posts.Count) return;
        Posts.Move(i, j);
    }

    public List<PostBlockDraft> ToDrafts()
        => Posts.Select(p => new PostBlockDraft(
            p.PostType, p.Name,
            p.IsStatic ? p.StaticContent : null,
            p.IsStatic ? "" : p.Header,
            p.IsStatic ? "" : p.Footer,
            // ⚠️ Kept on a Static block: there it colours the content, which is the only text it
            // has. Blanking it with the header would throw the setting away on save.
            p.HeaderColor,
            p.IsStatic ? "" : p.FooterColor)).ToList();
}
