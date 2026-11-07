namespace EveConsole.Services;

/// <summary>
/// Whether the app has finished starting: the main window is up. Work that can wait for it does,
/// so what the window needs to open isn't competing with it for the database.
/// </summary>
public static class AppStartup
{
    private static readonly TaskCompletionSource Done = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>The longest anything waits, so a start that never says it's done can't hold work back for ever.</summary>
    private static readonly TimeSpan MostWait = TimeSpan.FromSeconds(60);

    public static bool IsSettled => Done.Task.IsCompleted;

    /// <summary>Said once the main window is up, or once there's to be none.</summary>
    public static void Settled() => Done.TrySetResult();

    /// <summary>Done when the app has started, or after <see cref="MostWait"/> at the latest.</summary>
    public static Task WhenSettledAsync() => IsSettled ? Task.CompletedTask : Task.WhenAny(Done.Task, Task.Delay(MostWait));
}
