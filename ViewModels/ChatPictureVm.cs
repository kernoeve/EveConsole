using Avalonia.Media.Imaging;
using Avalonia.Threading;
using EveConsole.Services;
using ReactiveUI;

namespace EveConsole.ViewModels;

/// <summary>
/// A picture linked in a chat message, shown under it, scaled to fit. From the chat server's own file host it loads at
/// once; from anywhere else it waits for a click, as loading it tells that site this person's address
/// (<see cref="ChatPictures"/>).
/// </summary>
/// <remarks>
/// Loaded only when its line is on screen (the list is virtualized, and the picture is asked for by the screen), once
/// per link: a picture already loaded is kept, so scrolling back does not fetch it again.
/// </remarks>
public sealed class ChatPictureVm : ReactiveObject
{
    /// <summary>Most shown, in the chat. A bigger one is scaled down to fit; a smaller one is never blown up.</summary>
    public const double MostWidth = 360, MostHeight = 280;

    /// <summary>Widest kept in memory: a huge picture is decoded smaller, as it is only ever shown small here.</summary>
    private const int MostDecodedWidth = 1280;

    private const int MostKept = 80;

    private static readonly Lock Gate = new();
    private static readonly Dictionary<string, Bitmap> Kept = new(StringComparer.Ordinal);
    private static readonly LinkedList<string> KeptOrder = new();
    private static readonly Dictionary<string, Task<Bitmap?>> Loading = new(StringComparer.Ordinal);

    /// <summary>Links the person chose to show this run: shown again without asking when their line comes back.</summary>
    private static readonly HashSet<string> Chosen = new(StringComparer.Ordinal);

    /// <summary>A picture finished loading and took its room on the screen: a chat at its end scrolls to keep there.</summary>
    public static event Action? AnyShown;

    private enum PictureState { Waiting, Asking, Loading, Shown, Failed }

    private PictureState _state;
    private Bitmap? _picture;
    private bool _started;

    public ChatPictureVm(string url)
    {
        Url = url;
        Host = Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.Host : url;
        bool chosen;
        lock (Gate) chosen = Chosen.Contains(url);
        _state = chosen || ChatPictures.OwnHost(url) is not null ? PictureState.Waiting : PictureState.Asking;
    }

    /// <summary>The pictures among a message's links, each once.</summary>
    public static IReadOnlyList<ChatPictureVm> Of(IEnumerable<string> links) =>
        [.. links.Where(ChatPictures.IsPictureLink).Distinct(StringComparer.Ordinal).Select(l => new ChatPictureVm(l))];

    public string Url { get; }
    public string Host { get; }

    public string ShowTip => string.Format(System.Globalization.CultureInfo.CurrentCulture, Localization.GroupChatText.ShowPictureTip, Host);

    public bool NeedsClick => _state == PictureState.Asking;
    public bool IsLoading => _state is PictureState.Waiting or PictureState.Loading;
    public bool IsShown => _state == PictureState.Shown;
    public bool IsFailed => _state == PictureState.Failed;

    /// <summary>The picture. ⚠️ Asking for it is what starts loading it: the screen asks only for lines it shows.</summary>
    public Bitmap? Picture
    {
        get
        {
            // ⚠️ Posted: this is read while the screen binds, and starting raises changes it must not hear mid-read.
            if (_state == PictureState.Waiting && !_started) Dispatcher.UIThread.Post(Start);
            return _picture;
        }
    }

    public double ShownWidth { get; private set; }
    public double ShownHeight { get; private set; }

    /// <summary>The person asked for it: loads it, and any other line with the same link, this run.</summary>
    public void Show()
    {
        if (_state != PictureState.Asking) return;
        lock (Gate) Chosen.Add(Url);
        SetState(PictureState.Waiting);
        Start();
    }

    private void Start()
    {
        if (_started) return;
        _started = true;

        Bitmap? kept;
        lock (Gate) kept = Kept.GetValueOrDefault(Url);
        if (kept is not null) { Take(kept, false); return; }

        SetState(PictureState.Loading);
        _ = LoadAsync();
    }

    private async Task LoadAsync()
    {
        Task<Bitmap?> loading;
        lock (Gate)
        {
            if (!Loading.TryGetValue(Url, out loading!)) Loading[Url] = loading = Task.Run(() => FetchAsync(Url));
        }
        var picture = await loading;
        lock (Gate) Loading.Remove(Url);
        Dispatcher.UIThread.Post(() =>
        {
            if (picture is null) SetState(PictureState.Failed);
            else Take(picture, true);
        });
    }

    private static async Task<Bitmap?> FetchAsync(string url)
    {
        var bytes = await ChatPictures.FetchAsync(url, CancellationToken.None);
        if (bytes is null) return null;
        try
        {
            using var stream = new MemoryStream(bytes);
            var picture = new Bitmap(stream);
            if (picture.PixelSize.Width > MostDecodedWidth)
            {
                picture.Dispose();
                stream.Position = 0;
                picture = Bitmap.DecodeToWidth(stream, MostDecodedWidth);
            }
            Keep(url, picture);
            return picture;
        }
        catch (Exception e) when (e is not OutOfMemoryException) { return null; }   // not a picture after all
    }

    /// <summary>
    /// Kept for the next time its line shows, the oldest let go past the limit. ⚠️ Not disposed when let go: a line on
    /// screen may still be drawing it. It is freed once nothing holds it.
    /// </summary>
    private static void Keep(string url, Bitmap picture)
    {
        lock (Gate)
        {
            if (Kept.ContainsKey(url)) return;
            Kept[url] = picture;
            KeptOrder.AddLast(url);
            while (KeptOrder.Count > MostKept)
            {
                Kept.Remove(KeptOrder.First!.Value);
                KeptOrder.RemoveFirst();
            }
        }
    }

    private void Take(Bitmap picture, bool justLoaded)
    {
        var (w, h) = (picture.Size.Width, picture.Size.Height);
        var scale = Math.Min(1, Math.Min(MostWidth / Math.Max(1, w), MostHeight / Math.Max(1, h)));
        ShownWidth = Math.Max(1, Math.Round(w * scale));
        ShownHeight = Math.Max(1, Math.Round(h * scale));
        _picture = picture;
        this.RaisePropertyChanged(nameof(ShownWidth));
        this.RaisePropertyChanged(nameof(ShownHeight));
        this.RaisePropertyChanged(nameof(Picture));
        SetState(PictureState.Shown);
        if (justLoaded) AnyShown?.Invoke();
    }

    private void SetState(PictureState state)
    {
        if (_state == state) return;
        _state = state;
        this.RaisePropertyChanged(nameof(NeedsClick));
        this.RaisePropertyChanged(nameof(IsLoading));
        this.RaisePropertyChanged(nameof(IsShown));
        this.RaisePropertyChanged(nameof(IsFailed));
    }
}
