namespace EveConsole.ViewModels;

/// <summary>
/// A tool that reloads itself on a timer.
///
/// <para>Every tool's view model is built in the MainWindowViewModel constructor, whether or not
/// its tab is ever opened — so a per-minute refresh starts at launch and runs for the life of the
/// session, rebuilding a grid nobody is looking at. Worse, that work lands on the UI thread, so a
/// tool the user has never opened can still freeze the window.</para>
///
/// <para>The flag is a one-way latch, set the first time the tool is opened and never cleared. It
/// deliberately does not track whether the tab is currently visible: a tab can be detached into
/// its own window, or sit behind another, and in both cases it is still on screen and still wants
/// fresh numbers.</para>
/// </summary>
public interface IPeriodicRefresh
{
    bool AutoRefreshEnabled { get; set; }
}
