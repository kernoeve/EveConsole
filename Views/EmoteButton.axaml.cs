using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using EveConsole.Services.Jabber;
using ReactiveUI;

namespace EveConsole.Views;

/// <summary>A code the colon being typed could become.</summary>
public sealed class EmoteSuggestion(EmoteRef emote) : ReactiveObject
{
    private bool _current;

    public EmoteRef Emote => emote;
    public bool IsCurrent { get => _current; set => this.RaiseAndSetIfChanged(ref _current, value); }
}

/// <summary>
/// Emotes for a message box: the face button that opens the picker, and the list that comes up while a colon code is
/// being typed. Both are for the conversation's own server, and neither shows when that server has no packs.
///
/// <para>⚠️ The view the box is in has to hand its keys here first, through <see cref="HandleKey"/>: it sends on
/// Enter, and while the list is open Enter is for choosing from it.</para>
/// </summary>
public partial class EmoteButton : UserControl
{
    private const int MostSuggested = 8;

    public static readonly StyledProperty<EmoteSet?> EmotesProperty = AvaloniaProperty.Register<EmoteButton, EmoteSet?>(nameof(Emotes));
    public static readonly StyledProperty<TextBox?> TargetProperty = AvaloniaProperty.Register<EmoteButton, TextBox?>(nameof(Target));

    private EmoteSet? _listening;
    private TextBox? _box;
    private List<EmoteSuggestion> _suggestions = [];
    private int _current;
    private int _start = -1;
    private string _dismissed = "";

    public EmoteButton()
    {
        InitializeComponent();
        Picker.Picked += OnPicked;
        PickerFlyout.Opened += (_, _) => Picker.Show(Emotes ?? EmoteSet.None);
    }

    /// <summary>The conversation's server's emotes.</summary>
    public EmoteSet? Emotes { get => GetValue(EmotesProperty); set => SetValue(EmotesProperty, value); }

    /// <summary>The box a chosen code goes into.</summary>
    public TextBox? Target { get => GetValue(TargetProperty); set => SetValue(TargetProperty, value); }

    private EmoteSet Set => Emotes ?? EmoteSet.None;

    // A flyout is not a control, so its name gives no field.
    private FlyoutBase PickerFlyout => Opener.Flyout!;

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == EmotesProperty)
        {
            if (this.IsAttachedToVisualTree()) Listen();
            Refresh();
            CloseSuggest();
        }
        else if (change.Property == TargetProperty) Wire(this.IsAttachedToVisualTree() ? Target : null);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        Listen();
        Wire(Target);
        Refresh();

        // ⚠️ A popup is a topmost window of its own: left open when the person goes to another program, it sits over
        // that program. Both are put away the moment this window stops being the one in use.
        _window = TopLevel.GetTopLevel(this) as Window;
        if (_window is not null) _window.Deactivated += OnWindowDeactivated;
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        if (_listening is not null) _listening.Changed -= Refresh;
        _listening = null;
        Wire(null);
        CloseSuggest();
        PickerFlyout.Hide();
        if (_window is not null) _window.Deactivated -= OnWindowDeactivated;
        _window = null;
    }

    private Window? _window;

    private void OnWindowDeactivated(object? sender, EventArgs e)
    {
        CloseSuggest();
        PickerFlyout.Hide();
    }

    private void Listen()
    {
        if (ReferenceEquals(_listening, Emotes)) return;
        if (_listening is not null) _listening.Changed -= Refresh;
        _listening = Emotes;
        if (_listening is not null) _listening.Changed += Refresh;
    }

    private void Refresh()
    {
        Opener.IsVisible = !Set.IsEmpty;
        if (Set.IsEmpty) { CloseSuggest(); PickerFlyout.Hide(); }
    }

    private void Wire(TextBox? box)
    {
        if (ReferenceEquals(_box, box)) return;
        if (_box is not null)
        {
            _box.PropertyChanged -= OnBoxChanged;
            _box.LostFocus -= OnBoxLostFocus;
        }
        _box = box;
        if (_box is not null)
        {
            _box.PropertyChanged += OnBoxChanged;
            _box.LostFocus += OnBoxLostFocus;
        }
    }

    // ── The picker ────────────────────────────────────────────────────────────

    private void OnPicked(EmoteRef emote)
    {
        PickerFlyout.Hide();
        if (_box is null) return;

        var text = _box.Text ?? "";
        var (from, to) = (Math.Min(_box.SelectionStart, _box.SelectionEnd), Math.Max(_box.SelectionStart, _box.SelectionEnd));
        from = Math.Clamp(from, 0, text.Length);
        to = Math.Clamp(to, from, text.Length);
        if (from == to) from = to = Math.Clamp(_box.CaretIndex, 0, text.Length);

        // A space either side, so the code stands on its own as every kind of code needs.
        var insert = (from > 0 && !char.IsWhiteSpace(text[from - 1]) ? " " : "") + emote.Code + " ";
        _box.Text = text[..from] + insert + text[to..];
        _box.CaretIndex = from + insert.Length;
        _box.Focus();
    }

    // ── The list while typing ─────────────────────────────────────────────────

    private void OnBoxChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == TextBox.TextProperty || e.Property == TextBox.CaretIndexProperty) UpdateSuggest();
    }

    private void OnBoxLostFocus(object? sender, Avalonia.Interactivity.RoutedEventArgs e) =>
        // After a moment: a press on the list would otherwise close it before it counts.
        DispatcherTimer.RunOnce(() => { if (_box is { IsFocused: false }) CloseSuggest(); }, TimeSpan.FromMilliseconds(200));

    private void UpdateSuggest()
    {
        if (_box is null || Set.IsEmpty || EmoteSet.TypedCode(_box.Text ?? "", _box.CaretIndex) is not { } typed)
        {
            CloseSuggest();
            _dismissed = "";
            return;
        }

        // Put away with Escape: stays away until something else is typed.
        var key = typed.Start + typed.Typed;
        if (key == _dismissed) return;

        var found = Set.StartingWith(typed.Typed, MostSuggested);
        if (found.Count == 0) { CloseSuggest(); return; }

        _start = typed.Start;
        _suggestions = [.. found.Select(f => new EmoteSuggestion(f))];
        _current = 0;
        _suggestions[0].IsCurrent = true;
        SuggestList.ItemsSource = _suggestions;
        Suggest.PlacementTarget = _box;
        Suggest.IsOpen = true;
    }

    private void CloseSuggest()
    {
        if (Suggest.IsOpen) Suggest.IsOpen = false;
        _suggestions = [];
        _start = -1;
    }

    /// <summary>Up, Down, Enter, Tab and Escape while the list is open. True when the key was used here.</summary>
    public bool HandleKey(KeyEventArgs e)
    {
        if (!Suggest.IsOpen || _suggestions.Count == 0) return false;

        switch (e.Key)
        {
            case Key.Down: Move(1); break;
            case Key.Up: Move(-1); break;
            case Key.Enter or Key.Tab: Accept(_suggestions[_current].Emote); break;
            case Key.Escape:
                if (_box is not null && EmoteSet.TypedCode(_box.Text ?? "", _box.CaretIndex) is { } typed) _dismissed = typed.Start + typed.Typed;
                CloseSuggest();
                break;
            default: return false;
        }
        e.Handled = true;
        return true;
    }

    private void Move(int by)
    {
        _suggestions[_current].IsCurrent = false;
        _current = (_current + by + _suggestions.Count) % _suggestions.Count;
        _suggestions[_current].IsCurrent = true;
    }

    private void OnSuggestPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Control { DataContext: EmoteSuggestion suggestion }) return;
        e.Handled = true;
        Accept(suggestion.Emote);
    }

    private void Accept(EmoteRef emote)
    {
        if (_box is null || _start < 0) { CloseSuggest(); return; }

        var text = _box.Text ?? "";
        var caret = Math.Clamp(_box.CaretIndex, _start, text.Length);
        var insert = emote.Code + " ";
        var start = _start;
        CloseSuggest();
        _box.Text = text[..start] + insert + text[caret..];
        _box.CaretIndex = start + insert.Length;
        _box.Focus();
    }
}
