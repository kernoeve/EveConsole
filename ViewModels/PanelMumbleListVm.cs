using System.Collections.ObjectModel;
using Avalonia.Threading;
using EveConsole.Services.ClientLayouts;
using EveConsole.Services.Mumble;
using ReactiveUI;

namespace EveConsole.ViewModels;

/// <summary>
/// Everyone in this client's channel on one Mumble server, for a panel: the current and recent
/// talkers float to the top. Nothing else in the app keeps this list, so it's built here from what
/// the service knows, on the UI thread, whenever the channel or the talkers change.
/// </summary>
public sealed class PanelMumbleListVm(MumbleService service, int serverId) : ReactiveObject, IDisposable
{
    private readonly MumbleTalkOrder _order = new();
    private bool _started;

    public ObservableCollection<MumbleOverlayLine> Users { get; } = [];

    public void Start()
    {
        if (_started) return;
        _started = true;
        service.SessionChanged += OnSessionChanged;
        service.TalkersChanged += OnTalkersChanged;
        Refresh();
    }

    public void Dispose()
    {
        if (!_started) return;
        _started = false;
        service.SessionChanged -= OnSessionChanged;
        service.TalkersChanged -= OnTalkersChanged;
    }

    private void OnSessionChanged(MumbleSession session)
    {
        if (session.Server.Id == serverId) Dispatcher.UIThread.Post(Refresh);
    }

    private void OnTalkersChanged() => Dispatcher.UIThread.Post(Refresh);

    private void Refresh()
    {
        if (!_started) return;
        var session = service.SessionOf(serverId);
        var client = session?.Client;
        var me = client?.Me;
        if (session == null || client == null || me == null)
        {
            Users.Clear();
            return;
        }

        var talkers = service.Talkers().Where(t => t.ServerId == serverId).ToDictionary(t => t.Session, t => t.Mode);
        var inChannel = client.UsersIn(me.ChannelId);
        _order.KeepOnly(inChannel.Select(u => u.Session));
        _order.Heard(talkers.Keys, DateTime.UtcNow);

        var ordered = _order.Order(inChannel, u => u.Session, u => u.Name);
        foreach (var gone in Users.Where(line => ordered.All(u => u.Session != line.Session)).ToList()) Users.Remove(gone);
        for (var i = 0; i < ordered.Count; i++)
        {
            var user = ordered[i];
            var line = Users.FirstOrDefault(l => l.Session == user.Session);
            if (line == null)
            {
                line = new MumbleOverlayLine(user.Session, user.Name) { Avatar = MumbleOverlayLine.AvatarOf(session, user.Session, user.Name) };
                Users.Insert(i, line);
            }
            else
            {
                var at = Users.IndexOf(line);
                if (at != i) Users.Move(at, i);
            }
            line.Mode = talkers.GetValueOrDefault(user.Session, MumbleTalk.None);
            line.IsMuted = user.Mute || user.SelfMute;
            line.IsDeaf = user.Deaf || user.SelfDeaf;
        }
    }
}
