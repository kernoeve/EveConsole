using System.Diagnostics;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using EveConsole.Services;
using EveConsole.Services.Jabber;

namespace EveConsole.Views;

/// <summary>
/// One clock for every moving emote on screen, so a window full of them costs one timer, and two of the same one move
/// together. It only runs while something is listening.
/// </summary>
public static class EmoteClock
{
    private static readonly Stopwatch Watch = Stopwatch.StartNew();
    private static DispatcherTimer? _timer;
    private static Action? _tick;

    public static long Now => Watch.ElapsedMilliseconds;

    public static event Action Tick
    {
        add
        {
            _tick += value;
            if (_timer is not null) return;
            _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(30), DispatcherPriority.Render, (_, _) => _tick?.Invoke());
            _timer.Start();
        }
        remove
        {
            _tick -= value;
            if (_tick is not null || _timer is null) return;
            _timer.Stop();
            _timer = null;
        }
    }
}

/// <summary>
/// An emote's picture, moving if it is an animated GIF. In a message it is its own size, as Pidgin shows it, with only
/// a very large one made smaller; with <see cref="Box"/> set it is fitted into a square of that size, as in the picker.
/// </summary>
public sealed class EmoteView : Control
{
    /// <summary>The tallest an emote is shown in a message. Some packs have whole pictures in them.</summary>
    public const double MostHeight = 128;

    /// <summary>Where pictures come from. Set once when the app starts.</summary>
    public static EmoteService? Service { get; set; }

    public static readonly StyledProperty<EmoteRef?> EmoteProperty = AvaloniaProperty.Register<EmoteView, EmoteRef?>(nameof(Emote));
    public static readonly StyledProperty<double> BoxProperty = AvaloniaProperty.Register<EmoteView, double>(nameof(Box));
    public static readonly StyledProperty<bool> StillProperty = AvaloniaProperty.Register<EmoteView, bool>(nameof(Still));

    private EmoteFrames? _frames;
    private Bitmap? _shown;
    private bool _ticking;

    static EmoteView()
    {
        AffectsMeasure<EmoteView>(EmoteProperty, BoxProperty);
    }

    public EmoteView()
    {
        RenderOptions.SetBitmapInterpolationMode(this, BitmapInterpolationMode.HighQuality);
    }

    public EmoteRef? Emote { get => GetValue(EmoteProperty); set => SetValue(EmoteProperty, value); }
    public double Box { get => GetValue(BoxProperty); set => SetValue(BoxProperty, value); }

    /// <summary>The first frame only, as the picker shows a moving one until the pointer is on it.</summary>
    public bool Still { get => GetValue(StillProperty); set => SetValue(StillProperty, value); }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == EmoteProperty || change.Property == StillProperty) Load();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (_frames is null) Load();
        else Listen();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        StopListening();
    }

    private void Load()
    {
        var emote = Emote;
        var still = Still;
        StopListening();
        _frames = null;
        _shown = null;
        InvalidateVisual();
        if (emote is null || Service is not { } service || !this.IsAttachedToVisualTree()) return;

        _ = service.FramesAsync(emote.Hash, still).ContinueWith(t => Dispatcher.UIThread.Post(() =>
        {
            if (!ReferenceEquals(Emote, emote) || Still != still || t.Result is not { } frames) return;
            _frames = frames;
            _shown = frames.At(EmoteClock.Now);
            InvalidateVisual();
            Listen();
        }), TaskScheduler.Default);
    }

    private void Listen()
    {
        if (_ticking || _frames is not { IsAnimated: true } || !this.IsAttachedToVisualTree()) return;
        EmoteClock.Tick += OnTick;
        _ticking = true;
    }

    private void StopListening()
    {
        if (!_ticking) return;
        EmoteClock.Tick -= OnTick;
        _ticking = false;
    }

    private void OnTick()
    {
        if (_frames is null || !IsEffectivelyVisible) return;
        var frame = _frames.At(EmoteClock.Now);
        if (ReferenceEquals(frame, _shown)) return;
        _shown = frame;
        InvalidateVisual();
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        if (Emote is not { } emote) return default;
        if (Box > 0) return new Size(Box, Box);

        var (w, h) = ((double)Math.Max(1, emote.Width), (double)Math.Max(1, emote.Height));
        var scale = Math.Min(1, Math.Min(MostHeight / h, double.IsFinite(availableSize.Width) && availableSize.Width > 0 ? availableSize.Width / w : 1));
        return new Size(w * scale, h * scale);
    }

    public override void Render(DrawingContext context)
    {
        if (_shown is not { } bitmap) return;

        var size = bitmap.Size;
        var bounds = new Rect(Bounds.Size);
        var scale = Math.Min(1, Math.Min(bounds.Width / size.Width, bounds.Height / size.Height));
        if (Box <= 0) scale = Math.Min(bounds.Width / size.Width, bounds.Height / size.Height);

        var drawn = new Size(size.Width * scale, size.Height * scale);
        var at = new Rect(new Point((bounds.Width - drawn.Width) / 2, (bounds.Height - drawn.Height) / 2), drawn);
        context.DrawImage(bitmap, new Rect(size), at);
    }
}

/// <summary>
/// A message's text with its emote codes shown as their pictures, from the set of the server it was said on. A
/// message with none in it, or on a server with no packs, is plain text exactly as before.
///
/// <para>Copying gives back the codes, not the pictures, so what is pasted reads the same in any client.</para>
/// </summary>
public sealed class EmoteTextBlock : SelectableTextBlock
{
    public static readonly StyledProperty<string?> BodyProperty = AvaloniaProperty.Register<EmoteTextBlock, string?>(nameof(Body));
    public static readonly StyledProperty<EmoteSet?> EmotesProperty = AvaloniaProperty.Register<EmoteTextBlock, EmoteSet?>(nameof(Emotes));

    private EmoteSet? _listening;
    private IReadOnlyList<EmotePiece> _pieces = [];

    protected override Type StyleKeyOverride => typeof(SelectableTextBlock);

    public EmoteTextBlock()
    {
        AddHandler(CopyingToClipboardEvent, OnCopying);
    }

    public string? Body { get => GetValue(BodyProperty); set => SetValue(BodyProperty, value); }
    public EmoteSet? Emotes { get => GetValue(EmotesProperty); set => SetValue(EmotesProperty, value); }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == EmotesProperty && this.IsAttachedToVisualTree()) Listen();
        if (change.Property == BodyProperty || change.Property == EmotesProperty) Rebuild();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        Listen();
        Rebuild();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        if (_listening is not null) _listening.Changed -= Rebuild;
        _listening = null;
    }

    private void Listen()
    {
        if (ReferenceEquals(_listening, Emotes)) return;
        if (_listening is not null) _listening.Changed -= Rebuild;
        _listening = Emotes;
        if (_listening is not null) _listening.Changed += Rebuild;
    }

    private static readonly FontFamily Monospace = new("Cascadia Mono, Consolas, Menlo, DejaVu Sans Mono, monospace");

    /// <summary>
    /// The message as styled text (XEP-0393) with its emotes, nothing being styled or swapped inside code. ⚠️ Plain text
    /// stays as plain Text, the cheap way, for the many messages with neither.
    /// </summary>
    private void Rebuild()
    {
        var body = Body ?? "";
        var set = Emotes ?? EmoteSet.None;
        IReadOnlyList<StyledSpan> spans = MessageStyling.MayHaveStyling(body) ? MessageStyling.Parse(body) : [new StyledSpan(body, TextStyle.None)];

        var pieces = new List<EmotePiece>();
        var inlines = new InlineCollection();
        var plain = true;

        foreach (var span in spans)
        {
            IReadOnlyList<EmotePiece> parts = span.Style.HasFlag(TextStyle.Mono) ? [new EmotePiece(span.Text, null)] : set.Split(span.Text);
            foreach (var part in parts)
            {
                if (part.Text.Length == 0) continue;
                pieces.Add(part);

                if (part.Emote is { } emote)
                {
                    plain = false;
                    var view = new EmoteView { Emote = emote };
                    ToolTip.SetTip(view, emote.Code);
                    inlines.Add(new InlineUIContainer(view) { BaselineAlignment = BaselineAlignment.Center });
                    continue;
                }

                var run = new Run(part.Text);
                if (span.Style != TextStyle.None)
                {
                    plain = false;
                    StyleRun(run, span.Style);
                }
                inlines.Add(run);
            }
        }

        _pieces = pieces;
        if (plain)
        {
            Inlines?.Clear();
            Text = body;
            return;
        }

        Text = null;
        Inlines = inlines;
    }

    private void StyleRun(Run run, TextStyle style)
    {
        if (style.HasFlag(TextStyle.Bold)) run.FontWeight = FontWeight.Bold;
        if (style.HasFlag(TextStyle.Italic)) run.FontStyle = FontStyle.Italic;
        if (style.HasFlag(TextStyle.Strike)) run.TextDecorations = Avalonia.Media.TextDecorations.Strikethrough;
        if (style.HasFlag(TextStyle.Mono)) run.FontFamily = Monospace;

        // The marks faint, a quote muted: from the theme, so both follow it.
        if (style.HasFlag(TextStyle.Directive)) run.Bind(TextElement.ForegroundProperty, this.GetResourceObservable("TextFaintBrush"));
        else if (style.HasFlag(TextStyle.Quote)) run.Bind(TextElement.ForegroundProperty, this.GetResourceObservable("TextMutedBrush"));
    }

    private void OnCopying(object? sender, RoutedEventArgs e)
    {
        if (_pieces.All(p => p.Emote is null)) return;

        var (start, end) = (Math.Min(SelectionStart, SelectionEnd), Math.Max(SelectionStart, SelectionEnd));
        if (start == end) return;

        // How many characters a picture is in the selection, from the text the selection counts: Avalonia 11.3 makes it
        // none at all. ⚠️ Assumed to be one, a copy of a whole message lost its last character.
        var plain = _pieces.Where(p => p.Emote is null).Sum(p => p.Text.Length);
        var pictures = _pieces.Count(p => p.Emote is not null);
        var embedded = Math.Max(0, ((Inlines?.Text?.Length ?? plain) - plain) / pictures);

        var text = new StringBuilder();
        var at = 0;
        foreach (var piece in _pieces)
        {
            if (piece.Emote is null)
            {
                var (from, to) = (Math.Max(start, at), Math.Min(end, at + piece.Text.Length));
                if (from < to) text.Append(piece.Text.AsSpan(from - at, to - from));
                at += piece.Text.Length;
            }
            else
            {
                // A picture of no width is in the selection when the selection reaches its place, ends included.
                if (embedded > 0 ? Math.Max(start, at) < Math.Min(end, at + embedded) : start <= at && at <= end) text.Append(piece.Text);
                at += embedded;
            }
        }

        e.Handled = true;
        _ = TopLevel.GetTopLevel(this)?.Clipboard?.SetTextAsync(text.ToString());
    }
}
