using Avalonia.Threading;
using EveConsole.Services.ClientLayouts;

namespace EveConsole.Views;

/// <summary>
/// Keeps the panel windows in step with what <see cref="ClientLayoutService"/> says should show.
///
/// <para>A panel that should hide is hidden rather than closed: its window stays loaded so it can
/// come back at once when a client returns. They only close when placement is turned off, when a
/// layout no longer has them, or when the app exits.</para>
/// </summary>
public sealed class ClientPanelHost
{
    private readonly Dictionary<(string Monitor, string Panel), ClientPanelWindow> _windows = [];

    public ClientPanelHost(ClientLayoutService service) =>
        service.PanelsChanged += states => Dispatcher.UIThread.Post(() => Update(states));

    private void Update(IReadOnlyList<PanelState> states)
    {
        var wanted = states.ToDictionary(s => (s.MonitorKey, s.Panel.Id));
        foreach (var key in _windows.Keys.Where(k => !wanted.ContainsKey(k)).ToList())
        {
            _windows[key].Close();
            _windows.Remove(key);
        }

        foreach (var (key, state) in wanted)
        {
            _windows.TryGetValue(key, out var window);
            if (!state.Show)
            {
                window?.Hide();
                continue;
            }
            if (window == null) _windows[key] = window = new ClientPanelWindow();
            window.Show(state.Panel);
        }
    }

    /// <summary>The app is exiting. Clients stay where they are.</summary>
    public void CloseAll()
    {
        foreach (var window in _windows.Values) window.Close();
        _windows.Clear();
    }
}
