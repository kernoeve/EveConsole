using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using EveConsole.Services;

namespace EveConsole.Controls;

/// <summary>
/// A box of labels: each one a chip with an ✕, and a place to type another.
///
/// <para>Typing a label that does not exist yet IS how a label is created — there is no separate
/// step and no list to maintain. What is offered underneath is whatever is already in use, which
/// is what keeps two spellings of one tag from splitting a report in half.</para>
///
/// <para>⚠️ Compared without case, like everywhere else labels are handled. The chip keeps the
/// spelling already in use rather than the one just typed, so "bni" typed against an existing
/// "BNI" adds nothing and changes nothing.</para>
/// </summary>
public partial class LabelsBox : UserControl
{
    private readonly List<string> _labels = [];
    private List<string>          _known  = [];
    private readonly TextBox      _entry;

    public LabelsBox()
    {
        InitializeComponent();

        // Built in code rather than XAML because it lives inside the WrapPanel, after however
        // many chips there are — it is the last item in the flow, not a sibling of the panel.
        _entry = new TextBox
        {
            Background        = Brushes.Transparent,
            BorderThickness   = new Thickness(0),
            Foreground        = Brush.Parse("#e8e8f0"),
            CaretBrush        = Brush.Parse("#c8a84b"),
            FontSize          = 11,
            MinWidth          = 90,
            Padding           = new Thickness(3, 1),
            VerticalAlignment = VerticalAlignment.Center,
            Watermark         = "add a label…",
        };

        _entry.KeyDown     += OnEntryKey;
        _entry.TextChanged += (_, _) => RefreshSuggestions();
        _entry.GotFocus    += (_, _) => Frame.BorderBrush = Brush.Parse("#3a4a6a");
        _entry.LostFocus   += (_, _) =>
        {
            Frame.BorderBrush = Brush.Parse("#2a2a3a");

            // ⚠️ Commits what was typed on the way out. Half-typed text left in the box and then
            // abandoned by clicking Save is a label the person believed they had added.
            Commit(_entry.Text);
            Hide();
        };

        ChevronButton.Click += (_, _) =>
        {
            if (SuggestBox.IsVisible) Hide();
            else { _entry.Text = ""; ShowSuggestions(_known); _entry.Focus(); }
        };

        Rebuild();
    }

    /// <summary>The labels currently on the chip list.</summary>
    public IReadOnlyList<string> Labels => _labels;

    /// <summary>Every label the pickers should offer. Set before <see cref="SetLabels"/>.</summary>
    public void SetKnown(IEnumerable<string> known) =>
        _known = known.Select(OrderLabelService.Clean).Where(l => l.Length > 0)
                      .Distinct(StringComparer.OrdinalIgnoreCase)
                      .OrderBy(l => l, StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>Replaces what is in the box.</summary>
    public void SetLabels(IEnumerable<string> labels)
    {
        _labels.Clear();
        foreach (var label in labels.Select(OrderLabelService.Clean).Where(l => l.Length > 0))
            if (!_labels.Contains(label, StringComparer.OrdinalIgnoreCase))
                _labels.Add(label);
        Rebuild();
    }

    private void OnFramePressed(object? sender, PointerPressedEventArgs e)
    {
        // Anywhere in the frame puts the caret in the entry — the box reads as one field, so it
        // should behave as one. Not when the press landed on a chip's remove button, which has
        // its own job.
        if (e.Source is Button) return;
        _entry.Focus();
    }

    private void OnEntryKey(object? sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Enter or Key.Tab when !string.IsNullOrWhiteSpace(_entry.Text):
                Commit(_entry.Text);
                e.Handled = true;
                break;

            // ⚠️ Only on an empty box, so backspacing through typed text never eats a chip that
            // was already there.
            case Key.Back when string.IsNullOrEmpty(_entry.Text) && _labels.Count > 0:
                _labels.RemoveAt(_labels.Count - 1);
                Rebuild();
                e.Handled = true;
                break;

            case Key.Escape:
                _entry.Text = "";
                Hide();
                break;
        }
    }

    private void OnSuggestionPicked(object? sender, SelectionChangedEventArgs e)
    {
        if (SuggestList.SelectedItem is not string picked) return;

        SuggestList.SelectedItem = null;   // so the same one can be picked again later
        Commit(picked);
        _entry.Focus();
    }

    private void Commit(string? text)
    {
        var clean = OrderLabelService.Clean(text);
        _entry.Text = "";
        Hide();
        if (clean.Length == 0) return;

        // Existing spelling wins, so the chip matches what every other order already carries.
        clean = _known.FirstOrDefault(k => string.Equals(k, clean, StringComparison.OrdinalIgnoreCase))
                ?? clean;

        if (_labels.Contains(clean, StringComparer.OrdinalIgnoreCase)) return;

        _labels.Add(clean);
        if (!_known.Contains(clean, StringComparer.OrdinalIgnoreCase))
        {
            _known.Add(clean);
            _known.Sort(StringComparer.OrdinalIgnoreCase);
        }
        Rebuild();
    }

    private void RefreshSuggestions()
    {
        var typed = (_entry.Text ?? "").Trim();
        if (typed.Length == 0) { Hide(); return; }

        var matches = _known
            .Where(k => k.Contains(typed, StringComparison.OrdinalIgnoreCase)
                     && !_labels.Contains(k, StringComparer.OrdinalIgnoreCase))
            .ToList();

        ShowSuggestions(matches);

        // Says so rather than showing an empty box: nothing found is the moment somebody needs
        // telling that Enter makes a new one.
        EmptyNote.IsVisible = matches.Count == 0;
    }

    private void ShowSuggestions(IReadOnlyList<string> items)
    {
        var offer = items.Where(k => !_labels.Contains(k, StringComparer.OrdinalIgnoreCase)).ToList();
        SuggestList.ItemsSource = offer;
        SuggestBox.IsVisible    = offer.Count > 0;
        EmptyNote.IsVisible     = false;
    }

    private void Hide()
    {
        SuggestBox.IsVisible = false;
        EmptyNote.IsVisible  = false;
    }

    private void Rebuild()
    {
        Host.Children.Clear();

        foreach (var label in _labels)
        {
            var text = new TextBlock
            {
                Text              = label,
                FontSize          = 11,
                Foreground        = Brush.Parse("#c8c8d8"),
                VerticalAlignment = VerticalAlignment.Center,
            };

            var remove = new Button
            {
                Content           = "✕",
                FontSize          = 9,
                Padding           = new Thickness(4, 0),
                Margin            = new Thickness(3, 0, -2, 0),
                Background        = Brushes.Transparent,
                BorderThickness   = new Thickness(0),
                Foreground        = Brush.Parse("#777788"),
                VerticalAlignment = VerticalAlignment.Center,
            };

            var captured = label;
            remove.Click += (_, _) =>
            {
                _labels.RemoveAll(l => string.Equals(l, captured, StringComparison.OrdinalIgnoreCase));
                Rebuild();
            };

            Host.Children.Add(new Border
            {
                Background      = Brush.Parse("#1a2030"),
                BorderBrush     = Brush.Parse("#3a4a6a"),
                BorderThickness = new Thickness(1),
                CornerRadius    = new CornerRadius(2),
                Padding         = new Thickness(6, 1),
                Margin          = new Thickness(0, 2, 4, 2),
                Child           = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Children    = { text, remove },
                },
            });
        }

        Host.Children.Add(_entry);
    }
}
